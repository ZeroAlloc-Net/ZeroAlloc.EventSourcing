using AwesomeAssertions;
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

// --- initial state passed to the constructor, issue #413 ---

public sealed record CustomerTotals(string CustomerId, decimal Total, int OrderCount)
{
    public static CustomerTotals Empty { get; } = new(string.Empty, 0m, 0);
}

/// <summary>A record read model whose first event updates the starting value with <c>with</c>.</summary>
public sealed class CustomerTotalsProjection : ReplayableProjection<CustomerTotals>
{
    public CustomerTotalsProjection()
        : base(CustomerTotals.Empty)
    {
    }

    public override string GetProjectionKey() => "customer-totals";

    protected override CustomerTotals Apply(CustomerTotals current, EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current with { Total = current.Total + e.Amount, OrderCount = current.OrderCount + 1 },
        _ => current
    };

    public void SetCurrent(CustomerTotals value) => Current = value;
}

/// <summary>A collection read model: the first event calls a method on the starting value.</summary>
public sealed class OrderAmountsProjection : ReplayableProjection<System.Collections.Immutable.ImmutableDictionary<string, decimal>>
{
    public OrderAmountsProjection()
        : base(System.Collections.Immutable.ImmutableDictionary<string, decimal>.Empty)
    {
    }

    public override string GetProjectionKey() => "order-amounts";

    protected override System.Collections.Immutable.ImmutableDictionary<string, decimal> Apply(
        System.Collections.Immutable.ImmutableDictionary<string, decimal> current, EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current.SetItem(e.OrderId, e.Amount),
        OrderCancelledEvent e => current.Remove(e.OrderId),
        _ => current
    };

    public void SetCurrent(System.Collections.Immutable.ImmutableDictionary<string, decimal> value) => Current = value;
}

/// <summary>A value-type read model that uses the parameterless constructor.</summary>
public sealed class OrderCountProjection : ReplayableProjection<int>
{
    public override string GetProjectionKey() => "order-count";

    protected override int Apply(int current, EventEnvelope @event)
        => @event.Event is OrderPlacedEvent ? current + 1 : current;

    public void SetCurrent(int value) => Current = value;
}

/// <summary>
/// A projection on the parameterless constructor that sets <c>Current</c> itself, the pattern that predates
/// the initial-state constructor. A rebuild resets it to <c>default</c>, as before.
/// </summary>
public sealed class LegacyStartingValueProjection : ReplayableProjection<int>
{
    public LegacyStartingValueProjection() => Current = 50;

    public override string GetProjectionKey() => "legacy";

    protected override int Apply(int current, EventEnvelope @event) => current + 1;
}

public class ReplayableProjectionInitialStateTests
{
    private static async Task<IEventStore> StoreWith(StreamId streamId, params object[] events)
    {
        var eventStore = new EventStore(new InMemoryEventStoreAdapter(), new TestEventSerializer(), new TestEventTypeRegistry());
        await eventStore.AppendAsync(streamId, events.AsMemory(), StreamPosition.Start);
        return eventStore;
    }

    [Fact]
    public void Constructor_StartsAtTheInitialState()
    {
        new CustomerTotalsProjection().Current.Should().BeSameAs(CustomerTotals.Empty);
        new OrderAmountsProjection().Current.Should().BeEmpty();
        new OrderCountProjection().Current.Should().Be(0);
        new LegacyStartingValueProjection().Current.Should().Be(50);
    }

    [Fact]
    public async Task Rebuild_RecordReadModel_StartsFromTheInitialState()
    {
        var streamId = new StreamId("customer-1");
        var eventStore = await StoreWith(streamId, new OrderPlacedEvent("A", 10m), new OrderPlacedEvent("B", 5m));
        var projectionStore = new InMemoryProjectionStore();
        var projection = new CustomerTotalsProjection();
        projection.SetCurrent(new CustomerTotals("stale", 999m, 42));

        await projection.RebuildAsync(projectionStore, streamId, eventStore);

        projection.Current.Should().Be(new CustomerTotals(string.Empty, 15m, 2));
        var saved = await projectionStore.LoadAsync("customer-totals");
        System.Text.Json.JsonSerializer.Deserialize<CustomerTotals>(saved!).Should().Be(projection.Current);
    }

    [Fact]
    public async Task Rebuild_CollectionReadModel_StartsFromTheInitialState()
    {
        var streamId = new StreamId("orders");
        var eventStore = await StoreWith(streamId,
            new OrderPlacedEvent("A", 10m), new OrderPlacedEvent("B", 5m), new OrderCancelledEvent("A"));
        var projectionStore = new InMemoryProjectionStore();
        var projection = new OrderAmountsProjection();
        projection.SetCurrent(projection.Current.SetItem("stale", 1m));

        await projection.RebuildAsync(projectionStore, streamId, eventStore);

        projection.Current.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["B"] = 5m });
        var saved = await projectionStore.LoadAsync("order-amounts");
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(saved!)
            .Should().BeEquivalentTo(new Dictionary<string, decimal> { ["B"] = 5m });
    }

    [Fact]
    public async Task Rebuild_ParameterlessConstructor_StillResetsToDefault()
    {
        var streamId = new StreamId("orders");
        var eventStore = await StoreWith(streamId, new OrderPlacedEvent("A", 10m), new OrderPlacedEvent("B", 5m));
        var projectionStore = new InMemoryProjectionStore();
        var projection = new OrderCountProjection();
        projection.SetCurrent(100);

        await projection.RebuildAsync(projectionStore, streamId, eventStore);

        projection.Current.Should().Be(2);
        (await projectionStore.LoadAsync("order-count")).Should().Be("2");
    }

    [Fact]
    public async Task Rebuild_ParameterlessConstructorThatSetsCurrent_ResetsToDefaultAsBefore()
    {
        var streamId = new StreamId("orders");
        var eventStore = await StoreWith(streamId, new OrderPlacedEvent("A", 10m));
        var projection = new LegacyStartingValueProjection();

        await projection.RebuildAsync(new InMemoryProjectionStore(), streamId, eventStore);

        projection.Current.Should().Be(1);
    }

    [Fact]
    public async Task Rebuild_Twice_StartsFromTheInitialStateEachTime()
    {
        var streamId = new StreamId("customer-1");
        var eventStore = await StoreWith(streamId, new OrderPlacedEvent("A", 10m));
        var projection = new CustomerTotalsProjection();

        await projection.RebuildAsync(new InMemoryProjectionStore(), streamId, eventStore);
        await projection.RebuildAsync(new InMemoryProjectionStore(), streamId, eventStore);

        projection.Current.Should().Be(new CustomerTotals(string.Empty, 10m, 1));
    }
}
