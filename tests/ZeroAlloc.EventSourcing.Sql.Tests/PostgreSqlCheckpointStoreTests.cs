using FluentAssertions;
using Testcontainers.PostgreSql;
using Xunit;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Tests;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public class PostgreSqlCheckpointStoreTests : CheckpointStoreContractTests, IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlCheckpointStore _store = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder().Build();
        await _container.StartAsync();

        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        _store = new PostgreSqlCheckpointStore(_dataSource);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.StopAsync();
    }

    protected override ICheckpointStore CreateStore() => _store;
}
