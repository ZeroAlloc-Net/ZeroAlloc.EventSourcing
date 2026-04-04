# Snapshot-Optimized Loading Example

## Overview

The `SnapshotCachingRepositoryDecorator` wraps your existing `AggregateRepository` to optimize loading by using snapshots. When loading an aggregate, if a snapshot exists, the decorator restores state from the snapshot and only replays events after that point — significantly reducing replay overhead for long-lived streams.

## Basic Usage

```csharp
// Create inner repository (no snapshot logic)
var innerRepo = new AggregateRepository<Order, OrderId>(
    eventStore,
    () => new Order(),
    id => new StreamId($"order-{id.Value}"));

// Wrap with snapshot decorator
var snapshotRepo = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: innerRepo,
    snapshotStore: new InMemorySnapshotStore<OrderState>(),  // Or PostgreSqlSnapshotStore<OrderState>
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order());

// Use like normal repository
var result = await snapshotRepo.LoadAsync(new OrderId(123));
if (result.IsSuccess)
{
    var order = result.Value;
    // Order is fully loaded with state restored from snapshot
}
```

## Strategies

The decorator supports three strategies for handling snapshots:

### TrustSnapshot (Fastest)

Use when snapshots are immutable and always valid.

```csharp
strategy: SnapshotLoadingStrategy.TrustSnapshot
```

- Assumes snapshot is always up-to-date
- No validation overhead
- Best for high-frequency loads where snapshot invalidation is impossible

### ValidateAndReplay (Safest - Recommended)

Validates snapshot position exists in event store before using.

```csharp
strategy: SnapshotLoadingStrategy.ValidateAndReplay
```

- Protects against corrupted or stale snapshots
- Validates position exists before replay
- Falls back to full replay if position is invalid
- Recommended for production use

### IgnoreSnapshot (Debug/Testing)

Disable snapshot optimization without code changes.

```csharp
strategy: SnapshotLoadingStrategy.IgnoreSnapshot
```

- Always replays from start (no snapshot optimization)
- Useful for testing and debugging
- Allows disabling optimization without code changes

## Configuration in DI Container

```csharp
services
    .AddScoped<IAggregateRepository<Order, OrderId>>(sp =>
        new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepository: new AggregateRepository<Order, OrderId>(
                sp.GetRequiredService<IEventStore>(),
                () => new Order(),
                id => new StreamId($"order-{id.Value}")),
            snapshotStore: sp.GetRequiredService<ISnapshotStore<OrderState>>(),
            strategy: SnapshotLoadingStrategy.ValidateAndReplay,
            restoreState: (o, state, pos) => o.RestoreState(state, pos),
            eventStore: sp.GetRequiredService<IEventStore>(),
            streamIdFactory: id => new StreamId($"order-{id.Value}"),
            aggregateFactory: () => new Order()));
```

## Performance Impact

Snapshots significantly reduce loading time for long-lived streams:

| Stream Length | Without Snapshot | With Snapshot (Position 500) | Improvement |
|---|---|---|---|
| 100 events | 100% replay | 100% replay | No benefit |
| 500 events | 500% replay | Snapshot restore only | 100x faster |
| 1000 events | 1000% replay | ~500% partial replay | 2x faster |
| 5000 events | 5000% replay | ~4500% partial replay | ~10x faster |

Snapshots are most beneficial for high-event-count streams. For low-event-count streams, the snapshot overhead may exceed the benefit.
