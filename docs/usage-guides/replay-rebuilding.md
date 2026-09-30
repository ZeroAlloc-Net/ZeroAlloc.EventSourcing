# Usage Guide: Replay and Rebuilding

## Scenario: How Do I Load Aggregates and Handle Large Event Streams?

Event replay is fundamental to event sourcing. This guide covers loading aggregates from history, handling large event streams, and strategies for performance optimization.

## Loading from History: The Complete Cycle

Loading an aggregate goes through a repository. `AggregateRepository` performs the three steps for you: it creates an empty aggregate with your factory, reads every event in the stream, and applies each one to rebuild state. See [building-aggregates.md](./building-aggregates.md) for the repository interface definition and aggregate base class documentation:

```csharp
var repository = new AggregateRepository<Order, OrderId>(
    eventStore,
    () => new Order(),                         // Step 1: create an empty aggregate
    id => new StreamId($"order-{id.Value}"));  // which stream to read

// Steps 2 and 3: read all events from the stream and apply each one
var loaded = await repository.LoadAsync(orderId);
using var order = loaded.Value;

// Now order.State is fully reconstructed
Console.WriteLine($"Order status: {order.State.IsPlaced}");
Console.WriteLine($"Final version: {order.Version.Value}");  // position of the last event
```

A stream with no events loads as a fresh aggregate at `StreamPosition.Start`; `LoadAsync` does not fail for it.

### Event Envelope

Events are returned wrapped in `EventEnvelope`:

```csharp
public readonly record struct EventEnvelope(
    StreamId StreamId,        // Which stream
    StreamPosition Position,  // Position in stream (1-based)
    object Event,             // The event payload
    EventMetadata Metadata);  // Event id, type, timestamps, correlation
```

The `Position` is critical—it identifies where the event sits in the stream's ordering and is needed for optimistic concurrency control.

### Replayed Events vs. Raised Events

An event reaches the aggregate's state in one of two ways, and both end in the same `ApplyEvent` dispatch:

- **Replayed by the repository.** `LoadAsync` applies each stored event and sets both `Version` and `OriginalVersion` to its position. Nothing is queued for saving. The member the repository uses for this is internal: application code loads through a repository, not by replaying events itself.
- **Raised by a command.** `Raise(...)` inside a command method applies the event, increments `Version` and queues the event for the next save. `OriginalVersion` stays at the loaded version.

`OriginalVersion` is what the repository passes as the expected version on `SaveAsync`, which is how concurrent modifications are detected at save time.

## Event Replay Process and Guarantees

### Order Guarantee: Events in Stream Order

Events in a single stream are guaranteed to be in order:

```csharp
// Events in stream are ALWAYS in this order
Stream "order-123":
  Position 1: OrderPlacedEvent(...)
  Position 2: OrderConfirmedEvent(...)
  Position 3: OrderShippedEvent(...)
  Position 4: OrderDeliveredEvent(...)

// Replaying always produces consistent state
// because events are applied in the same order every time
```

This means replaying events is **idempotent**—replaying the same events multiple times always produces the same state.

### Starting Position

`ReadAsync` takes the position to read after. Reads exclude their start position, so reading from position `n` yields the events at `n + 1` onwards:

```csharp
// From the beginning: every event, positions 1, 2, 3, ...
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    Console.WriteLine($"{envelope.Position.Value}: {envelope.Event.GetType().Name}");
}

// Or after a specific position: events 501 onwards
var after = new StreamPosition(500);
await foreach (var envelope in eventStore.ReadAsync(streamId, after))
{
    Console.WriteLine($"{envelope.Position.Value}: {envelope.Event.GetType().Name}");
}
```

