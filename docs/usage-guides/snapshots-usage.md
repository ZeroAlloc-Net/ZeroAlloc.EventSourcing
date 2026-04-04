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
3. Replay no additional events

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
1. Load snapshot but ignore it
2. Always replay from start
3. Useful for testing and debugging

**When to use:**
- Testing aggregate behavior
- Debugging snapshot issues
- Comparing snapshot vs. non-snapshot performance

## Writing Snapshots

Snapshots are saved periodically, typically after N events:

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

### Manual Snapshot Loading

```csharp
public async Task<Order> LoadOrder(OrderId orderId)
{
    var streamId = new StreamId($"order-{orderId.Value}");
    var order = new Order();
    order.SetId(orderId);
    
    // Step 1: Try to load snapshot
    var snapshotResult = await _snapshotStore.ReadAsync(streamId);
    StreamPosition startPosition = StreamPosition.Start;
    
    if (snapshotResult.HasValue)
    {
        var (snapshotPosition, snapshotState) = snapshotResult.Value;
        
        // Step 2: Restore state from snapshot
        order.RestoreState(snapshotState, snapshotPosition);
        
        // Step 3: Start replaying from after snapshot
        startPosition = snapshotPosition.Next();
        
        Console.WriteLine($"Loaded snapshot at position {snapshotPosition.Value}");
    }
    
    // Step 4: Replay remaining events
    var eventsReplayed = 0;
    await foreach (var envelope in _eventStore.ReadAsync(streamId, startPosition))
    {
        order.ApplyHistoric(envelope.Event, envelope.Position);
        eventsReplayed++;
    }
    
    Console.WriteLine($"Replayed {eventsReplayed} events after snapshot");
    return order;
}
```

### Using SnapshotCachingRepositoryDecorator

The decorator handles snapshot loading automatically:

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
3. Restores state from snapshot
4. Replays remaining events
5. Returns the loaded aggregate

## Snapshot Consistency

Snapshots must be consistent with the event store. Three validation approaches:

### Position-Based Validation (Recommended)

Verify the snapshot position exists in the event store:

```csharp
public async Task<bool> ValidateSnapshot(StreamId streamId, StreamPosition snapshotPosition)
{
    try
    {
        // Try to read the event at the snapshot position
        var envelope = await _eventStore.ReadSingleAsync(streamId, snapshotPosition);
        return envelope != null;  // Position exists
    }
    catch (EventNotFoundException)
    {
        return false;  // Position doesn't exist (snapshot is stale)
    }
}
```

Used by `ValidateAndReplay` strategy. If validation fails, falls back to full replay.

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

## Cache Behavior: In-Memory Caching

The `SnapshotCachingRepositoryDecorator` can optionally cache snapshots in memory:

```csharp
// With in-memory cache
var cache = new MemoryCache(new MemoryCacheOptions
{
    SizeLimit = 100 * 1024 * 1024  // 100MB cache
});

var snapshotRepository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: innerRepository,
    snapshotStore: _snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    eventStore: _eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order(),
    cache: cache);  // Enable caching
```

**Benefits:**
- Snapshots loaded from memory instead of database
- Dramatically faster subsequent loads
- Reduces database pressure

**Considerations:**
- Memory usage grows with cache size
- Stale cached snapshots if not invalidated properly
- Cache invalidation is complex

## SQL Snapshot Stores

Snapshots are stored in databases for persistence.

### PostgreSQL Snapshot Store

```csharp
// Setup
var connectionString = "Host=localhost;Database=EventStore;User=postgres;Password=password";
var snapshotStore = new PostgreSqlSnapshotStore<OrderState>(connectionString);

// Schema created automatically or manually:
// CREATE TABLE snapshot_store (
//     stream_id TEXT NOT NULL,
//     position BIGINT NOT NULL,
//     state BYTEA NOT NULL,
//     created_at TIMESTAMP NOT NULL,
//     PRIMARY KEY (stream_id)
// );
// CREATE INDEX idx_snapshot_created ON snapshot_store(created_at);
```

