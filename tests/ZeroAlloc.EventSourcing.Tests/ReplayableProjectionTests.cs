using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

// --- test serializer and registry ---

internal sealed class TestEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}

internal sealed class TestEventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<string, Type> _map;

    public TestEventTypeRegistry()
    {
        _map = new()
        {
            [nameof(OrderPlacedEvent)] = typeof(OrderPlacedEvent),
            [nameof(OrderShippedEvent)] = typeof(OrderShippedEvent),
            [nameof(OrderCancelledEvent)] = typeof(OrderCancelledEvent),
        };
    }

    public bool TryGetType(string eventType, out Type? type) => _map.TryGetValue(eventType, out type);
    public string GetTypeName(Type type) => type.Name;
}

// --- test domain model ---

public record OrderEvent(string OrderId);
public record OrderPlacedEvent(string OrderId, decimal Amount) : OrderEvent(OrderId);
public record OrderShippedEvent(string OrderId, string TrackingCode) : OrderEvent(OrderId);
public record OrderCancelledEvent(string OrderId) : OrderEvent(OrderId);

public record OrderReadModel(string OrderId, decimal Amount, string? TrackingCode, bool IsCancelled);

/// <summary>
/// Replayable projection for testing. Demonstrates how to implement <see cref="ReplayableProjection{TReadModel}"/>
/// with rebuild capability.
/// </summary>
public sealed class ReplayableOrderProjection : ReplayableProjection<OrderReadModel>
{
    private readonly StreamId _streamId;

    public ReplayableOrderProjection(StreamId streamId)
    {
        _streamId = streamId;
        Current = new OrderReadModel(string.Empty, 0m, null, false);
    }

    public override string GetProjectionKey() => $"ReplayableOrderProjection-{_streamId.Value}";

    protected override OrderReadModel Apply(OrderReadModel current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e => new OrderReadModel(e.OrderId, e.Amount, null, false),
            OrderShippedEvent e => current with { TrackingCode = e.TrackingCode },
            OrderCancelledEvent => current with { IsCancelled = true },
            _ => current
        };
    }
}

// --- tests ---

/// <summary>
/// Unit tests for <see cref="ReplayableProjection{TReadModel}"/>. Verifies that projections
/// can be rebuilt by replaying events from the event store.
/// </summary>
public class ReplayableProjectionTests
{
    private static EventEnvelope MakeEnvelope(StreamId streamId, StreamPosition position, object @event)
    {
        return new EventEnvelope(
            StreamId: streamId,
            Position: position,
            Event: @event,
            Metadata: EventMetadata.New("TestEvent"));
    }

    [Fact]
    public async Task Rebuild_ProcessesAllEventsAndSavesState()
    {
        // Arrange
        var streamId = new StreamId("order-123");
        var store = new InMemoryProjectionStore();
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var projection = new ReplayableOrderProjection(streamId);

        // Append some events
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlacedEvent("ORD-001", 100m) }.AsMemory(),
            StreamPosition.Start);
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderShippedEvent("ORD-001", "TRACK-123") }.AsMemory(),
            new StreamPosition(1));

        // Act
        await projection.RebuildAsync(store, streamId, eventStore);

        // Assert
        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.Amount.Should().Be(100m);
        projection.Current.TrackingCode.Should().Be("TRACK-123");
        projection.Current.IsCancelled.Should().BeFalse();

        // Verify state was saved to store
        var saved = await store.LoadAsync(projection.GetProjectionKey());
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task Rebuild_ClearsOldStateBeforeReplaying()
    {
        // Arrange
        var streamId = new StreamId("order-456");
        var store = new InMemoryProjectionStore();
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var projection = new ReplayableOrderProjection(streamId);

        // Append events with different content
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlacedEvent("NEW-ORD-002", 50m) }.AsMemory(),
            StreamPosition.Start);

        // Act
        await projection.RebuildAsync(store, streamId, eventStore);

        // Assert - old state should be completely replaced
        projection.Current.OrderId.Should().Be("NEW-ORD-002");
        projection.Current.Amount.Should().Be(50m);
        projection.Current.TrackingCode.Should().BeNull();
        projection.Current.IsCancelled.Should().BeFalse();
    }
}
