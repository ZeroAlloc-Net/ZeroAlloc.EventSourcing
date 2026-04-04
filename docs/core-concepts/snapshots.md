# Core Concepts: Snapshots

Snapshots are a performance optimization for event sourcing. They reduce the cost of loading long-lived aggregates by storing periodic snapshots of state, allowing you to replay from a snapshot rather than from the beginning of the event stream.

## Why Snapshots?

In event sourcing, loading an aggregate requires replaying all events:

```csharp
var order = new Order();
order.SetId(orderId);

// Replay ALL events from the beginning
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
}
```

For young aggregates (10-100 events), this is fast. But for long-lived aggregates with thousands of events, replay becomes expensive:

| Stream Size | Replay Time | Problem |
|---|---|---|
| 100 events | ~10ms | Fast |
| 1,000 events | ~100ms | Acceptable |
| 10,000 events | ~1s | Getting slow |
| 100,000 events | ~10s | Unacceptable |

**Snapshots solve this by storing periodic state:**

```
Stream: [OrderPlaced] [Confirmed] [Shipped] ... [Event #10,000]

Without snapshot: Replay all 10,000 events
With snapshot at position 5,000:
  1. Load snapshot → instant state at position 5,000
  2. Replay only events 5,001-10,000 → fast
  Result: 2x or 10x faster loading
```

## When to Use Snapshots

Use snapshots when:

1. **Aggregates have many events** — Typically 500+ events per stream
2. **Loading performance matters** — High-frequency reads of the same aggregates
3. **Replay is expensive** — Your Apply methods do complex computations
4. **Aggregate lifetime is long** — Orders, customers, accounts that exist for years

Don't use snapshots when:

1. **Aggregates are small** — Most streams under 100 events
2. **Loading performance is not critical** — Bulk operations, batch processing
3. **Snapshot storage complexity isn't worth it** — Simple systems without high load

## Snapshot Storage Interface

ZeroAlloc.EventSourcing defines a storage-agnostic snapshot API:

```csharp
public interface ISnapshotStore<TState> where TState : struct
{
    // Read the most recent snapshot for a stream
    ValueTask<(StreamPosition Position, TState State)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default);

    // Write a snapshot at a specific position
    ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        TState state,
        CancellationToken ct = default);
}
```

### ReadAsync: Loading Snapshots

```csharp
var snapshotStore = new InMemorySnapshotStore<OrderState>();

var result = await snapshotStore.ReadAsync(streamId);
if (result.HasValue)
{
    var (position, state) = result.Value;
    Console.WriteLine($"Snapshot at position {position.Value}");
    Console.WriteLine($"State: {state}");
}
else
{
    Console.WriteLine("No snapshot found");
}
```

Returns `null` if no snapshot exists. This allows graceful fallback to full replay.

### WriteAsync: Saving Snapshots

```csharp
// After loading and modifying an aggregate
var finalState = order.State;
var finalPosition = order.Version;

await snapshotStore.WriteAsync(streamId, finalPosition, finalState);
```

Snapshots are typically written:
- After processing a command (keep the latest state)
- Periodically during batch operations
- On a schedule (e.g., every 100 events)

## Loading Strategies

Three strategies determine how snapshots are used:

### ValidateAndReplay (Recommended)

Check that the snapshot position exists in the event store before using it. Fall back to full replay if validation fails.

```csharp
var snapshotRepo = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: baseRepo,
    snapshotStore: snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    ...
);

// Usage:
var order = await snapshotRepo.LoadAsync(orderId);
```

**Advantages:**
- Protects against corrupted or stale snapshots
- Automatically detects if snapshot position is invalid
- Falls back gracefully to full replay

**When to use:** Production systems where snapshot integrity isn't guaranteed.

### TrustSnapshot (Fastest)

Assume snapshots are always valid. Skip validation overhead and restore state directly from the snapshot.

```csharp
strategy: SnapshotLoadingStrategy.TrustSnapshot
```

**Advantages:**
- No validation overhead
- Minimal latency for loading
- Best performance for high-frequency loads

**When to use:** Snapshots are immutable and always valid (e.g., read-only systems, internal caching).

### IgnoreSnapshot (Debug/Testing)

Bypass snapshot optimization entirely. Always replay from the beginning, as if no snapshots exist.

```csharp
strategy: SnapshotLoadingStrategy.IgnoreSnapshot
```

**Advantages:**
- Disables optimization without code changes
- Useful for testing and debugging
- Simplifies troubleshooting

**When to use:** Development, testing, or temporarily disabling snapshots for verification.

## Snapshot Consistency

Snapshots must be consistent with the event stream. A snapshot represents state at a specific position, so:

**Key rule:** A snapshot at position P is valid only if position P exists in the stream.

```csharp
// Position 1: OrderPlaced
// Position 2: Confirmed
// Position 3: Shipped
// [Snapshot of state at position 3]
// Position 4: Refund initiated

// When loading:
// - Snapshot is valid (position 3 exists)
// - Restore state from snapshot
// - Replay events 4+ from stream
```

If a snapshot points to a position that no longer exists (corrupted, migrated, etc.), it's stale and should be discarded.

### Position-Based Safety

Snapshots are identified by position, not just time. This ensures:

```csharp
// Safe: Position is immutable in the event store
snapshot1 = (position: 100, state: X)  // Always points to the same moment
snapshot2 = (position: 100, state: X)  // Always identical

// Unsafe: Time-based snapshots can drift
snapshot1 = (timestamp: 2024-04-01 10:00, state: X)  // What if position 99.5?
snapshot2 = (timestamp: 2024-04-01 10:00, state: Y)  // Might be different
```

