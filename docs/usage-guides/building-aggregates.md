# Usage Guide: Building Aggregates

## Scenario: How Do I Write Aggregate Code That's Clean and Maintainable?

Aggregates are the core of your event-sourced domain model. This guide shows patterns for structuring aggregates, handling state transitions, testing, and evolving behavior over time.

## Repository Interface (Shared Across All Guides)

All examples in this guide and related guides load and save aggregates through the library's
generic repository interface, `IAggregateRepository<TAggregate, TId>` in
`ZeroAlloc.EventSourcing.Aggregates`:

```csharp
public interface IAggregateRepository<TAggregate, TId>
    where TAggregate : IAggregate
    where TId : struct
{
    /// Load an aggregate by replaying its event stream
    ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default);

    /// Append the aggregate's uncommitted events, expecting the stream at aggregate.OriginalVersion
    ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default);
}
```

`AggregateRepository<TAggregate, TId>` implements it over an `IEventStore`, and
`SnapshotCachingRepositoryDecorator<TAggregate, TId, TState>` wraps it to load from snapshots:

```csharp
var repository = new AggregateRepository<Order, OrderId>(
    eventStore,
    () => new Order(),
    id => new StreamId($"order-{id.Value}"));

var saved = await repository.SaveAsync(order, orderId);
if (saved.IsFailure && saved.Error.Code == "CONFLICT")
{
    // Someone else appended to the stream since this order was loaded: reload and retry
}

var loaded = await repository.LoadAsync(orderId);
using var reloaded = loaded.Value;
```

Loading a stream that has no events succeeds with a fresh aggregate whose `Version` is
`StreamPosition.Start`. `LoadAsync` sets the loaded aggregate's `Id` to the id you passed, also
for an empty stream and for a snapshot load.

If you prefer a domain-specific interface for a single aggregate, wrap the generic one:

```csharp
public interface IOrderRepository
{
    ValueTask<Result<Order, StoreError>> LoadAsync(OrderId id, CancellationToken ct = default);
    ValueTask<Result<AppendResult, StoreError>> SaveAsync(Order order, OrderId id, CancellationToken ct = default);
}
```

**Key point:** Whether you use the generic interface or domain-specific variants, the pattern is the same. Throughout the usage guides, we use the generic `IAggregateRepository<TAggregate, TId>` pattern for clarity and reusability.

## Aggregate Class Structure

Every aggregate inherits from `Aggregate<TId, TState>`:

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // 1. Identity
    public void SetId(OrderId id) => Id = id;
    
    // 2. Commands (public methods that validate and raise events)
    public void Place(string orderNumber, decimal total) { ... }
    public void Confirm() { ... }
    public void Ship(string trackingNumber) { ... }
    
    // 3. Event dispatcher: ApplyEvent is emitted by the source generator,
    //    because the class is partial. You can also write it by hand; see below.
}
```

### Key Design Principles

1. **Sealed classes** — Inheritance defeats the single-responsibility principle
2. **Partial for generators** — The source generator emits `ApplyEvent` and an event type registry
3. **Public commands, private helpers** — Domain logic is public; internals are private
4. **No property setters** — Only `Raise()` changes state

## State Definition

State is a struct implementing `IAggregateState<T>`:

```csharp
public partial struct OrderState : IAggregateState<OrderState>
{
    // 1. Required: Initial state
    public static OrderState Initial => default;
    
