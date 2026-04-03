# Phase 7: Advanced Projections & End-to-End Integration Tests — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Extend projections with production features (filtering, rebuilding, consistency validation, batching) and battle-test the entire event sourcing stack with comprehensive integration tests.

**Architecture:** Five advanced projection types (FilteredProjection, ReplayableProjection, ConsistencyCheckedProjection, BatchedProjection) layering on Phase 5's base Projection, combined with IProjectionStore for state persistence. End-to-end integration tests validate Phases 1-6 working together using real databases (InMemory, PostgreSQL, SQL Server).

**Tech Stack:** Projection<TReadModel> from Phase 5, IEventStore, ISnapshotStore from Phases 5.1/6, Testcontainers for database testing, EventEnvelope pattern.

---

## Task 1: Implement FilteredProjection<TReadModel>

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/FilteredProjection.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/FilteredProjectionTests.cs`

**Step 1: Write unit tests (TDD)**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public sealed class FilteredProjectionTests
{
    [Fact]
    public async Task HandleAsync_IncludedEvent_AppliesChange()
    {
        // Arrange
        var projection = new TestFilteredProjection();
        var @event = new EventEnvelope(
            new StreamId("test"),
            new StreamPosition(1),
            new TestIncludedEvent { Value = 100 },
            EventMetadata.Default);

        // Act
        await projection.HandleAsync(@event);

        // Assert
        projection.Current.Value.Should().Be(100);
    }

    [Fact]
    public async Task HandleAsync_ExcludedEvent_SkipsChange()
    {
        // Arrange
        var projection = new TestFilteredProjection();
        projection.Current = new TestReadModel { Value = 50 };
        var @event = new EventEnvelope(
            new StreamId("test"),
            new StreamPosition(1),
            new TestExcludedEvent(),
            EventMetadata.Default);

        // Act
        await projection.HandleAsync(@event);

        // Assert
        projection.Current.Value.Should().Be(50); // Unchanged
    }

    [Fact]
    public async Task HandleAsync_MultipleIncluded_AppliesSequentially()
    {
        // Arrange
        var projection = new TestFilteredProjection();

        // Act
        await projection.HandleAsync(new EventEnvelope(
            new StreamId("test"), new StreamPosition(1),
            new TestIncludedEvent { Value = 100 }, EventMetadata.Default));
        await projection.HandleAsync(new EventEnvelope(
            new StreamId("test"), new StreamPosition(2),
            new TestIncludedEvent { Value = 50 }, EventMetadata.Default));

        // Assert
        projection.Current.Value.Should().Be(150); // 100 + 50
    }

    private class TestFilteredProjection : FilteredProjection<TestReadModel>
    {
        protected override bool IncludeEvent(EventEnvelope @event) =>
            @event.Event is TestIncludedEvent;

        protected override TestReadModel Apply(TestReadModel current, EventEnvelope @event) =>
            @event.Event switch
            {
                TestIncludedEvent e => new TestReadModel { Value = current.Value + e.Value },
                _ => current
            };
    }

    private record TestReadModel { public int Value { get; set; } }
    private record TestIncludedEvent { public int Value { get; set; } }
    private record TestExcludedEvent;
}
```