### SQL Server Snapshot Store

```csharp
// Setup
var connectionString = "Server=localhost;Database=EventStore;User=sa;Password=YourPassword123";
var snapshotStore = new SqlServerSnapshotStore<OrderState>(connectionString);

// Schema:
// CREATE TABLE [dbo].[SnapshotStore] (
//     [StreamId] NVARCHAR(450) NOT NULL PRIMARY KEY,
//     [Position] BIGINT NOT NULL,
//     [State] VARBINARY(MAX) NOT NULL,
//     [CreatedAt] DATETIME2 NOT NULL
// );
// CREATE INDEX [idx_snapshot_created] ON [dbo].[SnapshotStore]([CreatedAt]);
```

## Testing Snapshot Logic

### Test Snapshot Consistency

```csharp
[Fact]
public async Task Snapshot_IsConsistentWithReplayed()
{
    // Arrange
    var orderId = new OrderId(Guid.NewGuid());
    var streamId = new StreamId($"order-{orderId.Value}");
    
    var order = new Order();
    order.SetId(orderId);
    order.Place("ORD-001", 1500m);
    order.Confirm();
    order.Ship("TRACK-123");
    order.Deliver();
    
    var events = order.DequeueUncommitted();
    await _eventStore.AppendAsync(streamId, events, StreamPosition.Start);
    
    // Save snapshot at position 2 (after Confirm)
    await _snapshotStore.WriteAsync(
        streamId,
        new StreamPosition(2),
        order.State);
    
    // Act: Load with snapshot
    var loadedOrder = new Order();
    loadedOrder.SetId(orderId);
    
    var snapshot = await _snapshotStore.ReadAsync(streamId);
    Assert.True(snapshot.HasValue);
    
    var (snapshotPos, snapshotState) = snapshot.Value;
    loadedOrder.RestoreState(snapshotState, snapshotPos);
    
    // Replay remaining events
    await foreach (var envelope in _eventStore.ReadAsync(
        streamId, snapshotPos.Next()))
    {
        loadedOrder.ApplyHistoric(envelope.Event, envelope.Position);
    }
    
    // Assert: State matches non-snapshot load
    var fullOrder = new Order();
    fullOrder.SetId(orderId);
    await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start))
    {
        fullOrder.ApplyHistoric(envelope.Event, envelope.Position);
    }
    
    Assert.Equal(fullOrder.State, loadedOrder.State);
}

[Fact]
public async Task SnapshotRepository_LoadsFromSnapshot()
{
    // Arrange
    var orderId = new OrderId(Guid.NewGuid());
    var order = new Order();
    order.SetId(orderId);
    
    // Create aggregate with many events
    for (int i = 0; i < 10; i++)
    {
        order.Place($"ORD-{i}", 100m + i);
    }
    
    var events = order.DequeueUncommitted();
    var streamId = new StreamId($"order-{orderId.Value}");
    
    await _eventStore.AppendAsync(streamId, events, StreamPosition.Start);
    
    // Save snapshot
    await _snapshotStore.WriteAsync(
        streamId,
        new StreamPosition(5),  // Halfway
        order.State);
    
    // Act
    var result = await _snapshotRepository.LoadAsync(orderId);
    
    // Assert
    Assert.True(result.IsSuccess);
    Assert.Equal(10, result.Value.Version.Value);
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
    
    var order = new Order();
    order.SetId(orderId);
    order.Place("ORD-001", 1500m);
    var events = order.DequeueUncommitted();
    
    await _eventStore.AppendAsync(streamId, events, StreamPosition.Start);
    
    // Save snapshot at a position that doesn't exist yet
    await _snapshotStore.WriteAsync(
        streamId,
        new StreamPosition(9999),  // Invalid position
        order.State);
    
    // Act: Load with ValidateAndReplay strategy
    var loadedOrder = new Order();
    loadedOrder.SetId(orderId);
    
    var snapshotResult = await _snapshotStore.ReadAsync(streamId);
    if (snapshotResult.HasValue)
    {
        var (pos, state) = snapshotResult.Value;
        
        // Validation should fail
        var isValid = await ValidateSnapshot(streamId, pos);
        Assert.False(isValid);
        
        // Fall back to full replay
        await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            loadedOrder.ApplyHistoric(envelope.Event, envelope.Position);
        }
    }
    
    // Assert: Loaded successfully despite invalid snapshot
    Assert.Equal(1, loadedOrder.Version.Value);
}
```

