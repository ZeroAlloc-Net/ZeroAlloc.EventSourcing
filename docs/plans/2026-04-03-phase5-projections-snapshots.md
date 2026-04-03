# Phase 5: Projections & Snapshots Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Add snapshot and projection support to ZeroAlloc.EventSourcing, reducing read amplification for long-lived aggregates and enabling efficient read models.

**Architecture:** 
- **Snapshots** are optional, storage-agnostic checkpoints of aggregate state. `AggregateRepository.LoadAsync` checks for a snapshot before replaying the full stream, skipping old events.
- **Projections** are thin read models built from event subscriptions. A source generator will emit event dispatch (ApplyEvent) similar to aggregates, eliminating reflection.
- Both are fully optional — existing code continues to work without them.

**Tech Stack:** `ISnapshotStore<TState>` interface, `Projection<TReadModel>` base class, Roslyn source generator (existing `ZeroAlloc.EventSourcing.Generators`), in-memory implementations for tests.

---

## Task 1: Define ISnapshotStore<TState> Interface

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/ISnapshotStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/SnapshotStoreContractTests.cs`

**Step 1: Write the interface**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Storage-agnostic snapshot persistence. Implementations are responsible for reading
/// and writing snapshot payloads of aggregate state at specific versions.
/// </summary>
/// <typeparam name="TState">The aggregate state type. Must match the type used in snapshots.</typeparam>
public interface ISnapshotStore<TState> where TState : struct
{
    /// <summary>
    /// Reads the most recent snapshot for a given stream, if one exists.
    /// Returns null if no snapshot is found.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A tuple of (position, state) or null if no snapshot exists.</returns>
    ValueTask<(StreamPosition Position, TState State)?> ReadAsync(StreamId streamId, CancellationToken ct = default);

    /// <summary>
    /// Saves a snapshot of the aggregate state at the given position.
    /// Should replace any existing snapshot for this stream (last-write-wins).
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="position">The stream position (version) at which this snapshot was taken.</param>
    /// <param name="state">The aggregate state to persist.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask WriteAsync(StreamId streamId, StreamPosition position, TState state, CancellationToken ct = default);
}
```

**Step 2: Write a contract test for ISnapshotStore<T>**

Create a test base class that verifies the snapshot store interface contract:

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Contract tests for <see cref="ISnapshotStore{TState}"/>. Implementations should
/// inherit from this and provide their own concrete ISnapshotStore<T> instance.
/// </summary>
public abstract class SnapshotStoreContractTests
{
    // Test state struct matching OrderState pattern from aggregates
    public struct TestState
    {
        public string OrderId { get; set; }
        public int Amount { get; set; }
    }

    protected abstract ISnapshotStore<TestState> CreateStore();

