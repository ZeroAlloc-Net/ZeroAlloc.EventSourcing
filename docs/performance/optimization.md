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
var repository = new AggregateRepository<Order, OrderId>(
    eventStore,
    () => new Order(),
    id => new StreamId($"order-{id.Value}"));

// Replays all 5000 events
using var order = (await repository.LoadAsync(orderId)).Value;
```

**Solution: Snapshot Every 100 Events**

```csharp
// Writes a snapshot after a save once 100 events have been appended since the last one,
// and loads from the latest snapshot plus the events after it
var snapshotRepository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: repository,
    snapshotStore: snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (o, state, pos) => o.RestoreState(state, pos),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order(),
    snapshotPolicy: SnapshotPolicy.EveryNEvents(100),
    extractState: o => o.State);

// Replays at most the events since the last snapshot
using var order = (await snapshotRepository.LoadAsync(orderId)).Value;
```

**Impact:**
- Without snapshot: 9.5 ms
- With snapshot: 796 ns (read) + 30 μs (load remaining 50 events) = ~31 μs
- **Improvement: 300x faster**

**Trade-off:** Adds ~2.6 μs per 100 events to creation/update operations.

### Strategy 1B: Lazy Snapshot Loading

Don't always snapshot. Only snapshot when aggregate becomes too large:

```csharp
// A policy that only snapshots once a stream is large
public sealed class LargeStreamSnapshotPolicy : ISnapshotPolicy
{
    public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition)
        => currentPosition.Value >= 1_000
        && currentPosition.Value - (lastSnapshotPosition?.Value ?? 0) >= 500;
}

var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: new AggregateRepository<Order, OrderId>(
        eventStore,
        () => new Order(),
        id => new StreamId($"order-{id.Value}")),
    snapshotStore: snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (o, state, pos) => o.RestoreState(state, pos),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order(),
    snapshotPolicy: new LargeStreamSnapshotPolicy(),
    extractState: o => o.State);

