using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public class InMemoryEventStoreAdapterTests
{
    private static RawEvent MakeRaw(StreamPosition pos, string type = "TestEvent")
        => new(pos, type, new byte[] { 1, 2, 3 }, EventMetadata.New(type));

    [Fact]
    public async Task Append_ToNewStream_Succeeds()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var events = new[] { MakeRaw(StreamPosition.Start) }.AsMemory();

        var result = await adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task Append_WithWrongExpectedVersion_ReturnsConflict()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var events = new[] { MakeRaw(StreamPosition.Start) }.AsMemory();

        await adapter.AppendAsync(id, events, StreamPosition.Start);

        // Append again with wrong expected version (still 0 instead of 1)
        var result = await adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Read_AfterAppend_ReturnsAllEvents()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var raw1 = MakeRaw(StreamPosition.Start, "OrderPlaced");
        var raw2 = MakeRaw(new StreamPosition(1), "OrderShipped");
        var events = new[] { raw1, raw2 }.AsMemory();

        await adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderPlaced");
        read[1].EventType.Should().Be("OrderShipped");
    }

    [Fact]
    public async Task Read_FromPosition_ReturnsEventsFromThatPositionOnward()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var events = new[]
        {
            MakeRaw(StreamPosition.Start, "OrderPlaced"),
            MakeRaw(new StreamPosition(1), "OrderShipped"),
            MakeRaw(new StreamPosition(2), "OrderDelivered"),
        }.AsMemory();

        await adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, new StreamPosition(1)))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderShipped");
    }

    [Fact]
    public async Task Read_FromNonExistentStream_ReturnsEmpty()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("does-not-exist");

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().BeEmpty();
    }
}
