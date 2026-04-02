# Phase 2: Aggregate Layer Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Deliver a zero-allocation DDD aggregate base class, a source-generated `ApplyEvent` dispatcher, an `IEventTypeRegistry` generator, and a repository that round-trips aggregates through the event store.

**Architecture:** `Aggregate<TId,TState>` separates identity/versioning (the class) from domain state (a struct), with uncommitted events pooled via `HeapPooledList<object>`. A Roslyn incremental generator scans partial aggregate classes and emits the `ApplyEvent` switch expression + an `IEventTypeRegistry` implementation — both at compile time with no reflection. `AggregateRepository<TAggregate,TId>` builds on the existing `IEventStore` abstraction from Phase 1.

**Tech Stack:** .NET 9/10 multi-target, `ZeroAlloc.Collections.HeapPooledList<T>`, `ZeroAlloc.Results`, Roslyn `IIncrementalGenerator` (netstandard2.0), xUnit, FluentAssertions.

**Snapshots deferred:** `ISnapshotStore<TState>` and snapshot integration in the repository are Phase 2b.

---

## Pre-flight

```bash
cd c:/Projects/Prive/ZeroAlloc.EventSourcing
dotnet test ZeroAlloc.EventSourcing.slnx --framework net9.0  # must be 19/19 green
```

---

### Task 1: Wire Aggregates project dependencies

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj`
- Modify: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ZeroAlloc.EventSourcing.Aggregates.Tests.csproj`
- Modify: `Directory.Packages.props` (add `Microsoft.CodeAnalysis.CSharp` + `Microsoft.CodeAnalysis.Analyzers` versions if missing)

**Step 1: Add references to the Aggregates library**

```bash
# Core dependency
dotnet add src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj \
  reference src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj

# ZeroAlloc.Collections for HeapPooledList<T>
dotnet add src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj \
  package ZeroAlloc.Collections

# ZeroAlloc.Results (already in CPM, just reference)
dotnet add src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj \
  package ZeroAlloc.Results
```

**Step 2: Add references to the Aggregates test project**

```bash
# Test the aggregates library
dotnet add tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ZeroAlloc.EventSourcing.Aggregates.Tests.csproj \
  reference src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj

# InMemory adapter for repository integration tests
dotnet add tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ZeroAlloc.EventSourcing.Aggregates.Tests.csproj \
  reference src/ZeroAlloc.EventSourcing.InMemory/ZeroAlloc.EventSourcing.InMemory.csproj

dotnet add tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ZeroAlloc.EventSourcing.Aggregates.Tests.csproj \
  package FluentAssertions
```

> Note: `ZeroAlloc.Collections` version is already declared in `Directory.Packages.props` (0.1.4). The `dotnet add package` command will write `<PackageReference Include="ZeroAlloc.Collections" />` with no version attribute — correct for CPM. If it writes a version, remove it.

**Step 3: Verify build**

```bash
dotnet build ZeroAlloc.EventSourcing.slnx
```
Expected: 0 errors, 0 warnings.

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Aggregates/ tests/ZeroAlloc.EventSourcing.Aggregates.Tests/
git commit -m "chore(aggregates): wire project and test dependencies"
```

---

### Task 2: `IAggregateState<TSelf>` and aggregate interfaces

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Aggregates/IAggregateState.cs`
- Create: `src/ZeroAlloc.EventSourcing.Aggregates/IAggregateRepository.cs`

No TDD here — these are interface/marker contracts only. Tests come in Tasks 3 and 4.

**Step 1: Create `IAggregateState<TSelf>`**

```csharp
// src/ZeroAlloc.EventSourcing.Aggregates/IAggregateState.cs
namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Marker interface for aggregate state types. Implementations must be value types (structs)
/// to ensure state transitions are allocation-free on the hot path.
/// </summary>
/// <typeparam name="TSelf">The implementing state type (CRTP pattern).</typeparam>
public interface IAggregateState<TSelf> where TSelf : struct, IAggregateState<TSelf>
{
    /// <summary>Returns the initial (empty) state for a newly created aggregate.</summary>
    static abstract TSelf Initial { get; }
}
```

**Step 2: Create `IAggregateRepository<TAggregate, TId>`**

```csharp
// src/ZeroAlloc.EventSourcing.Aggregates/IAggregateRepository.cs
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Loads and saves aggregates via the event store.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type.</typeparam>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
public interface IAggregateRepository<TAggregate, TId>
    where TAggregate : Aggregate<TId, IAggregateDummyState>   // placeholder — see note below
    where TId : struct
{
    /// <summary>Loads an aggregate by replaying its event stream. Returns a new empty aggregate if the stream does not exist.</summary>
    ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default);

    /// <summary>Appends uncommitted events from the aggregate to the event store.</summary>
    ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, CancellationToken ct = default);
}
```

> **Note on the generic constraint:** C# does not support open generic constraints of the form `where TAggregate : Aggregate<TId, ?>`. Use an unconstrained `TAggregate` with a factory delegate instead (see Task 4). Remove the placeholder constraint when implementing Task 4.

