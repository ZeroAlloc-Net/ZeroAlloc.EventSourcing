using FluentAssertions;
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

    private static ReadOnlyMemory<RawEvent> Raw(string eventType)
        => new[] { new RawEvent(StreamPosition.Start, eventType, new byte[] { 0 }, EventMetadata.New(eventType)) }.AsMemory();
}
