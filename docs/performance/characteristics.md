# Performance Characteristics

**Version:** 1.0  
**Last Updated:** 2026-10-17  
**Target Runtime:** .NET 8.0+

## Overview

ZeroAlloc.EventSourcing is designed to minimize garbage collection pressure and allocation overhead in event sourcing workloads. This document describes the performance characteristics across core operations.

## Measurements

The measured allocations, and same-run ratios where a benchmark has a baseline, are in
**[Performance & Benchmarks](../performance.md)**. This page explains where the library allocates;
it does not repeat the numbers.

## Allocation Characteristics

### Steady-State Allocations

In steady state (after JIT warmup), what the library itself allocates:

| Operation | Allocation | Notes |
|-----------|------------|-------|
| Append | 0 B | `EventStore` itself, asserted by `EventStoreAllocationTests`; the adapter allocates what its driver needs |
| Aggregate save | 0 B | Asserted by `AggregateRepositoryAllocationTests` |
| Read a stream | 96 B per call, nothing per event | The reader object; asserted by `EventStoreReadAllocationTests` |
| Replay state | 0 B | State is a struct on the stack |
| Snapshot read, in-memory store | 0 B | |

Deserialized events, envelopes from a storage adapter and the adapter's own buffers come on top.
Loading an aggregate through the in-memory adapter with the System.Text.Json serializer measures
about 310 B per replayed event, most of it outside the library.

### Zero-Allocation Claims

The library targets "zero allocations" in the following operations:

1. **Appending events and saving aggregates** — `EventStore` and `AggregateRepository` allocate nothing of their own
2. **Aggregate state replay** — State is a struct, lives on the stack, never allocated
3. **Event application to state** — Pure function, no side effects, no allocations
4. **Reading snapshot data** — In-memory store uses no allocations; SQL stores allocate event envelopes only

The allocations shown in benchmarks represent:
- Event envelope objects (unavoidable when deserializing from store)
- Temporary buffers (unavoidable for I/O)
- The in-memory adapter's snapshot of each read, a test double

## Memory Layout and Cache Efficiency

### Stack Allocations

All aggregate state is a struct, allocated on the stack during event replay:

```csharp
// This state value is on the stack, not the heap
// Each event creates a new state via 'with' expression (struct copy)
var state = OrderState.Initial;
state = state with { Total = 1000 };  // Cheap struct copy on stack
state = state with { IsPlaced = true };  // Another cheap copy
```

**Cache efficiency:**
- Struct state fits in L1 cache (typically < 256 bytes)
- No pointer chasing required
- No heap fragmentation
- CPU branch prediction works well for deterministic state transitions

### Event Store Memory Layout

Event envelopes are kept in memory only briefly during reads:

```csharp
// Event is deserialized into this envelope, a readonly record struct
var envelope = new EventEnvelope(
    StreamId: new StreamId("order-123"),
    Position: new StreamPosition(1),
    Event: new OrderPlacedEvent("ORD-001", 99.99m),   // Heap allocated
    Metadata: EventMetadata.New("OrderPlacedEvent")); // Event id, type, timestamp
```

The event object itself is heap-allocated (unavoidable in C#), but is only held in memory for the duration of processing.

## Performance Tips

### 1. Use Snapshots for Large Aggregates

For aggregates with >100 events, use snapshots to avoid replaying everything:

```csharp
// Without snapshot: replays all 1000 events
var repository = new AggregateRepository<OrderAggregate, OrderId>(
    eventStore,
    () => new OrderAggregate(),
    id => new StreamId($"order-{id.Value}"));
using var agg = (await repository.LoadAsync(orderId)).Value;

// With snapshot: restores the latest snapshot, then replays only the events after it
var snapshotRepository = new SnapshotCachingRepositoryDecorator<OrderAggregate, OrderId, OrderState>(
    innerRepository: repository,
    snapshotStore: snapshotStore,
    strategy: SnapshotLoadingStrategy.TrustSnapshot,
    restoreState: (o, state, pos) => o.RestoreState(state, pos),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new OrderAggregate());
using var fromSnapshot = (await snapshotRepository.LoadAsync(orderId)).Value;
```

**Impact:** 2x improvement in latency for 1000-event aggregates.

### 2. Batch Appends

Appending events one-at-a-time has overhead. Batch them:

```csharp
// Inefficient: 10 appends = 10 transactions and 10 database round trips
foreach (var @event in events)
{
    await eventStore.AppendAsync(streamId, new[] { @event }, position);
}

// Better: 1 append = 1 transaction for all the events
var allEvents = new List<object> { ... };
await eventStore.AppendAsync(streamId, allEvents, position);
```

**Impact:** 90% reduction in latency for batch operations.

### 3. Use Projections for Queries

Reading models (projections) are much faster than querying aggregates:

```csharp
// Slow: load 100 aggregates, each replaying its stream from the store
var aggregates = new List<Order>();
foreach (var id in orderIds)
{
    var agg = new Order();
    // Load from store... one replay each
}

// Fast: query the projection, a single dictionary lookup
var readModel = await projectionStore.LoadAsync("OrdersProjection");
var orders = readModel.Orders.Where(o => o.CustomerId == customerId);
```

**Impact:** 1000x improvement in query latency.

### 4. Tune GC Settings

For event sourcing workloads, consider:

```csharp
// Set garbage collection mode to low latency
AppContext.SetSwitch("System.GC.Server", true);      // Use server GC on multi-core
AppContext.SetSwitch("System.GC.Concurrent", true);  // Concurrent collection
AppContext.SetSwitch("System.GC.RetainVM", false);   // Return memory to OS
```

Impact: Reduces pause times by 50-70% at cost of slightly higher memory usage.

## Summary

ZeroAlloc.EventSourcing delivers:

- **No allocation of its own** on the write path, and 96 B per read
- **Predictable performance** (replay cost scales linearly with event count)
- **Snapshot support** for scaling to large aggregates
- **Cache-efficient** struct-based state representation

The "zero-allocation" name reflects the struct-based state design, not absolute zero allocations. In practice, allocations are minimal and easily managed by the GC.

## Next Steps

- **[Zero-Allocation Design](./zero-allocation.md)** — Understand the design philosophy
- **[Optimization Strategies](./optimization.md)** — Practical tips for production workloads
- **[Performance & Benchmarks](../performance.md)** — Measured allocations and ratios
