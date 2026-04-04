# Optimization Strategies

**Version:** 1.0  
**Last Updated:** 2026-04-04

## Introduction

This document provides practical optimization strategies for production ZeroAlloc.EventSourcing systems. It covers aggregate design, snapshot strategies, projection optimization, and batch operations.

## 1. Aggregate Optimization

### Strategy 1A: Snapshot Every N Events

For aggregates with growing event streams, snapshots prevent replay of the entire history:

**Problem:**

```csharp
// Order has 5000 events. Loading takes:
// 30 μs (base) + 5000 × 1.9 μs (replay) = 9,530 μs = 9.5 ms
var order = new Order();
order.SetId(orderId);

await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
}
```

**Solution: Snapshot Every 100 Events**

```csharp
public class OrderSnapshot
{
    public OrderId Id { get; set; }
    public OrderState State { get; set; }
    public StreamPosition Position { get; set; }
}

// Create snapshot after every 100 events
var eventCount = 0;
var state = OrderState.Initial;

await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    state = (OrderState)envelope.Event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        _ => state
    };

    eventCount++;

    // Snapshot every 100 events
    if (eventCount % 100 == 0)
    {
        await snapshotStore.SaveAsync(
            new OrderSnapshot 
            { 
                Id = orderId, 
                State = state, 
                Position = envelope.Position 
            }
        );
    }
}
```

**Impact:**
- Without snapshot: 9.5 ms
- With snapshot: 796 ns (read) + 30 μs (load remaining 50 events) = ~31 μs
- **Improvement: 300x faster**

**Trade-off:** Adds ~2.6 μs per 100 events to creation/update operations.

### Strategy 1B: Lazy Snapshot Loading

Don't always snapshot. Only snapshot when aggregate becomes too large:

```csharp
public static async Task<Order> LoadOrderAsync(
    OrderId orderId,
    IEventStore eventStore,
    ISnapshotStore snapshotStore)
{
    var order = new Order();
    order.SetId(orderId);

    var snapshot = await snapshotStore.LoadAsync(orderId.ToString());
    
    if (snapshot != null)
    {
        // Start from snapshot position, not from start
        var startPosition = snapshot.Position;
        order.LoadSnapshot(snapshot);
    }
    else
    {
        // No snapshot yet, load from start
        var startPosition = StreamPosition.Start;
    }

    // Only replay events after snapshot
    await foreach (var envelope in eventStore.ReadAsync(streamId, startPosition))
    {
        order.ApplyHistoric(envelope.Event, envelope.Position);
    }

    return order;
}
```

**When to use:**
- Rarely-updated aggregates (snapshots add overhead)
- Memory-constrained systems (snapshots consume storage)
- Slow snapshot stores (SQL, network-based)

### Strategy 1C: Snapshots on Write, Not on Read

Create snapshots after writing, not before reading:

```csharp
public async Task SaveOrderAsync(Order order, IEventStore eventStore, ISnapshotStore snapshotStore)
{
    // 1. Append new events
    var result = await eventStore.AppendAsync(streamId, order.DequeueUncommitted(), order.OriginalVersion);
    
    if (!result.IsSuccess)
        throw new OptimisticLockException();

    // 2. Update version tracking
    order.AcceptVersion(result.Value.Position);

    // 3. Snapshot if needed
    if (order.Version % 100 == 0)
    {
        await snapshotStore.SaveAsync(
            new OrderSnapshot 
            { 
                Id = order.Id, 
                State = order.State, 
                Position = order.Version 
            }
        );
    }
}
```

**Benefits:**
- Snapshots created during write operations (off-peak usage)
- Reads don't pay snapshot overhead
- Snapshots always recent when needed

## 2. Event Store Optimization

### Strategy 2A: Batch Event Writes

Never append single events. Batch them:

```csharp
// INEFFICIENT: 5 separate appends
var events = new[] { evt1, evt2, evt3, evt4, evt5 };
foreach (var @event in events)
{
    await eventStore.AppendAsync(streamId, new[] { @event }, position);
    // 5 × 13.76 μs = 68.8 μs
}

// EFFICIENT: Single append
await eventStore.AppendAsync(streamId, events, position);
// ~15 μs (mostly fixed overhead)
```

**Impact:** 5-10x faster for batch operations

**Best practice:** Batch at application level before persisting:

```csharp
public class CommandBatchProcessor
{
    private readonly Queue<(StreamId streamId, IEnumerable<object> events, StreamPosition position)> _batch;

    public async Task ProcessAsync()
    {
        // Collect commands for 100 ms or 1000 commands
        while (await _batchChannel.Reader.WaitToReadAsync(TimeSpan.FromMilliseconds(100)))
        {
            // Batch multiple commands
            var batch = new List<(StreamId, IEnumerable<object>, StreamPosition)>();
            while (_batchChannel.Reader.TryRead(out var item) && batch.Count < 1000)
            {
                batch.Add(item);
            }

            // Append all at once
            foreach (var (streamId, events, position) in batch)
            {
                await eventStore.AppendAsync(streamId, events, position);
            }
        }
    }
}
```

### Strategy 2B: Parallel Reads

For reading multiple aggregates, use parallel streams:

```csharp
// SEQUENTIAL: Load 100 orders = 100 × 30 μs = 3 ms
var orders = new List<Order>();
foreach (var orderId in orderIds)
{
    var order = await LoadOrderAsync(orderId);
    orders.Add(order);
}

// PARALLEL: Load 100 orders in parallel = ~30 μs (load time) (4 tasks at 30 μs each)
var orders = await Task.WhenAll(
    orderIds.Select(id => LoadOrderAsync(id))
);
```

**Caution:** Parallel reads increase memory pressure. Monitor memory usage:

```csharp
// CONTROLLED PARALLEL: Max 10 concurrent loads
var semaphore = new SemaphoreSlim(10);
var orders = await Task.WhenAll(
    orderIds.Select(async id =>
    {
        await semaphore.WaitAsync();
        try
        {
            return await LoadOrderAsync(id);
        }
        finally
        {
            semaphore.Release();
        }
    })
);
```

## 3. Projection Optimization

### Strategy 3A: Filter Events in Projections

Apply filters to avoid processing irrelevant events:

```csharp
public class OrderProjection : Projection
{
    private readonly Dictionary<OrderId, OrderReadModel> _models = new();

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        // Only process order events
        if (envelope.Event is not OrderEvent orderEvent)
            return false;

        // Apply to read model
        switch (orderEvent)
        {
            case OrderPlacedEvent e:
                _models[e.OrderId] = new OrderReadModel { OrderId = e.OrderId, Total = e.Total };
                break;
            case OrderShippedEvent e:
                if (_models.TryGetValue(e.OrderId, out var model))
                    model.IsShipped = true;
                break;
        }

        return true;
    }
}
```

**Impact:** Reduces processing overhead by 80-90% when filtering out 90% of events.

### Strategy 3B: Batch Projection Updates

Update read models in batches instead of individually:

```csharp
// INEFFICIENT: Update after each event
public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
{
    UpdateReadModel(envelope);
    await projectionStore.SaveAsync("order-projection", JsonSerializer.Serialize(_readModel));
    return true;
}

// EFFICIENT: Batch writes
public class BatchedOrderProjection : Projection
{
    private int _eventsSinceLastSave = 0;
    private const int BATCH_SIZE = 100;

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        UpdateReadModel(envelope);
        _eventsSinceLastSave++;

        if (_eventsSinceLastSave >= BATCH_SIZE)
        {
            await projectionStore.SaveAsync("order-projection", JsonSerializer.Serialize(_readModel));
            _eventsSinceLastSave = 0;
        }

        return true;
    }
}
```

**Impact:** Reduces write operations by 100x. Trade-off: durability (if system crashes, last batch is lost).

### Strategy 3C: Multiple Specialized Projections

Instead of one monolithic projection, use multiple specialized ones:

```csharp
// MONOLITHIC: One projection for all queries
public class OrderProjection : Projection
{
    private class OrderModel
    {
        public OrderId Id { get; set; }
        public decimal Total { get; set; }
        public bool IsShipped { get; set; }
        public int LineItemCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CustomerName { get; set; }
        public List<string> Tags { get; set; }
        // ... 20 more fields
    }
}

// SPECIALIZED: Multiple projections, each optimized for one query
public class OrderTotalsProjection : Projection  // For: "Sum of all orders"
{
    private Dictionary<OrderId, decimal> _totals = new();
}

public class ShippingProjection : Projection  // For: "Which orders shipped today?"
{
    private List<(OrderId, DateTime)> _shipped = new();
}

public class CustomersProjection : Projection  // For: "Orders by customer"
{
    private Dictionary<CustomerId, List<OrderId>> _customerOrders = new();
}
```

