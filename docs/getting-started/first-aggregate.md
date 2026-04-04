# Your First Aggregate

Build your first event-sourced aggregate in 5 minutes.

## What You'll Build

An `Order` aggregate that:
- Creates orders with items
- Tracks order status (pending, confirmed, shipped)
- Applies events to maintain state

## Step 1: Define Events

Events represent things that happened in your domain.

```csharp
using ZeroAlloc.EventSourcing;

namespace MyProject.Domain.Events;

public record OrderCreated(OrderId OrderId, CustomerId CustomerId, decimal Amount)
{
    public static EventType EventType => new("order-created");
}

public record ItemAdded(string ItemId, decimal Price)
{
    public static EventType EventType => new("item-added");
}

public record OrderShipped
{
    public static EventType EventType => new("order-shipped");
}
```

## Step 2: Define State

State is the current condition of your aggregate.

```csharp
namespace MyProject.Domain;

public struct OrderState
{
    public OrderId OrderId { get; set; }
    public CustomerId CustomerId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Pending";
    public List<(string ItemId, decimal Price)> Items { get; set; } = [];
}
```

## Step 3: Define Your Aggregate

Aggregates contain behavior and apply events.

```csharp
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;

namespace MyProject.Domain.Aggregates;

public class Order : IAggregate<OrderState>
{
    private OrderState _state = new();
    
    public StreamId StreamId { get; private set; }
    public OrderState State => _state;
    public long Version { get; private set; }
    public IReadOnlyList<object> UncommittedEvents { get; } = new List<object>();

    // Factory method for new orders
    public static Order CreateOrder(OrderId id, CustomerId customerId, decimal amount)
    {
        var order = new Order();
        order.Raise(new OrderCreated(id, customerId, amount));
        return order;
    }

    // Add item to order
    public void AddItem(string itemId, decimal price)
    {
        Raise(new ItemAdded(itemId, price));
    }

    // Ship the order
    public void Ship()
    {
        if (_state.Status != "Confirmed")
            throw new InvalidOperationException("Can only ship confirmed orders");
        
        Raise(new OrderShipped());
    }

    // Apply events to state
    public void ApplyEvent(object @event)
    {
        switch (@event)
        {
            case OrderCreated oc:
                _state.OrderId = oc.OrderId;
                _state.CustomerId = oc.CustomerId;
                _state.Amount = oc.Amount;
                _state.Status = "Pending";
                break;
            
            case ItemAdded ia:
                _state.Items.Add((ia.ItemId, ia.Price));
                break;
            
            case OrderShipped:
                _state.Status = "Shipped";
                break;
        }
    }

    private void Raise(object @event)
    {
        ApplyEvent(@event);
        ((List<object>)UncommittedEvents).Add(@event);
        Version++;
    }
}
```

## Step 4: Save and Load

Persist your aggregate with an event store.

```csharp
// Save
var order = Order.CreateOrder(new OrderId(123), new CustomerId(456), 1000m);
order.AddItem("ITEM-001", 500m);

var eventStore = new InMemoryEventStore();
var streamId = new StreamId($"order-{order.State.OrderId.Value}");
// await eventStore.AppendAsync(streamId, /* events */);

// Load
var loadedOrder = new Order();
// await foreach (var @event in eventStore.ReadAsync(streamId))
// {
//     loadedOrder.ApplyEvent(@event);
// }
```

## Next Steps

- [Quick Start Example](./quick-start.md) - Full working example
- [Core Concepts: Aggregates](../core-concepts/aggregates.md) - Deeper dive
- [Usage Guide: Domain Modeling](../usage-guides/domain-modeling.md) - Advanced patterns
