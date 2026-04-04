using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Examples.GettingStarted;

/// <summary>
/// This example shows how to create your first aggregate with ZeroAlloc.EventSourcing.
///
/// An aggregate is a domain object that:
/// 1. Has a unique identity (OrderId)
/// 2. Maintains state (OrderState)
/// 3. Handles commands (Place, Confirm, Ship)
/// 4. Raises events (OrderPlaced, OrderConfirmed, etc.)
/// </summary>

// Step 1: Define the aggregate identity (use a value type)
public readonly record struct OrderId(Guid Value);

// Step 2: Define the aggregate state (must be a struct)
public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;

    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }

    // State transitions are pure functions that create new state
    // Use 'with' expression for immutable updates
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };

    internal OrderState Apply(OrderConfirmedEvent _) =>
        this with { IsConfirmed = true };

    internal OrderState Apply(OrderShippedEvent e) =>
        this with { IsShipped = true, TrackingNumber = e.TrackingNumber };
}

// Step 3: Define the events (immutable records)
public record OrderPlacedEvent(string OrderNumber, decimal Total);
public record OrderConfirmedEvent;
public record OrderShippedEvent(string TrackingNumber);

// Step 4: Define the aggregate (inherits from Aggregate<TId, TState>)
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Command: Place an order
    // This command validates and raises an event
    public void Place(string orderNumber, decimal total)
    {
        if (total <= 0)
            throw new InvalidOperationException("Order total must be positive");

        // Raise an event
        // This updates internal state but doesn't persist yet
        Raise(new OrderPlacedEvent(orderNumber, total));
    }

    // Command: Confirm the order
    // Validates business rules before raising event
    public void Confirm()
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot confirm before placing order");

        if (State.IsConfirmed)
            throw new InvalidOperationException("Order already confirmed");

        Raise(new OrderConfirmedEvent());
    }

    // Command: Ship the order
    // More complex validation
    public void Ship(string trackingNumber)
    {
        if (!State.IsConfirmed)
            throw new InvalidOperationException("Cannot ship unconfirmed order");

        if (string.IsNullOrWhiteSpace(trackingNumber))
            throw new ArgumentException("Tracking number required", nameof(trackingNumber));

        Raise(new OrderShippedEvent(trackingNumber));
    }

    // Event dispatch: tells the aggregate how to apply events
    // Pattern match on event type and call appropriate Apply method
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderConfirmedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        _ => state
    };
}

// Step 5: Use the aggregate
public class CreateFirstAggregateExample
{
    public static async Task Main()
    {
        // Create an in-memory event store (for this example)
        var eventStore = new InMemoryEventStore();

        // Create a new order aggregate
        var order = new Order();
        var orderId = new OrderId(Guid.NewGuid());
        order.SetId(orderId);

        // Execute commands
        order.Place("ORD-001", 1500m);      // Raises OrderPlacedEvent
        order.Confirm();                     // Raises OrderConfirmedEvent
        order.Ship("TRACK-ABC123");          // Raises OrderShippedEvent

        // Dequeue uncommitted events (events that haven't been persisted yet)
        var events = order.DequeueUncommitted();
        Console.WriteLine($"Raised {events.Count} events:");
        foreach (var @event in events)
        {
            Console.WriteLine($"  - {@event.GetType().Name}");
        }

        // Persist to event store
        var streamId = new StreamId($"order-{orderId.Value}");
        var result = await eventStore.AppendAsync(streamId, events, StreamPosition.Start);

        if (result.IsSuccess)
        {
            Console.WriteLine($"\nSuccessfully appended {events.Count} events");
            Console.WriteLine($"First position: {result.Value.FirstPosition}");
            Console.WriteLine($"Last position: {result.Value.LastPosition}");

            // Update version tracking for next save
            order.AcceptVersion(result.Value.LastPosition);
        }
        else
        {
            Console.WriteLine($"Error appending events: {result.Error}");
        }

        // View final state
        Console.WriteLine($"\nFinal aggregate state:");
        Console.WriteLine($"  IsPlaced: {order.State.IsPlaced}");
        Console.WriteLine($"  IsConfirmed: {order.State.IsConfirmed}");
        Console.WriteLine($"  IsShipped: {order.State.IsShipped}");
        Console.WriteLine($"  Total: {order.State.Total}");
        Console.WriteLine($"  TrackingNumber: {order.State.TrackingNumber}");
    }
}