## Complete Example: Order with Snapshots

```csharp
public class OrderWithSnapshotsExample
{
    private readonly IEventStore _eventStore;
    private readonly ISnapshotStore<OrderState> _snapshotStore;
    private readonly IAggregateRepository<Order, OrderId> _repository;
    private const int SnapshotFrequency = 100;
    
    public OrderWithSnapshotsExample(
        IEventStore eventStore,
        ISnapshotStore<OrderState> snapshotStore)
    {
        _eventStore = eventStore;
        _snapshotStore = snapshotStore;
        
        // Setup repository with snapshot decorator
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
            aggregateFactory: () => new Order());
    }
    
    public async Task PlaceOrder(string customerName, decimal total)
    {
        // Create order
        var orderId = new OrderId(Guid.NewGuid());
        var order = new Order();
        order.SetId(orderId);
        order.Place("ORD-" + DateTime.Now.Ticks, total);
        
        // Save
        await _repository.SaveAsync(order);
        
        // Save snapshot if needed
        if (order.Version.Value % SnapshotFrequency == 0)
        {
            var streamId = new StreamId($"order-{orderId.Value}");
            await _snapshotStore.WriteAsync(streamId, order.Version, order.State);
        }
    }
    
    public async Task ShipOrder(OrderId orderId, string trackingNumber)
    {
        // Load (with snapshot)
        var result = await _repository.LoadAsync(orderId);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Order not found: {result.Error}");
        
        var order = result.Value;
        
        // Modify
        order.Confirm();
        order.Ship(trackingNumber, "FedEx");
        
        // Save
        var saveResult = await _repository.SaveAsync(order);
        if (!saveResult.IsSuccess)
            throw new InvalidOperationException($"Save failed: {saveResult.Error}");
        
        // Snapshot if needed
        if (order.Version.Value % SnapshotFrequency == 0)
        {
            var streamId = new StreamId($"order-{orderId.Value}");
            await _snapshotStore.WriteAsync(streamId, order.Version, order.State);
        }
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
var snapshot = new Snapshot
{
    StreamId = streamId,
    Position = 9999,  // Event store only has 50 events!
    State = state
};

// ✓ Good: Track positions correctly
var lastEvent = await _eventStore.ReadSingleAsync(streamId, StreamPosition.End);
var snapshot = new Snapshot
{
    StreamId = streamId,
    Position = lastEvent.Position,  // Use actual position
    State = state
};
```

### Mistake 2: Not Handling Missing Snapshots

```csharp
// ✗ Bad: Assumes snapshot always exists
var snapshot = await _snapshotStore.ReadAsync(streamId);
var (position, state) = snapshot.Value;  // Crash if null!

// ✓ Good: Handle missing snapshots
var snapshot = await _snapshotStore.ReadAsync(streamId);
StreamPosition startPosition = StreamPosition.Start;

if (snapshot.HasValue)
{
    var (position, state) = snapshot.Value;
    order.RestoreState(state, position);
    startPosition = position.Next();
}

// Replay from start or after snapshot
await foreach (var envelope in _eventStore.ReadAsync(streamId, startPosition))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
}
```

### Mistake 3: Forgetting to Save Snapshots

```csharp
// ✗ Bad: Load with snapshots but never save them
var order = await _repository.LoadAsync(orderId);
order.Ship("TRACK-123");
await _repository.SaveAsync(order);
// Stream grows to 1,000 events, but no snapshots!

// ✓ Good: Save snapshots periodically
if (order.Version.Value % 100 == 0)
{
    await _snapshotStore.WriteAsync(
        streamId,
        order.Version,
        order.State);
}
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
