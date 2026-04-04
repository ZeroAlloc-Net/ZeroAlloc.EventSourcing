# Your First Aggregate

Build your first event-sourced aggregate in 5 minutes.

## What You'll Build

An `Order` aggregate that:
- Places orders with a total amount
- Ships orders with tracking information
- Applies events to maintain state

## Step 1: Define the Order ID and Events

Events represent things that happened in your domain. Start by defining the aggregate ID type and events.

```csharp
using ZeroAlloc.EventSourcing;

namespace MyProject.Domain;

// Define the aggregate ID
public readonly record struct OrderId(Guid Value);

// Events are simple records describing what happened
public record OrderPlacedEvent(string OrderId, decimal Total);

public record OrderShippedEvent(string TrackingNumber);
```

## Step 2: Define State

State is the current condition of your aggregate. It must implement `IAggregateState<T>` with an `Initial` property and methods to apply events.

```csharp
namespace MyProject.Domain;

public partial struct OrderState : IAggregateState<OrderState>
{
    // Required: Initial state
    public static OrderState Initial => default;
    
    // State properties
    public bool IsPlaced { get; private set; }
    public decimal Total { get; private set; }
    public string TrackingNumber { get; private set; }

    // Apply events to produce new state
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };

    internal OrderState Apply(OrderShippedEvent e) =>
        this with { TrackingNumber = e.TrackingNumber };
}
```

## Step 3: Define Your Aggregate

Aggregates inherit from `Aggregate<TId, TState>` and contain domain behavior.

```csharp
using ZeroAlloc.EventSourcing;

namespace MyProject.Domain;

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Domain behavior: Place an order
    public void Place(string orderId, decimal total)
    {
        Raise(new OrderPlacedEvent(orderId, total));
    }

    // Domain behavior: Ship an order
    public void Ship(string trackingNumber)
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot ship an order that hasn't been placed");
        
        Raise(new OrderShippedEvent(trackingNumber));
    }

    // Set the aggregate ID (called after loading or during creation)
    public void SetId(OrderId id) => Id = id;

    // Apply events to state - returns new state
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        _ => state
    };
}
```

## Step 4: Save and Load

Persist your aggregate with an event store and reload it.

```csharp
using ZeroAlloc.EventSourcing;

// Create and modify an aggregate
var orderId = new OrderId(Guid.NewGuid());
var order = new Order();
order.SetId(orderId);
order.Place("ORD-001", 1500m);
order.Ship("TRACK-12345");

// Get uncommitted events
var events = order.DequeueUncommitted();
// events now contains: [OrderPlacedEvent, OrderShippedEvent]

// Persist events to store
var eventStore = new InMemoryEventStore();
var streamId = StreamId.From(orderId);
await eventStore.AppendAsync(streamId, events);

// Load aggregate from event store
var loadedOrder = new Order();
loadedOrder.SetId(orderId);

// Replay all events
await foreach (var @event in eventStore.ReadAsync(streamId))
{
    // ApplyEvent is called internally by the aggregate
    loadedOrder.LoadFromHistory(@event);
}

// State is now fully reconstructed
Console.WriteLine($"Order IsPlaced: {loadedOrder.State.IsPlaced}");  // true
Console.WriteLine($"Order Total: {loadedOrder.State.Total}");        // 1500
Console.WriteLine($"Tracking: {loadedOrder.State.TrackingNumber}");   // TRACK-12345
```

## Next Steps

- [Quick Start Example](./quick-start.md) - Full working example
- [Core Concepts: Aggregates](../core-concepts/aggregates.md) - Deeper dive
- [Usage Guide: Domain Modeling](../usage-guides/domain-modeling.md) - Advanced patterns
