using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

/// <summary>
/// Tests for <see cref="SnapshotCachingRepositoryDecorator{TAggregate,TId,TState}"/>.
/// Verifies that the decorator correctly loads aggregates using snapshots,
/// restores state, and replays events according to the configured strategy.
/// </summary>
public sealed class SnapshotCachingRepositoryDecoratorTests
{
    private static (IEventStore eventStore, InMemorySnapshotStore<OrderState> snapshotStore, SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState> decorator) BuildRepo()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = new JsonAggregateSerializer();
        var registry = new OrderEventTypeRegistry();
        var eventStore = new EventStore(adapter, serializer, registry);
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var streamIdFactory = new Func<OrderId, StreamId>(id => new StreamId($"order-{id.Value}"));
        var aggregateFactory = new Func<Order>(() => new Order());
        var innerRepo = new AggregateRepository<Order, OrderId>(eventStore, aggregateFactory, streamIdFactory);
        var decorator = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepo,
            snapshotStore,
            SnapshotLoadingStrategy.TrustSnapshot,
            RestoreOrderState,
            eventStore,
            streamIdFactory,
            aggregateFactory);
        return (eventStore, snapshotStore, decorator);
    }

    private static void RestoreOrderState(Order aggregate, OrderState state, StreamPosition fromPosition)
    {
        // Restore the state directly onto the aggregate
        // This simulates the state restoration that would happen before replaying newer events
        aggregate.ApplyHistoric(new OrderPlacedEvent(state.OrderId!, state.Total), new StreamPosition(1));
        if (state.IsShipped)
        {
            aggregate.ApplyHistoric(new OrderShippedEvent(state.TrackingNumber!), new StreamPosition(2));
        }
    }

    [Fact]
    public async Task Decorator_LoadWithoutSnapshot_DelegatesToInnerRepository()
    {
        // Arrange
        var (eventStore, _, decorator) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());
        var streamId = new StreamId($"order-{id.Value}");

        // Create and save an order directly via the event store
        var order = new Order();
        order.SetId(id);
        order.Place("ORD-1", 99.99m);

        var uncommitted = order.DequeueUncommitted();
        var events = new object[uncommitted.Length];
        for (var i = 0; i < uncommitted.Length; i++)
            events[i] = uncommitted[i];
        await eventStore.AppendAsync(streamId, events.AsMemory(), StreamPosition.Start);

        // No snapshot exists
        // Act
        var result = await decorator.LoadAsync(id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.State.IsPlaced.Should().BeTrue();
        result.Value.State.Total.Should().Be(99.99m);
        result.Value.Version.Value.Should().Be(1);
    }

    [Fact]
    public async Task Decorator_LoadWithSnapshot_SkipsOldEvents()
    {
        // Arrange
        var (eventStore, snapshotStore, decorator) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());
        var streamId = new StreamId($"order-{id.Value}");

        // Create an order with multiple events
        var order = new Order();
        order.SetId(id);
        order.Place("ORD-1", 100m);
        order.Ship("TRACK-123");

        // Save both events to event store
        var uncommitted = order.DequeueUncommitted();
        var events = new object[uncommitted.Length];
        for (var i = 0; i < uncommitted.Length; i++)
            events[i] = uncommitted[i];
        await eventStore.AppendAsync(streamId, events.AsMemory(), StreamPosition.Start);

        // Save snapshot after first event (at position 1)
        var snapshotState = OrderState.Initial with
        {
            OrderId = "ORD-1",
            TrackingNumber = null
        };
        // Manually apply the event to get the correct state
        snapshotState = snapshotState.Apply(new OrderPlacedEvent("ORD-1", 100m));
        await snapshotStore.WriteAsync(streamId, new StreamPosition(1), snapshotState);

        // Act: Load with TrustSnapshot strategy — should restore from snapshot and replay from position 2
        var result = await decorator.LoadAsync(id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.State.IsPlaced.Should().BeTrue();
        result.Value.State.IsShipped.Should().BeTrue(); // Replayed after snapshot
        result.Value.State.Total.Should().Be(100m);
        result.Value.Version.Value.Should().Be(2); // At the latest position
    }

    [Fact]
    public async Task Decorator_IgnoreSnapshot_ReplaysFull()
    {
        // Arrange
        var (eventStore, snapshotStore, _) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());
        var streamId = new StreamId($"order-{id.Value}");

        // Create and save an order with events
        var order = new Order();
        order.SetId(id);
        order.Place("ORD-1", 50m);
        order.Ship("TRACK-456");

        var uncommitted = order.DequeueUncommitted();
        var events = new object[uncommitted.Length];
        for (var i = 0; i < uncommitted.Length; i++)
            events[i] = uncommitted[i];
        await eventStore.AppendAsync(streamId, events.AsMemory(), StreamPosition.Start);

        // Save a snapshot at position 1
        var snapshotState = OrderState.Initial with
        {
            OrderId = "ORD-1",
            TrackingNumber = null
        };
        // Manually apply the event to get the correct state
        snapshotState = snapshotState.Apply(new OrderPlacedEvent("ORD-1", 50m));
        await snapshotStore.WriteAsync(streamId, new StreamPosition(1), snapshotState);

        // Create decorator with IgnoreSnapshot strategy
        var streamIdFactory = new Func<OrderId, StreamId>(id => new StreamId($"order-{id.Value}"));
        var aggregateFactory = new Func<Order>(() => new Order());
        var innerRepo = new AggregateRepository<Order, OrderId>(eventStore, aggregateFactory, streamIdFactory);
        var decorator = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepo,
            snapshotStore,
            SnapshotLoadingStrategy.IgnoreSnapshot,
            RestoreOrderState,
            eventStore,
            streamIdFactory,
            aggregateFactory);

        // Act: Load with IgnoreSnapshot strategy — should replay all events from start
        var result = await decorator.LoadAsync(id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.State.IsPlaced.Should().BeTrue();
        result.Value.State.IsShipped.Should().BeTrue();
        result.Value.State.Total.Should().Be(50m);
        result.Value.Version.Value.Should().Be(2);
    }

    [Fact]
    public async Task Decorator_ValidateAndReplay_UsesSnapshotWhenPositionValid()
    {
        // Arrange
        var (eventStore, snapshotStore, _) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());
        var streamId = new StreamId($"order-{id.Value}");

        // Create and save an order with multiple events
        var order = new Order();
        order.SetId(id);
        order.Place("ORD-1", 100m);
        order.Ship("TRACK-456");

        var uncommitted = order.DequeueUncommitted();
        var events = new object[uncommitted.Length];
        for (var i = 0; i < uncommitted.Length; i++)
            events[i] = uncommitted[i];
        await eventStore.AppendAsync(streamId, events.AsMemory(), StreamPosition.Start);

        // Take snapshot at position 2 (after both events)
        var snapshotState = OrderState.Initial with
        {
            OrderId = "ORD-1",
            TrackingNumber = null
        };
        // Apply both events to get the correct state
        snapshotState = snapshotState.Apply(new OrderPlacedEvent("ORD-1", 100m));
        snapshotState = snapshotState.Apply(new OrderShippedEvent("TRACK-456"));
        await snapshotStore.WriteAsync(streamId, new StreamPosition(2), snapshotState);

        // Create decorator with ValidateAndReplay strategy
        var streamIdFactory = new Func<OrderId, StreamId>(id => new StreamId($"order-{id.Value}"));
        var aggregateFactory = new Func<Order>(() => new Order());
        var innerRepo = new AggregateRepository<Order, OrderId>(eventStore, aggregateFactory, streamIdFactory);
        var decorator = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepo,
            snapshotStore,
            SnapshotLoadingStrategy.ValidateAndReplay,
            RestoreOrderState,
            eventStore,
            streamIdFactory,
            aggregateFactory);

        // Act
        var result = await decorator.LoadAsync(id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.State.IsPlaced.Should().BeTrue();
        result.Value.State.IsShipped.Should().BeTrue();
        result.Value.State.Total.Should().Be(100m);
        result.Value.Version.Value.Should().Be(2);
    }

    [Fact]
    public async Task Decorator_SaveAsync_DelegatesToInnerRepository()
    {
        // Arrange
        var (_, _, decorator) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        var order = new Order();
        order.SetId(id);
        order.Place("ORD-1", 75m);

        // Act
        var result = await decorator.SaveAsync(order, id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        order.DequeueUncommitted().Length.Should().Be(0); // Cleared after save
    }
}

