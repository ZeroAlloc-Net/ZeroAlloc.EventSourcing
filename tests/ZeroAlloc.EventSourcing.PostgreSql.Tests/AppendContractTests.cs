using Npgsql;
using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.PostgreSql.Tests;

/// <summary><see cref="PostgreSqlEventStoreAdapter"/> against the append contract: one reused insert command per append, all or nothing, see #472.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class AppendContractTests(PostgreSqlContainerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _dataSource = NpgsqlDataSource.Create(_database.GetConnectionString());
        _adapter = new PostgreSqlEventStoreAdapter(_dataSource);
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public Task A_multi_event_append_stores_every_field_of_every_event()
        => AppendContract.A_multi_event_append_stores_every_field_of_every_event(_adapter);

    [Fact]
    public Task A_failing_insert_stores_none_of_the_append()
        => AppendContract.A_failing_insert_stores_none_of_the_append(_adapter);
}