**Corrected interface (use this):**

```csharp
// src/ZeroAlloc.EventSourcing.Aggregates/IAggregateRepository.cs
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Loads and saves aggregates via the event store.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type.</typeparam>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
public interface IAggregateRepository<TAggregate, TId>
    where TId : struct
{
    /// <summary>Loads an aggregate by replaying its event stream. Returns a new empty aggregate if the stream does not exist.</summary>
    ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default);

    /// <summary>Appends uncommitted events from the aggregate to the event store.</summary>
    ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, CancellationToken ct = default);
}
```

**Step 3: Build**

```bash
dotnet build ZeroAlloc.EventSourcing.slnx
```
Expected: 0 errors, 0 warnings.

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Aggregates/
git commit -m "feat(aggregates): add IAggregateState<TSelf> and IAggregateRepository<TAggregate,TId>"
```

---

### Task 3: `Aggregate<TId, TState>` base class (TDD)

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Aggregates/Aggregate.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/AggregateTests.cs`

This task uses a hand-written `ApplyEvent` override (no source generator yet). The generator in Task 5 will replace that boilerplate for consumer code.

**Step 1: Write failing tests**

```csharp
// tests/ZeroAlloc.EventSourcing.Aggregates.Tests/AggregateTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

// --- test domain model ---

public readonly record struct OrderId(Guid Value);

public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderShippedEvent(string TrackingNumber);

public struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public bool IsPlaced { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }

    internal OrderState Apply(OrderPlacedEvent e) => this with { IsPlaced = true, Total = e.Total };
    internal OrderState Apply(OrderShippedEvent e) => this with { IsShipped = true };
}

// Concrete aggregate — manually implements ApplyEvent (generator does this in Task 5)
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderId, decimal total) =>
        Raise(new OrderPlacedEvent(orderId, total));

    public void Ship(string tracking) =>
        Raise(new OrderShippedEvent(tracking));

    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        _ => state
    };
}

// --- tests ---

public class AggregateTests
{
    [Fact]
    public void NewAggregate_HasZeroVersion_AndInitialState()
    {
        var order = new Order();
        order.Version.Should().Be(StreamPosition.Start);
        order.OriginalVersion.Should().Be(StreamPosition.Start);
        order.State.IsPlaced.Should().BeFalse();
    }

    [Fact]
    public void Raise_AddsToUncommittedAndUpdatesState()
    {
        var order = new Order();
        order.Place("ORD-1", 99.99m);

        var uncommitted = order.DequeueUncommitted();
        uncommitted.Length.Should().Be(1);
        uncommitted[0].Should().BeOfType<OrderPlacedEvent>();
        order.State.IsPlaced.Should().BeTrue();
        order.State.Total.Should().Be(99.99m);
    }

    [Fact]
    public void DequeueUncommitted_ClearsTheQueue()
    {
        var order = new Order();
        order.Place("ORD-1", 1m);

        order.DequeueUncommitted(); // first dequeue
        order.DequeueUncommitted().Length.Should().Be(0); // second should be empty
    }

    [Fact]
    public void Raise_MultipleEvents_AllAppendedInOrder()
    {
        var order = new Order();
        order.Place("ORD-1", 50m);
        order.Ship("TRACK-123");

        var uncommitted = order.DequeueUncommitted();
        uncommitted.Length.Should().Be(2);
        uncommitted[0].Should().BeOfType<OrderPlacedEvent>();
        uncommitted[1].Should().BeOfType<OrderShippedEvent>();
        order.State.IsShipped.Should().BeTrue();
    }

    [Fact]
    public void ApplyHistoricEvent_UpdatesState_WithoutAddingToUncommitted()
    {
        var order = new Order();
        // Simulate loading from store — applying historic event
        order.ApplyHistoric(new OrderPlacedEvent("ORD-1", 75m), new StreamPosition(1));

        order.State.IsPlaced.Should().BeTrue();
        order.Version.Value.Should().Be(1);
        order.OriginalVersion.Value.Should().Be(1);
        order.DequeueUncommitted().Length.Should().Be(0); // not uncommitted
    }
}
```

