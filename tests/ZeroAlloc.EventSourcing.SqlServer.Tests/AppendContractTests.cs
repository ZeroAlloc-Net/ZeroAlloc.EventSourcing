using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

/// <summary><see cref="SqlServerEventStoreAdapter"/> against the append contract: one reused insert command per append, all or nothing, see #472.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class AppendContractTests(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private SqlServerEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _adapter = new SqlServerEventStoreAdapter(_database.GetConnectionString());
        await _adapter.EnsureSchemaAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public Task A_multi_event_append_stores_every_field_of_every_event()
        => AppendContract.A_multi_event_append_stores_every_field_of_every_event(_adapter);

    [Fact]
    public Task A_failing_insert_stores_none_of_the_append()
        => AppendContract.A_failing_insert_stores_none_of_the_append(_adapter);
}
