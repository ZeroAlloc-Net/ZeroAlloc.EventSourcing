# Usage Guide: Domain Modeling

## Scenario: How Do I Model My Domain for Event Sourcing?

Event sourcing is not just a persistence pattern—it's a way of thinking about your domain. This guide shows how to design aggregates, events, and state from domain scenarios, helping you build systems that are both auditable and maintainable.

## From Traditional Objects to Event-Sourced Aggregates

In traditional ORM approaches, you model state directly:

```csharp
// Traditional: Mutable state
public class Order
{
    public Guid Id { get; set; }
    public string Status { get; set; }
    public decimal Total { get; set; }
    public string? TrackingNumber { get; set; }
    
    public void Ship(string tracking)
    {
        this.Status = "Shipped";
        this.TrackingNumber = tracking;
    }
}
```

In event sourcing, you model behavior and facts instead:

```csharp
// Event Sourcing: Immutable facts of what happened
public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderShippedEvent(string TrackingNumber);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public bool IsPlaced { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };
    
    internal OrderState Apply(OrderShippedEvent e) =>
        this with { TrackingNumber = e.TrackingNumber };
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Ship(string tracking)
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot ship unplaced order");
        Raise(new OrderShippedEvent(tracking));
    }
}
```

**Key difference:** Instead of mutating state, you raise immutable events and apply them to derive state.

## Identifying Aggregate Boundaries

An aggregate is a consistency boundary—a group of related entities that must be kept consistent together. Choosing the right boundaries is crucial.

### Consistency vs. Transactions

Use a single aggregate when:

1. **Consistency must be transactional** — Updates must never be partially applied
2. **State is tightly coupled** — Changes to one field require changes to another
3. **Validation spans multiple fields** — Rules reference multiple properties

Example: Order and OrderLineItems should be a single aggregate because:
- Adding an item must update the total atomically
- Removing an item requires recalculating the total
- Validation: "Total must equal sum of items"

```csharp
public readonly record struct OrderId(Guid Value);

public record OrderPlacedEvent(string OrderId, decimal Total, OrderLineItem[] Items);
public record OrderLineItemAddedEvent(string ItemId, decimal UnitPrice, int Quantity);
public record OrderLineItemRemovedEvent(string ItemId);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    
    public bool IsPlaced { get; private set; }
    public decimal Total { get; private set; }
    public Dictionary<string, OrderLineItem> Items { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with 
        { 
            IsPlaced = true, 
            Total = e.Total,
            Items = e.Items.ToDictionary(i => i.ItemId)
        };
    
    internal OrderState Apply(OrderLineItemAddedEvent e) =>
        this with 
        { 
            Total = Total + (e.UnitPrice * e.Quantity),
            Items = Items.Append(new(e.ItemId, e.UnitPrice, e.Quantity))
                .ToDictionary(i => i.ItemId)
        };
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void AddLineItem(string itemId, decimal unitPrice, int quantity)
    {
        if (State.Items.ContainsKey(itemId))
            throw new InvalidOperationException("Item already exists");
        Raise(new OrderLineItemAddedEvent(itemId, unitPrice, quantity));
    }
}

public record OrderLineItem(string ItemId, decimal UnitPrice, int Quantity);
```

Use separate aggregates when:

1. **Consistency is eventual** — Updates can be asynchronous
2. **Scales independently** — Different access patterns or lifetimes
3. **Owned by different services** — Microservices architecture

Example: Order and Customer should be separate aggregates because:
- Customer updates don't block order updates
- Customers may exist without orders
- Different services may manage them

```csharp
// Separate aggregates
public readonly record struct OrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);

public record OrderPlacedEvent(string OrderId, CustomerId CustomerId, decimal Total);

// Order only references CustomerId, not the entire Customer object
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderId, CustomerId customerId, decimal total)
    {
        Raise(new OrderPlacedEvent(orderId, customerId, total));
    }
}
```

## Designing Events from Domain Scenarios

Start with domain scenarios, not database tables. Think about what happens in your business.

### Scenario: Order Lifecycle

**The story:**
1. Customer places an order with items
2. Order is confirmed (payment approved)
3. Order is shipped
4. Customer receives it
5. Customer may return items

**Events that represent this:**