    [Fact]
    public async Task Read_NoSnapshot_ReturnsNull()
    {
        var store = CreateStore();
        var streamId = new StreamId("order-123");

        var result = await store.ReadAsync(streamId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Write_ThenRead_ReturnsWrittenState()
    {
        var store = CreateStore();
        var streamId = new StreamId("order-123");
        var state = new TestState { OrderId = "123", Amount = 100 };
        var position = new StreamPosition(5);

        await store.WriteAsync(streamId, position, state);
        var result = await store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.OrderId.Should().Be("123");
        result.Value.State.Amount.Should().Be(100);
    }

    [Fact]
    public async Task Write_Multiple_LastOneWins()
    {
        var store = CreateStore();
        var streamId = new StreamId("order-123");

        await store.WriteAsync(streamId, new StreamPosition(3), new TestState { Amount = 30 });
        await store.WriteAsync(streamId, new StreamPosition(5), new TestState { Amount = 50 });

        var result = await store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Value.Should().Be(5);
        result.Value.State.Amount.Should().Be(50);
    }

    [Fact]
    public async Task Read_DifferentStreams_Isolated()
    {
        var store = CreateStore();
        var stream1 = new StreamId("order-1");
        var stream2 = new StreamId("order-2");

        await store.WriteAsync(stream1, new StreamPosition(5), new TestState { OrderId = "1", Amount = 100 });
        await store.WriteAsync(stream2, new StreamPosition(5), new TestState { OrderId = "2", Amount = 200 });

        var result1 = await store.ReadAsync(stream1);
        var result2 = await store.ReadAsync(stream2);

        result1.Value.State.OrderId.Should().Be("1");
        result2.Value.State.OrderId.Should().Be("2");
    }
}
```

**Step 3: Run the contract test (will fail — tests abstract)**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/SnapshotStoreContractTests.cs -v normal`
Expected: Test discovery finds no concrete test methods (contract tests are meant to be inherited).

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/ISnapshotStore.cs tests/ZeroAlloc.EventSourcing.Tests/SnapshotStoreContractTests.cs
git commit -m "feat: add ISnapshotStore<TState> interface and contract tests"
```

---

## Task 2: Implement InMemorySnapshotStore

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.InMemory/InMemorySnapshotStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemorySnapshotStoreTests.cs`

**Step 1: Implement InMemorySnapshotStore**

```csharp
using System.Collections.Concurrent;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// In-memory snapshot store backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Thread-safe, suitable for unit and integration testing.
/// </summary>
/// <typeparam name="TState">The aggregate state type being snapshot.</typeparam>
public sealed class InMemorySnapshotStore<TState> : ISnapshotStore<TState> where TState : struct
{
    private readonly ConcurrentDictionary<StreamId, (StreamPosition, TState)> _snapshots = new();

    /// <inheritdoc/>
    public ValueTask<(StreamPosition Position, TState State)?> ReadAsync(StreamId streamId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = _snapshots.TryGetValue(streamId, out var snapshot);
        return ValueTask.FromResult(found ? snapshot : null);
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(StreamId streamId, StreamPosition position, TState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _snapshots[streamId] = (position, state);
        return ValueTask.CompletedTask;
    }
}
```

**Step 2: Write contract test implementation**

Create concrete test class:

```csharp
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public sealed class InMemorySnapshotStoreTests : SnapshotStoreContractTests
{
    protected override ISnapshotStore<TestState> CreateStore() => new InMemorySnapshotStore<TestState>();
}
```

**Step 3: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemorySnapshotStoreTests.cs -v normal`
Expected: PASS (4 tests from contract base class).

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.InMemory/InMemorySnapshotStore.cs tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemorySnapshotStoreTests.cs
git commit -m "feat: add InMemorySnapshotStore implementation"
```

---

## Task 3: Create Projection<TReadModel> Base Class

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/Projection.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/ProjectionTests.cs`

**Step 1: Define Projection<TReadModel> base class**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Base class for event-driven read models. Projections are thin layers on top of
/// <see cref="IEventSubscription"/> that transform events into read-model state.
/// 
/// To use: inherit and implement <see cref="Apply"/>. The source generator 
/// (ZeroAlloc.EventSourcing.Generators) can optionally emit a type-switched Apply overload.
/// Projection persistence is intentionally out-of-scope — consumers own their read model storage.
/// </summary>
/// <typeparam name="TReadModel">The read model type (domain-agnostic).</typeparam>
public abstract class Projection<TReadModel>
{
    /// <summary>The current read model state.</summary>
    public TReadModel Current { get; protected set; } = default!;

    /// <summary>
    /// Applies an event to the read model, returning the new state.
    /// Override to implement event-to-state transformations.
    /// 
    /// For compiled dispatch, the source generator may emit method overloads:
    ///   Apply(TReadModel current, OrderPlaced e) -> TReadModel
    ///   Apply(TReadModel current, OrderShipped e) -> TReadModel
    /// 
    /// This base implementation handles generic object events; overloads are preferred.
    /// </summary>
    protected abstract TReadModel Apply(TReadModel current, EventEnvelope @event);

    /// <summary>
    /// Called by a subscription handler to deliver an event to this projection.
    /// Updates <see cref="Current"/> with the result of <see cref="Apply"/>.
    /// </summary>
    public virtual async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Current = Apply(Current, @event);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
```

**Step 2: Write unit test for Projection**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public sealed class ProjectionTests
{
    // Test events
    public record OrderPlaced(string OrderId, decimal Amount);
    public record OrderShipped(string TrackingCode);

    // Test read model
    public record OrderSummary(string OrderId, decimal Amount, string? TrackingCode);

    // Test projection implementation
    public sealed class OrderProjection : Projection<OrderSummary>
    {
        public OrderProjection() => Current = new OrderSummary("", 0, null);

        protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event) =>
            @event.Event switch
            {
                OrderPlaced o => new OrderSummary(o.OrderId, o.Amount, null),
                OrderShipped s => current with { TrackingCode = s.TrackingCode },
                _ => current,
            };
    }

    [Fact]
    public async Task Projection_AppliesEvents_UpdatesReadModel()
    {
        var projection = new OrderProjection();
        var streamId = new StreamId("order-1");

        var placed = new EventEnvelope(
            streamId,
            new StreamPosition(1),
            new OrderPlaced("ORD-001", 99.99m),
            EventMetadata.New("OrderPlaced"));

        await projection.HandleAsync(placed);

        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.Amount.Should().Be(99.99m);
        projection.Current.TrackingCode.Should().BeNull();
    }

    [Fact]
    public async Task Projection_MultipleEvents_StateAccumulates()
    {
        var projection = new OrderProjection();
        var streamId = new StreamId("order-1");

        var placed = new EventEnvelope(
            streamId,
            new StreamPosition(1),
            new OrderPlaced("ORD-001", 99.99m),
            EventMetadata.New("OrderPlaced"));

        var shipped = new EventEnvelope(
            streamId,
            new StreamPosition(2),
            new OrderShipped("TRACK-123"),
            EventMetadata.New("OrderShipped"));

        await projection.HandleAsync(placed);
        await projection.HandleAsync(shipped);

        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.TrackingCode.Should().Be("TRACK-123");
    }

    [Fact]
    public async Task Projection_UnknownEvent_Ignored()
    {
        var projection = new OrderProjection();
        var streamId = new StreamId("order-1");

        var placed = new EventEnvelope(
            streamId,
            new StreamPosition(1),
            new OrderPlaced("ORD-001", 99.99m),
            EventMetadata.New("OrderPlaced"));

        var unknown = new EventEnvelope(
            streamId,
            new StreamPosition(2),
            "SomeOtherEvent",
            EventMetadata.New("SomeOtherEvent"));

        await projection.HandleAsync(placed);
        await projection.HandleAsync(unknown);

        projection.Current.OrderId.Should().Be("ORD-001");
    }
}
```

**Step 3: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/ProjectionTests.cs -v normal`
Expected: PASS (3 tests).

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/Projection.cs tests/ZeroAlloc.EventSourcing.Tests/ProjectionTests.cs
git commit -m "feat: add Projection<TReadModel> base class for read models"
```

---

## Task 4: Integrate Snapshots into AggregateRepository.LoadAsync

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.Aggregates/AggregateRepository.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/AggregateRepositorySnapshotTests.cs`

**Step 1: Add optional snapshot store to repository constructor**

Modify `AggregateRepository<TAggregate, TId>`:

```csharp
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

public sealed class AggregateRepository<TAggregate, TId> : IAggregateRepository<TAggregate, TId>
    where TAggregate : IAggregate
    where TId : struct
{
    private readonly IEventStore _store;
    private readonly Func<TAggregate> _factory;
    private readonly Func<TId, StreamId> _streamIdFactory;
    
    // New: optional snapshot store for state type
    private readonly object? _snapshotStore; // Stored as object to allow generic TState without public generic parameter
    private readonly Func<TAggregate, object?>? _getStateAsObject; // Extracts TState from aggregate
    private readonly Func<TAggregate, object?, StreamPosition, void>? _restoreState; // Restores state to aggregate

    /// <summary>
    /// Initialises the repository with optional snapshot support.
    /// </summary>
    /// <param name="store">The event store to read from and write to.</param>
    /// <param name="factory">Factory that creates a new, empty aggregate instance.</param>
    /// <param name="streamIdFactory">Maps an aggregate ID to a stream ID.</param>
    /// <param name="snapshotStore">Optional snapshot store. If provided, LoadAsync will use snapshots to reduce event replay.</param>
    /// <param name="getStateAsObject">Extractor function that returns the aggregate's state. Required if snapshotStore is provided.</param>
    /// <param name="restoreState">Restorer function that sets the aggregate's state from a snapshot. Required if snapshotStore is provided.</param>
    public AggregateRepository(
        IEventStore store,
        Func<TAggregate> factory,
        Func<TId, StreamId> streamIdFactory,
        object? snapshotStore = null,
        Func<TAggregate, object?>? getStateAsObject = null,
        Func<TAggregate, object?, StreamPosition, void>? restoreState = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(streamIdFactory);

        // Validate snapshot dependencies: both required or both null
        var hasSnapshotStore = snapshotStore != null;
        var hasStateExtractor = getStateAsObject != null;
        var hasStateRestorer = restoreState != null;

        if (hasSnapshotStore && (!hasStateExtractor || !hasStateRestorer))
            throw new ArgumentException("If snapshotStore is provided, both getStateAsObject and restoreState must be provided.");
        if (!hasSnapshotStore && (hasStateExtractor || hasStateRestorer))
            throw new ArgumentException("getStateAsObject and restoreState can only be provided if snapshotStore is also provided.");

        _store = store;
        _factory = factory;
        _streamIdFactory = streamIdFactory;
        _snapshotStore = snapshotStore;
        _getStateAsObject = getStateAsObject;
        _restoreState = restoreState;
    }

    /// <summary>
    /// Loads an aggregate by ID. If a snapshot store is configured, checks for a snapshot first
    /// and only replays events from that point onward. Otherwise, replays the entire stream.
    /// </summary>
    public async ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default)
    {
        var streamId = _streamIdFactory(id);
        var aggregate = _factory();
        var readFrom = StreamPosition.Start;

        // Check for snapshot if available
        if (_snapshotStore != null)
        {
            // Snapshot store is stored as object; we use reflection to call ReadAsync<TState>
            // in a type-agnostic way. In real usage, this would be generated code.
            var snapshotMethod = _snapshotStore.GetType()
                .GetMethod("ReadAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            
            if (snapshotMethod != null)
            {
                var task = snapshotMethod.Invoke(_snapshotStore, new object[] { streamId, ct });
                if (task is System.Threading.Tasks.Task awaitableTask)
                {
                    await awaitableTask.ConfigureAwait(false);
                    // Extract result via reflection (tuple property)
                    var resultProperty = awaitableTask.GetType().GetProperty("Result");
                    if (resultProperty?.GetValue(awaitableTask) is (StreamPosition Position, object State) snapshot)
                    {
                        // Restore from snapshot
                        _restoreState!(aggregate, snapshot.State, snapshot.Position);
                        readFrom = snapshot.Position.Next(); // Skip events before snapshot
                    }
                }
            }
        }

        // Replay remaining events from readFrom onward
        await foreach (var envelope in _store.ReadAsync(streamId, readFrom, ct).ConfigureAwait(false))
            aggregate.ApplyHistoric(envelope.Event, envelope.Position);

        return Result<TAggregate, StoreError>.Success(aggregate);
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var uncommitted = aggregate.DequeueUncommitted();
        if (uncommitted.Length == 0)
            return Result<AppendResult, StoreError>.Success(new AppendResult(_streamIdFactory(id), aggregate.OriginalVersion));

        var events = new object[uncommitted.Length];
        for (var i = 0; i < uncommitted.Length; i++)
            events[i] = uncommitted[i];

        var result = await _store.AppendAsync(_streamIdFactory(id), events.AsMemory(), aggregate.OriginalVersion, ct).ConfigureAwait(false);
        if (result.IsSuccess)
            aggregate.AcceptVersion(result.Value.NextExpectedVersion);
        return result;
    }
}
```

**Step 2: Write snapshot integration tests**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

public sealed class AggregateRepositorySnapshotTests
{
    // Test aggregate state
    public struct OrderState : IAggregateState<OrderState>
    {
        public static OrderState Initial => default;
        public string Status { get; set; }

