# Usage Guide: Snapshots Usage

## Scenario: My Aggregates Have Thousands of Events; How Do I Optimize?

Snapshots solve the performance problem of replaying long event streams. This guide shows when to use them, how to implement them, and how to avoid common pitfalls.

## When Snapshots Are Needed

Use snapshots when:

1. **Aggregates accumulate many events** — Typically 500+ events per stream
2. **Loading performance is critical** — High-frequency reads of the same aggregates
3. **Replay is expensive** — Complex Apply logic, many field updates
4. **Aggregates are long-lived** — Orders, customers, accounts existing for years

Example: An Order aggregate with:
- One OrderPlacedEvent
- Multiple ItemAddedEvent (50+ items)
- One OrderConfirmedEvent  
- One OrderShippedEvent
- Multiple ItemReturnedEvent (returns for items)
- Total: 1,000+ events over time

Without snapshots: Replay 1,000 events every time you load the order.
With snapshots: Load snapshot at position 500, replay only 500 recent events.

### Don't Use Snapshots When:

1. **Aggregates are small** — Most streams under 100 events
2. **Loading performance is not critical** — Batch operations, low-frequency access
3. **Snapshot storage complexity isn't worth it** — Simple systems without high load

## Snapshot Strategies

ZeroAlloc.EventSourcing supports three loading strategies:

### ValidateAndReplay (Recommended)

Validates the snapshot before using it:

```csharp
var strategy = SnapshotLoadingStrategy.ValidateAndReplay;
```

**How it works:**
1. Load snapshot
2. Verify snapshot position exists in event store
3. If valid, restore state and replay from after snapshot
4. If invalid, fall back to full replay

`SnapshotCachingRepositoryDecorator` implements these steps. The only piece you write is the
`restoreState` callback, which puts the snapshot onto the fresh aggregate through the public
`Aggregate<TId, TState>.RestoreState(state, position)`:

```csharp
restoreState: (order, state, pos) => order.RestoreState(state, pos),
```

