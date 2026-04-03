# Phase 7: Advanced Projections & End-to-End Integration Tests — Design Document

**Date:** 2026-04-04  
**Status:** Approved

---

## Overview

Extend projections with production features (filtering, rebuilding, consistency checks, batching) and validate the entire system (Phases 1-6) through comprehensive end-to-end integration tests.

**Goals:**
1. Enable advanced projection patterns (filtering, rebuilding, consistency validation)
2. Battle-test entire event sourcing stack with real databases
3. Prove snapshots, projections, and aggregates work together

---

## Architecture

### Components Overview

```
Projection<TReadModel> (Phase 5)
  ├─ FilteredProjection<TReadModel> (NEW — Phase 7a)
  ├─ ReplayableProjection<TReadModel> (NEW — Phase 7b)
  └─ ConsistencyCheckedProjection<TReadModel> (NEW — Phase 7c)

IProjectionStore<TReadModel> (NEW — Phase 7b)
  ├─ InMemoryProjectionStore<TReadModel> (Phase 7b)
  └─ [SQL implementations deferred to Phase 7+]

BatchedProjection<TReadModel> (NEW — Phase 7d)

Integration Tests Suite (Phase 7e)
  ├─ Basic scenarios (load → snapshot → rebuild)
  ├─ Complex workflows (multi-aggregate, filtering, consistency)
  └─ Production scenarios (concurrency, error recovery)
```

---

## Components

### 7a) FilteredProjection<TReadModel>

**Purpose:** Process only a subset of events.

**API:**

```csharp
public abstract class FilteredProjection<TReadModel> : Projection<TReadModel>
{
    /// <summary>
    /// Override to filter which events trigger Apply().
    /// Return true to process the event, false to skip.
    /// </summary>
    protected abstract bool IncludeEvent(EventEnvelope @event);

    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        if (IncludeEvent(@event))
        {
            Current = Apply(Current, @event);
        }
        
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
```

**Example:**

```csharp
public class OrderProjection : FilteredProjection<OrderSummary>
{
    protected override bool IncludeEvent(EventEnvelope @event) =>
        @event.Event switch
        {
            OrderPlaced => true,
            OrderShipped => true,
            OrderCancelled => true,
            _ => false,  // Ignore other events
        };

    protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event) =>
        @event.Event switch
        {
            OrderPlaced o => new OrderSummary(o.OrderId, o.Amount),
            OrderShipped s => current with { Status = "Shipped" },
            _ => current,
        };
}
```

### 7b) Projection Rebuilding & State Persistence

**IProjectionStore<TReadModel>:**

```csharp
public interface IProjectionStore<TReadModel>
{
    /// <summary>
    /// Saves the projection's current read model state.
    /// </summary>
    ValueTask SaveAsync(
        string projectionKey,
        TReadModel state,
        StreamPosition lastProcessedPosition,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the projection's last saved state and checkpoint.
    /// </summary>
    ValueTask<(TReadModel State, StreamPosition Checkpoint)?> LoadAsync(
        string projectionKey,
        CancellationToken ct = default);

    /// <summary>
    /// Clears the projection state (for rebuild).
    /// </summary>
    ValueTask ClearAsync(string projectionKey, CancellationToken ct = default);
}
```

**InMemoryProjectionStore<TReadModel>:**

```csharp
public sealed class InMemoryProjectionStore<TReadModel> : IProjectionStore<TReadModel>
{
    private readonly ConcurrentDictionary<string, (TReadModel, StreamPosition)> _store = new();

    public ValueTask SaveAsync(
        string projectionKey,
        TReadModel state,
        StreamPosition lastProcessedPosition,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _store[projectionKey] = (state, lastProcessedPosition);
        return ValueTask.CompletedTask;
    }

    public ValueTask<(TReadModel, StreamPosition)?> LoadAsync(
        string projectionKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = _store.TryGetValue(projectionKey, out var result);
        return ValueTask.FromResult(found ? result : null);
    }

    public ValueTask ClearAsync(string projectionKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _store.TryRemove(projectionKey, out _);
        return ValueTask.CompletedTask;
    }
}
```

**Rebuilding Pattern:**