**Benefits:**
- Each projection is small (faster to apply)
- Only relevant fields are stored (less memory)
- Can rebuild one projection without affecting others
- Better cache locality

## 4. Concurrency Optimization

### Strategy 4A: Optimistic Locking with Retry

When concurrent writes conflict, retry with backoff:

```csharp
public async Task SaveOrderAsync(Order order)
{
    const int MAX_RETRIES = 3;
    
    for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
    {
        try
        {
            var result = await eventStore.AppendAsync(
                streamId, 
                order.DequeueUncommitted(), 
                order.OriginalVersion  // Check position hasn't changed
            );

            if (result.IsSuccess)
            {
                order.AcceptVersion(result.Value.Position);
                return;  // Success
            }
        }
        catch (OptimisticLockException)
        {
            // Version mismatch; reload and retry
            await ReloadOrderAsync(order);
            
            if (attempt < MAX_RETRIES - 1)
                await Task.Delay(10 * (int)Math.Pow(2, attempt));  // Exponential backoff
        }
    }

    throw new ConcurrentWriteException("Max retries exceeded");
}
```

**When to use:**
- High-contention aggregates (same order written by multiple processes)
- Retry logic already in place

### Strategy 4B: Aggregate Partitioning

Partition aggregates by ID to reduce contention:

```csharp
public class PartitionedOrderRepository
{
    private readonly OrderRepository[] _partitions;
    private readonly int _partitionCount;

    public async Task<Order> LoadAsync(OrderId orderId)
    {
        // Select partition based on order ID hash
        var partition = GetPartition(orderId);
        return await partition.LoadAsync(orderId);
    }

    private OrderRepository GetPartition(OrderId orderId)
    {
        var hash = orderId.Value.GetHashCode();
        var index = Math.Abs(hash) % _partitionCount;
        return _partitions[index];
    }
}
```

**Benefits:**
- Reduces contention on individual aggregates
- Enables parallel writes to different partitions
- Improves CPU cache locality

## 5. Memory Optimization

### Strategy 5A: Use In-Memory Projections for Hot Data

Keep frequently-accessed projections in memory:

```csharp
public class InMemoryOrderProjection : IOrderProjection
{
    private readonly ConcurrentDictionary<OrderId, OrderReadModel> _cache = new();

    public async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        switch (envelope.Event)
        {
            case OrderPlacedEvent e:
                _cache[e.OrderId] = new OrderReadModel { Id = e.OrderId, Total = e.Total };
                break;
            case OrderShippedEvent e:
                if (_cache.TryGetValue(e.OrderId, out var model))
                    model.IsShipped = true;
                break;
        }
        return true;
    }

    public OrderReadModel? GetOrder(OrderId id)
    {
        _cache.TryGetValue(id, out var model);
        return model;
    }
}
```

**Use case:** Orders from today (in memory), archived orders (on disk)

### Strategy 5B: Snapshot Compression

Compress snapshot state to reduce storage:

```csharp
public class CompressedSnapshotStore : ISnapshotStore
{
    public async ValueTask SaveAsync(string key, string state, CancellationToken ct = default)
    {
        // Compress JSON
        var bytes = Encoding.UTF8.GetBytes(state);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        var compressedBase64 = Convert.ToBase64String(compressed.ToArray());
        await _inner.SaveAsync(key, compressedBase64, ct);
    }

    public async ValueTask<string?> LoadAsync(string key, CancellationToken ct = default)
    {
        var compressed = await _inner.LoadAsync(key, ct);
        if (compressed == null)
            return null;

        // Decompress
        var bytes = Convert.FromBase64String(compressed);
        using var stream = new MemoryStream(bytes);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return await reader.ReadToEndAsync();
    }
}
```

**Impact:** 70-80% size reduction for snapshot storage. Trade-off: ~2x slower to compress/decompress.

## 6. Diagnostic and Monitoring

### Strategy 6A: Track Event Replay Time

Monitor how long aggregate loading takes:

```csharp
public class DiagnosticsOrderRepository
{
    private readonly IEventStore _eventStore;
    private readonly ISnapshotStore _snapshotStore;

    public async Task<Order> LoadAsync(OrderId orderId)
    {
        var sw = Stopwatch.StartNew();

        var order = new Order();
        order.SetId(orderId);

        // Track snapshot load time
        var snapshotSw = Stopwatch.StartNew();
        var snapshot = await _snapshotStore.LoadAsync(orderId.ToString());
        var snapshotLoadMs = snapshotSw.ElapsedMilliseconds;

        var startPosition = snapshot?.Position ?? StreamPosition.Start;

        if (snapshot != null)
            order.LoadSnapshot(snapshot);

        // Track replay time
        var replaySw = Stopwatch.StartNew();
        var eventCount = 0;

        await foreach (var envelope in _eventStore.ReadAsync(streamId, startPosition))
        {
            order.ApplyHistoric(envelope.Event, envelope.Position);
            eventCount++;
        }

        var replayMs = replaySw.ElapsedMilliseconds;

        var totalMs = sw.ElapsedMilliseconds;

        // Log diagnostics
        _logger.LogInformation(
            "Loaded order {OrderId}: {Total}ms total, {Snapshot}ms snapshot, {Replay}ms replay ({EventCount} events)",
            orderId, totalMs, snapshotLoadMs, replayMs, eventCount
        );

        return order;
    }
}
```

**Metrics to track:**
- Snapshot load time (should be <1 ms)
- Replay time (should scale linearly with event count)
- Event count (if growing, snapshots needed)

### Strategy 6B: GC Pressure Monitoring

Monitor garbage collection impact:

```csharp
public class GcMonitor
{
    private long _gen0Collections;
    private long _gen1Collections;
    private long _gen2Collections;

    public void Start()
    {
        _gen0Collections = GC.CollectionCount(0);
        _gen1Collections = GC.CollectionCount(1);
        _gen2Collections = GC.CollectionCount(2);
    }

    public void Report(string operation)
    {
        var gen0Now = GC.CollectionCount(0);
        var gen1Now = GC.CollectionCount(1);
        var gen2Now = GC.CollectionCount(2);

        _logger.LogInformation(
            "{Operation}: GC collections: Gen0={Gen0} Gen1={Gen1} Gen2={Gen2}",
            operation,
            gen0Now - _gen0Collections,
            gen1Now - _gen1Collections,
            gen2Now - _gen2Collections
        );
    }
}
```

## 7. Workload-Specific Patterns

### High-Throughput Write Pattern

For systems writing millions of events/sec:

```csharp
// Batch writes + minimal snapshot overhead
var batch = new List<(StreamId, IEnumerable<object>, StreamPosition)>();

while (commandChannel.Reader.TryRead(out var command) && batch.Count < 1000)
{
    var order = await LoadOrderAsync(command.OrderId);
    order.Execute(command);
    batch.Add((streamId, order.DequeueUncommitted(), order.OriginalVersion));
}

foreach (var (streamId, events, position) in batch)
{
    await eventStore.AppendAsync(streamId, events, position);
}
```

### Read-Heavy Pattern

For systems with many reads, few writes:

```csharp
// Keep projections in memory
var projection = new InMemoryOrderProjection();

// Rebuild projection once on startup
await projection.RebuildAsync(eventStore);

// Queries are <1 μs
var order = projection.GetOrder(orderId);
```

### Mixed Pattern

For systems with both reads and writes:

```csharp
// Snapshot every 100 events
// Batch writes every 10 ms or 100 events
// Keep read models in memory
// Rebuild projections on startup
```

## Summary

Key optimization strategies:

1. **Snapshots** — For large aggregates (>100 events)
2. **Batching** — For high-throughput writes
3. **Filtering** — For projections (only process relevant events)
4. **Parallel reads** — For loading multiple aggregates
5. **In-memory projections** — For hot data
6. **Monitoring** — Track replay time and GC impact

Apply optimizations in priority order:

1. **Profiling first** — Identify bottlenecks before optimizing
2. **Snapshots** — Often the biggest win (300x faster)
3. **Batching** — Next level (5-10x faster)
4. **Parallelization** — For concurrent workloads (CPU-bound)
5. **Memory tuning** — For GC-sensitive workloads

Don't optimize prematurely. Start with the simple approach, measure, then apply strategies as needed.

## Next Steps

- **[Performance Characteristics](./characteristics.md)** — Detailed latency/throughput data
- **[Benchmark Results](./benchmarks.md)** — Raw benchmark data and analysis
- **[Usage Guide: Domain Modeling](../usage-guides/domain-modeling.md)** — Practical patterns
