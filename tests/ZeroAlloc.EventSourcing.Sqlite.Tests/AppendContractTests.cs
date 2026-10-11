using Microsoft.Data.Sqlite;
using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.Sqlite.Tests;

/// <summary><see cref="SqliteEventStoreAdapter"/> against the append contract: one reused insert command per append, all or nothing, see #472.</summary>
public sealed class AppendContractTests : IAsyncLifetime
{
    private SqliteConnection? _keepAlive;
    private SqliteEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        // The keep-alive connection holds the shared in-memory database open between commands.
        var connectionString = $"Data Source=file:append-{Guid.NewGuid():N}?mode=memory&cache=shared";
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
    public Task A_multi_event_append_stores_every_field_of_every_event()
        => AppendContract.A_multi_event_append_stores_every_field_of_every_event(_adapter);

    [Fact]
    public Task A_failing_insert_stores_none_of_the_append()
        => AppendContract.A_failing_insert_stores_none_of_the_append(_adapter);
}
