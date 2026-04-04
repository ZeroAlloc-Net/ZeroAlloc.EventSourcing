# Usage Guide: Replay and Rebuilding

## Scenario: How Do I Load Aggregates and Handle Large Event Streams?

Event replay is fundamental to event sourcing. This guide covers loading aggregates from history, handling large event streams, and strategies for performance optimization.

## Loading from History: The Complete Cycle

Loading an aggregate from an event store requires three steps. See [building-aggregates.md](./building-aggregates.md) for the repository interface definition and aggregate base class documentation:

```csharp
// Step 1: Create an empty aggregate
var order = new Order();
order.SetId(orderId);

// Step 2: Read all events from the stream
var eventCount = 0;
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    // Step 3: Apply each event to rebuild state
    order.ApplyHistoric(envelope.Event, envelope.Position);
    eventCount++;
}

// Now order.State is fully reconstructed
Console.WriteLine($"Loaded {eventCount} events");
Console.WriteLine($"Order status: {order.State.IsPlaced}");
Console.WriteLine($"Final version: {order.Version}");
```

### Event Envelope

Events are returned wrapped in `EventEnvelope`:

```csharp
public record EventEnvelope
{
    public object Event { get; set; }           // The event payload
    public StreamId StreamId { get; set; }      // Which stream
    public StreamPosition Position { get; set; } // Position in stream
    public EventMetadata? Metadata { get; set; } // Optional metadata
}
```

The `Position` is critical—it identifies where the event sits in the stream's ordering and is needed for optimistic concurrency control.

### ApplyHistoric vs. ApplyEvent

Two methods apply events to state:

```csharp
// Internal: Used when loading from store (sets Version)
order.ApplyHistoric(envelope.Event, envelope.Position);

// Internal: Used when raising new events (in Raise())
order.ApplyEvent(state, @event);  // Doesn't update Version
```

`ApplyHistoric` updates the aggregate's internal `Version` tracking, which is essential for detecting concurrent modifications at save time.

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

Start replaying from a specific position:

```csharp
// From the beginning
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
}

// Or from a specific position (useful with snapshots)
var startPosition = new StreamPosition(500);
await foreach (var envelope in eventStore.ReadAsync(streamId, startPosition))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
}
```

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
public class AggregateRebuilder<TId, TState>
    where TId : struct
    where TState : struct, IAggregateState<TState>
{
    private readonly IEventStore _eventStore;
    private readonly IAggregateRepository<TId, TState> _repository;
    private readonly Func<StreamId, TId> _extractId;
    
    public AggregateRebuilder(
        IEventStore eventStore,
        IAggregateRepository<TId, TState> repository,
        Func<StreamId, TId> extractId)
    {
        _eventStore = eventStore;
        _repository = repository;
        _extractId = extractId;  // Helper to extract TId from StreamId
    }
    
    public async Task RebuildAll(StreamId filter, CancellationToken ct = default)
    {
        // Read all streams matching filter
        await foreach (var envelope in _eventStore.ReadAllAsync(filter, ct))
        {
            // Extract stream ID from envelope
            var streamId = envelope.StreamId;
            
            // Extract aggregate ID from stream ID
            // This is domain-specific; example: streamId "order-{guid}" -> OrderId
            var aggregateId = _extractId(streamId);
            
            // Load aggregate (which replays all events)
            var result = await _repository.LoadAsync(aggregateId, ct);
            if (!result.IsSuccess)
            {
                Console.WriteLine($"Failed to load {streamId}: {result.Error}");
                continue;
            }
            
            // Aggregate is now fully reconstructed with new logic
            Console.WriteLine($"Rebuilt {streamId}");
        }
    }
}

// Usage example:
// For Order aggregates with stream ID format "order-{guid}":
var rebuilder = new AggregateRebuilder<OrderId, OrderState>(
    eventStore,
    repository,
    extractId: streamId =>
    {
        // Extract GUID from stream ID "order-{guid}"
        var guidPart = streamId.Value.Substring("order-".Length);
        return new OrderId(Guid.Parse(guidPart));
    });

await rebuilder.RebuildAll(new StreamId("order-*"));
```

### Snapshot Optimization

Snapshots skip replaying old events:

```csharp
// Without snapshot: replay all 10,000 events
var order = new Order();
order.SetId(orderId);
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
}