```csharp
public sealed class RebuildableProjection<TReadModel> : FilteredProjection<TReadModel>
{
    private readonly IProjectionStore<TReadModel> _store;
    private readonly string _projectionKey;
    private StreamPosition _checkpoint;

    public async ValueTask RebuildAsync(
        StreamId streamId,
        IEventStore eventStore,
        CancellationToken ct = default)
    {
        // Clear projection state
        await _store.ClearAsync(_projectionKey, ct);
        Current = default!;
        _checkpoint = StreamPosition.Start;

        // Replay all events
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start, ct))
        {
            await HandleAsync(envelope, ct);
            _checkpoint = envelope.Position;
        }

        // Save final state
        await _store.SaveAsync(_projectionKey, Current, _checkpoint, ct);
    }
}
```

### 7c) Projection State Consistency Checks

**Purpose:** Detect gaps or mismatches between projection state and event store.

**IProjectionConsistencyChecker<TReadModel>:**

```csharp
public interface IProjectionConsistencyChecker<TReadModel>
{
    /// <summary>
    /// Verifies projection is up-to-date with event store.
    /// Returns discrepancy if any.
    /// </summary>
    ValueTask<ProjectionConsistencyReport?> VerifyAsync(
        string projectionKey,
        StreamId streamId,
        IEventStore eventStore,
        CancellationToken ct = default);
}

public record ProjectionConsistencyReport(
    bool IsConsistent,
    StreamPosition ProjectionCheckpoint,
    StreamPosition EventStoreLatestPosition,
    string? DiscrepancyReason);
```

**Implementation:**

```csharp
public sealed class ProjectionConsistencyChecker<TReadModel> : IProjectionConsistencyChecker<TReadModel>
{
    private readonly IProjectionStore<TReadModel> _projectionStore;
    private readonly IEventStore _eventStore;

    public async ValueTask<ProjectionConsistencyReport?> VerifyAsync(
        string projectionKey,
        StreamId streamId,
        IEventStore eventStore,
        CancellationToken ct = default)
    {
        // Load projection checkpoint
        var projectionState = await _projectionStore.LoadAsync(projectionKey, ct);
        if (projectionState is null)
            return null;  // No projection to check

        var (_, projectionCheckpoint) = projectionState.Value;

        // Find latest event in stream
        StreamPosition latestPosition = StreamPosition.Start;
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start, ct))
            latestPosition = envelope.Position;

        var isConsistent = projectionCheckpoint.Value >= latestPosition.Value;
        
        return new ProjectionConsistencyReport(
            IsConsistent: isConsistent,
            ProjectionCheckpoint: projectionCheckpoint,
            EventStoreLatestPosition: latestPosition,
            DiscrepancyReason: isConsistent ? null : 
                $"Projection behind by {latestPosition.Value - projectionCheckpoint.Value} events");
    }
}
```

### 7d) Batched Event Processing

**Purpose:** Collect events, apply in batches (useful for expensive operations).

**BatchedProjection<TReadModel>:**

```csharp
public abstract class BatchedProjection<TReadModel> : FilteredProjection<TReadModel>
{
    private readonly int _batchSize;
    private readonly List<EventEnvelope> _batch = new();

    protected BatchedProjection(int batchSize = 100) => _batchSize = batchSize;

    /// <summary>
    /// Called when batch is full or stream ends.
    /// Override to implement batch processing logic.
    /// </summary>
    protected abstract ValueTask FlushBatchAsync(IReadOnlyList<EventEnvelope> events, CancellationToken ct);

    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IncludeEvent(@event))
        {
            _batch.Add(@event);
            Current = Apply(Current, @event);

            if (_batch.Count >= _batchSize)
            {
                await FlushBatchAsync(_batch, ct);
                _batch.Clear();
            }
        }
    }

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        if (_batch.Count > 0)
        {
            await FlushBatchAsync(_batch, ct);
            _batch.Clear();
        }
    }
}
```

**Example: Batch Database Writes**

```csharp
public class OrderReportProjection : BatchedProjection<OrderReport>
{
    private readonly IOrderReportRepository _reportRepo;

    protected override async ValueTask FlushBatchAsync(
        IReadOnlyList<EventEnvelope> events,
        CancellationToken ct)
    {
        // Batch write to database
        await _reportRepo.UpsertManyAsync(
            events.Select(e => CreateReport(e.Event)),
            ct);
    }
}
```