// With a snapshot: restores it and replays only the events after its position.
// No snapshot yet: replays from the start.
using var order = (await repository.LoadAsync(orderId)).Value;
```

**When to use:**
- Rarely-updated aggregates (snapshots add overhead)
- Memory-constrained systems (snapshots consume storage)
- Slow snapshot stores (SQL, network-based)

### Strategy 1C: Snapshots on Write, Not on Read

Create snapshots after writing, not before reading:

`SnapshotCachingRepositoryDecorator` does exactly this when it is given a `snapshotPolicy` and
`extractState` (as in Strategy 1A):

```csharp
public async Task SaveOrderAsync(Order order, OrderId orderId)
{
    // 1. Appends the new events with order.OriginalVersion as the expected version
    // 2. On success, writes a snapshot at the new stream version if the policy says so
    var result = await _snapshotRepository.SaveAsync(order, orderId);

    if (result.IsFailure)
        throw new InvalidOperationException($"Save failed: {result.Error}");  // e.g. CONFLICT
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

The examples in this section use these events and read model:

```csharp
public record OrderPlacedEvent(string OrderId, string CustomerId, decimal Total);
public record OrderShippedEvent(string OrderId, DateTimeOffset ShippedAt);
public record CustomerRenamedEvent(string CustomerId, string Name);

public sealed record OrderReadModel(string OrderId, decimal Total, bool IsShipped);
```

### Strategy 3A: Filter Events in Projections

Apply filters to avoid processing irrelevant events. `FilteredProjection<TReadModel>` calls
`IncludeEvent` first and skips `Apply` for the events it rejects:

```csharp
public class OrderProjection : FilteredProjection<ImmutableDictionary<string, OrderReadModel>>
{
    public OrderProjection()
    {
        Current = ImmutableDictionary<string, OrderReadModel>.Empty;
    }

    // Only order events reach Apply; everything else is skipped before any work is done
    protected override bool IncludeEvent(EventEnvelope @event)
        => @event.Event is OrderPlacedEvent or OrderShippedEvent;

    protected override ImmutableDictionary<string, OrderReadModel> Apply(
        ImmutableDictionary<string, OrderReadModel> current,
        EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current.SetItem(e.OrderId, new OrderReadModel(e.OrderId, e.Total, IsShipped: false)),
        OrderShippedEvent e when current.TryGetValue(e.OrderId, out var model) =>
            current.SetItem(e.OrderId, model with { IsShipped = true }),
        _ => current
    };
}
```

**Impact:** Reduces processing overhead by 80-90% when filtering out 90% of events.

### Strategy 3B: Batch Projection Updates

Update read models in batches instead of individually:

```csharp
// INEFFICIENT: Write the read model after each event
public class EveryEventOrderTotalsProjection : Projection<ImmutableDictionary<string, decimal>>
{
    private readonly IProjectionStore _projectionStore;

    public EveryEventOrderTotalsProjection(IProjectionStore projectionStore)
    {
        _projectionStore = projectionStore;
        Current = ImmutableDictionary<string, decimal>.Empty;
    }

    protected override ImmutableDictionary<string, decimal> Apply(
        ImmutableDictionary<string, decimal> current,
        EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e ? current.SetItem(e.OrderId, e.Total) : current;

    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        await base.HandleAsync(@event, ct);
        await _projectionStore.SaveAsync("order-totals", JsonSerializer.Serialize(Current), ct);
    }
}
// EFFICIENT: Batch writes; BatchedProjection calls FlushBatchAsync once per 100 events
public class BatchedOrderTotalsProjection : BatchedProjection<ImmutableDictionary<string, decimal>>
{
    private readonly IProjectionStore _projectionStore;

    public BatchedOrderTotalsProjection(IProjectionStore projectionStore)
        : base(batchSize: 100)
    {
        _projectionStore = projectionStore;
        Current = ImmutableDictionary<string, decimal>.Empty;
    }

    protected override bool IncludeEvent(EventEnvelope @event) => @event.Event is OrderPlacedEvent;

    protected override ImmutableDictionary<string, decimal> Apply(
        ImmutableDictionary<string, decimal> current,
        EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e ? current.SetItem(e.OrderId, e.Total) : current;

    protected override async ValueTask FlushBatchAsync(IReadOnlyList<EventEnvelope> batch, CancellationToken ct = default)
        => await _projectionStore.SaveAsync("order-totals", JsonSerializer.Serialize(Current), ct);
}
```

Call `FlushAsync()` when a run ends, to write the last partial batch.

**Impact:** Reduces write operations by 100x. Trade-off: durability (if system crashes, last batch is lost).

### Strategy 3C: Multiple Specialized Projections

Instead of one monolithic projection, use multiple specialized ones:

```csharp
// MONOLITHIC: One read model for all queries
public sealed record OrderModel(
    string OrderId,
    decimal Total,
    bool IsShipped,
    int LineItemCount,
    DateTimeOffset CreatedAt,
    string CustomerName,
    IReadOnlyList<string> Tags);  // ... 20 more fields

// SPECIALIZED: Multiple projections, each optimized for one query
public class OrderTotalsProjection : Projection<decimal>  // For: "Sum of all orders"
{
    protected override decimal Apply(decimal current, EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e ? current + e.Total : current;
}

public class ShippingProjection : Projection<ImmutableList<(string OrderId, DateTimeOffset ShippedAt)>>  // For: "Which orders shipped today?"
{
    public ShippingProjection() => Current = [];

    protected override ImmutableList<(string OrderId, DateTimeOffset ShippedAt)> Apply(
        ImmutableList<(string OrderId, DateTimeOffset ShippedAt)> current,
        EventEnvelope @event)
        => @event.Event is OrderShippedEvent e ? current.Add((e.OrderId, e.ShippedAt)) : current;
}

public class CustomersProjection : Projection<ImmutableDictionary<string, ImmutableList<string>>>  // For: "Orders by customer"
{
    public CustomersProjection() => Current = ImmutableDictionary<string, ImmutableList<string>>.Empty;

    protected override ImmutableDictionary<string, ImmutableList<string>> Apply(
        ImmutableDictionary<string, ImmutableList<string>> current,
        EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e
            ? current.SetItem(e.CustomerId, (current.GetValueOrDefault(e.CustomerId) ?? []).Add(e.OrderId))
            : current;
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
public async Task ExecuteWithRetryAsync(OrderId orderId, Action<Order> command)
{
    const int MAX_RETRIES = 3;

    for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
    {
        // Load the current version and run the command against it
        using var order = (await _repository.LoadAsync(orderId)).Value;
        command(order);

        // Appends with order.OriginalVersion as the expected version
        var result = await _repository.SaveAsync(order, orderId);
        if (result.IsSuccess)
            return;  // Success

        if (result.Error.Code != "CONFLICT")
            throw new InvalidOperationException(result.Error.ToString());

        // Another writer got there first; back off, then reload and retry
        if (attempt < MAX_RETRIES - 1)
            await Task.Delay(10 * (int)Math.Pow(2, attempt));  // Exponential backoff
    }

    throw new InvalidOperationException("Max retries exceeded");
}
```

A conflict is returned as a failed `Result` with `Error.Code == "CONFLICT"`, not thrown. Retry by
reloading and re-running the command, never by re-saving the same aggregate instance: its events
were decided against a version that is no longer current.

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
/// <summary>
/// Keeps today's orders in memory. One thread feeds events; any number of threads query.
/// Current is an immutable dictionary that each event replaces as a whole, so a query never
/// sees a half-applied update and needs no lock.
/// </summary>
public class InMemoryOrderProjection : Projection<ImmutableDictionary<string, OrderReadModel>>
{
    public InMemoryOrderProjection()
    {
        Current = ImmutableDictionary<string, OrderReadModel>.Empty;
    }

    protected override ImmutableDictionary<string, OrderReadModel> Apply(
        ImmutableDictionary<string, OrderReadModel> current,
        EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current.SetItem(e.OrderId, new OrderReadModel(e.OrderId, e.Total, IsShipped: false)),
        OrderShippedEvent e when current.TryGetValue(e.OrderId, out var model) =>
            current.SetItem(e.OrderId, model with { IsShipped = true }),
        _ => current
    };

    public OrderReadModel? GetOrder(string orderId)
        => Current.GetValueOrDefault(orderId);
}
```

**Use case:** Orders from today (in memory), archived orders (on disk)

### Strategy 5B: Snapshot Compression

Compress snapshot state to reduce storage:

```csharp
// A custom ISnapshotStore<TState> that stores each snapshot as gzip-compressed JSON.
// The dictionary stands in for your storage: a table row, a blob, a cache entry.
public sealed class CompressedSnapshotStore<TState> : ISnapshotStore<TState>
    where TState : struct
{
    private readonly ConcurrentDictionary<string, (StreamPosition Position, byte[] Payload)> _rows = new();
    private readonly JsonTypeInfo<TState> _typeInfo;  // from a JsonSerializerContext: AOT-safe

    public CompressedSnapshotStore(JsonTypeInfo<TState> typeInfo) => _typeInfo = typeInfo;

    public ValueTask WriteAsync(StreamId streamId, StreamPosition position, TState state, CancellationToken ct = default)
    {
        // Compress JSON
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gzip, state, _typeInfo);
        }

        _rows[streamId.Value] = (position, compressed.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask<(StreamPosition Position, TState State)?> ReadAsync(StreamId streamId, CancellationToken ct = default)
    {
        if (!_rows.TryGetValue(streamId.Value, out var row))
            return ValueTask.FromResult<(StreamPosition, TState)?>(null);

        // Decompress
        using var gzip = new GZipStream(new MemoryStream(row.Payload), CompressionMode.Decompress);
        var state = JsonSerializer.Deserialize(gzip, _typeInfo);
        return ValueTask.FromResult<(StreamPosition, TState)?>((row.Position, state));
    }
}
```

**Impact:** 70-80% size reduction for snapshot storage. Trade-off: ~2x slower to compress/decompress.

## 6. Diagnostic and Monitoring

### Strategy 6A: Track Event Replay Time

Monitor how long aggregate loading takes:

```csharp
public class DiagnosticsOrderRepository : IAggregateRepository<Order, OrderId>
{
    private readonly IAggregateRepository<Order, OrderId> _inner;  // e.g. the snapshot decorator
    private readonly ISnapshotStore<OrderState> _snapshotStore;
    private readonly ILogger _logger;

    public DiagnosticsOrderRepository(
        IAggregateRepository<Order, OrderId> inner,
        ISnapshotStore<OrderState> snapshotStore,
        ILogger logger)
    {
        _inner = inner;
        _snapshotStore = snapshotStore;
        _logger = logger;
    }

    public async ValueTask<Result<Order, StoreError>> LoadAsync(OrderId orderId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await _inner.LoadAsync(orderId, ct);
        var totalMs = sw.ElapsedMilliseconds;

        if (result.IsSuccess)
        {
            // Events replayed = version reached minus the snapshot position the load started from
            var snapshot = await _snapshotStore.ReadAsync(new StreamId($"order-{orderId.Value}"), ct);
            var replayed = result.Value.Version.Value - (snapshot?.Position.Value ?? 0);

            _logger.LogInformation(
                "Loaded order {OrderId}: {Total}ms, version {Version}, ~{Replayed} events replayed",
                orderId, totalMs, result.Value.Version.Value, replayed);
        }

        return result;
    }

    public ValueTask<Result<AppendResult, StoreError>> SaveAsync(Order order, OrderId orderId, CancellationToken ct = default)
        => _inner.SaveAsync(order, orderId, ct);
}
```

The `ZeroAlloc.EventSourcing.Telemetry` package records load and save timings for every aggregate
repository without custom code; see [OpenTelemetry Instrumentation](../telemetry.md).

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
// Run several commands against an aggregate, then save once:
// one append per aggregate instead of one per command
while (commandChannel.Reader.TryRead(out var command))
{
    using var order = (await repository.LoadAsync(command.OrderId)).Value;
    order.Execute(command);

    // Appends every event the command raised in a single AppendAsync call
    var saved = await repository.SaveAsync(order, command.OrderId);
    if (saved.IsFailure)
        await HandleFailedSaveAsync(command, saved.Error);  // e.g. retry on CONFLICT
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