// With snapshot: restore state from position 5,000, replay only events 5,001-10,000
var order = new Order();
order.SetId(orderId);

// Check for snapshot
var snapshotResult = await snapshotStore.ReadAsync(streamId);
StreamPosition startPosition = StreamPosition.Start;

if (snapshotResult.HasValue)
{
    var (snapshotPosition, snapshotState) = snapshotResult.Value;
    
    // Restore state from snapshot
    order.RestoreState(snapshotState, snapshotPosition);
    
    // Start replaying from after the snapshot
    startPosition = snapshotPosition.Next();
}

// Replay remaining events
await foreach (var envelope in eventStore.ReadAsync(streamId, startPosition))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
}
```

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

// After loading, save a snapshot every N events
if (order.Version.Value % 100 == 0)  // Every 100 events
{
    await snapshotStore.WriteAsync(streamId, order.Version, order.State);
}
```

See [Snapshots Usage](./snapshots-usage.md) for details.

### Solution 2: Archival Strategy

For very old events, periodically archive to slower storage:

```csharp
public class EventArchiver
{
    private readonly IEventStore _live;
    private readonly IEventStore _archive;
    
    public async Task Archive(StreamId streamId, StreamPosition before)
    {
        // Copy old events to archive
        await foreach (var envelope in _live.ReadAsync(streamId, StreamPosition.Start))
        {
            if (envelope.Position.Value >= before.Value)
                break;
            
            await _archive.AppendAsync(streamId, new[] { envelope.Event }, envelope.Position);
        }
        
        // Delete from live store (keep recent events)
        await _live.DeleteAsync(streamId, before);
    }
}
```

### Solution 3: Event Compaction

Periodically create a new stream with compacted events:

```csharp
public class EventCompactor
{
    public async Task CompactStream(StreamId streamId)
    {
        // Read entire stream
        var aggregates = new List<object>();
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            aggregates.Add(envelope.Event);
        }
        
        // Create "snapshot event" with all current state
        var compactEvent = new StreamCompactedEvent(
            FinalState: order.State,
            EventCount: aggregates.Count
        );
        
        // Create new stream starting with compact event
        var compactStreamId = new StreamId($"{streamId.Value}_compact");
        await eventStore.AppendAsync(compactStreamId, new[] { compactEvent }, StreamPosition.Start);
    }
}
```

## Replay Safety and Idempotency

### Idempotent Replay

Replaying the same events multiple times always produces the same state:

```csharp
// First load
var order1 = new Order();
order1.SetId(orderId);
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    order1.ApplyHistoric(envelope.Event, envelope.Position);
var state1 = order1.State;

// Second load (identical)
var order2 = new Order();
order2.SetId(orderId);
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    order2.ApplyHistoric(envelope.Event, envelope.Position);
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
        // Resume from last processed position
        var startPosition = _lastProcessedPosition.Next();
        
        await foreach (var envelope in eventStore.ReadAsync(streamId, startPosition))
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
var order1 = new Order();
order1.SetId(orderId);
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    order1.ApplyHistoric(envelope.Event, envelope.Position);
// order1.OriginalVersion == 5 (stream has 5 events)

// Client 2 loads
var order2 = new Order();
order2.SetId(orderId);
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    order2.ApplyHistoric(envelope.Event, envelope.Position);
// order2.OriginalVersion == 5

// Client 1 modifies and saves
order1.Ship("TRACK-1");
await repository.SaveAsync(order1);  // Success, saves at position 6

// Client 2 tries to modify and save (conflict!)
order2.Confirm();
var result = await repository.SaveAsync(order2);
// result.Error == StoreError.Conflict
// order2.OriginalVersion (5) != stream version (6)

// Client 2 must retry
var order3 = new Order();
order3.SetId(orderId);
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    order3.ApplyHistoric(envelope.Event, envelope.Position);
// Now includes order1's Ship event
order3.Confirm();
await repository.SaveAsync(order3);  // Success
```

## Testing Replay Logic

Test that aggregates load correctly from events:

```csharp
[Fact]
public async Task LoadAggregate_ReconstructsStateFromEvents()
{
    // Arrange
    var orderId = new OrderId(Guid.NewGuid());
    var streamId = new StreamId($"order-{orderId.Value}");
    
    var order = new Order();
    order.SetId(orderId);
    order.Place("ORD-001", 1500m);
    order.Confirm();
    order.Ship("TRACK-123");
    
    var events = order.DequeueUncommitted();
    await eventStore.AppendAsync(streamId, events, StreamPosition.Start);
    
    // Act: Load from store
    var loadedOrder = new Order();
    loadedOrder.SetId(orderId);
    
    int eventCount = 0;
    await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    {
        loadedOrder.ApplyHistoric(envelope.Event, envelope.Position);
        eventCount++;
    }
    
    // Assert
    Assert.Equal(3, eventCount);
    Assert.True(loadedOrder.State.IsPlaced);
    Assert.True(loadedOrder.State.IsConfirmed);
    Assert.True(loadedOrder.State.IsShipped);
    Assert.Equal("TRACK-123", loadedOrder.State.TrackingNumber);
    Assert.Equal(1500m, loadedOrder.State.Total);
}

[Fact]
public async Task LoadPartialStream_ReplaysSincePosition()
{
    // Arrange
    var orderId = new OrderId(Guid.NewGuid());
    var streamId = new StreamId($"order-{orderId.Value}");
    
    // Create order with 5 events
    var order = new Order();
    order.SetId(orderId);
    order.Place("ORD-001", 1500m);
    order.Confirm();
    order.Ship("TRACK-123");
    order.Deliver();
    // (5th event would be delivered)
    
    var events = order.DequeueUncommitted();
    await eventStore.AppendAsync(streamId, events, StreamPosition.Start);
    
    // Act: Load from position 3 (after Ship)
    var loadedOrder = new Order();
    loadedOrder.SetId(orderId);
    
    var startPosition = new StreamPosition(3);
    await foreach (var envelope in eventStore.ReadAsync(streamId, startPosition))
    {
        loadedOrder.ApplyHistoric(envelope.Event, envelope.Position);
    }
    
    // Assert: Only has state after position 3
    // (Missing IsPlaced, IsConfirmed, IsShipped would be false
    // but they depend on previous events)
    Assert.Equal(new StreamPosition(4), loadedOrder.Version);
}

[Fact]
public async Task MultipleLoads_ProduceIdenticalState()
{
    // Arrange & Act
    var state1 = await LoadAggregateState();
    var state2 = await LoadAggregateState();
    var state3 = await LoadAggregateState();
    
    // Assert
    Assert.Equal(state1, state2);
    Assert.Equal(state2, state3);
}

private async Task<OrderState> LoadAggregateState()
{
    var order = new Order();
    order.SetId(new OrderId(Guid.NewGuid()));
    
    await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    {
        order.ApplyHistoric(envelope.Event, envelope.Position);
    }
    
    return order.State;
}
```

## Complete Load/Modify/Save Cycle

End-to-end example:

```csharp
public class OrderService
{
    private readonly IEventStore _eventStore;
    private readonly ISnapshotStore<OrderState> _snapshotStore;
    
    public async Task ShipOrder(OrderId orderId, string trackingNumber)
    {
        var streamId = new StreamId($"order-{orderId.Value}");
        
        // === LOAD ===
        var order = new Order();
        order.SetId(orderId);
        
        // Try to load from snapshot
        var snapshotResult = await _snapshotStore.ReadAsync(streamId);
        StreamPosition startPosition = StreamPosition.Start;
        
        if (snapshotResult.HasValue)
        {
            var (snapshotPosition, state) = snapshotResult.Value;
            order.RestoreState(state, snapshotPosition);
            startPosition = snapshotPosition.Next();
        }
        
        // Replay remaining events
        await foreach (var envelope in _eventStore.ReadAsync(streamId, startPosition))
        {
            order.ApplyHistoric(envelope.Event, envelope.Position);
        }
        
        // === MODIFY ===
        order.Ship(trackingNumber, "FedEx");
        
        // === SAVE ===
        var events = order.DequeueUncommitted();
        var result = await _eventStore.AppendAsync(
            streamId, 
            events, 
            order.OriginalVersion);
        
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Failed to save order: {result.Error}");
        
        // === SNAPSHOT (optional, every 100 events) ===
        if (order.Version.Value % 100 == 0)
        {
            await _snapshotStore.WriteAsync(
                streamId,
                order.Version,
                order.State);
        }
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