```csharp
public record OrderPlacedEvent(
    string OrderId,
    string CustomerId,
    OrderLineItem[] Items,
    decimal Total,
    DateTime PlacedAt);

public record OrderConfirmedEvent(
    string PaymentTransactionId,
    DateTime ConfirmedAt);

public record OrderShippedEvent(
    string TrackingNumber,
    string Carrier,
    DateTime ShippedAt);

public record OrderDeliveredEvent(
    DateTime DeliveredAt);

public record OrderReturnRequestedEvent(
    string ReturnId,
    OrderLineItem[] ItemsToReturn,
    decimal RefundAmount,
    DateTime RequestedAt);

public record OrderReturnCompletedEvent(
    string ReturnId,
    decimal RefundedAmount,
    DateTime CompletedAt);

public record OrderLineItem(
    string ItemId,
    string ItemName,
    decimal UnitPrice,
    int Quantity);
```

**Why these events?**

- **Immutable facts** — Each event records what actually happened, not predictions
- **Complete data** — Include all context needed (TrackingNumber, Carrier, not just Status="Shipped")
- **Audit trail** — Future auditors can reconstruct the full order history
- **Temporal queries** — Ask "when was order confirmed?" directly from events
- **Domain language** — Events use business terminology (OrderShipped, not Order.Status=2)

## State Design: Immutable Structs and Tracking Changes

State is derived from events. Design it to capture what the aggregate knows at any moment.

### Small, Focused State

```csharp
public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    
    // Minimal state: only what's needed for behavior
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public bool IsDelivered { get; private set; }
    public bool HasReturnRequest { get; private set; }
    
    public CustomerId CustomerId { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    
    // Apply events to derive new state
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with 
        { 
            IsPlaced = true,
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
    
    internal OrderState Apply(OrderReturnRequestedEvent e) =>
        this with { HasReturnRequest = true };
}
```

### Computed Properties (Careful!)

Avoid storing data that can be derived from other state:

```csharp
public partial struct OrderState : IAggregateState<OrderState>
{
    // ✓ Good: Store the base facts
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    
    // ✗ Problematic: Derived state that duplicates other fields
    public string Status 
    { 
        get 
        {
            return IsShipped ? "Shipped"
                : IsConfirmed ? "Confirmed"
                : IsPlaced ? "Placed"
                : "Unknown";
        }
    }
    
    // Better: Computed on-demand in the aggregate
    // (not stored in state)
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Computed property (not part of state)
    public string Status
    {
        get
        {
            return State.IsShipped ? "Shipped"
                : State.IsConfirmed ? "Confirmed"
                : State.IsPlaced ? "Placed"
                : "Unknown";
        }
    }
}
```

## Command Validation Patterns

Validation happens before events are raised. The question is: "Can this action happen right now?"

### Validation in Commands

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Confirm(string paymentTransactionId)
    {
        // Validation 1: Can only confirm if placed
        if (!State.IsPlaced)
            throw new InvalidOperationException(
                "Cannot confirm order that hasn't been placed");
        
        // Validation 2: Can't confirm twice
        if (State.IsConfirmed)
            throw new InvalidOperationException(
                "Order is already confirmed");
        
        // Validation 3: Business rule
        if (State.Total <= 0)
            throw new InvalidOperationException(
                "Cannot confirm order with zero or negative total");
        
        // All validations passed, raise the event
        Raise(new OrderConfirmedEvent(paymentTransactionId, DateTime.UtcNow));
    }
    
    public void RequestReturn(string[] itemIds, string reason)
    {
        // Validation 1: Can only return shipped orders
        if (!State.IsDelivered)
            throw new InvalidOperationException(
                "Cannot return undelivered order");
        
        // Validation 2: Can't request return twice
        if (State.HasReturnRequest)
            throw new InvalidOperationException(
                "Return already requested");
        
        // Validation 3: Must return something
        if (itemIds == null || itemIds.Length == 0)
            throw new ArgumentException(
                "Must specify at least one item to return");
        
        var returnId = Guid.NewGuid().ToString();
        var refundAmount = CalculateRefund(itemIds);
        
        Raise(new OrderReturnRequestedEvent(
            returnId, 
            itemIds,
            refundAmount,
            DateTime.UtcNow));
    }
    
    private decimal CalculateRefund(string[] itemIds)
    {
        // Implement refund calculation logic
        return 0m;  // Placeholder
    }
}
```

### Testing Validation

```csharp
[Fact]
public void Confirm_ThrowsWhen_OrderNotPlaced()
{
    // Arrange
    var order = new Order();
    order.SetId(new OrderId(Guid.NewGuid()));
    
    // Act & Assert
    var ex = Assert.Throws<InvalidOperationException>(
        () => order.Confirm("tx-123"));
    
    Assert.Contains("not been placed", ex.Message);
}