**Step 2: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/FilteredProjectionTests.cs -v normal`
Expected: FAIL (class doesn't exist)

**Step 3: Implement FilteredProjection<TReadModel>**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Projection that filters which events are processed.
/// Only events matching IncludeEvent predicate are applied.
/// </summary>
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

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/FilteredProjectionTests.cs -v normal`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/FilteredProjection.cs tests/ZeroAlloc.EventSourcing.Tests/FilteredProjectionTests.cs
git commit -m "feat: implement FilteredProjection for selective event processing"
```

---

## Task 2: Implement IProjectionStore<TReadModel> and InMemoryProjectionStore<TReadModel>

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/IProjectionStore.cs`
- Create: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryProjectionStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemoryProjectionStoreTests.cs`

**Step 1: Write unit tests**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public sealed class InMemoryProjectionStoreTests
{
    [Fact]
    public async Task SaveAsync_ThenLoadAsync_ReturnsState()
    {
        // Arrange
        var store = new InMemoryProjectionStore<TestProjectionState>();
        var state = new TestProjectionState { Total = 100 };
        var position = new StreamPosition(5);

        // Act
        await store.SaveAsync("proj-1", state, position);
        var result = await store.LoadAsync("proj-1");

        // Assert
        result.Should().NotBeNull();
        result.Value.State.Total.Should().Be(100);
        result.Value.Checkpoint.Should().Be(position);
    }

    [Fact]
    public async Task LoadAsync_NoState_ReturnsNull()
    {
        // Arrange
        var store = new InMemoryProjectionStore<TestProjectionState>();

        // Act
        var result = await store.LoadAsync("missing");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_MultipleTimes_LastOneWins()
    {
        // Arrange
        var store = new InMemoryProjectionStore<TestProjectionState>();

        // Act
        await store.SaveAsync("proj-1", new TestProjectionState { Total = 50 }, new StreamPosition(2));
        await store.SaveAsync("proj-1", new TestProjectionState { Total = 150 }, new StreamPosition(5));
        var result = await store.LoadAsync("proj-1");

        // Assert
        result.Value.State.Total.Should().Be(150);
        result.Value.Checkpoint.Should().Be(new StreamPosition(5));
    }

    [Fact]
    public async Task ClearAsync_RemovesState()
    {
        // Arrange
        var store = new InMemoryProjectionStore<TestProjectionState>();
        await store.SaveAsync("proj-1", new TestProjectionState { Total = 100 }, new StreamPosition(1));

        // Act
        await store.ClearAsync("proj-1");
        var result = await store.LoadAsync("proj-1");

        // Assert
        result.Should().BeNull();
    }

    private record TestProjectionState { public int Total { get; set; } }
}
```

**Step 2: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemoryProjectionStoreTests.cs -v normal`
Expected: FAIL

**Step 3: Implement IProjectionStore interface**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Stores and retrieves projection state checkpoints.
/// </summary>
public interface IProjectionStore<TReadModel>
{
    /// <summary>
    /// Saves the projection's current read model state and checkpoint.
    /// </summary>
    ValueTask SaveAsync(
        string projectionKey,
        TReadModel state,
        StreamPosition lastProcessedPosition,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the projection's last saved state and checkpoint.
    /// Returns null if no state exists for the projection key.
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

**Step 4: Implement InMemoryProjectionStore<TReadModel>**

```csharp
using System.Collections.Concurrent;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// In-memory implementation of IProjectionStore for testing and simple scenarios.
/// </summary>
public sealed class InMemoryProjectionStore<TReadModel> : IProjectionStore<TReadModel>
{
    private readonly ConcurrentDictionary<string, (TReadModel State, StreamPosition Position)> _store = new();

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

