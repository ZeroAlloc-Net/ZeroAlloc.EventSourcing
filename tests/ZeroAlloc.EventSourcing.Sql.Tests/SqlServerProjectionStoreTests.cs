using FluentAssertions;
using Testcontainers.MsSql;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection("SqlServer")]
public sealed class SqlServerProjectionStoreTests : ProjectionStoreContractTests, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private SqlServerProjectionStore _store = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _store = new SqlServerProjectionStore(_container.GetConnectionString());
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
    }

    protected override IProjectionStore CreateStore() => _store;

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        await _store.EnsureSchemaAsync(); // second call must not throw
    }
}