[Fact]
public void Confirm_RaisesEvent_WhenValid()
{
    // Arrange
    var order = new Order();
    order.SetId(new OrderId(Guid.NewGuid()));
    order.Place("ORD-001", 1500m);
    order.DequeueUncommitted();  // Clear initial events
    
    // Act
    order.Confirm("tx-123");
    
    // Assert
    var events = order.DequeueUncommitted();
    Assert.Single(events);
    Assert.IsType<OrderConfirmedEvent>(events[0]);
}
```

## Identity Design: Value Types for IDs

Aggregate IDs should be value types for performance and type safety:

```csharp
// ✓ Good: Readonly record struct
public readonly record struct OrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);
public readonly record struct InvoiceId(Guid Value);

// Benefits:
// 1. Stack-allocated (no heap pressure)
// 2. Value equality (new OrderId(guid) == new OrderId(guid))
// 3. Hashable (can use in dictionaries/sets)
// 4. Type-safe (can't confuse OrderId with CustomerId)

// Usage
var orderId = new OrderId(Guid.NewGuid());
var order = new Order();
order.SetId(orderId);

// ✗ Avoid: Using raw Guid
var order = new Order();
// What is this Guid? No type safety!
```

### ID Generation

Generate IDs at the application layer, not in the aggregate:

```csharp
// Application/Command Handler
public class PlaceOrderHandler
{
    private readonly IAggregateRepository<Order, OrderId> _repository;
    
    public async Task Handle(PlaceOrderCommand command)
    {
        // Generate ID at the application layer
        var orderId = new OrderId(Guid.NewGuid());
        
        // Create aggregate
        var order = new Order();
        order.SetId(orderId);
        
        // Apply the command
        order.Place(
            orderNumber: "ORD-" + DateTime.Now.Ticks,
            customerId: command.CustomerId,
            total: command.Total);
        
        // Save
        await _repository.SaveAsync(order);
    }
}
```

## Complete Order Domain Example

Here's a complete, production-grade Order domain:

```csharp
// IDs
public readonly record struct OrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);

// Events
public record OrderPlacedEvent(
    string OrderNumber,
    CustomerId CustomerId,
    OrderLineItem[] Items,
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

public record OrderCancelledEvent(
    string Reason,
    DateTime CancelledAt);

public record OrderLineItem(
    string ItemId,
    string ItemName,
    decimal UnitPrice,
    int Quantity);

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
    public string TrackingNumber { get; private set; }
    
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
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public string Status => State.IsCancelled ? "Cancelled"
        : State.IsDelivered ? "Delivered"
        : State.IsShipped ? "Shipped"
        : State.IsConfirmed ? "Confirmed"
        : State.IsPlaced ? "Placed"
        : "Unknown";

    public void Place(string orderNumber, CustomerId customerId, 
        OrderLineItem[] items, decimal total)
    {
        if (State.IsPlaced)
            throw new InvalidOperationException(
                "Order has already been placed");
        
        if (items == null || items.Length == 0)
            throw new ArgumentException("Order must have items");
        
        if (total <= 0)
            throw new ArgumentException("Total must be positive");
        
        Raise(new OrderPlacedEvent(orderNumber, customerId, items, 
            total, DateTime.UtcNow));
    }

    public void Confirm(string paymentTransactionId)
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException(
                "Cannot confirm unplaced order");
        
        if (State.IsConfirmed)
            throw new InvalidOperationException(
                "Order is already confirmed");
        
        Raise(new OrderConfirmedEvent(paymentTransactionId, DateTime.UtcNow));
    }

    public void Ship(string trackingNumber, string carrier)
    {
        if (!State.IsConfirmed)
            throw new InvalidOperationException(
                "Cannot ship unconfirmed order");
        
        if (State.IsShipped)
            throw new InvalidOperationException(
                "Order is already shipped");
        
        Raise(new OrderShippedEvent(trackingNumber, carrier, DateTime.UtcNow));
    }

    public void Deliver()
    {
        if (!State.IsShipped)
            throw new InvalidOperationException(
                "Cannot deliver unshipped order");
        
        if (State.IsDelivered)
            throw new InvalidOperationException(
                "Order is already delivered");
        
        Raise(new OrderDeliveredEvent(DateTime.UtcNow));
    }

    public void Cancel(string reason)
    {
        if (State.IsCancelled)
            throw new InvalidOperationException(
                "Order is already cancelled");
        
        if (State.IsShipped)
            throw new InvalidOperationException(
                "Cannot cancel shipped order");
        
        Raise(new OrderCancelledEvent(reason, DateTime.UtcNow));
    }

    public void SetId(OrderId id) => Id = id;

    protected override OrderState ApplyEvent(OrderState state, object @event) =>
        @event switch
        {
            OrderPlacedEvent e => state.Apply(e),
            OrderConfirmedEvent e => state.Apply(e),
            OrderShippedEvent e => state.Apply(e),
            OrderDeliveredEvent e => state.Apply(e),
            OrderCancelledEvent e => state.Apply(e),
            _ => state
        };
}
```

## When NOT to Use Event Sourcing

Not every domain benefits from event sourcing. Avoid it for:

1. **Simple CRUD applications** — Blog posts, basic admin panels
2. **Pure reporting systems** — Read-only analytics dashboards
3. **Systems with extreme throughput** — Millions of events per second
4. **Uncertain domains** — Early-stage products with unstable requirements
5. **Small teams with no CQRS experience** — Learning curve is real

## Common Modeling Mistakes and Fixes

### Mistake 1: Storing too much state

```csharp
// ✗ Bad: Redundant state
public partial struct OrderState : IAggregateState<OrderState>
{
    public decimal Total { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal Subtotal { get; private set; }  // Derivable from Total and Tax
    public decimal DiscountAmount { get; private set; }
    public List<OrderLineItem> Items { get; private set; }  // Total is derivable from Items
}

// ✓ Good: Store facts, derive the rest
public partial struct OrderState : IAggregateState<OrderState>
{
    public decimal Total { get; private set; }
    public List<OrderLineItem> Items { get; private set; }
    
