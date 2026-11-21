using AwesomeAssertions;
using Npgsql;
using ZeroAlloc.EventSourcing.Testing;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlProjectionStoreTests(PostgreSqlContainerFixture fixture) : ProjectionStoreContractTests, IAsyncLifetime
{
    private TestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlProjectionStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _dataSource = NpgsqlDataSource.Create(_database.GetConnectionString());
        _store = new PostgreSqlProjectionStore(_dataSource);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _database.DisposeAsync();
    }

    protected override IProjectionStore CreateStore() => _store;

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        await _store.EnsureSchemaAsync(); // second call must not throw
    }

    /// <summary>
    /// A 1.x table keeps its state as TEXT. EnsureSchemaAsync converts the column to BYTEA and stores
    /// each state as its UTF-8 bytes, so text written by 1.x, non-ASCII included, loads as the bytes a
    /// text serializer writes. #425.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_ConvertsTextStateToUtf8Bytes()
    {
        await ExecuteAsync("""
            DROP TABLE projection_states;
            CREATE TABLE projection_states (
                projection_key VARCHAR(256) PRIMARY KEY,
                state TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );
            INSERT INTO projection_states VALUES ('text', '{"name":"日本 ü €"}', NOW());
            """);

        await _store.EnsureSchemaAsync();
        await _store.EnsureSchemaAsync();

        (await ScalarAsync("""
            SELECT data_type FROM information_schema.columns
            WHERE table_name = 'projection_states' AND column_name = 'state'
            """)).Should().Be("bytea");
        (await _store.LoadAsync("text"))!.Value.ToArray()
            .Should().Equal(System.Text.Encoding.UTF8.GetBytes("""{"name":"日本 ü €"}"""));
    }

    /// <summary>App instances that start together convert a 1.x table once between them.</summary>
    [Fact]
    public async Task EnsureSchemaAsync_ConcurrentCallsConvertTextTableOnce()
    {
        await ExecuteAsync("""
            DROP TABLE projection_states;
            CREATE TABLE projection_states (
                projection_key VARCHAR(256) PRIMARY KEY,
                state TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );
            INSERT INTO projection_states VALUES ('text', '{}', NOW());
            """);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new PostgreSqlProjectionStore(_dataSource).EnsureSchemaAsync().AsTask()));

        Text(await _store.LoadAsync("text")).Should().Be("{}");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
