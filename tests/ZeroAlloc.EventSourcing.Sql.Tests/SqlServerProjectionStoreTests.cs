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
        await _store.SaveAsync(SqlServerSchemaInspector.JapanId, Utf8("japan"));
        await _store.SaveAsync(SqlServerSchemaInspector.ChinaId, Utf8("china"));

        Text(await _store.LoadAsync(SqlServerSchemaInspector.JapanId)).Should().Be("japan");
        Text(await _store.LoadAsync(SqlServerSchemaInspector.ChinaId)).Should().Be("china");
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
        (await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "projection_states"))
            ["state"].Should().Be("varbinary(-1)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "projection_states"))
            .Should().Equal("PK CLUSTERED (projection_key)");
        Text(await _store.LoadAsync("legacy")).Should().Be("{}");

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
        (await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "projection_states"))
            ["state"].Should().Be("varbinary(-1)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "projection_states"))
            .Should().Equal("PK CLUSTERED (projection_key)");
        Text(await _store.LoadAsync("legacy")).Should().Be("{}");
    }

    /// <summary>
    /// A 1.x table keeps its state as NVARCHAR(MAX). EnsureSchemaAsync converts the column to
    /// VARBINARY(MAX) and stores each state as its UTF-8 bytes, so text written by 1.x, non-ASCII
    /// included, loads as the bytes a text serializer writes. #425.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_ConvertsTextStateToUtf8Bytes()
    {
        var connectionString = _database.GetConnectionString();
        await SqlServerSchemaInspector.ExecuteAsync(connectionString, """
            DROP TABLE dbo.projection_states;
            CREATE TABLE dbo.projection_states (
                projection_key NVARCHAR(256)  NOT NULL PRIMARY KEY,
                state          NVARCHAR(MAX)  NOT NULL,
                updated_at     DATETIME2      NOT NULL
            );
            """);
        // A separate batch: one batch is compiled against the table as it was before the batch ran.
        await SqlServerSchemaInspector.ExecuteAsync(connectionString, """
            INSERT INTO dbo.projection_states VALUES (N'text', N'{"name":"日本 ü €"}', SYSUTCDATETIME());
            INSERT INTO dbo.projection_states VALUES (N'empty', N'', SYSUTCDATETIME());
            """);

        await _store.EnsureSchemaAsync();
        await _store.EnsureSchemaAsync();

        (await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "projection_states"))
            ["state"].Should().Be("varbinary(-1)");
        (await _store.LoadAsync("text"))!.Value.ToArray()
            .Should().Equal(System.Text.Encoding.UTF8.GetBytes("""{"name":"日本 ü €"}"""));
        (await _store.LoadAsync("empty"))!.Value.Length.Should().Be(0);

        await _store.SaveAsync("binary", new byte[] { 0x00, 0xFF });
        (await _store.LoadAsync("binary"))!.Value.ToArray().Should().Equal(new byte[] { 0x00, 0xFF });
    }

    // The table as versions before #384 created it. The INSERT is a separate batch: one batch is
    // compiled against the table as it was before the batch ran.
    private static async Task CreateLegacyTableAsync(string connectionString)
    {
        await SqlServerSchemaInspector.ExecuteAsync(connectionString, """
            DROP TABLE dbo.projection_states;
            CREATE TABLE dbo.projection_states (
                projection_key VARCHAR(256)   NOT NULL PRIMARY KEY,
                state          NVARCHAR(MAX)  NOT NULL,
                updated_at     DATETIME2      NOT NULL
            );
            """);
        await SqlServerSchemaInspector.ExecuteAsync(connectionString,
            "INSERT INTO dbo.projection_states VALUES ('legacy', N'{}', SYSUTCDATETIME());");
    }
}