    // These are computed in the aggregate, not stored
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public decimal CalculateSubtotal() => State.Items.Sum(i => i.UnitPrice * i.Quantity);
    public decimal CalculateTax() => CalculateSubtotal() * 0.1m;
}
```

### Mistake 2: Mixing domain events with technical events

```csharp
// ✗ Bad: Technical event mixed with domain events
public record OrderCreatedTechnicallyEvent;  // When?
public record OrderPlacedEvent(string OrderId, decimal Total);  // Domain fact

// ✓ Good: Only domain events
public record OrderPlacedEvent(string OrderId, decimal Total);
// If you need to track "created," use OrderPlaced as the creation event
```

### Mistake 3: Events that are too fine-grained

```csharp
// ✗ Bad: Too granular
public record TotalChangedEvent(decimal NewTotal);
public record ItemAddedEvent(string ItemId);
public record PriceUpdatedEvent(decimal NewPrice);

// ✓ Good: Capture complete business operations
public record OrderLineItemAddedEvent(string ItemId, string Name, decimal UnitPrice, int Quantity);
```

### Mistake 4: Silently losing invalid state transitions

```csharp
// ✗ Bad: Ignores invalid transitions
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Confirm()
    {
        // Silently does nothing if order not placed
        if (State.IsPlaced)
            Raise(new OrderConfirmedEvent());
    }
}

// ✓ Good: Explicit about failures
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Confirm()
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException(
                "Cannot confirm unplaced order");
        Raise(new OrderConfirmedEvent());
    }
}
```

## Summary

Effective domain modeling for event sourcing requires:

1. **Think in events, not mutations** — "What happened?" not "What changed?"
2. **Choose aggregate boundaries carefully** — Consistency vs. eventual consistency tradeoff
3. **Design events from domain scenarios** — Events are facts, not instructions
4. **Keep state minimal** — Store what matters, derive the rest
5. **Validate before raising events** — Ensure consistency at the aggregate boundary
6. **Use value types for IDs** — Type safety and allocation efficiency
7. **Test validation thoroughly** — Invalid states should be impossible

## Next Steps

- **[Building Aggregates](./building-aggregates.md)** — Implement clean, testable aggregate code
- **[Core Concepts: Aggregates](../core-concepts/aggregates.md)** — Deep dive into aggregate patterns
- **[Using Projections](./projections-usage.md)** — Query your event-sourced data efficiently
