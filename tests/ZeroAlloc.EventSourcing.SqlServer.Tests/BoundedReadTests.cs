using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

/// <summary><see cref="SqlServerEventStoreAdapter"/> against the bounded read contract: the query carries a TOP, see #473.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class BoundedReadTests(SqlServerContainerFixture fixture) : IAsyncLifetime
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
    public Task Stream_reads_return_at_most_maxCount_events()
        => BoundedReadContract.Stream_reads_return_at_most_maxCount_events_page_by_page(_adapter);

    [Fact]
    public Task Global_reads_return_at_most_maxCount_events()
        => BoundedReadContract.Global_reads_return_at_most_maxCount_events_page_by_page(_adapter);

    [Fact]
    public void Rejects_a_maxCount_below_one()
        => BoundedReadContract.Rejects_a_maxCount_below_one(_adapter);
}
