using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Outbox;
using ZeroAlloc.EventSourcing.Sqlite;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

/// <summary>
/// Mirrors <see cref="CrossAggregateIntegrationTests"/> but against a SQLite-backed
/// event store, proving the Outbox + SQLite pairing works end-to-end. The SQLite
/// adapter ships with <c>global_position</c> from day one (no cursor-conflation bug
/// ever existed), so no placeholder pre-seed is ever needed — this test reads as a
/// clean copy-paste recipe for adopters of the new ZA.EventSourcing.Sqlite package.
/// </summary>
public class SqliteCrossAggregateIntegrationTests : IAsyncLifetime
{
    private string _connectionString = "";
    private SqliteConnection? _keepAlive;

    public async Task InitializeAsync()
    {
        // Use a unique in-memory database name per test instance + a held-open "keep alive"
        // connection so the database is destroyed when the test disposes (not when the
        // adapter's transient connections close).
        _connectionString = $"Data Source=file:outbox-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task Handler_triggered_by_outbox_saves_new_event_that_outbox_picks_up_on_next_poll()
    {
        var adapter = new SqliteEventStoreAdapter(_connectionString);
        await adapter.EnsureSchemaAsync();

        var store = TestHarness.NewEventStoreWithAdapter(adapter);
        var checkpoints = new InMemoryCheckpointStore();
        var recorder = new RecordingDispatcher();

        // The handler-as-OnDispatch: when the dispatcher delivers a TestEventA, append a
        // TestEventB to a SEPARATE stream (credit-1). The SQLite adapter's global_position
        // column gives the outbox a true monotonic cursor, so the new TestEventB surfaces
        // on the next poll without any pre-seed gymnastics.
        recorder.OnDispatch = async ev =>
        {
            if (ev is TestEventA a)
            {
                await store.AppendAsync(
                    new StreamId("credit-1"),
                    new object[] { new TestEventB($"credited-{a.Value}") }.AsMemory(),
                    StreamPosition.Start).ConfigureAwait(false);
            }
        };

        // Seed: append the initial event on the order stream.
        await store.AppendAsync(
            new StreamId("order-1"),
            new object[] { new TestEventA(50) }.AsMemory(),
            StreamPosition.Start);

        var sut = new OutboxDispatcher(
            store, checkpoints, recorder, deadLetters: null,
            new OutboxOptions { ConsumerId = "sqlite-test-cross", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);

        await sut.StartAsync(default);
        await TestHarness.WaitUntil(
            () => recorder.Dispatched.OfType<TestEventB>().Any(),
            TimeSpan.FromSeconds(3));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().Contain(e => e is TestEventA);
        recorder.Dispatched.Should().Contain(e => e is TestEventB);
    }
}
