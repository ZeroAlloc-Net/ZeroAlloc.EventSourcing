using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing.Testing;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Integration tests for <see cref="SqlServerCheckpointStore"/> using a real SQL Server instance via Testcontainers.
/// Inherits all contract tests from <see cref="CheckpointStoreContractTests"/>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerCheckpointStoreTests(SqlServerContainerFixture fixture) : CheckpointStoreContractTests, IAsyncLifetime
{
    private TestDatabase _database = null!;
    private SqlServerCheckpointStore _store = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _store = new SqlServerCheckpointStore(_database.GetConnectionString());
        await _store.EnsureSchemaAsync();
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    /// <inheritdoc/>
    protected override ICheckpointStore CreateStore() => _store;

    /// <summary>
    /// Verifies that calling <see cref="SqlServerCheckpointStore.EnsureSchemaAsync"/> multiple times does not throw.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        var exception = await Record.ExceptionAsync(async () =>
        {
            await _store.EnsureSchemaAsync();
            await _store.EnsureSchemaAsync();
        });

        exception.Should().BeNull();
    }
    /// <summary>
    /// Two consumer ids that differ only outside the database code page keep a checkpoint each.
    /// A VARCHAR key turned both into <c>??-consumer</c>, so they silently shared one row.
    /// </summary>
    [Fact]
    public async Task NonLatinConsumerIds_KeepSeparateCheckpoints()
    {
        await _store.WriteAsync(SqlServerSchemaInspector.JapanId, new StreamPosition(1));
        await _store.WriteAsync(SqlServerSchemaInspector.ChinaId, new StreamPosition(2));

        (await _store.ReadAsync(SqlServerSchemaInspector.JapanId)).Should().Be(new StreamPosition(1));
        (await _store.ReadAsync(SqlServerSchemaInspector.ChinaId)).Should().Be(new StreamPosition(2));
    }

    /// <summary>
    /// A table created with the VARCHAR key of earlier versions is converted to NVARCHAR in place,
    /// keeping its rows and its primary key, and running the migration again changes nothing.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_MigratesLegacyVarcharTable()
    {
        var connectionString = _database.GetConnectionString();
        await CreateLegacyTableAsync(connectionString);

        await _store.EnsureSchemaAsync();
        await _store.EnsureSchemaAsync();

        var types = await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "consumer_checkpoints");
        types["consumer_id"].Should().Be("nvarchar(256)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "consumer_checkpoints"))
            .Should().Equal("PK CLUSTERED (consumer_id)");
        (await _store.ReadAsync("legacy")).Should().Be(new StreamPosition(7));

        await NonLatinConsumerIds_KeepSeparateCheckpoints();
    }

    /// <summary>
    /// App instances that start together run the migration at the same time; exactly one converts
    /// the table and the others find it done.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_ConcurrentCallsMigrateLegacyTableOnce()
    {
        var connectionString = _database.GetConnectionString();
        await CreateLegacyTableAsync(connectionString);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new SqlServerCheckpointStore(connectionString).EnsureSchemaAsync().AsTask()));

        (await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "consumer_checkpoints"))
            ["consumer_id"].Should().Be("nvarchar(256)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "consumer_checkpoints"))
            .Should().Equal("PK CLUSTERED (consumer_id)");
        (await _store.ReadAsync("legacy")).Should().Be(new StreamPosition(7));
    }

    /// <summary>Concurrent schema creation on an empty database does not race on CREATE TABLE.</summary>
    [Fact]
    public async Task EnsureSchemaAsync_ConcurrentCallsOnEmptyDatabaseSucceed()
    {
        var connectionString = _database.GetConnectionString();
        await SqlServerSchemaInspector.ExecuteAsync(connectionString, "DROP TABLE dbo.consumer_checkpoints");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new SqlServerCheckpointStore(connectionString).EnsureSchemaAsync().AsTask()));

        (await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "consumer_checkpoints"))
            ["consumer_id"].Should().Be("nvarchar(256)");
    }

    // The table as versions before #384 created it.
    private static Task CreateLegacyTableAsync(string connectionString) =>
        SqlServerSchemaInspector.ExecuteAsync(connectionString, """
            DROP TABLE dbo.consumer_checkpoints;
            CREATE TABLE dbo.consumer_checkpoints (
                consumer_id VARCHAR(256) NOT NULL PRIMARY KEY,
                position    BIGINT       NOT NULL,
                updated_at  DATETIME2    NOT NULL
            );
            INSERT INTO dbo.consumer_checkpoints VALUES ('legacy', 7, SYSUTCDATETIME());
            """);

    /// <summary>
    /// The schema step waits for the schema lock that another instance holds, so two instances
    /// never interleave the check for VARCHAR columns with the conversion. Concurrent calls alone
    /// rarely hit that window, so this holds the lock itself.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_WaitsForSchemaLockHeldByAnotherInstance()
    {
        await using var holder = new SqlConnection(_database.GetConnectionString());
        await holder.OpenAsync();
        await using var tx = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (var lockCmd = holder.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = """
                EXEC sp_getapplock @Resource = 'ZeroAlloc.EventSourcing.Sql.schema:dbo.consumer_checkpoints',
                    @LockMode = 'Exclusive', @LockOwner = 'Transaction'
                """;
            await lockCmd.ExecuteNonQueryAsync();
        }

        var ensure = _store.EnsureSchemaAsync().AsTask();
        var finishedFirst = await Task.WhenAny(ensure, Task.Delay(TimeSpan.FromSeconds(2)));
        finishedFirst.Should().NotBeSameAs(ensure, "the schema lock is held by another transaction");

        await tx.CommitAsync();
        await ensure.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