Use position-based snapshots (which ZeroAlloc.EventSourcing does by default).

## Snapshot Caching Patterns

Snapshots are typically managed through a caching decorator:

```csharp
// Standard repository
var baseRepository = new AggregateRepository<Order, OrderId>(
    eventStore,
    () => new Order(),
    id => new StreamId($"order-{id.Value}"));

// Wrap with snapshot caching
var cachedRepository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: baseRepository,
    snapshotStore: new InMemorySnapshotStore<OrderState>(),
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order());

// Load (uses snapshot if available)
var order = await cachedRepository.LoadAsync(orderId);
```

**How it works:**

1. Check if a snapshot exists
2. If yes, restore state and replay recent events
3. If no, replay from the beginning
4. After loading, optionally save a snapshot

## Built-in Implementations

### InMemorySnapshotStore

For development and testing:

```csharp
var snapshotStore = new InMemorySnapshotStore<OrderState>();

// Use like any other snapshot store
await snapshotStore.WriteAsync(streamId, position, state);
var result = await snapshotStore.ReadAsync(streamId);
```

**Characteristics:**
- Snapshots stored in memory
- Lost on process restart
- Fast for testing
- Single-threaded

### PostgreSQL Snapshot Store

For production with PostgreSQL:

```csharp
var snapshotStore = new PostgreSqlSnapshotStore<OrderState>(
    connectionString: "Server=localhost;Database=snapshots;...",
    tableName: "snapshots",
    serializer: new OrderSnapshotSerializer()
);
```

**Characteristics:**
- Persistent snapshots across restarts
- ACID guarantees
- Highly available
- Production-grade reliability

### SQL Server Snapshot Store

For production with SQL Server:

```csharp
var snapshotStore = new SqlServerSnapshotStore<OrderState>(
    connectionString: "Server=localhost;Database=snapshots;...",
    tableName: "snapshots",
    serializer: new OrderSnapshotSerializer()
);
```

## Partial vs. Complete Snapshots

**Complete snapshots** store the entire aggregate state:

```csharp
public partial struct OrderState : IAggregateState<OrderState>
{
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    public List<LineItem> Items { get; private set; }
}

// Snapshot = entire OrderState
// Smaller state → smaller snapshots → faster restore
```

**Partial snapshots** (advanced) store only a subset. Not commonly used, but possible for very large aggregates:

```csharp
// Snapshot only the most-accessed fields
public record PartialOrderSnapshot(
    OrderId Id,
    decimal Total,
    bool IsShipped
);
```

For most systems, complete snapshots are simpler and sufficient.

## Benchmarking and Deciding Snapshot Intervals

When should you create snapshots? Common strategies:

### By Event Count

Create a snapshot every N events:

```csharp
long eventCount = 0;
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
    eventCount++;
    
    if (eventCount % 100 == 0)  // Every 100 events
    {
        await snapshotStore.WriteAsync(streamId, envelope.Position, order.State);
    }
}
```

### By Time

Create a snapshot periodically:

```csharp
var lastSnapshot = await snapshotStore.ReadAsync(streamId);
if (lastSnapshot == null || DateTime.UtcNow - lastSnapshot.Timestamp > TimeSpan.FromHours(1))
{
    await snapshotStore.WriteAsync(streamId, stream.Position, state);
}
```

### By Size

Create a snapshot if the state object exceeds a size threshold:

```csharp
int stateSize = JsonSerializer.Serialize(state).Length;
if (stateSize > 10_000)  // Larger than 10KB
{
    await snapshotStore.WriteAsync(streamId, stream.Position, state);
}
```

### Recommended Heuristics

- **Young aggregates (< 100 events)** — No snapshots needed
- **Moderate aggregates (100-500 events)** — Snapshot every 200 events
- **Large aggregates (> 500 events)** — Snapshot every 100 events or every hour
- **Very large aggregates (> 5,000 events)** — Snapshot every 50 events

Measure your own system; these are starting points.

## Performance Impact Example

For an Order aggregate with 10,000 events:

**Without snapshots:**
```
Load time: 1,000ms (replay all 10,000 events)
```

**With snapshot at position 5,000:**
```
Load time:
  - Read snapshot: ~1ms
  - Replay events 5,001-10,000: ~500ms
  - Total: ~501ms (2x faster)
```

**With snapshot at position 9,000:**
```
Load time:
  - Read snapshot: ~1ms
  - Replay events 9,001-10,000: ~100ms
  - Total: ~101ms (10x faster)
```

The benefit depends on:
- How many events exist
- How expensive each Apply operation is
- How frequently you load the aggregate

## Summary

Snapshots are:
- **Performance optimizations** — Reduce replay overhead
- **Position-based** — Always safe (positions are immutable)
- **Optional** — Works with or without them
- **Strategy-driven** — ValidateAndReplay (safe), TrustSnapshot (fast), IgnoreSnapshot (debug)
- **Storage-agnostic** — In-memory, PostgreSQL, SQL Server implementations
- **Composable** — Added via repository decorator, not intrusive

Snapshots are worth using for aggregates with high event counts or frequent loads, but add complexity. Start without them and add them when profiling shows they help.

## Next Steps

- **[Core Concepts: Projections](./projections.md)** — Building read models from events
- **[Example: Snapshot-Optimized Loading](../examples/SnapshotOptimizedLoading.md)** — Complete working example
- **[Performance Guide](../performance.md)** — Benchmarks and optimization strategies
