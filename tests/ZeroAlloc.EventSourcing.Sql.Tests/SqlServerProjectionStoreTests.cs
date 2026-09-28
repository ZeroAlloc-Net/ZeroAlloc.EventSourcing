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
}