`RestoreState` sets `State`, `Version` and `OriginalVersion` exactly as replaying the events up to
`position` would have, and queues no events. It only works on a fresh aggregate, one that has not
raised, replayed or restored anything, and throws `InvalidOperationException` otherwise. Replaying
events onto an aggregate is internal to the repositories, so load through the decorator rather than
writing the replay loop yourself; see [Using SnapshotCachingRepositoryDecorator](#using-snapshotcachingrepositorydecorator).

**Guarantees:**
- Protects against corrupted snapshots
- Handles missing events gracefully
- Safest for production

**When to use:** Always, unless you have extreme performance constraints

### TrustSnapshot (Fastest)

Assumes snapshots are always valid:

```csharp
var strategy = SnapshotLoadingStrategy.TrustSnapshot;
```

**How it works:**
1. Load snapshot
2. Restore state immediately
3. Replay only the events after the snapshot position, without checking that position first

**Guarantees:**
- Fastest possible loading
- No validation overhead
- Assumes snapshot is immutable and always correct

**When to use:** 
- High-frequency, high-load systems
- Where snapshot invalidation is impossible
- After thorough testing

### IgnoreSnapshot (Testing/Debugging)

Disables snapshot optimization:

```csharp
var strategy = SnapshotLoadingStrategy.IgnoreSnapshot;
```

**How it works:**
1. Never reads the snapshot store
2. Always replays from start through the inner repository
3. Useful for testing and debugging

**When to use:**
- Testing aggregate behavior
- Debugging snapshot issues
- Comparing snapshot vs. non-snapshot performance

## Writing Snapshots

Snapshots are saved periodically, typically after N events. The simplest way is to let
`SnapshotCachingRepositoryDecorator` write them after each successful save, by passing
`snapshotPolicy: SnapshotPolicy.EveryNEvents(100)` and `extractState: order => order.State`
(see [Using SnapshotCachingRepositoryDecorator](#using-snapshotcachingrepositorydecorator)).
To write them yourself, use `ISnapshotStore<TState>.WriteAsync` with a position and state taken
from the same aggregate:

```csharp
public class OrderSnapshotService
{
    private readonly IEventStore _eventStore;
    private readonly ISnapshotStore<OrderState> _snapshotStore;
    private const int SnapshotFrequency = 100;  // Every 100 events
    
    public async Task SaveIfNeeded(OrderId orderId, Order order)
    {
        var streamId = new StreamId($"order-{orderId.Value}");
        
        // Check if we should save a snapshot
        if (order.Version.Value % SnapshotFrequency == 0)
        {
            // Save snapshot at current position
            await _snapshotStore.WriteAsync(
                streamId,
                order.Version,
                order.State);
            
            Console.WriteLine($"Snapshot saved at position {order.Version.Value}");
        }
    }
}
```

### Snapshot Timing

Choose frequency based on:
- **Event volume** — More events = more frequent snapshots
- **Replay cost** — Expensive Apply logic = more frequent snapshots
- **Storage cost** — Frequent snapshots = more disk usage

**Guidelines:**
- Every 100 events: Most domains
- Every 50 events: High-frequency changes
- Every 500 events: Mostly-static aggregates

```csharp
// Example: Different frequencies for different aggregates
public class SnapshotStrategyProvider
{
    public int GetSnapshotFrequency(Type aggregateType)
    {
        return aggregateType.Name switch
        {
            nameof(Order) => 100,           // Orders accumulate fast
            nameof(Customer) => 200,        // Customers change slower
            nameof(Product) => 500,         // Products rarely change
            _ => 100                        // Default
        };
    }
}
```

## Loading with Snapshots

### Using SnapshotCachingRepositoryDecorator

The decorator handles snapshot loading. Replaying events onto an aggregate is internal to the
repositories, so this is how application code loads from a snapshot. See building-aggregates.md for
the repository interface definition.

```csharp
// Setup: Create the decorated repository
var innerRepository = new AggregateRepository<Order, OrderId>(
    _eventStore,
    () => new Order(),
    id => new StreamId($"order-{id.Value}"));

var snapshotRepository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: innerRepository,
    snapshotStore: _snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    eventStore: _eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order());

// Usage: Just call LoadAsync, snapshots happen automatically
var result = await snapshotRepository.LoadAsync(orderId);
if (result.IsSuccess)
{
    var order = result.Value;
    Console.WriteLine($"Order loaded, version: {order.Version}");
}
```

The decorator:
1. Attempts to load snapshot
2. Validates snapshot position (if strategy allows)
3. Creates a fresh aggregate with `aggregateFactory` and calls `restoreState` on it
4. Replays remaining events
5. Returns the loaded aggregate

`restoreState` calls `Aggregate<TId, TState>.RestoreState(state, position)`, which sets `State`,
`Version` and `OriginalVersion` as if the events up to `position` had been replayed. The next save
therefore uses the right expected version. `RestoreState` throws `InvalidOperationException` on an
aggregate that has already raised, replayed or restored anything, so `aggregateFactory` must return a
new instance every time.

To have the decorator write snapshots as well, pass `snapshotPolicy` and `extractState`:

```csharp
snapshotPolicy: SnapshotPolicy.EveryNEvents(100),
extractState: order => order.State,
```

## Snapshot Consistency

Snapshots must be consistent with the event store. Three validation approaches:

### Position-Based Validation (Recommended)

Verify the snapshot position exists in the event store:

```csharp
public async Task<bool> ValidateSnapshot(
    StreamId streamId, StreamPosition snapshotPosition, CancellationToken ct = default)
{
    // A snapshot at Start covers no events, so there is nothing to check
    if (snapshotPosition == StreamPosition.Start)
        return true;

    // Reads exclude their start position, so reading from the position before the
    // snapshot yields the event AT the snapshot position first, if it exists
    var before = new StreamPosition(snapshotPosition.Value - 1);
    await foreach (var envelope in _eventStore.ReadAsync(streamId, before, ct))
    {
        return envelope.Position == snapshotPosition;  // Position exists
    }

    return false;  // Position doesn't exist (snapshot is stale)
}
```

This is the check the `ValidateAndReplay` strategy makes. If validation fails, the decorator falls back to a full replay.

### Hash/Checksum Validation

Store a hash of events up to snapshot position:

```csharp
public record SnapshotWithChecksum(
    StreamPosition Position,
    OrderState State,
    string EventsHash);

public class SnapshotValidator
{
    public async Task<bool> ValidateSnapshot(
        StreamId streamId,
        SnapshotWithChecksum snapshot)
    {
        // Recalculate hash of events up to position
        var hash = "";
        await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            if (envelope.Position.Value > snapshot.Position.Value)
                break;
            
            hash += envelope.Event.GetHashCode().ToString();
        }
        
        // Compare with stored hash
        return hash == snapshot.EventsHash;
    }
}
```

### Event Count Validation

Verify event count matches what's expected:

```csharp
public async Task<bool> ValidateSnapshot(StreamId streamId, StreamPosition snapshotPosition)
{
    // Count events up to snapshot position
    var count = 0;
    await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start))
    {
        count++;
        if (envelope.Position.Value >= snapshotPosition.Value)
            break;
    }
    
    // Check if count matches position (position 100 = 100 events)
    return count == snapshotPosition.Value;
}
```

## SQL Snapshot Stores

Snapshots are stored in databases for persistence.

### PostgreSQL Snapshot Store

```csharp
// Setup
var connectionString = "Host=localhost;Database=EventStore;User=postgres;Password=password";
var dataSource = NpgsqlDataSource.Create(connectionString);
var snapshotStore = new PostgreSqlSnapshotStore<OrderState>(dataSource);

// Create the table if it does not exist (SnapshotSchema.PostgreSqlCreateTable):
await snapshotStore.EnsureSchemaAsync();
// CREATE TABLE IF NOT EXISTS snapshots (
//     stream_id   VARCHAR(255)   NOT NULL,
//     position    BIGINT         NOT NULL,
//     state_type  VARCHAR(500)   NOT NULL,
//     payload     BYTEA          NOT NULL,
//     created_at  TIMESTAMPTZ    NOT NULL,
//     PRIMARY KEY (stream_id)
// );
```

### SQL Server Snapshot Store

```csharp
// Setup
var connectionString = "Server=localhost;Database=EventStore;User=sa;Password=YourPassword123";
var snapshotStore = new SqlServerSnapshotStore<OrderState>(connectionString);

// Create the table if it does not exist (SnapshotSchema.SqlServerCreateTable):
await snapshotStore.EnsureSchemaAsync();
// CREATE TABLE dbo.snapshots (
//     stream_id   NVARCHAR(255)      NOT NULL,
//     position    BIGINT             NOT NULL,
//     state_type  NVARCHAR(500)      NOT NULL,
//     payload     VARBINARY(MAX)     NOT NULL,
//     created_at  DATETIMEOFFSET     NOT NULL,
//     CONSTRAINT PK_snapshots PRIMARY KEY (stream_id)
// );
```

## Testing Snapshot Logic

### Test Snapshot Consistency

```csharp
[Fact]
public async Task Snapshot_IsConsistentWithReplayed()
{
    // Arrange: two events, then a snapshot of the state after them, then two more events
    var orderId = new OrderId(Guid.NewGuid());
    var streamId = new StreamId($"order-{orderId.Value}");

    using var order = new Order();
    order.Place("ORD-001", 1500m);
    order.Confirm();
    await _innerRepository.SaveAsync(order, orderId);
    await _snapshotStore.WriteAsync(streamId, order.Version, order.State);

    order.Ship("TRACK-123");
    order.Deliver();
    await _innerRepository.SaveAsync(order, orderId);

    // Act: load through the snapshot, and by replaying the whole stream
    using var fromSnapshot = (await _snapshotRepository.LoadAsync(orderId)).Value;
    using var fullReplay = (await _innerRepository.LoadAsync(orderId)).Value;

    // Assert: same state, and the same version for the next save's concurrency check
    Assert.Equal(fullReplay.State, fromSnapshot.State);
    Assert.Equal(fullReplay.OriginalVersion, fromSnapshot.OriginalVersion);
}

[Fact]
public async Task SnapshotRepository_LoadsFromSnapshot()
{
    // Arrange: two events and a snapshot after them
    var orderId = new OrderId(Guid.NewGuid());
    var streamId = new StreamId($"order-{orderId.Value}");

    using var order = new Order();
    order.Place("ORD-001", 1500m);
    order.Confirm();
    await _innerRepository.SaveAsync(order, orderId);
    await _snapshotStore.WriteAsync(streamId, order.Version, order.State);  // Position 2

    // Two more events after the snapshot
    order.Ship("TRACK-123");
    order.Deliver();
    await _innerRepository.SaveAsync(order, orderId);

    // Act: restores the snapshot at 2, then replays events 3 and 4
    var result = await _snapshotRepository.LoadAsync(orderId);

    // Assert
    Assert.True(result.IsSuccess);
    using var loaded = result.Value;
    Assert.Equal(4, loaded.Version.Value);
    Assert.Equal(4, loaded.OriginalVersion.Value);
}
```

### Test Snapshot Fallback

```csharp
[Fact]
public async Task ValidateAndReplay_FallsBackToFullReplay_WhenSnapshotInvalid()
{
    // Arrange
    var orderId = new OrderId(Guid.NewGuid());
    var streamId = new StreamId($"order-{orderId.Value}");

    using var order = new Order();
    order.Place("ORD-001", 1500m);
    await _innerRepository.SaveAsync(order, orderId);

    // Save snapshot at a position that doesn't exist yet
    await _snapshotStore.WriteAsync(
        streamId,
        new StreamPosition(9999),  // Invalid position
        order.State);

    // Act: the decorator is configured with SnapshotLoadingStrategy.ValidateAndReplay
    var result = await _snapshotRepository.LoadAsync(orderId);

    // Assert: validation failed, so it replayed the whole stream instead
    Assert.True(result.IsSuccess);
    using var loadedOrder = result.Value;
    Assert.Equal(1, loadedOrder.Version.Value);
    Assert.True(loadedOrder.State.IsPlaced);
}
```

## Complete Example: Order with Snapshots

```csharp
public class OrderWithSnapshotsExample
{
    private readonly IAggregateRepository<Order, OrderId> _repository;
    private const int SnapshotFrequency = 100;

    public OrderWithSnapshotsExample(
        IEventStore eventStore,
        ISnapshotStore<OrderState> snapshotStore)
    {
        // Setup repository with snapshot decorator. It loads from the latest snapshot and,
        // after each successful save, writes a new one once SnapshotFrequency events have
        // been appended since the last.
        var innerRepository = new AggregateRepository<Order, OrderId>(
            eventStore,
            () => new Order(),
            id => new StreamId($"order-{id.Value}"));
        
        _repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepository: innerRepository,
            snapshotStore: snapshotStore,
            strategy: SnapshotLoadingStrategy.ValidateAndReplay,
            restoreState: (order, state, pos) => order.RestoreState(state, pos),
            eventStore: eventStore,
            streamIdFactory: id => new StreamId($"order-{id.Value}"),
            aggregateFactory: () => new Order(),
            snapshotPolicy: SnapshotPolicy.EveryNEvents(SnapshotFrequency),
            extractState: order => order.State);
    }

    public async Task<OrderId> PlaceOrder(string customerName, decimal total)
    {
        // Create order
        var orderId = new OrderId(Guid.NewGuid());
        using var order = new Order();
        order.Place("ORD-" + DateTime.Now.Ticks, total);

        // Save (and snapshot, if the policy says so)
        var saveResult = await _repository.SaveAsync(order, orderId);
        if (!saveResult.IsSuccess)
            throw new InvalidOperationException($"Save failed: {saveResult.Error}");

        return orderId;
    }

    public async Task ShipOrder(OrderId orderId, string trackingNumber)
    {
        // Load (with snapshot)
        var result = await _repository.LoadAsync(orderId);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Order could not be loaded: {result.Error}");

        using var order = result.Value;

        // Modify
        order.Confirm();
        order.Ship(trackingNumber);

        // Save (and snapshot, if the policy says so)
        var saveResult = await _repository.SaveAsync(order, orderId);
        if (!saveResult.IsSuccess)
            throw new InvalidOperationException($"Save failed: {saveResult.Error}");
    }
}
```

## Performance Impact: Before and After

**Without Snapshots (1,000 event order):**
- Load time: ~100ms
- Database: Single full scan of 1,000 events
- Memory: Deserialize all 1,000 events

**With Snapshots (at position 500):**
- Load time: ~10ms (90% improvement!)
- Database: Single snapshot read + scan 500 events
- Memory: Deserialize 500 events

**Summary:**
- Snapshots provide 5-10x improvement for long streams
- Break-even: ~100-200 events
- Recommended: 500+ events

## Common Mistakes

### Mistake 1: Snapshot Position Out of Sync

```csharp
// ✗ Bad: Position doesn't match event store
await _snapshotStore.WriteAsync(
    streamId,
    new StreamPosition(9999),  // Event store only has 50 events!
    state);

// ✓ Good: take the position and the state from the same loaded aggregate.
// After a load, Version is the position of the last event replayed into State.
using var order = (await _innerRepository.LoadAsync(orderId)).Value;
await _snapshotStore.WriteAsync(streamId, order.Version, order.State);
```

Better still, let the decorator write snapshots on save with `snapshotPolicy` and `extractState`: it uses the position the save actually appended up to.

### Mistake 2: Restoring Onto an Aggregate That Already Has Events

```csharp
// ✗ Bad: the factory hands out the same instance, so the second load restores a snapshot
// on top of events the first load already applied. RestoreState throws InvalidOperationException.
var shared = new Order();
var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    // ...
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    aggregateFactory: () => shared);

// ✓ Good: a new aggregate for every load
var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    // ...
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    aggregateFactory: () => new Order());
```

A missing snapshot needs no handling: the decorator falls back to a full replay through the inner
repository.

### Mistake 3: Forgetting to Save Snapshots

```csharp
// ✗ Bad: Load with snapshots but never save them
var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    // ...
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    aggregateFactory: () => new Order());
// No snapshotPolicy: the stream grows to 1,000 events, but no snapshots!

// ✓ Good: write snapshots periodically on save
var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    // ...
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    aggregateFactory: () => new Order(),
    snapshotPolicy: SnapshotPolicy.EveryNEvents(100),
    extractState: order => order.State);
```

## Summary

Effective snapshot usage requires:

1. **Identify when needed** — 500+ events per stream
2. **Choose a strategy** — ValidateAndReplay for safety, TrustSnapshot for speed
3. **Save periodically** — Every 100-500 events depending on domain
4. **Validate before using** — Check snapshot position exists
5. **Test thoroughly** — Verify snapshot consistency and fallback behavior
6. **Monitor performance** — Measure load time improvement

## Next Steps

- **[Using Projections](./projections-usage.md)** — Build efficient read models
- **[SQL Adapters](./sql-adapters.md)** — Configure PostgreSQL and SQL Server storage
- **[Performance Guide](../performance.md)** — Detailed benchmarking and optimization