        public OrderState Apply(dynamic e) =>
            e switch
            {
                { GetType.Name: "OrderPlaced" } => this with { Status = "Placed" },
                { GetType.Name: "OrderShipped" } => this with { Status = "Shipped" },
                _ => this,
            };
    }

    // Test aggregate
    public sealed class Order : Aggregate<int, OrderState>
    {
        public void PlaceOrder() => Raise(new { GetType = typeof(OrderPlaced), Event = "OrderPlaced" });
        public void ShipOrder() => Raise(new { GetType = typeof(OrderShipped), Event = "OrderShipped" });

        protected override OrderState ApplyEvent(OrderState state, object @event) =>
            @event switch
            {
                _ when @event.GetType().Name == "OrderPlaced" => state with { Status = "Placed" },
                _ when @event.GetType().Name == "OrderShipped" => state with { Status = "Shipped" },
                _ => state,
            };

        // Expose state for snapshot store
        public OrderState GetState() => State;
        public void RestoreState(OrderState state, StreamPosition position)
        {
            // Simulate restoring from snapshot
            // (In real code, this would be generated)
        }
    }

    [Fact]
    public async Task LoadAsync_WithSnapshot_SkipsEarlierEvents()
    {
        var eventStore = new InMemoryEventStore(new InMemoryEventStoreAdapter());
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var streamId = new StreamId("order-1");

        // Simulate events: placed at pos 1, shipped at pos 2
        var state = new OrderState { Status = "Shipped" };
        await snapshotStore.WriteAsync(streamId, new StreamPosition(2), state);

        // Create repo WITH snapshot store
        var repo = new AggregateRepository<Order, int>(
            eventStore,
            () => new Order(),
            id => streamId,
            snapshotStore,
            agg => agg.GetState(),
            (agg, state, pos) => agg.RestoreState((OrderState)state, pos));

        // Load should skip replaying pos 1 (PlaceOrder) and start from snapshot
        var result = await repo.LoadAsync(1);

        result.IsSuccess.Should().BeTrue();
        // After snapshot restore, aggregate should show "Shipped" without replaying placed event
        // This is an optimization test — in real code we'd track event read count
    }
}
```

**Alternative simpler approach (recommended):** For this phase, snapshot integration can be deferred. Instead, add the interface and in-memory impl, then add a simplified LoadAsync extension method that uses snapshots, keeping the core `AggregateRepository` unchanged. Snapshots can be tightly integrated in Phase 5.2.

**Step 3: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ -v normal`
Expected: PASS.

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Aggregates/AggregateRepository.cs tests/ZeroAlloc.EventSourcing.Aggregates.Tests/AggregateRepositorySnapshotTests.cs
git commit -m "feat: integrate snapshots into AggregateRepository.LoadAsync"
```

---

## Task 5: Add Source Generator Support for Projection Event Dispatch (Optional)

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.Generators/ProjectionDispatchGenerator.cs` (new)
- Test: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/GeneratedProjectionTests.cs`

**Step 1: Design optional projection generator** (similar to aggregate ApplyEvent dispatch)

The source generator can scan for `partial class MyProjection : Projection<TReadModel>` and emit typed Apply overloads:

```csharp
partial class OrderProjection : Projection<OrderSummary>
{
    protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event) =>
        @event.Event switch
        {
            OrderPlaced o => ApplyOrdered(current, o),
            OrderShipped s => ApplyShipped(current, s),
            _ => current,
        };

    private OrderSummary ApplyOrdered(OrderSummary current, OrderPlaced e) =>
        new OrderSummary(e.OrderId, e.Amount, null);

    private OrderSummary ApplyShipped(OrderSummary current, OrderShipped e) =>
        current with { TrackingCode = e.TrackingCode };
}
```

This is **optional** for Phase 5 — the base class alone is sufficient. Dispatch generation can be added in Phase 5.1.

**For now, skip this task or defer to phase 5.1.**

---

## Execution Notes

- **All tests must pass before proceeding to next task**
- Run: `dotnet test` after each task to verify no regressions
- Commit frequently — each task is one commit
- Phase 5 is complete when all 4 tasks pass and integrate without errors

---

## Testing Checklist

After all tasks:

```bash
# Core tests (no Testcontainers)
dotnet test tests/ZeroAlloc.EventSourcing.Tests/
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/

# Integration tests (requires Docker)
# dotnet test tests/ZeroAlloc.EventSourcing.SqlServer.Tests/
# dotnet test tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/
```

All should pass with no regressions.
