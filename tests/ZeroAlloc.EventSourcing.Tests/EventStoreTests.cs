using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

// Test event
internal record OrderPlaced(string OrderId, decimal Total);

// Minimal STJ serializer for tests
internal sealed class JsonEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}

// Minimal type registry for tests
internal sealed class TestTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<string, Type> _map = new()
    {
        [nameof(OrderPlaced)] = typeof(OrderPlaced),
    };

    public bool TryGetType(string eventType, out Type? type) => _map.TryGetValue(eventType, out type);
    public string GetTypeName(Type type) => type.Name;
}

public class EventStoreTests
{
    private static IEventStore BuildStore()
        => new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new TestTypeRegistry());

    [Fact]
    public async Task Append_ThenRead_RoundTripsEvent()
    {
        var store = BuildStore();
        var id = new StreamId("orders-1");
        var @event = new OrderPlaced("ORD-001", 99.99m);

        var appendResult = await store.AppendAsync(id, new object[] { @event }.AsMemory(), StreamPosition.Start);
        appendResult.IsSuccess.Should().BeTrue();

        var events = new List<EventEnvelope>();
        await foreach (var e in store.ReadAsync(id))
            events.Add(e);

        events.Should().HaveCount(1);
        var read = events[0].Event.Should().BeOfType<OrderPlaced>().Subject;
        read.OrderId.Should().Be("ORD-001");
        read.Total.Should().Be(99.99m);
    }

    [Fact]
    public async Task Append_SetsEventMetadata()
    {
        var store = BuildStore();
        var id = new StreamId("orders-1");

        await store.AppendAsync(id, new object[] { new OrderPlaced("X", 1m) }.AsMemory(), StreamPosition.Start);

        var events = new List<EventEnvelope>();
        await foreach (var e in store.ReadAsync(id))
            events.Add(e);

        events[0].Metadata.EventId.Should().NotBe(Guid.Empty);
        events[0].Metadata.EventType.Should().Be("OrderPlaced");
        events[0].Metadata.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Append_WithWrongExpectedVersion_ReturnsConflict()
    {
        var store = BuildStore();
        var id = new StreamId("orders-1");
        var @event = new OrderPlaced("ORD-001", 1m);

        await store.AppendAsync(id, new object[] { @event }.AsMemory(), StreamPosition.Start);
        var result = await store.AppendAsync(id, new object[] { @event }.AsMemory(), StreamPosition.Start);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Read_UnknownEventType_IsSkipped()
    {
        // Adapter has an event of type "UnknownEvent" not in the registry
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var raw = new RawEvent(StreamPosition.Start, "UnknownEvent", new byte[] { 1 }, EventMetadata.New("UnknownEvent"));
        await adapter.AppendAsync(id, new[] { raw }.AsMemory(), StreamPosition.Start);

        var store = new EventStore(adapter, new JsonEventSerializer(), new TestTypeRegistry());

        var events = new List<EventEnvelope>();
        await foreach (var e in store.ReadAsync(id))
            events.Add(e);

        events.Should().BeEmpty(); // unknown type silently skipped
    }
}