    // 2. State properties
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    
    // 3. Apply methods (pure functions)
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };
    
    internal OrderState Apply(OrderConfirmedEvent e) =>
        this with { IsConfirmed = true };
    
    internal OrderState Apply(OrderShippedEvent e) =>
        this with { IsShipped = true, TrackingNumber = e.TrackingNumber };
}
```

### Why Structs?

- **Stack allocation** — No heap pressure during event replay
- **Value semantics** — Each state is independent
- **Immutability via `with`** — Natural update syntax

## Required Base Classes

All aggregates and states depend on these base classes from ZeroAlloc.EventSourcing:

### Aggregate<TId, TState>

The base class for all aggregates. Key methods and properties:

```csharp
public abstract class Aggregate<TId, TState> : IAggregate, IDisposable
    where TId : struct
    where TState : struct, IAggregateState<TState>
{
    /// The aggregate's identity
    public TId Id { get; protected set; }
    
    /// Current state (built from replayed events)
    public TState State { get; private set; }
    
    /// Current version (position in event stream)
    public StreamPosition Version { get; private set; }
    
    /// Version at the time the aggregate was loaded
    /// Used for optimistic concurrency control
    public StreamPosition OriginalVersion { get; private set; }
    
    /// Apply an event to the current state (called by Raise and during replay)
    /// Emitted by the source generator for a partial class, or overridden by hand
    protected abstract TState ApplyEvent(TState state, object @event);

    /// Raise a new event from a command
    /// Queues event, applies it to state, increments version
    protected void Raise<TEvent>(TEvent @event) where TEvent : notnull { ... }

    /// Restore state from a snapshot: sets State, Version and OriginalVersion to the snapshot's
    /// Called by the snapshot decorator's restoreState callback, before replaying the remaining events
    /// Throws InvalidOperationException unless the aggregate is fresh
    public void RestoreState(TState state, StreamPosition position) { ... }

    /// Returns the pooled buffer of uncommitted events
    public void Dispose() { ... }
}
```

**Key usage patterns:**
- `Raise()` to emit events from commands
- `RestoreState()` in the `restoreState` callback of `SnapshotCachingRepositoryDecorator`:
  `restoreState: (order, state, pos) => order.RestoreState(state, pos)`. Only a fresh aggregate
  can be restored; one that has raised, replayed or restored anything throws `InvalidOperationException`
- `Dispose()` when you are done with the aggregate, usually through `using var order = ...`

Replaying stored events and taking the uncommitted events for a save are internal to the
library: `AggregateRepository` does both in `LoadAsync` and `SaveAsync`. Application code and
tests go through the repository. The number of events raised but not yet saved is
`Version.Value - OriginalVersion.Value`.

### IAggregateState<TState>

The interface for aggregate state structs:

```csharp
public interface IAggregateState<TState>
    where TState : struct, IAggregateState<TState>
{
    /// Initial/empty state
    static abstract TState Initial { get; }
}
```

Every state struct must:
1. Implement `IAggregateState<TSelf>`
2. Define `static Initial => default;`
3. Provide `Apply(EventType)` methods for each event type
4. Use `private set` on properties (immutable)

### State Apply Methods: Pure Functions

Apply methods must be pure—no side effects, no external dependencies:

```csharp
// ✓ Good: Pure function
internal OrderState Apply(OrderPlacedEvent e) =>
    this with { IsPlaced = true, Total = e.Total };

// ✗ Bad: Has side effects
internal OrderState Apply(OrderPlacedEvent e)
{
    _logger.Log("Order placed");  // Side effect!
    return this with { IsPlaced = true, Total = e.Total };
}

// ✗ Bad: Non-deterministic
internal OrderState Apply(OrderPlacedEvent e) =>
    this with { IsPlaced = true, Total = DateTime.Now.Ticks };  // Random!

// ✗ Bad: External dependency
internal OrderState Apply(OrderPlacedEvent e) =>
    this with { IsPlaced = true, Total = _validator.CalculateTotal(e) };  // Coupled!
```

**Why purity matters:**

- **Replay safety** — Replaying the same events always produces the same state
- **Testability** — Test state transitions without mocks or external services
- **Determinism** — No timing-dependent bugs

## Raising Events

Use `Raise()` to emit events. This immediately applies the event to state:

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderNumber, decimal total)
    {
        // Validate before raising
        if (State.IsPlaced)
            throw new InvalidOperationException("Order already placed");
        
        // Raise the event
        // This: 1) Queues the event in uncommitted list
        //       2) Applies the event to current state
        //       3) Increments aggregate version
        Raise(new OrderPlacedEvent(orderNumber, total));
        
        // After Raise(), State reflects the event
        // State.IsPlaced == true
    }

    public void Confirm()
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot confirm unplaced order");
        
        Raise(new OrderConfirmedEvent());
    }
}
```

