using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public class GlobalStreamTests
{
    [Fact]
    public async Task Global_read_returns_events_from_all_streams_in_append_order()
    {
        var adapter = new InMemoryEventStoreAdapter();
        await adapter.AppendAsync(new StreamId("order-1"), Raw("A"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("customer-1"), Raw("B"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("order-1"), Raw("C"), new StreamPosition(1));

        var events = new List<string>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            events.Add(e.EventType);

        events.Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task Global_read_with_from_returns_only_subsequent_events()
    {
        var adapter = new InMemoryEventStoreAdapter();
        await adapter.AppendAsync(new StreamId("s1"), Raw("A"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s2"), Raw("B"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s3"), Raw("C"), StreamPosition.Start);

        var events = new List<string>();
        // EXCLUSIVE semantics: from=2 returns events with global_position > 2 — only C (pos=3).
        await foreach (var e in adapter.ReadAsync(StreamId.Global, new StreamPosition(2)))
            events.Add(e.EventType);

        events.Should().Equal("C");
    }

    [Fact]
    public async Task Per_stream_read_unaffected_by_global_counter()
    {
        var adapter = new InMemoryEventStoreAdapter();
        await adapter.AppendAsync(new StreamId("s1"), Raw("A1"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s2"), Raw("B1"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s1"), Raw("A2"), new StreamPosition(1));

        var events = new List<string>();
        await foreach (var e in adapter.ReadAsync(new StreamId("s1"), StreamPosition.Start))
            events.Add(e.EventType);

        events.Should().Equal("A1", "A2");
    }

    [Fact]
    public async Task Global_position_starts_at_1_and_increments_monotonically()
    {
        var adapter = new InMemoryEventStoreAdapter();
        await adapter.AppendAsync(new StreamId("s1"), Raw("A"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s2"), Raw("B"), StreamPosition.Start);

        var positions = new List<long>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            positions.Add(e.Position.Value);

        positions.Should().Equal(1, 2);
    }

    [Fact]
    public async Task Concurrent_appends_leave_no_gap_and_paged_global_reads_return_each_event_once()
    {
        const int streams = 8;
        const int perStream = 250;
        // Typed as the interface: ReadBatchAsync is an interface member, which StreamConsumer calls.
        IEventStoreAdapter adapter = new InMemoryEventStoreAdapter();

        // Appends to different streams race each other; every global position must still be
        // visible exactly once, with no gap a paging reader could skip past.
        await Task.WhenAll(Enumerable.Range(0, streams).Select(s => Task.Run(async () =>
        {
            for (var i = 0; i < perStream; i++)
                await adapter.AppendAsync(new StreamId($"s{s}"), Raw($"s{s}-{i}"), new StreamPosition(i));
        })));

        var positions = new List<long>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        var from = StreamPosition.Start;
        while (true)
        {
            var page = 0;
            await foreach (var e in adapter.ReadBatchAsync(StreamId.Global, from, 37))
            {
                positions.Add(e.Position.Value);
                types.Add(e.EventType);
                from = e.Position;
                page++;
            }

            if (page == 0) break;
        }

        positions.Should().Equal(Enumerable.Range(1, streams * perStream).Select(p => (long)p));
        types.Should().HaveCount(streams * perStream);
    }

    [Fact]
    public async Task A_subscriber_that_appends_does_not_deadlock()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await adapter.SubscribeAsync(new StreamId("s1"), StreamPosition.Start, async (e, ct) =>
        {
            if (string.Equals(e.EventType, "A", StringComparison.Ordinal))
            {
                await adapter.AppendAsync(new StreamId("s2"), Raw("B"), StreamPosition.Start, ct);
                appended.SetResult();
            }
        });
        await sub.StartAsync();

        await adapter.AppendAsync(new StreamId("s1"), Raw("A"), StreamPosition.Start);

        await appended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var events = new List<string>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            events.Add(e.EventType);
        events.Should().Equal("A", "B");
    }

    private static ReadOnlyMemory<RawEvent> Raw(string eventType)
        => new[] { new RawEvent(StreamPosition.Start, eventType, new byte[] { 0 }, EventMetadata.New(eventType)) }.AsMemory();
}
