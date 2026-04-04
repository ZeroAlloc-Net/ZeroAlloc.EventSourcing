# Core Concepts: Aggregates

An aggregate is the command handler in your domain model. It encapsulates business logic, enforces consistency rules, and raises domain events. In event sourcing, aggregates are the bridge between the user's intent (commands) and the immutable record of what happened (events).

## What is an Aggregate?

An aggregate is a cluster of domain objects treated as a single unit. It has:

1. **Identity** — A unique identifier (OrderId, CustomerId, etc.)
2. **State** — The current condition, derived from events
3. **Behavior** — Methods that handle commands and raise events
4. **Consistency boundary** — Rules that must always be true

**The order aggregate example:**

```csharp
public readonly record struct OrderId(Guid Value);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderId, decimal total)
    {
        Raise(new OrderPlacedEvent(orderId, total));
    }

    public void Confirm()
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot confirm before placing");
        Raise(new OrderConfirmedEvent());
    }

    public void Ship(string trackingNumber)
    {
        if (!State.IsConfirmed)
            throw new InvalidOperationException("Cannot ship before confirming");
        Raise(new OrderShippedEvent(trackingNumber));
    }

    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderConfirmedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        _ => state
    };
}
```

The aggregate is responsible for:
- Validating commands before accepting them
- Raising events that represent what happened
- Maintaining the current state
- Replaying from history to reconstruct state

## Aggregate Identity

Every aggregate has a unique identifier. The ID should be:

- **A value type** (struct) — Allocation-free and efficiently hashable
- **Immutable** — Once set, it never changes
- **Unique** — No two aggregates can share the same ID

**Defining an aggregate ID:**

```csharp
// ✓ Good: Value type, immutable, clear domain meaning
public readonly record struct OrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);
public readonly record struct InvoiceId(Guid Value);

// Alternative: Without record syntax (also fine)
public readonly struct OrderId
{
    public Guid Value { get; }
    public OrderId(Guid value) => Value = value;
}
```

Using `readonly record struct` is recommended because it:
- Avoids heap allocations
- Is immutable by default
- Supports value equality (two OrderIds with the same Guid are equal)
- Is concise and readable

**Using aggregate IDs:**

```csharp
var orderId = new OrderId(Guid.NewGuid());
var order = new Order();
order.SetId(orderId);  // Set the ID once during creation

// Later, use the ID to load the order from the store
var streamId = new StreamId($"order-{orderId.Value}");
await eventStore.ReadAsync(streamId, ...);
```

## Aggregate Roots vs. Entities

In domain-driven design (DDD), an aggregate consists of:

- **Aggregate Root** — The main entity that owns the identity and enforces consistency
- **Entities** — Objects within the aggregate that have identity but can only be accessed through the root
- **Value Objects** — Immutable objects with no identity

In the Order aggregate:

```csharp
public readonly record struct OrderId(Guid Value);  // Value type (ID)

public partial struct OrderState : IAggregateState<OrderState>
{
    public List<LineItem> Items { get; private set; }  // Entities (no independent ID)
}

public record LineItem(string ProductId, int Quantity, decimal Price);  // Value object

public sealed partial class Order : Aggregate<OrderId, OrderState>  // Aggregate root
{
    // Only the root (Order) can be loaded from the event store
    // LineItems are only accessed through Order
}
```

**Key principle:** You load aggregates by their root ID, never by sub-entity IDs. The aggregate root is responsible for all entities within it.

## State Definition

The aggregate's state is a `struct` that implements `IAggregateState<T>`. It must:

1. Be a `struct` (allocation-free during replay)
2. Implement `IAggregateState<T>`
3. Provide an `Initial` property
4. Have immutable Apply methods

```csharp
public partial struct OrderState : IAggregateState<OrderState>
{
    // Required: Define initial empty state
    public static OrderState Initial => default;
    
    // State properties
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }

    // Apply methods are pure functions: state + event → new state
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };

    internal OrderState Apply(OrderConfirmedEvent e) =>
        this with { IsConfirmed = true };

    internal OrderState Apply(OrderShippedEvent e) =>
        this with { IsShipped = true, TrackingNumber = e.TrackingNumber };
}
```

**Why a struct?**
- Structs are stack-allocated, avoiding GC pressure
- During replay (applying thousands of events), each event creates a new state via immutable updates
- Using structs means these state values don't allocate on the heap
- This is why the library is called "ZeroAlloc.EventSourcing" — it minimizes allocations during the expensive replay operation

**Why immutable?**
- Apply methods use `with` to produce new state, never mutating in place
- This ensures state transitions are pure functions: same event + same state = same new state
- Pure functions are testable, debuggable, and safe to replay

## Behavior Patterns

Aggregates define behavior through command methods. Command methods:

1. Validate the command against current state
2. Raise events if validation passes
3. Never directly mutate state (always use `Raise()`)

### Simple Command (No Validation)

```csharp
public void Confirm()
{
    Raise(new OrderConfirmedEvent());
}
```

### Command with Validation

```csharp
public void Ship(string trackingNumber)
{
    // Validate before raising
    if (!State.IsConfirmed)
        throw new InvalidOperationException("Cannot ship unconfirmed order");
    
    Raise(new OrderShippedEvent(trackingNumber));
}
```

### Conditional Behavior

```csharp
public void Refund(decimal amount)
{
    if (!State.IsShipped && !State.IsDelivered)
        throw new InvalidOperationException("Cannot refund unshipped order");
    
    if (amount > State.Total)
        throw new InvalidOperationException("Refund exceeds order total");
    
    Raise(new RefundInitiatedEvent(amount));
}
```

### Multiple Events from One Command

Sometimes a single command raises multiple events:

```csharp
public void ProcessAndConfirm(decimal paymentAmount)
{
    if (paymentAmount != State.Total)
        throw new InvalidOperationException("Payment amount must match order total");
    
    Raise(new PaymentProcessedEvent(paymentAmount));
    Raise(new OrderConfirmedEvent());  // Multiple events
}
```

## Aggregate Lifecycle

An aggregate goes through distinct phases:

### 1. Creation

```csharp
var order = new Order();
order.SetId(new OrderId(Guid.NewGuid()));
order.Place("ORD-001", 1500m);  // First command

var events = order.DequeueUncommitted();  // Get the events
await eventStore.AppendAsync(streamId, events, StreamPosition.Start);
```

### 2. In-Memory Modification

```csharp
order.Confirm();    // State is updated in memory
order.Ship("TRACK-123");
var newEvents = order.DequeueUncommitted();
```

### 3. Persistence

```csharp
// Save new events to the store
var result = await eventStore.AppendAsync(
    streamId,
    newEvents,
    order.OriginalVersion  // For optimistic locking
);

if (result.IsSuccess)
    order.AcceptVersion(result.Value.Position);  // Update version tracking
```

### 4. Loading from History

```csharp
var loadedOrder = new Order();
loadedOrder.SetId(orderId);

// Replay all events to reconstruct state
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    loadedOrder.ApplyHistoric(envelope.Event, envelope.Position);
}

// loadedOrder is now fully reconstructed
loadedOrder.Ship("TRACK-456");  // Can continue modifying
```

## Consistency Boundaries

A consistency boundary defines what must always be true. In event sourcing, you enforce consistency through validation in command methods.

**Example: Order consistency rules**

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Rule 1: Order must be placed before shipping
    public void Ship(string trackingNumber)
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot ship unplaced order");
        Raise(new OrderShippedEvent(trackingNumber));
    }

    // Rule 2: Order must be confirmed before shipping
    public void Confirm()
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot confirm unplaced order");
        Raise(new OrderConfirmedEvent());
    }

    // Rule 3: Cannot confirm twice
    public void Confirm()
    {
        if (State.IsConfirmed)
            throw new InvalidOperationException("Order already confirmed");
        Raise(new OrderConfirmedEvent());
    }
}
```

These rules enforce a state machine: Order must follow the path: Placed → Confirmed → Shipped.

**What belongs in the aggregate vs. outside?**

| In the aggregate | Outside the aggregate |
|---|---|
| State specific to this order | Queries across multiple orders |
| Rules that must be enforced before events | Read models (projections) |
| Business logic for this entity | Reporting and analytics |
| Validation of commands | Communication with other services |

If you need to check a rule across multiple aggregates ("Can we ship this order if customer is blocked?"), that check should happen outside the aggregate, before you call a command method.

## Comparison with Traditional ORM Entities

### Traditional ORM (e.g., Entity Framework)

```csharp
public class Order  // Persisted class
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; }

    public void Ship(string trackingNumber)
    {
        // Mutate state directly
        this.Status = "Shipped";
        this.TrackingNumber = trackingNumber;
    }
}

var order = db.Orders.Find(orderId);
order.Ship("TRACK-123");
db.SaveChanges();  // Updates the row in database
```

**Issues:**
- No history of how the order reached its current state
- Difficult to audit or debug state changes
- Concurrent modifications can lose updates silently
- Temporal queries ("What was this order's state on April 1?") are hard

### Event-Sourced Aggregate

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Ship(string trackingNumber)
    {
        // Validate, then raise an event
        if (!State.IsConfirmed)
            throw new InvalidOperationException("Cannot ship unconfirmed order");
        Raise(new OrderShippedEvent(trackingNumber));
    }
}

var order = await repository.LoadAsync(orderId);
order.Ship("TRACK-123");
await repository.SaveAsync(order);  // Appends events to store
```

**Benefits:**
- Complete audit trail of every state change
- State is derived from immutable events
- Concurrent modifications are detected via position-based locking
- Temporal queries are trivial (replay events up to a point in time)
- Debugging is easier (full event history tells the story)

## Aggregate Versioning

Aggregates can evolve without breaking history. When you need to add behavior:

**Original aggregate:**
```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderId, decimal total)
    {
        Raise(new OrderPlacedEvent(orderId, total));
    }

    public void Ship(string trackingNumber)
    {
        Raise(new OrderShippedEvent(trackingNumber));
    }
}
```

**Later, you add refund support:**
```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderId, decimal total) { ... }
    public void Ship(string trackingNumber) { ... }
    
    // New behavior
    public void Refund(decimal amount)
    {
        if (!State.IsShipped)
            throw new InvalidOperationException("Cannot refund unshipped order");
        Raise(new RefundInitiatedEvent(amount));
    }

    // Handle new event type in ApplyEvent
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        RefundInitiatedEvent e => state.Apply(e),  // New
        _ => state
    };
}
```

Old orders (without any refund events) continue to work. New orders can use refunds. The event history is always consistent.

## Summary

Aggregates are:
- **Command handlers** — Process user intent and raise events
- **Consistency enforcers** — Validate rules before accepting commands
- **State containers** — Hold current state derived from events
- **Boundaries** — Define what must always be true together
- **Identity holders** — Unique, immutable ID (a value type)
- **Struct-based state** — Allocation-efficient during replay
- **Immutable transitions** — State updates via pure Apply methods

Aggregates sit at the heart of event sourcing: they translate commands into immutable events, maintain consistency, and support replay from history.

## Next Steps

- **[Core Concepts: Events](./events.md)** — The events aggregates raise
- **[Core Concepts: Event Store](./event-store.md)** — How events are persisted
- **[Quick Start Example](../getting-started/quick-start-example.md)** — Complete working Order aggregate