    public ValueTask<(TReadModel State, StreamPosition Checkpoint)?> LoadAsync(
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

**Step 5: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemoryProjectionStoreTests.cs -v normal`
Expected: PASS (4 tests)

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/IProjectionStore.cs src/ZeroAlloc.EventSourcing.InMemory/InMemoryProjectionStore.cs tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemoryProjectionStoreTests.cs
git commit -m "feat: add IProjectionStore interface and InMemoryProjectionStore implementation"
```

---

## Task 3: Implement ReplayableProjection with Rebuilding

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/ReplayableProjection.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/ReplayableProjectionTests.cs`

**Step 1: Write unit tests**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public sealed class ReplayableProjectionTests : IAsyncLifetime
{
    private InMemoryEventStoreAdapter _adapter = null!;
    private InMemoryEventStore _eventStore = null!;
    private InMemoryProjectionStore<TestProjectionState> _projectionStore = null!;

    public async Task InitializeAsync()
    {
        _adapter = new InMemoryEventStoreAdapter();
        _eventStore = new InMemoryEventStore(_adapter);
        _projectionStore = new InMemoryProjectionStore<TestProjectionState>();
    }

    public async Task DisposeAsync() { }

    [Fact]
    public async Task RebuildAsync_StartsFromBeginning_ReprocessesAllEvents()
    {
        // Arrange
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, 0, new[] {
            new TestEvent { Value = 10 },
            new TestEvent { Value = 20 },
            new TestEvent { Value = 30 }
        });

        var projection = new TestReplayableProjection(_projectionStore, _eventStore);

        // Act
        await projection.RebuildAsync(streamId);

        // Assert
        var saved = await _projectionStore.LoadAsync("test-proj");
        saved.Should().NotBeNull();
        saved.Value.State.Total.Should().Be(60); // 10+20+30
    }

    [Fact]
    public async Task RebuildAsync_ClearsExistingState_StartsOver()
    {
        // Arrange
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, 0, new[] {
            new TestEvent { Value = 100 }
        });

        var projection = new TestReplayableProjection(_projectionStore, _eventStore);

        // Save old state
        await _projectionStore.SaveAsync("test-proj", new TestProjectionState { Total = 999 }, new StreamPosition(1));

        // Act
        await projection.RebuildAsync(streamId);

        // Assert
        var saved = await _projectionStore.LoadAsync("test-proj");
        saved.Value.State.Total.Should().Be(100); // Old state cleared and rebuilt
    }

    private class TestReplayableProjection : ReplayableProjection<TestProjectionState>
    {
        public TestReplayableProjection(IProjectionStore<TestProjectionState> store, IEventStore eventStore)
            : base(store, "test-proj", eventStore) { }

        protected override TestProjectionState Apply(TestProjectionState current, EventEnvelope @event) =>
            @event.Event switch
            {
                TestEvent e => new TestProjectionState { Total = current.Total + e.Value },
                _ => current
            };
    }

    private record TestProjectionState { public int Total { get; set; } }
    private record TestEvent { public int Value { get; set; } }
}
```

**Step 2: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/ReplayableProjectionTests.cs -v normal`
Expected: FAIL

**Step 3: Implement ReplayableProjection**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Projection that can rebuild from scratch by replaying all events.
/// Saves state to IProjectionStore after rebuilding.
/// </summary>
public abstract class ReplayableProjection<TReadModel> : Projection<TReadModel>
{
    private readonly IProjectionStore<TReadModel> _store;
    private readonly string _projectionKey;
    private readonly IEventStore _eventStore;

    protected ReplayableProjection(
        IProjectionStore<TReadModel> store,
        string projectionKey,
        IEventStore eventStore)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projectionKey);
        ArgumentNullException.ThrowIfNull(eventStore);

        _store = store;
        _projectionKey = projectionKey;
        _eventStore = eventStore;
    }

    /// <summary>
    /// Rebuilds projection by clearing state and replaying all events from stream.
    /// </summary>
    public async ValueTask RebuildAsync(StreamId streamId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Clear existing state
        await _store.ClearAsync(_projectionKey, ct).ConfigureAwait(false);
        Current = default!;

        // Replay all events
        var position = StreamPosition.Start;
        await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start, ct).ConfigureAwait(false))
        {
            Current = Apply(Current, envelope);
            position = envelope.Position;
        }

        // Save final state
        await _store.SaveAsync(_projectionKey, Current, position, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/ReplayableProjectionTests.cs -v normal`
Expected: PASS (2 tests)

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/ReplayableProjection.cs tests/ZeroAlloc.EventSourcing.Tests/ReplayableProjectionTests.cs
git commit -m "feat: implement ReplayableProjection with state persistence and rebuilding"
```

---

## Task 4: Implement ProjectionConsistencyChecker

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/ProjectionConsistencyChecker.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/ProjectionConsistencyCheckerTests.cs`

**Step 1: Write unit tests**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public sealed class ProjectionConsistencyCheckerTests : IAsyncLifetime
{
    private InMemoryEventStoreAdapter _adapter = null!;
    private InMemoryEventStore _eventStore = null!;
    private InMemoryProjectionStore<TestProjectionState> _projectionStore = null!;
    private ProjectionConsistencyChecker<TestProjectionState> _checker = null!;

    public async Task InitializeAsync()
    {
        _adapter = new InMemoryEventStoreAdapter();
        _eventStore = new InMemoryEventStore(_adapter);
        _projectionStore = new InMemoryProjectionStore<TestProjectionState>();
        _checker = new ProjectionConsistencyChecker<TestProjectionState>(_projectionStore, _eventStore);
    }

    public async Task DisposeAsync() { }

    [Fact]
    public async Task VerifyAsync_NoProjection_ReturnsNull()
    {
        // Act
        var result = await _checker.VerifyAsync("missing", new StreamId("test"));

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_ProjectionUpToDate_ReturnsConsistent()
    {
        // Arrange
        var streamId = new StreamId("test");
        await _eventStore.AppendAsync(streamId, 0, new[] { new TestEvent { Value = 10 } });
        await _projectionStore.SaveAsync("proj-1", new TestProjectionState { Total = 10 }, new StreamPosition(1));

        // Act
        var result = await _checker.VerifyAsync("proj-1", streamId);

        // Assert
        result.Should().NotBeNull();
        result.Value.IsConsistent.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_ProjectionBehind_ReturnsInconsistent()
    {
        // Arrange
        var streamId = new StreamId("test");
        await _eventStore.AppendAsync(streamId, 0, new[] {
            new TestEvent { Value = 10 },
            new TestEvent { Value = 20 }
        });
        await _projectionStore.SaveAsync("proj-1", new TestProjectionState { Total = 10 }, new StreamPosition(1));

        // Act
        var result = await _checker.VerifyAsync("proj-1", streamId);

        // Assert
        result.Should().NotBeNull();
        result.Value.IsConsistent.Should().BeFalse();
        result.Value.DiscrepancyReason.Should().Contain("behind");
    }

    private record TestProjectionState { public int Total { get; set; } }
    private record TestEvent { public int Value { get; set; } }
}
```

**Step 2: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/ProjectionConsistencyCheckerTests.cs -v normal`
Expected: FAIL

**Step 3: Implement types and checker**

```csharp
namespace ZeroAlloc.EventSourcing;

public record ProjectionConsistencyReport(
    bool IsConsistent,
    StreamPosition ProjectionCheckpoint,
    StreamPosition EventStoreLatestPosition,
    string? DiscrepancyReason);

/// <summary>
/// Validates that projection state is consistent with event store.
/// </summary>
public sealed class ProjectionConsistencyChecker<TReadModel>
{
    private readonly IProjectionStore<TReadModel> _projectionStore;
    private readonly IEventStore _eventStore;

    public ProjectionConsistencyChecker(
        IProjectionStore<TReadModel> projectionStore,
        IEventStore eventStore)
    {
        ArgumentNullException.ThrowIfNull(projectionStore);
        ArgumentNullException.ThrowIfNull(eventStore);

        _projectionStore = projectionStore;
        _eventStore = eventStore;
    }

    /// <summary>
    /// Verifies projection is up-to-date with event store.
    /// </summary>
    public async ValueTask<ProjectionConsistencyReport?> VerifyAsync(
        string projectionKey,
        StreamId streamId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var projectionState = await _projectionStore.LoadAsync(projectionKey, ct).ConfigureAwait(false);
        if (projectionState is null)
            return null;

        var (_, projectionCheckpoint) = projectionState.Value;

        // Find latest event position
        StreamPosition latestPosition = StreamPosition.Start;
        await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start, ct).ConfigureAwait(false))
        {
            latestPosition = envelope.Position;
        }

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

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/ProjectionConsistencyCheckerTests.cs -v normal`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/ProjectionConsistencyChecker.cs tests/ZeroAlloc.EventSourcing.Tests/ProjectionConsistencyCheckerTests.cs
git commit -m "feat: implement ProjectionConsistencyChecker for validation"
```

---

## Task 5: Implement BatchedProjection<TReadModel>

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/BatchedProjection.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/BatchedProjectionTests.cs`

**Step 1: Write unit tests**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public sealed class BatchedProjectionTests
{
    [Fact]
    public async Task HandleAsync_AccumulatesBatch_FlushesAtSize()
    {
        // Arrange
        var projection = new TestBatchedProjection(batchSize: 3);
        var events = Enumerable.Range(1, 5)
            .Select(i => new EventEnvelope(
                new StreamId("test"),
                new StreamPosition(i),
                new TestBatchEvent { Value = i },
                EventMetadata.Default))
            .ToList();

        // Act
        foreach (var @event in events.Take(3))
            await projection.HandleAsync(@event);

        // Assert - Batch flushed at size 3
        projection.FlushedBatches.Should().HaveCount(1);
        projection.FlushedBatches[0].Should().HaveCount(3);
    }

    [Fact]
    public async Task FlushAsync_FlushesPendingBatch()
    {
        // Arrange
        var projection = new TestBatchedProjection(batchSize: 10);
        var events = Enumerable.Range(1, 3)
            .Select(i => new EventEnvelope(
                new StreamId("test"),
                new StreamPosition(i),
                new TestBatchEvent { Value = i },
                EventMetadata.Default))
            .ToList();

        foreach (var @event in events)
            await projection.HandleAsync(@event);

        // Act
        await projection.FlushAsync();

        // Assert
        projection.FlushedBatches.Should().HaveCount(1);
        projection.FlushedBatches[0].Should().HaveCount(3);
    }

    private class TestBatchedProjection : BatchedProjection<TestProjectionState>
    {
        public List<List<EventEnvelope>> FlushedBatches { get; } = new();

        public TestBatchedProjection(int batchSize = 100) : base(batchSize) { }

        protected override bool IncludeEvent(EventEnvelope @event) => @event.Event is TestBatchEvent;

        protected override TestProjectionState Apply(TestProjectionState current, EventEnvelope @event) =>
            @event.Event switch
            {
                TestBatchEvent e => new TestProjectionState { Total = current.Total + e.Value },
                _ => current
            };

        protected override ValueTask FlushBatchAsync(IReadOnlyList<EventEnvelope> events, CancellationToken ct)
        {
            FlushedBatches.Add(events.ToList());
            return ValueTask.CompletedTask;
        }
    }

    private record TestProjectionState { public int Total { get; set; } }
    private record TestBatchEvent { public int Value { get; set; } }
}
```

**Step 2: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/BatchedProjectionTests.cs -v normal`
Expected: FAIL

**Step 3: Implement BatchedProjection**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Projection that batches events before processing.
/// Useful for efficient database operations or external APIs.
/// </summary>
public abstract class BatchedProjection<TReadModel> : FilteredProjection<TReadModel>
{
    private readonly int _batchSize;
    private readonly List<EventEnvelope> _batch = new();

    protected BatchedProjection(int batchSize = 100)
    {
        if (batchSize <= 0)
            throw new ArgumentException("Batch size must be positive", nameof(batchSize));
        _batchSize = batchSize;
    }

    /// <summary>
    /// Called when batch is full or FlushAsync is called.
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
                await FlushBatchAsync(_batch, ct).ConfigureAwait(false);
                _batch.Clear();
            }
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Flushes any pending batch items.
    /// Call after processing all events.
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_batch.Count > 0)
        {
            await FlushBatchAsync(_batch, ct).ConfigureAwait(false);
            _batch.Clear();
        }
    }

    /// <summary>
    /// Required override from FilteredProjection.
    /// Default: include all events. Override for filtering.
    /// </summary>
    protected override bool IncludeEvent(EventEnvelope @event) => true;
}
```

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/BatchedProjectionTests.cs -v normal`
Expected: PASS (2 tests)

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/BatchedProjection.cs tests/ZeroAlloc.EventSourcing.Tests/BatchedProjectionTests.cs
git commit -m "feat: implement BatchedProjection for efficient bulk event processing"
```

---

## Task 6: End-to-End Integration Tests

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.Tests/AdvancedProjectionsIntegrationTests.cs`

**Step 1: Write comprehensive integration tests**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public sealed class AdvancedProjectionsIntegrationTests : IAsyncLifetime
{
    private InMemoryEventStoreAdapter _adapter = null!;
    private InMemoryEventStore _eventStore = null!;
    private InMemorySnapshotStore<OrderState> _snapshotStore = null!;
    private InMemoryProjectionStore<OrderSummary> _projectionStore = null!;

    public async Task InitializeAsync()
    {
        _adapter = new InMemoryEventStoreAdapter();
        _eventStore = new InMemoryEventStore(_adapter);
        _snapshotStore = new InMemorySnapshotStore<OrderState>();
        _projectionStore = new InMemoryProjectionStore<OrderSummary>();
    }

    public async Task DisposeAsync() { }

    [Fact]
    public async Task FilteredProjection_ReceivesOnlyMatchingEvents()
    {
        // Arrange
        var streamId = new StreamId("order-1");
        var projection = new TestOrderProjection();

        // Act: Mix of included and excluded events
        await projection.HandleAsync(new EventEnvelope(
            streamId, new StreamPosition(1),
            new OrderPlaced { OrderId = 1, Amount = 100m },
            EventMetadata.Default));

        await projection.HandleAsync(new EventEnvelope(
            streamId, new StreamPosition(2),
            new OrderShipped { TrackingCode = "ABC123" },
            EventMetadata.Default));

        await projection.HandleAsync(new EventEnvelope(
            streamId, new StreamPosition(3),
            new UnrelatedEvent { },
            EventMetadata.Default));

        // Assert - Only 2 events processed (not the unrelated one)
        projection.Current.Amount.Should().Be(100m);
        projection.Current.Status.Should().Be("Shipped");
    }

    [Fact]
    public async Task ReplayableProjection_RebuildRestoresCompleteState()
    {
        // Arrange
        var streamId = new StreamId("order-2");
        await _eventStore.AppendAsync(streamId, 0, new object[] {
            new OrderPlaced { OrderId = 2, Amount = 250m },
            new OrderShipped { TrackingCode = "XYZ789" },
            new OrderCancelled { Reason = "Customer requested" }
        });

        var projection = new TestReplayableOrderProjection(_projectionStore, _eventStore);

        // Act
        await projection.RebuildAsync(streamId);

        // Assert
        var saved = await _projectionStore.LoadAsync("order-projection");
        saved.Should().NotBeNull();
        saved.Value.State.Amount.Should().Be(250m);
        saved.Value.State.Status.Should().Be("Cancelled");
        saved.Value.Checkpoint.Should().Be(new StreamPosition(3));
    }

    [Fact]
    public async Task ConsistencyChecker_DetectsProjectionGaps()
    {
        // Arrange
        var streamId = new StreamId("order-3");
        await _eventStore.AppendAsync(streamId, 0, new object[] {
            new OrderPlaced { OrderId = 3, Amount = 150m },
            new OrderShipped { TrackingCode = "GAP123" }
        });

        // Projection behind
        await _projectionStore.SaveAsync("order-proj", new OrderSummary(), new StreamPosition(1));

        var checker = new ProjectionConsistencyChecker<OrderSummary>(_projectionStore, _eventStore);

        // Act
        var report = await checker.VerifyAsync("order-proj", streamId);

        // Assert
        report.Should().NotBeNull();
        report.Value.IsConsistent.Should().BeFalse();
        report.Value.DiscrepancyReason.Should().Contain("behind");
    }

    [Fact]
    public async Task FullWorkflow_LoadAggregate_ProjectEvent_SnapshotAndRebuild()
    {
        // Arrange: Create aggregate events
        var aggregateId = new OrderId(999);
        var streamId = new StreamId($"order-{aggregateId.Value}");

        var order = new Order();
        order.SetId(aggregateId);
        order.PlaceOrder(1000m);
        order.ShipOrder("FULL-WF-001");

        // Save aggregate
        var eventStore = _eventStore;
        await eventStore.AppendAsync(streamId, 0, order.UncommittedEvents.ToArray());

        // Create projection
        var projection = new TestReplayableOrderProjection(_projectionStore, eventStore);
        await projection.RebuildAsync(streamId);

        // Save snapshot
        await _snapshotStore.WriteAsync(streamId, new StreamPosition(2), order.State);

        // Act: Verify projection and snapshot are consistent
        var projectionState = await _projectionStore.LoadAsync("order-projection");
        var snapshotState = await _snapshotStore.ReadAsync(streamId);

        // Assert
        projectionState.Should().NotBeNull();
        snapshotState.Should().NotBeNull();
        projectionState.Value.State.Amount.Should().Be(1000m);
        snapshotState.Value.State.Amount.Should().Be(1000m);
    }

    // Test implementations
    private class TestOrderProjection : FilteredProjection<OrderSummary>
    {
        protected override bool IncludeEvent(EventEnvelope @event) =>
            @event.Event is OrderPlaced or OrderShipped or OrderCancelled;

        protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event) =>
            @event.Event switch
            {
                OrderPlaced o => new OrderSummary { Amount = o.Amount, Status = "Placed" },
                OrderShipped _ => current with { Status = "Shipped" },
                OrderCancelled _ => current with { Status = "Cancelled" },
                _ => current
            };
    }