### Multiple Events from a Single Command

Some commands should raise multiple events:

```csharp
public void PlaceAndConfirm(string orderNumber, decimal total)
{
    // Validate
    if (State.IsPlaced)
        throw new InvalidOperationException("Order already placed");
    
    // Raise multiple events atomically
    Raise(new OrderPlacedEvent(orderNumber, total));
    Raise(new OrderConfirmedEvent());
    
    // Both events are now pending on the aggregate
    // When saved, both are persisted in a single append
}

// Later: one save appends both events in a single append
var saved = await repository.SaveAsync(order, orderId);  // appends [OrderPlacedEvent, OrderConfirmedEvent]
```

## Event Type Registry

An `IEventTypeRegistry` maps event types to the names stored with each event. The event store uses it to serialize and deserialize events; on read it skips events whose name it cannot resolve.

The source generator emits one for every partial aggregate whose state has internal `Apply`
methods, named `<Aggregate>EventTypeRegistry`: `OrderEventTypeRegistry` for `Order`. It maps
each event type handled by an `Apply` method to its short type name.

The registry is a class in the namespace of the aggregate. For an aggregate nested in other types,
its name starts with the names of the containing types, joined with underscores:
`Retail_OrderEventTypeRegistry` for `Retail.Order`. A generic containing type is followed by its
number of type parameters: `Module1_OrderEventTypeRegistry` for `Module<T>.Order`. So two
aggregates with the same name in different containing types get two registries.

You can also implement the interface yourself, for example to keep old names readable after renaming an event type:

```csharp
public sealed class OrderEventTypeRegistry : IEventTypeRegistry
{
    private static readonly Dictionary<string, Type> ByName = new()
    {
        [nameof(OrderPlacedEvent)] = typeof(OrderPlacedEvent),
        [nameof(OrderConfirmedEvent)] = typeof(OrderConfirmedEvent),
        [nameof(OrderShippedEvent)] = typeof(OrderShippedEvent),
        [nameof(OrderCancelledEvent)] = typeof(OrderCancelledEvent),
        [nameof(OrderDeliveredEvent)] = typeof(OrderDeliveredEvent),
    };

    public bool TryGetType(string eventType, out Type? type) => ByName.TryGetValue(eventType, out type);

    public string GetTypeName(Type type) => type.Name;
}
```

If you write your own `OrderEventTypeRegistry`, write the `ApplyEvent` override by hand as well.
Otherwise the generator emits a class with the same name and the build fails.

Once defined, pass it to the EventStore:

```csharp
var registry = new OrderEventTypeRegistry();
var serializer = new JsonEventSerializer();
var eventStore = new EventStore(adapter, serializer, registry);
```

See sql-adapters.md and domain-modeling.md for more details on registry setup.

## Event Dispatcher: ApplyEvent

The `ApplyEvent` method routes events to their state apply methods:

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    protected override OrderState ApplyEvent(OrderState state, object @event) =>
        @event switch
        {
            OrderPlacedEvent e => state.Apply(e),
            OrderConfirmedEvent e => state.Apply(e),
            OrderShippedEvent e => state.Apply(e),
            OrderCancelledEvent e => state.Apply(e),
            _ => state  // Unknown events are ignored
        };
}
```

### Using Source Generators

ZeroAlloc.EventSourcing generates this dispatcher for you. No attribute is needed; the class only
has to be `partial`:

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Source generator creates ApplyEvent automatically!
    // No need to hand-write the dispatcher
}
```

The generator:
- Finds all `internal` `Apply` methods with one parameter on the state struct
- Creates the `ApplyEvent` override that routes events to them, and an `OrderEventTypeRegistry`
- Ensures compile-time type safety
- Skips an aggregate that already overrides `ApplyEvent` by hand

