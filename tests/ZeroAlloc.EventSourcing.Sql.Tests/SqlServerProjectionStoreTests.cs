using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Testing;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerProjectionStoreTests(SqlServerContainerFixture fixture) : ProjectionStoreContractTests, IAsyncLifetime
{
    private TestDatabase _database = null!;
    private SqlServerProjectionStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _store = new SqlServerProjectionStore(_database.GetConnectionString());
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    protected override IProjectionStore CreateStore() => _store;

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        await _store.EnsureSchemaAsync(); // second call must not throw
    }

    /// <summary>
    /// Two projection keys that differ only outside the database code page keep a state each.
    /// A VARCHAR key turned both into <c>??-consumer</c>, so the second save overwrote the first.
    /// </summary>
    [Fact]
    public async Task NonLatinKeys_KeepSeparateStates()
    {
        await _store.SaveAsync(SqlServerSchemaInspector.JapanId, "japan");
        await _store.SaveAsync(SqlServerSchemaInspector.ChinaId, "china");

        (await _store.LoadAsync(SqlServerSchemaInspector.JapanId)).Should().Be("japan");
        (await _store.LoadAsync(SqlServerSchemaInspector.ChinaId)).Should().Be("china");
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

        (await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "projection_states"))
            ["projection_key"].Should().Be("nvarchar(256)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "projection_states"))
            .Should().Equal("PK CLUSTERED (projection_key)");
        (await _store.LoadAsync("legacy")).Should().Be("{}");

        await NonLatinKeys_KeepSeparateStates();
    }

    /// <summary>App instances that start together run the migration once between them.</summary>
    [Fact]
    public async Task EnsureSchemaAsync_ConcurrentCallsMigrateLegacyTableOnce()
    {
        var connectionString = _database.GetConnectionString();
        await CreateLegacyTableAsync(connectionString);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new SqlServerProjectionStore(connectionString).EnsureSchemaAsync().AsTask()));

        (await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "projection_states"))
            ["projection_key"].Should().Be("nvarchar(256)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "projection_states"))
            .Should().Equal("PK CLUSTERED (projection_key)");
        (await _store.LoadAsync("legacy")).Should().Be("{}");
    }

    // The table as versions before #384 created it.
    private static Task CreateLegacyTableAsync(string connectionString) =>
        SqlServerSchemaInspector.ExecuteAsync(connectionString, """
            DROP TABLE dbo.projection_states;
            CREATE TABLE dbo.projection_states (
                projection_key VARCHAR(256)   NOT NULL PRIMARY KEY,
                state          NVARCHAR(MAX)  NOT NULL,
                updated_at     DATETIME2      NOT NULL
            );
            INSERT INTO dbo.projection_states VALUES ('legacy', N'{}', SYSUTCDATETIME());
            """);
}
