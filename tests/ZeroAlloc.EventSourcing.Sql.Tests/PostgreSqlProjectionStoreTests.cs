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
}