    private class TestReplayableOrderProjection : ReplayableProjection<OrderSummary>
    {
        public TestReplayableOrderProjection(IProjectionStore<OrderSummary> store, IEventStore eventStore)
            : base(store, "order-projection", eventStore) { }

        protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event) =>
            @event.Event switch
            {
                OrderPlaced o => new OrderSummary { Amount = o.Amount, Status = "Placed" },
                OrderShipped _ => current with { Status = "Shipped" },
                OrderCancelled _ => current with { Status = "Cancelled" },
                _ => current
            };
    }

    // Domain objects
    private record OrderSummary { public decimal Amount { get; set; } public string Status { get; set; } }
    private record OrderPlaced { public int OrderId { get; set; } public decimal Amount { get; set; } }
    private record OrderShipped { public string TrackingCode { get; set; } }
    private record OrderCancelled { public string Reason { get; set; } }
    private record UnrelatedEvent;
}
```

**Step 2: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/AdvancedProjectionsIntegrationTests.cs -v normal`
Expected: FAIL (some types/methods may not exist yet)

**Step 3: Run tests again**

After adjusting for existing types, run again.
Expected: PASS (4 integration tests)

**Step 4: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.Tests/AdvancedProjectionsIntegrationTests.cs
git commit -m "test: add end-to-end integration tests for advanced projections"
```

---

## Testing Checklist

After all tasks:

```bash
# Run all Phase 7 tests
dotnet test tests/ZeroAlloc.EventSourcing.Tests/FilteredProjectionTests.cs -v normal
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemoryProjectionStoreTests.cs -v normal
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ReplayableProjectionTests.cs -v normal
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ProjectionConsistencyCheckerTests.cs -v normal
dotnet test tests/ZeroAlloc.EventSourcing.Tests/BatchedProjectionTests.cs -v normal
dotnet test tests/ZeroAlloc.EventSourcing.Tests/AdvancedProjectionsIntegrationTests.cs -v normal

# Run full suite (no database integration yet)
dotnet test --filter "FullyQualifiedName!~PostgreSql&FullyQualifiedName!~SqlServer" -v normal
```

All should pass with no regressions.

---

## Success Criteria

- ✅ FilteredProjection filters events correctly
- ✅ IProjectionStore saves/loads/clears state
- ✅ InMemoryProjectionStore works correctly
- ✅ ReplayableProjection rebuilds from scratch
- ✅ ProjectionConsistencyChecker detects gaps
- ✅ BatchedProjection batches correctly
- ✅ End-to-end integration tests pass
- ✅ All tests passing (40+ new tests)
- ✅ No regressions in existing tests