An aggregate can be nested in another type, for example a static class per bounded context. The
generated `ApplyEvent` then goes into the nested aggregate, which needs every containing type to be
`partial` as well; otherwise the generator reports [ZAES005](../diagnostics.md#zaes005). A generic
aggregate is not generated and gets [ZAES006](../diagnostics.md#zaes006). A `file` aggregate cannot be
extended from a generated file, so it gets the error [ZAES007](../diagnostics.md#zaes007).

```csharp
public static partial class Retail
{
    public sealed partial class Order : Aggregate<OrderId, OrderState> { }
}
```

## Testing Aggregates

Test aggregates by calling commands and asserting on state and on the raised events. The
uncommitted events are internal, so to see them, save the aggregate through a repository over the
in-memory event store and read the stream back:

```csharp
public class OrderTests
{
    private readonly OrderId _orderId = new(Guid.NewGuid());
    private readonly IEventStore _eventStore = new EventStore(
        new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new OrderEventTypeRegistry());
    private readonly IAggregateRepository<Order, OrderId> _repository;

    public OrderTests()
    {
        _repository = new AggregateRepository<Order, OrderId>(
            _eventStore, () => new Order(), id => new StreamId($"order-{id.Value}"));
    }

    // Saves the order and returns the events that save appended
    private async Task<List<object>> SaveAndReadNewEventsAsync(Order order)
    {
        var from = order.OriginalVersion;
        var saved = await _repository.SaveAsync(order, _orderId);
        Assert.True(saved.IsSuccess);

        var events = new List<object>();
        await foreach (var envelope in _eventStore.ReadAsync(new StreamId($"order-{_orderId.Value}"), from))
            events.Add(envelope.Event);
        return events;
    }

    [Fact]
    public async Task Place_RaisesOrderPlacedEvent()
    {
        // Arrange
        using var order = new Order();

        // Act
        order.Place("ORD-001", 1500m);

        // Assert: Check raised events
        var events = await SaveAndReadNewEventsAsync(order);
        var placedEvent = Assert.IsType<OrderPlacedEvent>(Assert.Single(events));
        Assert.Equal("ORD-001", placedEvent.OrderId);
        Assert.Equal(1500m, placedEvent.Total);
    }

    [Fact]
    public void Confirm_ThrowsWhen_OrderNotPlaced()
    {
        // Arrange
        using var order = new Order();
        
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => order.Confirm());
        
        Assert.Contains("unplaced", ex.Message);
    }

    [Fact]
    public async Task Confirm_RaisesEvent_WhenValid()
    {
        // Arrange
        using var order = new Order();
        order.Place("ORD-001", 1500m);
        await SaveAndReadNewEventsAsync(order);  // Persist the earlier event

        // Act
        order.Confirm();

        // Assert: only the event raised since the last save
        var events = await SaveAndReadNewEventsAsync(order);
        Assert.IsType<OrderConfirmedEvent>(Assert.Single(events));
    }

    [Fact]
    public void Ship_ThrowsWhen_OrderNotConfirmed()
    {
        // Arrange
        using var order = new Order();
        order.Place("ORD-001", 1500m);
        
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => order.Ship("TRACK-123"));
        
        Assert.Contains("not confirmed", ex.Message);
    }

    [Fact]
    public async Task Ship_RaisesEvent_WhenValid()
    {
        // Arrange
        using var order = new Order();
        order.Place("ORD-001", 1500m);
        order.Confirm();
        await SaveAndReadNewEventsAsync(order);  // Persist the earlier events

        // Act
        order.Ship("TRACK-123");

        // Assert
        var events = await SaveAndReadNewEventsAsync(order);
        var shippedEvent = Assert.IsType<OrderShippedEvent>(Assert.Single(events));
        Assert.Equal("TRACK-123", shippedEvent.TrackingNumber);
    }

    [Fact]
    public void StateReflectsAllRaisedEvents()
    {
        // Arrange
        using var order = new Order();
        
        // Act
        order.Place("ORD-001", 1500m);
        order.Confirm();
        order.Ship("TRACK-123");
        
        // Assert: State reflects all events
        Assert.True(order.State.IsPlaced);
        Assert.True(order.State.IsConfirmed);
        Assert.True(order.State.IsShipped);
        Assert.Equal("TRACK-123", order.State.TrackingNumber);
        Assert.Equal(1500m, order.State.Total);
    }
}
```

`JsonEventSerializer` stands for any `IEventSerializer`. See
[Testing Aggregates](../testing/testing-aggregates.md) for a reusable harness, and
[`docs/examples/03-testing/TestingAggregates.cs`](../examples/03-testing/TestingAggregates.cs)
for a version that the library's test suite compiles and runs.

### Testing State Transitions

Test applying events directly to state:

```csharp
public class OrderStateTests
{
    [Fact]
    public void Apply_OrderPlacedEvent_SetsState()
    {
        // Arrange
        var state = OrderState.Initial;
        var @event = new OrderPlacedEvent("ORD-001", 1500m);
        
        // Act
        var newState = state.Apply(@event);
        
        // Assert
        Assert.True(newState.IsPlaced);
        Assert.Equal(1500m, newState.Total);
        Assert.False(newState.IsConfirmed);  // Unchanged
    }

    [Fact]
    public void Apply_MultipleEvents_ChainsTogether()
    {
        // Arrange
        var state = OrderState.Initial;
        
        // Act: Apply multiple events in sequence
        state = state.Apply(new OrderPlacedEvent("ORD-001", 1500m));
        state = state.Apply(new OrderConfirmedEvent());
        state = state.Apply(new OrderShippedEvent("TRACK-123"));
        
        // Assert
        Assert.True(state.IsPlaced);
        Assert.True(state.IsConfirmed);
        Assert.True(state.IsShipped);
        Assert.Equal("TRACK-123", state.TrackingNumber);
    }
}
```

## Versioning Aggregates

As requirements evolve, your events change. Handle old and new event formats gracefully.

### Event Versioning Strategy

When you need to change an event:

1. **Keep the old event type** — Don't delete it
2. **Create a new event type** — With v2, v3, etc. suffix
3. **Handle both versions** — Add an `Apply` method for each version to the state

Example: OrderPlacedEvent gets a new field:

```csharp
// Original event (from 2024)
public record OrderPlacedEvent(string OrderId, decimal Total);

// New event (from 2025, with additional field)
public record OrderPlacedEventV2(
    string OrderId,
    decimal Total,
    string CustomerId);  // New field

// The generated ApplyEvent dispatches both versions, because the state has an
// Apply method for each. With a hand-written ApplyEvent, add a case for OrderPlacedEventV2.

// State handles both event types
public partial struct OrderState : IAggregateState<OrderState>
{
    public string? CustomerId { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with 
        { 
            IsPlaced = true, 
            Total = e.Total,
            CustomerId = null  // Old events didn't have this
        };
    
    internal OrderState Apply(OrderPlacedEventV2 e) =>
        this with 
        { 
            IsPlaced = true, 
            Total = e.Total,
            CustomerId = e.CustomerId
        };
}
```

### New Events with Defaults

For missing data in old events, use sensible defaults:

```csharp
public partial struct OrderState : IAggregateState<OrderState>
{
    public string CustomerId { get; private set; }
    public string Source { get; private set; }  // New field
    
    // Old events didn't have Source
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with 
        { 
            IsPlaced = true, 
            Total = e.Total,
            Source = "Unknown"  // Default for legacy events
        };
    
    // New events have explicit Source
    internal OrderState Apply(OrderPlacedEventV2 e) =>
        this with 
        { 
            IsPlaced = true, 
            Total = e.Total,
            Source = e.Source
        };
}
```

## IDE Support and Source Generators

### Enabling Source Generators

Add the NuGet package:

```bash
dotnet add package ZeroAlloc.EventSourcing.Generators
```

Make your aggregate `partial`, with `internal` `Apply` methods on its state:

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Generator creates ApplyEvent and OrderEventTypeRegistry
}
```

### View Generated Code

In Visual Studio, expand the project's **Dependencies → Analyzers → ZeroAlloc.EventSourcing.Generators**
node in Solution Explorer. For each aggregate there are two files:

- `<Namespace>.<Aggregate>.ApplyEvent.g.cs`, the `ApplyEvent` switch (from `AggregateDispatchGenerator`)
- `<Namespace>.<Aggregate>EventTypeRegistry.g.cs`, the event type registry (from `EventTypeRegistryGenerator`)

To write them to disk, set this in the project file and rebuild; they appear under
`obj/<Configuration>/<TargetFramework>/generated/ZeroAlloc.EventSourcing.Generators/`:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

## Common Pitfalls

### Pitfall 1: Mutable State

```csharp
// ✗ Bad: State is mutable
public partial struct OrderState : IAggregateState<OrderState>
{
    public List<string> Items { get; set; }  // Mutable!
    
    internal OrderState Apply(ItemAddedEvent e)
    {
        this.Items.Add(e.ItemId);  // Mutates state!
        return this;
    }
}

// ✓ Good: State is immutable
public partial struct OrderState : IAggregateState<OrderState>
{
    public IReadOnlyList<string> Items { get; private set; }  // Read-only
    
    internal OrderState Apply(ItemAddedEvent e) =>
        this with 
        { 
            Items = Items.Append(e.ItemId).ToList()  // New list
        };
}
```

### Pitfall 2: Side Effects in Apply

```csharp
// ✗ Bad: Side effects in Apply
public partial struct OrderState : IAggregateState<OrderState>
{
    internal OrderState Apply(OrderPlacedEvent e)
    {
        _emailService.SendOrderConfirmation(e.OrderId);  // Side effect!
        return this with { IsPlaced = true };
    }
}

// ✓ Good: Pure Apply, side effects in application layer
public partial struct OrderState : IAggregateState<OrderState>
{
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true };
}

// In application layer / saga
public class OrderPlacementSaga
{
    public async Task Handle(OrderPlacedEvent @event)
    {
        // Side effects happen here, not in Apply
        await _emailService.SendConfirmation(@event.OrderId);
    }
}
```

### Pitfall 3: Non-Deterministic State

```csharp
// ✗ Bad: Non-deterministic Apply
public partial struct OrderState : IAggregateState<OrderState>
{
    public DateTime PlacedAt { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { PlacedAt = DateTime.Now };  // Different every time!
}

// ✓ Good: Deterministic, use event timestamp
// The command puts the time on the event when it raises it
public record OrderPlacedEvent(string OrderNumber, decimal Total, DateTime PlacedAt);

public partial struct OrderState : IAggregateState<OrderState>
{
    public DateTime PlacedAt { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { PlacedAt = e.PlacedAt };  // From event
}
```

### Pitfall 4: Forgetting to Update Apply When Adding State

```csharp
// ✗ Bad: Added new state property but didn't update Apply
public partial struct OrderState : IAggregateState<OrderState>
{
    public bool IsPlaced { get; private set; }
    public DateTime PlacedAt { get; private set; }  // New property
    public string Source { get; private set; }      // New property
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true };  // Missing PlacedAt and Source!
}

// ✓ Good: Update Apply for all new properties, carried on the event
public record OrderPlacedEvent(string OrderNumber, decimal Total, DateTime PlacedAt, string? Source);

public partial struct OrderState : IAggregateState<OrderState>
{
    public bool IsPlaced { get; private set; }
    public DateTime PlacedAt { get; private set; }
    public string Source { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with 
        { 
            IsPlaced = true,
            PlacedAt = e.PlacedAt,
            Source = e.Source ?? "Direct"
        };
}
```

## Complete Production-Grade Example

```csharp
// ============ Domain Model ============

public readonly record struct OrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);

// Events
public record OrderPlacedEvent(
    string OrderNumber,
    CustomerId CustomerId,
    decimal Total,
    DateTime PlacedAt);

public record OrderConfirmedEvent(
    string PaymentTransactionId,
    DateTime ConfirmedAt);

public record OrderShippedEvent(
    string TrackingNumber,
    string Carrier,
    DateTime ShippedAt);

public record OrderDeliveredEvent(DateTime DeliveredAt);
public record OrderCancelledEvent(string Reason, DateTime CancelledAt);

// State
public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public bool IsDelivered { get; private set; }
    public bool IsCancelled { get; private set; }
    
    public string OrderNumber { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with
        {
            IsPlaced = true,
            OrderNumber = e.OrderNumber,
            CustomerId = e.CustomerId,
            Total = e.Total
        };

    internal OrderState Apply(OrderConfirmedEvent e) =>
        this with { IsConfirmed = true };

    internal OrderState Apply(OrderShippedEvent e) =>
        this with
        {
            IsShipped = true,
            TrackingNumber = e.TrackingNumber
        };

    internal OrderState Apply(OrderDeliveredEvent e) =>
        this with { IsDelivered = true };

    internal OrderState Apply(OrderCancelledEvent e) =>
        this with { IsCancelled = true };
}

// Aggregate: partial, so the source generator emits ApplyEvent and OrderEventTypeRegistry
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public string Status =>
        State.IsCancelled ? "Cancelled"
        : State.IsDelivered ? "Delivered"
        : State.IsShipped ? "Shipped"
        : State.IsConfirmed ? "Confirmed"
        : State.IsPlaced ? "Placed"
        : "Unknown";

    public void Place(string orderNumber, CustomerId customerId, decimal total)
    {
        if (State.IsPlaced)
            throw new InvalidOperationException("Order already placed");
        
        if (string.IsNullOrWhiteSpace(orderNumber))
            throw new ArgumentException("Order number required");
        
        if (total <= 0)
            throw new ArgumentException("Total must be positive");
        
        Raise(new OrderPlacedEvent(orderNumber, customerId, total, DateTime.UtcNow));
    }

    public void Confirm(string paymentTransactionId)
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot confirm unplaced order");
        
        if (State.IsConfirmed)
            throw new InvalidOperationException("Order already confirmed");
        
        Raise(new OrderConfirmedEvent(paymentTransactionId, DateTime.UtcNow));
    }

    public void Ship(string trackingNumber, string carrier)
    {
        if (!State.IsConfirmed)
            throw new InvalidOperationException("Cannot ship unconfirmed order");
        
        if (State.IsShipped)
            throw new InvalidOperationException("Order already shipped");
        
        Raise(new OrderShippedEvent(trackingNumber, carrier, DateTime.UtcNow));
    }

    public void Deliver()
    {
        if (!State.IsShipped)
            throw new InvalidOperationException("Cannot deliver unshipped order");
        
        if (State.IsDelivered)
            throw new InvalidOperationException("Order already delivered");
        
        Raise(new OrderDeliveredEvent(DateTime.UtcNow));
    }

    public void Cancel(string reason)
    {
        if (State.IsCancelled)
            throw new InvalidOperationException("Order already cancelled");
        
        if (State.IsShipped)
            throw new InvalidOperationException("Cannot cancel shipped order");
        
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Cancellation reason required");
        
        Raise(new OrderCancelledEvent(reason, DateTime.UtcNow));
    }

    public void SetId(OrderId id) => Id = id;

    // Source generator creates ApplyEvent automatically
}
```

## Summary

Clean aggregate code requires:

1. **Structure** — Inherit from `Aggregate<TId, TState>`, use sealed partial classes
2. **State** — Structs with private setters and pure Apply methods
3. **Commands** — Public methods that validate then raise events
4. **Testing** — Test commands and state transitions without mocks
5. **Versioning** — Handle multiple event versions gracefully
6. **No side effects** — Keep Apply pure, move side effects to application layer

## Next Steps

- **[Replay and Rebuilding](./replay-rebuilding.md)** — Load aggregates efficiently
- **[Testing Aggregates](../testing/)** — Comprehensive testing strategies
- **[Core Concepts: Aggregates](../core-concepts/aggregates.md)** — Deep design patterns