**Step 2: Run tests — confirm fail**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ --framework net9.0
```
Expected: FAIL — `Aggregate<TId, TState>` not defined.

**Step 3: Implement `Aggregate<TId, TState>`**

```csharp
// src/ZeroAlloc.EventSourcing.Aggregates/Aggregate.cs
using ZeroAlloc.Collections;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Base class for DDD aggregate roots. Separates identity/versioning (this class) from domain
/// state (<typeparamref name="TState"/>, a struct) for allocation-free state transitions.
/// Uncommitted events are pooled via <see cref="HeapPooledList{T}"/>.
/// </summary>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
/// <typeparam name="TState">The aggregate state type. Must be a struct implementing <see cref="IAggregateState{TSelf}"/>.</typeparam>
public abstract class Aggregate<TId, TState> : IDisposable
    where TId : struct
    where TState : struct, IAggregateState<TState>
{
    private readonly HeapPooledList<object> _uncommitted = new();
    private bool _disposed;

    /// <summary>The aggregate identifier.</summary>
    public TId Id { get; protected set; }

    /// <summary>The current version — equal to the number of events applied (including uncommitted).</summary>
    public StreamPosition Version { get; private set; } = StreamPosition.Start;

    /// <summary>The version when this aggregate was loaded from the store. Used for optimistic concurrency on save.</summary>
    public StreamPosition OriginalVersion { get; private set; } = StreamPosition.Start;

    /// <summary>The current aggregate state. Updated on every <see cref="Raise{TEvent}"/> and <see cref="ApplyHistoric"/>.</summary>
    protected TState State { get; private set; } = TState.Initial;

    /// <summary>
    /// Raises a new domain event: adds it to the uncommitted queue and applies it to state immediately.
    /// Call this from command methods on the concrete aggregate.
    /// </summary>
    protected void Raise<TEvent>(TEvent @event) where TEvent : notnull
    {
        _uncommitted.Add(@event);
        State = ApplyEvent(State, @event);
        Version = Version.Next();
    }

    /// <summary>
    /// Applies a historic event loaded from the store. Does NOT add to the uncommitted queue.
    /// Called by <see cref="AggregateRepository{TAggregate,TId}"/> during load.
    /// </summary>
    internal void ApplyHistoric(object @event, StreamPosition position)
    {
        State = ApplyEvent(State, @event);
        Version = position;
        OriginalVersion = position;
    }

    /// <summary>
    /// Returns the uncommitted events as a read-only span and clears the queue.
    /// Called by <see cref="AggregateRepository{TAggregate,TId}"/> during save.
    /// </summary>
    internal ReadOnlySpan<object> DequeueUncommitted()
    {
        if (_uncommitted.Count == 0) return ReadOnlySpan<object>.Empty;
        var span = _uncommitted.AsReadOnlySpan().ToArray(); // snapshot before clear
        _uncommitted.Clear();
        return span;
    }

    /// <summary>
    /// Routes an event to the correct state transition. Implemented by the source generator
    /// (ZeroAlloc.EventSourcing.Generators) for <c>partial</c> aggregate classes.
    /// </summary>
    protected abstract TState ApplyEvent(TState state, object @event);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uncommitted.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

> **Note on `DequeueUncommitted`:** `AsReadOnlySpan()` returns a span over the pooled buffer. We must snapshot it to an array before `Clear()` returns the buffer to the pool — otherwise the span would reference freed memory. This is the one allocation in `DequeueUncommitted`, which is called once per save (acceptable).

**Step 4: Run tests — confirm they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ --framework net9.0
```
Expected: 5 tests, all PASS.

**Step 5: Full build verify**

```bash
dotnet build ZeroAlloc.EventSourcing.slnx
```
Expected: 0 errors, 0 warnings.

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Aggregates/ tests/ZeroAlloc.EventSourcing.Aggregates.Tests/
git commit -m "feat(aggregates): implement Aggregate<TId,TState> base class with HeapPooledList"
```

---

### Task 4: `AggregateRepository<TAggregate, TId>` (TDD)

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Aggregates/AggregateRepository.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/AggregateRepositoryTests.cs`

The repository needs to create new aggregate instances. Since `Aggregate<TId,TState>` is abstract, the caller must supply a factory. The repository takes `Func<TAggregate>` to construct a fresh instance.

**Step 1: Write failing tests**

```csharp
// tests/ZeroAlloc.EventSourcing.Aggregates.Tests/AggregateRepositoryTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

public class AggregateRepositoryTests
{
    // Reuse Order, OrderId, OrderState, OrderPlacedEvent, OrderShippedEvent from AggregateTests.cs

    private static (IEventStore store, AggregateRepository<Order, OrderId> repo) BuildRepo()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = new JsonAggregateSerializer();   // defined below
        var registry = new OrderEventTypeRegistry();      // defined below
        var store = new EventStore(adapter, serializer, registry);
        var repo = new AggregateRepository<Order, OrderId>(store, () => new Order(), id => new StreamId($"order-{id.Value}"));
        return (store, repo);
    }

    [Fact]
    public async Task Load_NonExistentStream_ReturnsNewEmptyAggregate()
    {
        var (_, repo) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        var result = await repo.LoadAsync(id);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.IsPlaced.Should().BeFalse();
        result.Value.Version.Should().Be(StreamPosition.Start);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsAggregate()
    {
        var (_, repo) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        var order = new Order();
        order.Place("ORD-1", 99.99m);
        order.SetId(id); // helper for testing — sets Id

        await repo.SaveAsync(order);

        var loaded = await repo.LoadAsync(id);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.State.IsPlaced.Should().BeTrue();
        loaded.Value.State.Total.Should().Be(99.99m);
        loaded.Value.Version.Value.Should().Be(1);
        loaded.Value.OriginalVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task Save_SetsOriginalVersionAfterSave()
    {
        var (_, repo) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        var order = new Order();
        order.Place("ORD-1", 10m);
        order.SetId(id);

        var result = await repo.SaveAsync(order);

        result.IsSuccess.Should().BeTrue();
        // After save, uncommitted should be empty
        order.DequeueUncommitted().Length.Should().Be(0);
    }

    [Fact]
    public async Task Save_WithConcurrentModification_ReturnsConflict()
    {
        var (_, repo) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        // Save first version
        var order1 = new Order();
        order1.Place("ORD-1", 10m);
        order1.SetId(id);
        await repo.SaveAsync(order1);

        // Simulate second writer — also at version 0 (stale)
        var order2 = new Order();
        order2.Place("ORD-1", 20m);
        order2.SetId(id);
        // order2.OriginalVersion is still 0 because we loaded nothing

        var result = await repo.SaveAsync(order2);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }
}

// Test helpers —  inline serializer/registry for the aggregates test project
// (same pattern as EventStoreTests.cs in Phase 1)

internal sealed class JsonAggregateSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}

internal sealed class OrderEventTypeRegistry : IEventTypeRegistry
{
    private static readonly Dictionary<string, Type> Map = new()
    {
        [nameof(OrderPlacedEvent)] = typeof(OrderPlacedEvent),
        [nameof(OrderShippedEvent)] = typeof(OrderShippedEvent),
    };

    public bool TryGetType(string eventType, out Type? type) => Map.TryGetValue(eventType, out type);
    public string GetTypeName(Type type) => type.Name;
}
```

Also add a `SetId` helper and update `Order` in `AggregateTests.cs`:

```csharp
// Add to Order class in AggregateTests.cs:
public void SetId(OrderId id) => Id = id;
```

**Step 2: Run tests — confirm fail**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ --framework net9.0
```
Expected: FAIL — `AggregateRepository<TAggregate, TId>` not defined.

**Step 3: Implement `AggregateRepository<TAggregate, TId>`**

```csharp
// src/ZeroAlloc.EventSourcing.Aggregates/AggregateRepository.cs
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Default repository implementation. Loads aggregates by replaying their event stream
/// and saves uncommitted events with optimistic concurrency via <see cref="IEventStore"/>.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type.</typeparam>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public sealed class AggregateRepository<TAggregate, TId> : IAggregateRepository<TAggregate, TId>
    where TAggregate : Aggregate<TId, IAggregateDummyState>   // cannot constrain open TState here
    where TId : struct
{
    // REMOVE the above where constraint — see note in Step 3
}
```

> **C# constraint limitation:** You cannot write `where TAggregate : Aggregate<TId, ?>` with an open type parameter. The solution is to not constrain `TAggregate` at the class level, but instead enforce it implicitly through the factory delegate and method calls that require `Aggregate<TId,TState>` members. Use `dynamic` or a non-generic base interface.

**Correct approach — introduce a non-generic base interface:**

Add to `Aggregate.cs`:

```csharp
/// <summary>Non-generic base interface for <see cref="Aggregate{TId,TState}"/>. Used for repository constraints.</summary>
public interface IAggregate
{
    /// <summary>Applies a historic event during stream replay.</summary>
    internal void ApplyHistoric(object @event, StreamPosition position);

    /// <summary>Returns and clears the uncommitted event queue.</summary>
    internal ReadOnlySpan<object> DequeueUncommitted();

    /// <summary>The version at which this aggregate was loaded. Used for optimistic concurrency.</summary>
    StreamPosition OriginalVersion { get; }
}
```

Make `Aggregate<TId, TState>` implement `IAggregate`:

```csharp
public abstract class Aggregate<TId, TState> : IAggregate, IDisposable
    // ...
```

Note: `internal` members in an interface require the interface and aggregate to be in the same assembly — which they are (both in `ZeroAlloc.EventSourcing.Aggregates`). This works.

**Full implementation:**

```csharp
// src/ZeroAlloc.EventSourcing.Aggregates/AggregateRepository.cs
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Default repository implementation. Loads aggregates by replaying their event stream
/// and saves uncommitted events with optimistic concurrency via <see cref="IEventStore"/>.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type. Must implement <see cref="IAggregate"/>.</typeparam>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
public sealed class AggregateRepository<TAggregate, TId> : IAggregateRepository<TAggregate, TId>
    where TAggregate : IAggregate
    where TId : struct
{
    private readonly IEventStore _store;
    private readonly Func<TAggregate> _factory;
    private readonly Func<TId, StreamId> _streamIdFactory;

    /// <summary>
    /// Initialises the repository.
    /// </summary>
    /// <param name="store">The event store to read from and write to.</param>
    /// <param name="factory">Factory that creates a new, empty aggregate instance.</param>
    /// <param name="streamIdFactory">Maps an aggregate ID to a stream ID. Convention: <c>id => new StreamId($"order-{id.Value}")</c>.</param>
    public AggregateRepository(IEventStore store, Func<TAggregate> factory, Func<TId, StreamId> streamIdFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(streamIdFactory);
        _store = store;
        _factory = factory;
        _streamIdFactory = streamIdFactory;
    }

    /// <inheritdoc/>
    public async ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default)
    {
        var streamId = _streamIdFactory(id);
        var aggregate = _factory();

        await foreach (var envelope in _store.ReadAsync(streamId, StreamPosition.Start, ct).ConfigureAwait(false))
            aggregate.ApplyHistoric(envelope.Event, envelope.Position);

        return Result<TAggregate, StoreError>.Success(aggregate);
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var uncommitted = aggregate.DequeueUncommitted();
        if (uncommitted.Length == 0)
            return Result<AppendResult, StoreError>.Success(new AppendResult(default, aggregate.OriginalVersion));

        // Box each event as object for the IEventStore API
        var events = new object[uncommitted.Length];
        for (var i = 0; i < uncommitted.Length; i++)
            events[i] = uncommitted[i];

        var streamId = _streamIdFactory(/* need Id — see note */ default);
        // TODO: aggregate.Id is not accessible here because TAggregate is IAggregate, not Aggregate<TId,TState>
        // Fix: expose Id through IAggregate or use a typed constraint. See note below.
        return await _store.AppendAsync(streamId, events.AsMemory(), aggregate.OriginalVersion, ct);
    }
}
```

> **Problem:** `IAggregate` does not expose `Id` because `Id` is typed as `TId` — a type parameter not available on the non-generic interface. Fix: make `SaveAsync` accept the `id` as a separate parameter, OR add a typed extension to expose the stream ID directly.

**Simplest fix — change `SaveAsync` signature to accept the stream ID directly:**

Update `IAggregateRepository`:
```csharp
ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default);
```

And the test:
```csharp
await repo.SaveAsync(order, id);
```

This is the cleanest approach — the caller always knows the ID.

**Final `SaveAsync` implementation:**
```csharp
public async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default)
{
    var uncommitted = aggregate.DequeueUncommitted();
    if (uncommitted.Length == 0)
        return Result<AppendResult, StoreError>.Success(new AppendResult(_streamIdFactory(id), aggregate.OriginalVersion));

    var events = new object[uncommitted.Length];
    for (var i = 0; i < uncommitted.Length; i++)
        events[i] = uncommitted[i];

    return await _store.AppendAsync(_streamIdFactory(id), events.AsMemory(), aggregate.OriginalVersion, ct);
}
```

Update the test helper and tests accordingly (`repo.SaveAsync(order, id)` everywhere).

**Step 4: Run tests — confirm they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ --framework net9.0
```
Expected: All tests PASS (5 from Task 3 + 4 new = 9 total).

**Step 5: Full build**

```bash
dotnet build ZeroAlloc.EventSourcing.slnx
```
Expected: 0 errors, 0 warnings.

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Aggregates/ tests/ZeroAlloc.EventSourcing.Aggregates.Tests/
git commit -m "feat(aggregates): implement AggregateRepository with load/save and optimistic concurrency"
```

---

### Task 5: Source generator — `ApplyEvent` dispatch

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Generators/AggregateDispatchGenerator.cs`
- Create: `src/ZeroAlloc.EventSourcing.Generators/AggregateDispatchGeneratorModel.cs`
- Modify: `src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj` (add generator reference)
- Create: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/SourceGeneratorTests.cs`

The generator targets `partial` classes that inherit `Aggregate<TId, TState>`. It scans the state struct for `Apply(TEvent)` methods and emits the `ApplyEvent` switch expression.

**Step 1: Wire the Generators project as analyzer into Aggregates**

Add to `src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\ZeroAlloc.EventSourcing.Generators\ZeroAlloc.EventSourcing.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

This tells MSBuild to load the generator project as a Roslyn analyzer, not as a runtime assembly reference.

**Step 2: Write a failing test (source generator test)**

```csharp
// tests/ZeroAlloc.EventSourcing.Aggregates.Tests/SourceGeneratorTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

// A partial aggregate — the generator should emit ApplyEvent for this
public partial class ProductAggregate : Aggregate<ProductId, ProductState>
{
    // No ApplyEvent override here — generator must emit it
    public void Create(string name) => Raise(new ProductCreated(name));
    public void Discontinue() => Raise(new ProductDiscontinued());
}

public readonly record struct ProductId(Guid Value);

public record ProductCreated(string Name);
public record ProductDiscontinued;

public struct ProductState : IAggregateState<ProductState>
{
    public static ProductState Initial => default;
    public bool IsCreated { get; private set; }
    public bool IsDiscontinued { get; private set; }
    public string? Name { get; private set; }

    internal ProductState Apply(ProductCreated e) => this with { IsCreated = true, Name = e.Name };
    internal ProductState Apply(ProductDiscontinued _) => this with { IsDiscontinued = true };
}

public class SourceGeneratorTests
{
    [Fact]
    public void GeneratedApplyEvent_RoutesCorrectly()
    {
        var product = new ProductAggregate();
        product.Create("Widget");

        product.State.IsCreated.Should().BeTrue();
        product.State.Name.Should().Be("Widget");
        product.DequeueUncommitted().Length.Should().Be(1);
    }

    [Fact]
    public void GeneratedApplyEvent_UnknownEvent_ReturnStateUnchanged()
    {
        var product = new ProductAggregate();
        // Manually call ApplyHistoric with an unrecognized event
        product.ApplyHistoric(new object(), new StreamPosition(1));

        product.State.IsCreated.Should().BeFalse(); // state unchanged
    }
}
```

> **How to verify the generator ran:** If `ProductAggregate` compiles without an `ApplyEvent` override and the tests pass, the generator worked. If it doesn't compile, the generator is missing or incorrect.

**Step 3: Run — confirm compilation fails (ApplyEvent not generated yet)**

```bash
dotnet build tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ 2>&1 | grep -i "error\|override"
```
Expected: Build error — `ProductAggregate` does not implement `ApplyEvent`.

**Step 4: Implement the generator**

```csharp
// src/ZeroAlloc.EventSourcing.Generators/AggregateDispatchGeneratorModel.cs
namespace ZeroAlloc.EventSourcing.Generators;

internal sealed record AggregateInfo(
    string Namespace,
    string ClassName,
    string StateTypeName,
    string StateTypeFullName,
    IReadOnlyList<string> EventTypeNames,
    IReadOnlyList<string> EventTypeFullNames);
```

```csharp
// src/ZeroAlloc.EventSourcing.Generators/AggregateDispatchGenerator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ZeroAlloc.EventSourcing.Generators;

[Generator]
public sealed class AggregateDispatchGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var aggregates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsPartialClassWithBase(node),
                transform: static (ctx, _) => GetAggregateInfo(ctx))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        context.RegisterSourceOutput(aggregates, static (ctx, info) =>
        {
            var source = EmitApplyEvent(info);
            ctx.AddSource($"{info.ClassName}.ApplyEvent.g.cs", source);
        });
    }

    private static bool IsPartialClassWithBase(SyntaxNode node)
        => node is ClassDeclarationSyntax cls
            && cls.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))
            && cls.BaseList?.Types.Count > 0;

    private static AggregateInfo? GetAggregateInfo(GeneratorSyntaxContext ctx)
    {
        var cls = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(cls) as INamedTypeSymbol;
        if (symbol is null) return null;

        // Walk base types to find Aggregate<TId, TState>
        var baseType = symbol.BaseType;
        while (baseType is not null)
        {
            if (baseType.OriginalDefinition.ToDisplayString() ==
                "ZeroAlloc.EventSourcing.Aggregates.Aggregate<TId, TState>")
                break;
            baseType = baseType.BaseType;
        }
        if (baseType is null || baseType.TypeArguments.Length < 2) return null;

        var stateType = baseType.TypeArguments[1] as INamedTypeSymbol;
        if (stateType is null) return null;

        // Find internal Apply(TEvent) methods on the state struct
        var applyMethods = stateType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.Name == "Apply"
                     && m.Parameters.Length == 1
                     && (m.DeclaredAccessibility == Accessibility.Internal
                      || m.DeclaredAccessibility == Accessibility.Private))
            .ToList();

        if (applyMethods.Count == 0) return null;

        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        return new AggregateInfo(
            Namespace: ns,
            ClassName: symbol.Name,
            StateTypeName: stateType.Name,
            StateTypeFullName: stateType.ToDisplayString(),
            EventTypeNames: applyMethods.Select(m => m.Parameters[0].Type.Name).ToList(),
            EventTypeFullNames: applyMethods.Select(m => m.Parameters[0].Type.ToDisplayString()).ToList());
    }

    private static string EmitApplyEvent(AggregateInfo info)
    {
        var sb = new StringBuilder();
        var nsPrefix = string.IsNullOrEmpty(info.Namespace) ? "" : $"{info.Namespace}.";

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(info.Namespace))
        {
            sb.AppendLine($"namespace {info.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"partial class {info.ClassName}");
        sb.AppendLine("{");
        sb.AppendLine($"    protected override {info.StateTypeFullName} ApplyEvent({info.StateTypeFullName} state, object @event)");
        sb.AppendLine("        => @event switch");
        sb.AppendLine("        {");

        for (var i = 0; i < info.EventTypeFullNames.Count; i++)
        {
            sb.AppendLine($"            {info.EventTypeFullNames[i]} __e => state.Apply(__e),");
        }

        sb.AppendLine("            _ => state");
        sb.AppendLine("        };");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
```

**Step 5: Run tests — confirm they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ --framework net9.0
```
Expected: All tests PASS (9 prior + 2 new generator tests = 11 total). `ProductAggregate.ApplyEvent` is now generated.

**Step 6: Full build**

```bash
dotnet build ZeroAlloc.EventSourcing.slnx
```
Expected: 0 errors, 0 warnings.

**Step 7: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Generators/ src/ZeroAlloc.EventSourcing.Aggregates/ tests/ZeroAlloc.EventSourcing.Aggregates.Tests/
git commit -m "feat(generators): implement AggregateDispatchGenerator — source-generated ApplyEvent switch"
```

---

### Task 6: Source generator — `IEventTypeRegistry`

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Generators/EventTypeRegistryGenerator.cs`
- Create: `src/ZeroAlloc.EventSourcing.Generators/EventTypeRegistryGeneratorModel.cs`
- Modify: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/SourceGeneratorTests.cs` (add registry tests)

The registry generator scans for `partial` classes that opt in via `[GenerateEventTypeRegistry]` attribute, or reuses the `AggregateInfo` from Task 5 to derive the event types from all aggregates in the compilation. For simplicity: emit one `IEventTypeRegistry` implementation per aggregate type (named `<ClassName>EventTypeRegistry`).

**Step 1: Write failing registry tests**

```csharp
// Add to SourceGeneratorTests.cs:

public class EventTypeRegistryTests
{
    [Fact]
    public void GeneratedRegistry_ResolvesEventTypeByName()
    {
        // The generator emits ProductAggregateEventTypeRegistry
        var registry = new ProductAggregateEventTypeRegistry();

        registry.TryGetType("ProductCreated", out var type).Should().BeTrue();
        type.Should().Be(typeof(ProductCreated));

        registry.TryGetType("ProductDiscontinued", out var type2).Should().BeTrue();
        type2.Should().Be(typeof(ProductDiscontinued));
    }

    [Fact]
    public void GeneratedRegistry_GetTypeName_ReturnsShortName()
    {
        var registry = new ProductAggregateEventTypeRegistry();

        registry.GetTypeName(typeof(ProductCreated)).Should().Be("ProductCreated");
        registry.GetTypeName(typeof(ProductDiscontinued)).Should().Be("ProductDiscontinued");
    }

    [Fact]
    public void GeneratedRegistry_UnknownType_ReturnsFalse()
    {
        var registry = new ProductAggregateEventTypeRegistry();
        registry.TryGetType("NonExistentEvent", out _).Should().BeFalse();
    }
}
```

**Step 2: Run — confirm fail**

```bash
dotnet build tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ 2>&1 | grep "error"
```
Expected: Error — `ProductAggregateEventTypeRegistry` not defined.

**Step 3: Implement the registry generator**

```csharp
// src/ZeroAlloc.EventSourcing.Generators/EventTypeRegistryGenerator.cs
using Microsoft.CodeAnalysis;
using System.Text;

namespace ZeroAlloc.EventSourcing.Generators;

[Generator]
public sealed class EventTypeRegistryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Reuse the same aggregate discovery as AggregateDispatchGenerator
        var aggregates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => AggregateDispatchGenerator.IsPartialClassWithBaseSyntax(node),
                transform: static (ctx, _) => AggregateDispatchGenerator.GetAggregateInfoPublic(ctx))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        context.RegisterSourceOutput(aggregates, static (ctx, info) =>
        {
            var source = EmitRegistry(info);
            ctx.AddSource($"{info.ClassName}EventTypeRegistry.g.cs", source);
        });
    }

    private static string EmitRegistry(AggregateInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using ZeroAlloc.EventSourcing;");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(info.Namespace))
        {
            sb.AppendLine($"namespace {info.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"/// <summary>Source-generated <see cref=\"IEventTypeRegistry\"/> for <see cref=\"{info.ClassName}\"/>.</summary>");
        sb.AppendLine($"public sealed class {info.ClassName}EventTypeRegistry : IEventTypeRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly Dictionary<string, Type> _byName = new()");
        sb.AppendLine("    {");

        for (var i = 0; i < info.EventTypeFullNames.Count; i++)
        {
            sb.AppendLine($"        [\"{info.EventTypeNames[i]}\"] = typeof({info.EventTypeFullNames[i]}),");
        }

        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    private static readonly Dictionary<Type, string> _byType = new()");
        sb.AppendLine("    {");

        for (var i = 0; i < info.EventTypeFullNames.Count; i++)
        {
            sb.AppendLine($"        [typeof({info.EventTypeFullNames[i]})] = \"{info.EventTypeNames[i]}\",");
        }

        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc/>");
        sb.AppendLine("    public bool TryGetType(string eventType, out Type? type) => _byName.TryGetValue(eventType, out type);");
        sb.AppendLine();
        sb.AppendLine("    /// <inheritdoc/>");
        sb.AppendLine("    public string GetTypeName(Type type) => _byType.TryGetValue(type, out var name) ? name : type.Name;");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
```

> **Note:** The registry generator reuses `AggregateDispatchGenerator`'s discovery logic. Refactor `AggregateDispatchGenerator` to expose `IsPartialClassWithBaseSyntax` and `GetAggregateInfoPublic` as `internal static` methods so both generators can share them without duplication.

**Step 4: Run tests — confirm they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ --framework net9.0
```
Expected: All tests PASS (11 prior + 3 registry tests = 14 total).

**Step 5: Full build**

```bash
dotnet build ZeroAlloc.EventSourcing.slnx
```
Expected: 0 errors, 0 warnings.

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Generators/ tests/ZeroAlloc.EventSourcing.Aggregates.Tests/
git commit -m "feat(generators): emit IEventTypeRegistry implementation per aggregate"
```

---

### Task 7: End-to-end integration test

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/AggregateIntegrationTests.cs`

Verifies the full roundtrip: define a `partial` aggregate, let the generator emit `ApplyEvent` and the registry, save to the InMemory store, load back, verify state.

**Step 1: Write integration test**

```csharp
// tests/ZeroAlloc.EventSourcing.Aggregates.Tests/AggregateIntegrationTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

/// <summary>
/// Full roundtrip: aggregate command → save → load → verify state.
/// Uses the source-generated ApplyEvent (ProductAggregate from SourceGeneratorTests.cs)
/// and the source-generated ProductAggregateEventTypeRegistry.
/// </summary>
public class AggregateIntegrationTests
{
    private static (AggregateRepository<ProductAggregate, ProductId> repo, IEventStore store) BuildRepo()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var registry = new ProductAggregateEventTypeRegistry(); // source-generated
        var serializer = new JsonAggregateSerializer();         // from AggregateRepositoryTests.cs
        var store = new EventStore(adapter, serializer, registry);
        var repo = new AggregateRepository<ProductAggregate, ProductId>(
            store,
            () => new ProductAggregate(),
            id => new StreamId($"product-{id.Value}"));
        return (repo, store);
    }

    [Fact]
    public async Task FullRoundtrip_CreateProduct_SaveAndLoad_StateRestored()
    {
        var (repo, _) = BuildRepo();
        var id = new ProductId(Guid.NewGuid());

        // 1. Command
        var product = new ProductAggregate();
        product.SetId(id);
        product.Create("Widget Pro");

        // 2. Save
        var saveResult = await repo.SaveAsync(product, id);
        saveResult.IsSuccess.Should().BeTrue();

        // 3. Load fresh
        var loadResult = await repo.LoadAsync(id);
        loadResult.IsSuccess.Should().BeTrue();

        var loaded = loadResult.Value;
        loaded.State.IsCreated.Should().BeTrue();
        loaded.State.Name.Should().Be("Widget Pro");
        loaded.Version.Value.Should().Be(1);
        loaded.OriginalVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task FullRoundtrip_MultipleEvents_AllStatesRestored()
    {
        var (repo, _) = BuildRepo();
        var id = new ProductId(Guid.NewGuid());

        var product = new ProductAggregate();
        product.SetId(id);
        product.Create("Gadget");
        product.Discontinue();

        await repo.SaveAsync(product, id);

        var loaded = (await repo.LoadAsync(id)).Value;
        loaded.State.IsCreated.Should().BeTrue();
        loaded.State.IsDiscontinued.Should().BeTrue();
        loaded.Version.Value.Should().Be(2);
    }

    [Fact]
    public async Task FullRoundtrip_SaveTwice_SecondSaveFails_WithConflict()
    {
        var (repo, _) = BuildRepo();
        var id = new ProductId(Guid.NewGuid());

        var p1 = new ProductAggregate();
        p1.SetId(id);
        p1.Create("A");
        await repo.SaveAsync(p1, id);

        // Stale aggregate — OriginalVersion is still 0
        var p2 = new ProductAggregate();
        p2.SetId(id);
        p2.Create("B");

        var result = await repo.SaveAsync(p2, id);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }
}
```

Also add `SetId` to `ProductAggregate`:
```csharp
// Add to ProductAggregate in SourceGeneratorTests.cs:
public void SetId(ProductId id) => Id = id;
```

**Step 2: Run tests**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ --framework net9.0
```
Expected: All tests PASS (14 prior + 3 integration = 17 total in Aggregates.Tests).

**Step 3: Run full solution**

```bash
dotnet test ZeroAlloc.EventSourcing.slnx --framework net9.0
```
Expected: 19 (Phase 1) + 17 (Phase 2) = 36 tests, all PASS.

**Step 4: Full build (both TFMs)**

```bash
dotnet build ZeroAlloc.EventSourcing.slnx
```
Expected: 0 errors, 0 warnings.

**Step 5: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.Aggregates.Tests/
git commit -m "test(aggregates): add end-to-end integration tests for aggregate save/load roundtrip"
```

---

## Phase boundary

**Phase 2 is complete when:**
- `dotnet test ZeroAlloc.EventSourcing.slnx --framework net9.0` — 36+ tests, all green
- Source generator emits `ApplyEvent` for all `partial` aggregate classes
- Source generator emits `<ClassName>EventTypeRegistry` for all discovered aggregates
- `AggregateRepository` round-trips aggregates through the InMemory store

**Phase 2b — Snapshots** (separate plan): `ISnapshotStore<TState>`, `AggregateRepository` snapshot read path, snapshot tests.

**Phase 3 — SQL Adapters** (separate plan): PostgreSQL + SQL Server adapters, migration scripts, adapter tests.
