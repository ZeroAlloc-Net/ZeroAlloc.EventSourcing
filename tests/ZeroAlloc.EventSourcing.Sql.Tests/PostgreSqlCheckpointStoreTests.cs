using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Testing;
using Xunit;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Tests;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection(PostgreSqlCollection.Name)]
public class PostgreSqlCheckpointStoreTests(PostgreSqlContainerFixture fixture) : CheckpointStoreContractTests, IAsyncLifetime
{
    private TestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlCheckpointStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();

        _dataSource = NpgsqlDataSource.Create(_database.GetConnectionString());
        _store = new PostgreSqlCheckpointStore(_dataSource);
        await _store.EnsureSchemaAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _database.DisposeAsync();
    }

    protected override ICheckpointStore CreateStore() => _store;
}
