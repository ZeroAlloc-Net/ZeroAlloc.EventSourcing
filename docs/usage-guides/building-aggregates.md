# Usage Guide: Building Aggregates

## Scenario: How Do I Write Aggregate Code That's Clean and Maintainable?

Aggregates are the core of your event-sourced domain model. This guide shows patterns for structuring aggregates, handling state transitions, testing, and evolving behavior over time.

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
    
    // 3. Event dispatcher (ApplyEvent implementation)
    protected override OrderState ApplyEvent(OrderState state, object @event) => ...;
}
```

### Key Design Principles

1. **Sealed classes** — Inheritance defeats the single-responsibility principle
2. **Partial for generators** — Source generators can auto-generate `ApplyEvent`
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
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    
    // 3. Apply methods (pure functions)
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };
    
    internal OrderState Apply(OrderConfirmedEvent e) =>
        this with { IsConfirmed = true };
    
    internal OrderState Apply(OrderShippedEvent e) =>
        this with { IsPlaced = false, TrackingNumber = e.TrackingNumber };
}
```

### Why Structs?

- **Stack allocation** — No heap pressure during event replay
- **Value semantics** — Each state is independent
- **Immutability via `with`** — Natural update syntax

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
public void PlaceAndConfirm(string orderNumber, decimal total, string paymentId)
{
    // Validate
    if (State.IsPlaced)
        throw new InvalidOperationException("Order already placed");
    
    // Raise multiple events atomically
    Raise(new OrderPlacedEvent(orderNumber, total));
    Raise(new OrderConfirmedEvent(paymentId));
    
    // Both events are now in the aggregate's uncommitted list
    // When saved, both are persisted as a single transaction
}

// Later: Get all uncommitted events
var events = order.DequeueUncommitted();  // [OrderPlacedEvent, OrderConfirmedEvent]
```

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

ZeroAlloc.EventSourcing can auto-generate this dispatcher:

```csharp
// Decorate with [AggregateDispatch]
[AggregateDispatch]
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Source generator creates ApplyEvent automatically!
    // No need to hand-write the dispatcher
}
```

The generator:
- Finds all `Apply` methods in the state struct
- Creates a dispatch method that routes events to them
- Ensures compile-time type safety

## Testing Aggregates

Test aggregates by calling commands and asserting on raised events:

```csharp
public class OrderTests
{
    // Arrange: Create aggregate
    private Order CreateOrder()
    {
        var order = new Order();
        order.SetId(new OrderId(Guid.NewGuid()));
        return order;
    }

    [Fact]
    public void Place_RaisesOrderPlacedEvent()
    {
        // Arrange
        var order = CreateOrder();
        
        // Act
        order.Place("ORD-001", 1500m);
        
        // Assert: Check raised events
        var events = order.DequeueUncommitted();
        Assert.Single(events);
        Assert.IsType<OrderPlacedEvent>(events[0]);
        
        var placedEvent = (OrderPlacedEvent)events[0];
        Assert.Equal("ORD-001", placedEvent.OrderId);
        Assert.Equal(1500m, placedEvent.Total);
    }

    [Fact]
    public void Confirm_ThrowsWhen_OrderNotPlaced()
    {
        // Arrange
        var order = CreateOrder();
        
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => order.Confirm());
        
        Assert.Contains("not been placed", ex.Message);
    }

    [Fact]
    public void Confirm_RaisesEvent_WhenValid()
    {
        // Arrange
        var order = CreateOrder();
        order.Place("ORD-001", 1500m);
        order.DequeueUncommitted();  // Clear initial events
        
        // Act
        order.Confirm();
        
        // Assert
        var events = order.DequeueUncommitted();
        Assert.Single(events);
        Assert.IsType<OrderConfirmedEvent>(events[0]);
    }

    [Fact]
    public void Ship_ThrowsWhen_OrderNotConfirmed()
    {
        // Arrange
        var order = CreateOrder();
        order.Place("ORD-001", 1500m);
        
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => order.Ship("TRACK-123"));
        
        Assert.Contains("not confirmed", ex.Message);
    }

    [Fact]
    public void Ship_RaisesEvent_WhenValid()
    {
        // Arrange
        var order = CreateOrder();
        order.Place("ORD-001", 1500m);
        order.Confirm();
        order.DequeueUncommitted();  // Clear previous events
        
        // Act
        order.Ship("TRACK-123");
        
        // Assert
        var events = order.DequeueUncommitted();
        Assert.Single(events);
        var shippedEvent = Assert.IsType<OrderShippedEvent>(events[0]);
        Assert.Equal("TRACK-123", shippedEvent.TrackingNumber);
    }

    [Fact]
    public void StateReflectsAllRaisedEvents()
    {
        // Arrange
        var order = CreateOrder();
        
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
3. **Handle both versions** — Update the dispatcher to handle both

Example: OrderPlacedEvent gets a new field:

```csharp
// Original event (from 2024)
public record OrderPlacedEvent(string OrderId, decimal Total);

// New event (from 2025, with additional field)
public record OrderPlacedEventV2(
    string OrderId,
    decimal Total,
    string CustomerId);  // New field

// State dispatcher handles both
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    protected override OrderState ApplyEvent(OrderState state, object @event) =>
        @event switch
        {
            // Handle both old and new versions
            OrderPlacedEvent e => state.Apply(e),
            OrderPlacedEventV2 e => state.Apply(e),
            OrderConfirmedEvent e => state.Apply(e),
            _ => state
        };
}

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

Decorate your aggregate:

```csharp
[AggregateDispatch]
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Generator creates ApplyEvent
}
```

### View Generated Code

In Visual Studio:
1. Right-click project → Properties
2. Build → Outputs → Tick "Generate single file"
3. Open `obj/Debug/net8.0/YourProject.GlobalUsings.g.cs`

Or use a tool like `dotnet-script` to examine generated code.

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

// ✓ Good: Update Apply for all new properties
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
    
    public OrderState Apply(OrderPlacedEvent e) =>
        this with
        {
            IsPlaced = true,
            OrderNumber = e.OrderNumber,
            CustomerId = e.CustomerId,
            Total = e.Total
        };
    
    public OrderState Apply(OrderConfirmedEvent e) =>
        this with { IsConfirmed = true };
    
    public OrderState Apply(OrderShippedEvent e) =>
        this with
        {
            IsShipped = true,
            TrackingNumber = e.TrackingNumber
        };
    
    public OrderState Apply(OrderDeliveredEvent e) =>
        this with { IsDelivered = true };
    
    public OrderState Apply(OrderCancelledEvent e) =>
        this with { IsCancelled = true };
}

// Aggregate
[AggregateDispatch]
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