To rebuild an aggregate without replaying from the start, restore it from a snapshot: `SnapshotCachingRepositoryDecorator` restores the snapshot and replays only the events after it. See [Snapshot Optimization](#snapshot-optimization).

## Performance Considerations: Event Count Impact

Replay time scales linearly with event count. For large streams, this becomes expensive:

| Event Count | Replay Time | Status |
|---|---|---|
| 10 events | ~1ms | Fast |
| 100 events | ~10ms | Good |
| 1,000 events | ~100ms | Acceptable |
| 10,000 events | ~1s | Getting slow |
| 100,000 events | ~10s | Problematic |

**Rule of thumb:** If aggregates regularly exceed 500 events, use snapshots.

### Why Replay Time Increases

Each event requires:
1. Deserialization (JSON → object)
2. Event dispatch (pattern match to correct Apply method)
3. State update (struct copy with new values)

For 10,000 events, that's 10,000 deserializations, 10,000 dispatches, 10,000 state updates.

## Rebuilding from Scratch vs. Snapshots

### Rebuilding Entire Streams

When you change aggregate behavior, rebuild all aggregates by replaying events:

```csharp
public class AggregateRebuilder<TAggregate, TId, TState>
    where TAggregate : Aggregate<TId, TState>
    where TId : struct
    where TState : struct, IAggregateState<TState>
{
    private readonly IAggregateRepository<TAggregate, TId> _repository;
    private readonly ISnapshotStore<TState> _snapshotStore;
    private readonly Func<TId, StreamId> _streamIdFactory;

    public AggregateRebuilder(
        IAggregateRepository<TAggregate, TId> repository,  // a plain AggregateRepository: full replay
        ISnapshotStore<TState> snapshotStore,
        Func<TId, StreamId> streamIdFactory)
    {
        _repository = repository;
        _snapshotStore = snapshotStore;
        _streamIdFactory = streamIdFactory;
    }

    public async Task RebuildAll(IEnumerable<TId> aggregateIds, CancellationToken ct = default)
    {
        // IEventStore reads one stream at a time, so the ids come from your own
        // index or read model of existing aggregates.
        foreach (var aggregateId in aggregateIds)
        {
            // Load aggregate (which replays all events with the new logic)
            var result = await _repository.LoadAsync(aggregateId, ct);
            if (!result.IsSuccess)
            {
                Console.WriteLine($"Failed to load {aggregateId}: {result.Error}");
                continue;
            }

            using var aggregate = result.Value;
            if (aggregate.Version == StreamPosition.Start)
                continue;  // empty stream

            // Replace the old snapshot with one built by the new logic
            await _snapshotStore.WriteAsync(_streamIdFactory(aggregateId), aggregate.Version, aggregate.State, ct);
            Console.WriteLine($"Rebuilt {aggregateId} at version {aggregate.Version.Value}");
        }
    }
}

// Usage example:
var rebuilder = new AggregateRebuilder<Order, OrderId, OrderState>(
    new AggregateRepository<Order, OrderId>(
        eventStore,
        () => new Order(),
        id => new StreamId($"order-{id.Value}")),
    snapshotStore,
    id => new StreamId($"order-{id.Value}"));

await rebuilder.RebuildAll(orderIds);
```

[`CustomSnapshotStore.cs`](../examples/04-advanced/CustomSnapshotStore.cs) shows the same rebuild for a single aggregate (`SnapshotRebuilder`).

### Snapshot Optimization

Snapshots skip replaying old events:

```csharp
// Without snapshot: replay all 10,000 events
var repository = new AggregateRepository<Order, OrderId>(
    eventStore,
    () => new Order(),
    id => new StreamId($"order-{id.Value}"));
var order = (await repository.LoadAsync(orderId)).Value;

// With snapshot: restore state from position 5,000, replay only events 5,001-10,000
var snapshotRepository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: repository,
    snapshotStore: snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order());
var restored = (await snapshotRepository.LoadAsync(orderId)).Value;
```

`RestoreState` sets the aggregate's state, `Version` and `OriginalVersion` to the snapshot's, as
replaying up to that position would have, and only works on a fresh aggregate.

Snapshots are covered in detail in [Snapshots Usage](./snapshots-usage.md).

## Handling Event Ordering

### Single Stream: Guaranteed Order

Within a single stream, events are ordered:

```csharp
// These events ALWAYS arrive in this order
Stream "order-123":
  1. OrderPlacedEvent (position 1)
  2. OrderConfirmedEvent (position 2)
  3. OrderShippedEvent (position 3)

// No reordering, ever
```

### Multiple Streams: No Global Order

Events across different streams have no guaranteed order:

```csharp
// Stream A (order-123):   [OrderPlaced] [Confirmed] [Shipped]
// Stream B (order-456):   [OrderPlaced] [Confirmed]
// Stream C (invoice-789): [InvoiceSent]

// There's no guarantee about which stream's events arrive first
// Don't assume order across streams
```

## Dealing with Long Streams

For streams with thousands of events, replay becomes expensive. Solutions:

### Solution 1: Snapshots (Recommended)

Most effective for long-lived aggregates:

```csharp
var snapshotStore = new InMemorySnapshotStore<OrderState>();

// Write a snapshot on save once 100 events have been appended since the last one,
// and load from the latest snapshot plus the events after it
var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: new AggregateRepository<Order, OrderId>(
        eventStore,
        () => new Order(),
        id => new StreamId($"order-{id.Value}")),
    snapshotStore: snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order(),
    snapshotPolicy: SnapshotPolicy.EveryNEvents(100),
    extractState: order => order.State);
```

See [Snapshots Usage](./snapshots-usage.md) for details.

### Solution 2: Archival Strategy

For very old events, periodically archive to slower storage:

```csharp
public class EventArchiver
{
    private readonly IEventStore _live;
    private readonly IEventStore _archive;

    public EventArchiver(IEventStore live, IEventStore archive)
    {
        _live = live;
        _archive = archive;
    }

    public async Task Archive(StreamId streamId, StreamPosition before, CancellationToken ct = default)
    {
        // Copy the events before `before` to the archive, as one append to a new stream
        var old = new List<object>();
        await foreach (var envelope in _live.ReadAsync(streamId, StreamPosition.Start, ct))
        {
            if (envelope.Position.Value >= before.Value)
                break;
            old.Add(envelope.Event);
        }

        var archived = await _archive.AppendAsync(streamId, old.ToArray(), StreamPosition.Start, ct);
        if (archived.IsFailure)
            throw new InvalidOperationException($"Archive failed: {archived.Error}");

        // IEventStore is append-only and has no delete. Removing the archived events from
        // the live store is specific to your database, for example a DELETE on its events table.
    }
}
```

### Solution 3: Event Compaction

Periodically start a new stream whose first event carries the current state. The aggregate
needs an `Apply` for that event, so it can load from the compacted stream:

```csharp
// The compacted stream starts with this event
public sealed record OrderCompactedEvent(OrderState State, long EventCount);

// In OrderState:
//   internal OrderState Apply(OrderCompactedEvent e) => e.State;

public class EventCompactor
{
    private readonly IEventStore _eventStore;
    private readonly IAggregateRepository<Order, OrderId> _repository;

    public EventCompactor(IEventStore eventStore, IAggregateRepository<Order, OrderId> repository)
    {
        _eventStore = eventStore;
        _repository = repository;
    }

    public async Task CompactStream(OrderId orderId, CancellationToken ct = default)
    {
        // Load the current state by replaying the entire stream
        using var order = (await _repository.LoadAsync(orderId, ct)).Value;

        // Create "snapshot event" with all current state
        var compactEvent = new OrderCompactedEvent(order.State, order.Version.Value);

        // Create new stream starting with compact event
        var compactStreamId = new StreamId($"order-{orderId.Value}_compact");
        await _eventStore.AppendAsync(compactStreamId, new object[] { compactEvent }, StreamPosition.Start, ct);

        // Switching readers to the compacted stream, and retiring the old one,
        // is up to your application and database.
    }
}
```

For most aggregates, [snapshots](#snapshot-optimization) solve the same problem without a new stream.

## Replay Safety and Idempotency

### Idempotent Replay

Replaying the same events multiple times always produces the same state:

```csharp
// First load
using var order1 = (await repository.LoadAsync(orderId)).Value;
var state1 = order1.State;

// Second load (identical)
using var order2 = (await repository.LoadAsync(orderId)).Value;
var state2 = order2.State;

// state1 == state2 (always)
Assert.Equal(state1, state2);
```

This is guaranteed because:
1. Events are immutable (never change)
2. Apply methods are pure (deterministic)
3. Events are ordered (same order every time)

### Position Tracking

Track your position when processing events to avoid re-processing:

```csharp
public class ProjectionProcessor
{
    private StreamPosition _lastProcessedPosition = StreamPosition.Start;
    
    public async Task ProcessNew()
    {
        // Resume after the last processed position: reads exclude their start position
        await foreach (var envelope in eventStore.ReadAsync(streamId, _lastProcessedPosition))
        {
            // Process event
            await ProcessEvent(envelope.Event);
            
            // Update position
            _lastProcessedPosition = envelope.Position;
        }
    }
}
```

## Concurrent Loads: Optimistic Locking

When multiple clients load the same aggregate, conflicts are detected at save time:

```csharp
// Client 1 loads
using var order1 = (await repository.LoadAsync(orderId)).Value;
// order1.OriginalVersion == 5 (stream has 5 events)

// Client 2 loads
using var order2 = (await repository.LoadAsync(orderId)).Value;
// order2.OriginalVersion == 5

// Client 1 modifies and saves
order1.Ship("TRACK-1");
await repository.SaveAsync(order1, orderId);  // Success, stream is now at version 6

// Client 2 tries to modify and save (conflict!)
order2.Confirm();
var result = await repository.SaveAsync(order2, orderId);
// result.IsFailure and result.Error.Code == "CONFLICT"
// order2.OriginalVersion (5) != stream version (6)

// Client 2 must retry with a fresh load
using var order3 = (await repository.LoadAsync(orderId)).Value;
// Now includes order1's Ship event
order3.Confirm();
await repository.SaveAsync(order3, orderId);  // Success
```

## Testing Replay Logic

Test that aggregates load correctly from events. Save through a repository over the in-memory event store, then load through the same repository:

```csharp
public class ReplayTests
{
    private readonly IEventStore eventStore = new EventStore(
        new InMemoryEventStoreAdapter(),
        new JsonEventSerializer(),       // any IEventSerializer
        new OrderEventTypeRegistry());   // source-generated for Order
    private readonly AggregateRepository<Order, OrderId> repository;

    public ReplayTests() =>
        repository = new AggregateRepository<Order, OrderId>(
            eventStore, () => new Order(), id => new StreamId($"order-{id.Value}"));

    [Fact]
    public async Task LoadAggregate_ReconstructsStateFromEvents()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());

        using var order = new Order();
        order.Place("ORD-001", 1500m);
        order.Confirm();
        order.Ship("TRACK-123");
        await repository.SaveAsync(order, orderId);

        // Act: Load from store
        using var loadedOrder = (await repository.LoadAsync(orderId)).Value;

        // Assert
        Assert.Equal(new StreamPosition(3), loadedOrder.Version);  // 3 events replayed
        Assert.True(loadedOrder.State.IsPlaced);
        Assert.True(loadedOrder.State.IsConfirmed);
        Assert.True(loadedOrder.State.IsShipped);
        Assert.Equal("TRACK-123", loadedOrder.State.TrackingNumber);
        Assert.Equal(1500m, loadedOrder.State.Total);
    }

    [Fact]
    public async Task ReadAfterPosition_ReturnsOnlyLaterEvents()
    {
        // Arrange: an order with 4 events
        var orderId = new OrderId(Guid.NewGuid());
        var streamId = new StreamId($"order-{orderId.Value}");

        using var order = new Order();
        order.Place("ORD-001", 1500m);
        order.Confirm();
        order.Ship("TRACK-123");
        order.Deliver();
        await repository.SaveAsync(order, orderId);

        // Act: read after position 3 (after Ship)
        var positions = new List<StreamPosition>();
        await foreach (var envelope in eventStore.ReadAsync(streamId, new StreamPosition(3)))
        {
            positions.Add(envelope.Position);
        }

        // Assert: only the Deliver event, at position 4
        Assert.Equal(new[] { new StreamPosition(4) }, positions);
    }

    [Fact]
    public async Task MultipleLoads_ProduceIdenticalState()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        using (var order = new Order())
        {
            order.Place("ORD-001", 1500m);
            order.Confirm();
            await repository.SaveAsync(order, orderId);
        }

        // Act
        var state1 = await LoadAggregateState(orderId);
        var state2 = await LoadAggregateState(orderId);
        var state3 = await LoadAggregateState(orderId);

        // Assert
        Assert.Equal(state1, state2);
        Assert.Equal(state2, state3);
    }

    private async Task<OrderState> LoadAggregateState(OrderId orderId)
    {
        using var order = (await repository.LoadAsync(orderId)).Value;
        return order.State;
    }
}
```

## Complete Load/Modify/Save Cycle

End-to-end example:

```csharp
public class OrderService
{
    private readonly IAggregateRepository<Order, OrderId> _repository;

    public OrderService(IEventStore eventStore, ISnapshotStore<OrderState> snapshotStore)
    {
        _repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepository: new AggregateRepository<Order, OrderId>(
                eventStore,
                () => new Order(),
                id => new StreamId($"order-{id.Value}")),
            snapshotStore: snapshotStore,
            strategy: SnapshotLoadingStrategy.ValidateAndReplay,
            restoreState: (order, state, pos) => order.RestoreState(state, pos),
            eventStore: eventStore,
            streamIdFactory: id => new StreamId($"order-{id.Value}"),
            aggregateFactory: () => new Order(),
            snapshotPolicy: SnapshotPolicy.EveryNEvents(100),  // === SNAPSHOT (every 100 events) ===
            extractState: order => order.State);
    }

    public async Task ShipOrder(OrderId orderId, string trackingNumber)
    {
        // === LOAD === from the latest snapshot plus the events after it
        var loaded = await _repository.LoadAsync(orderId);
        if (!loaded.IsSuccess)
            throw new InvalidOperationException($"Failed to load order: {loaded.Error}");
        using var order = loaded.Value;

        // === MODIFY ===
        order.Ship(trackingNumber, "FedEx");

        // === SAVE === appends with order.OriginalVersion as the expected version
        var result = await _repository.SaveAsync(order, orderId);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Failed to save order: {result.Error}");
    }
}
```

## Performance Benchmarks

Approximate timings for replaying N events (on modern hardware):

| Events | Class State | Struct State | Snapshot at 500 |
|---|---|---|---|
| 10 | 0.1ms | 0.05ms | 0.05ms |
| 100 | 1ms | 0.5ms | 0.5ms |
| 1,000 | 10ms | 5ms | 5ms |
| 5,000 | 50ms | 25ms | 5ms (+ restore) |
| 10,000 | 100ms | 50ms | 10ms (+ restore) |
| 50,000 | 500ms | 250ms | 50ms (+ restore) |

**Lessons:**
- Struct state is 2-4x faster than class state
- Snapshots make a huge difference for long streams
- Deserialization is expensive; batch reads where possible

## Summary

Effective replay and rebuilding requires:

1. **Understand order guarantees** — Single stream is ordered, multiple streams aren't
2. **Track positions** — Use StreamPosition to avoid re-processing
3. **Use snapshots for long streams** — 500+ events? Use snapshots
4. **Test idempotency** — Verify replaying same events produces same state
5. **Handle concurrency** — Use optimistic locking, implement retry logic
6. **Monitor performance** — Measure replay time, adjust snapshot frequency

## Next Steps

- **[Snapshots Usage](./snapshots-usage.md)** — Optimize long stream loading
- **[Projections Usage](./projections-usage.md)** — Build read models from events
- **[Performance Guide](../performance.md)** — Detailed benchmarking
