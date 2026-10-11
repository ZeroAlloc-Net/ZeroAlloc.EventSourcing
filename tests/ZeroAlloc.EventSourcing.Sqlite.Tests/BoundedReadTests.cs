using Microsoft.Data.Sqlite;
using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.Sqlite.Tests;

/// <summary><see cref="SqliteEventStoreAdapter"/> against the bounded read contract: the query carries a LIMIT, see #473.</summary>
public sealed class BoundedReadTests : IAsyncLifetime
{
    private SqliteConnection? _keepAlive;
    private SqliteEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        // The keep-alive connection holds the shared in-memory database open between commands.
        var connectionString = $"Data Source=file:bounded-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        await _keepAlive.OpenAsync();
        _adapter = new SqliteEventStoreAdapter(connectionString);
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

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