---

## 7e) End-to-End Integration Tests

### Test Scenarios

**Basic Scenarios:**
1. `LoadAggregate_WithSnapshot_SkipsOldEvents` — Verify Phase 5.1 works
2. `SaveSnapshot_ThenRebuild_RestoresState` — Verify Phase 6 works
3. `ApplyProjection_FiltersEvents` — Verify Phase 7a works

**Complex Workflows:**
1. `OrderWorkflow_LoadAggregate_ApplyEvents_SaveSnapshot_RebuildProjection` — Full cycle
2. `MultiAggregate_CorrelatedEvents_ProjectionConsistency` — Order + Payment aggregate
3. `FilteredProjection_ReceivesOnlyMatchingEvents` — Filtering works correctly

**Production Scenarios:**
1. `ConcurrentAppends_SnapshotConsistency` — Concurrent writes don't corrupt snapshots
2. `ProjectionRebuild_CatchesUpToLatestEvents` — Rebuild completes successfully
3. `FailedSnapshot_FallsBackToFullReplay` — Error handling works
4. `LargeStream_SnapshotReducesReplayTime` — Performance benefit verified

### Test Infrastructure

**Databases Tested:**
- **In-Memory:** Fast feedback loop (default)
- **PostgreSQL:** Via Testcontainers (slow, comprehensive)
- **SQL Server:** Via Testcontainers (slow, comprehensive)

**Test Organization:**

```
IntegrationTests/
  ├─ BasicWorkflowTests.cs (3 tests, in-memory + SQL)
  ├─ ComplexWorkflowTests.cs (3 tests, in-memory + SQL)
  ├─ ProductionScenarioTests.cs (4 tests, in-memory + SQL)
  ├─ PerformanceTests.cs (snapshot vs. full replay benchmarks)
  └─ Fixtures/
      ├─ OrderAggregate.cs
      ├─ OrderProjection.cs
      ├─ PaymentAggregate.cs
      └─ MultiAggregateWorkflow.cs
```

**Example Test:**

```csharp
[Theory]
[InlineData("InMemory")]
[InlineData("PostgreSQL")]
[InlineData("SqlServer")]
public async Task OrderWorkflow_LoadWithSnapshot_ReplayFromSnapshot(string database)
{
    // Arrange
    var aggregate = new Order();
    aggregate.PlaceOrder(new OrderId(1), 100m);
    aggregate.ShipOrder("TRACK-123");

    var repo = CreateRepository(database);
    var snapshotStore = CreateSnapshotStore(database);

    // Act 1: Save original aggregate
    await repo.SaveAsync(aggregate, new OrderId(1));

    // Act 2: Take snapshot at position 2
    await snapshotStore.WriteAsync(
        new StreamId("order-1"),
        new StreamPosition(2),
        aggregate.State);

    // Act 3: Load with snapshot
    var loaded = await repo.LoadAsync(new OrderId(1));

    // Assert
    loaded.Value.State.Status.Should().Be(OrderStatus.Shipped);
    loaded.Value.OriginalVersion.Value.Should().Be(2);
}
```

---

## Data Model: Domain Test Fixtures

**Order Aggregate:**
- `PlaceOrder(OrderId, decimal amount)`
- `ShipOrder(string trackingCode)`
- `CancelOrder(string reason)`

**Payment Aggregate:**
- `AuthorizePayment(string cardToken, decimal amount)`
- `CapturePayment()`
- `RefundPayment(decimal refundAmount)`

**Correlations:**
- Order.OrderId → Payment.OrderId
- PaymentAuthorized event includes OrderId
- ProjectionBuilder tracks correlations

---

## Success Criteria

- ✅ FilteredProjection filters events correctly
- ✅ RebuildableProjection clears and replays state
- ✅ ProjectionConsistencyChecker detects gaps
- ✅ BatchedProjection flushes at batch size
- ✅ All test scenarios pass (in-memory + PostgreSQL + SQL Server)
- ✅ Performance tests show snapshot benefit
- ✅ Error recovery works (snapshot failure, rebuild failure)
- ✅ Concurrent operations are safe
