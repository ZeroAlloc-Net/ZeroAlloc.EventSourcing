using System;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Examples.GettingStarted;

/// <summary>
/// This example shows how to create your first aggregate with ZeroAlloc.EventSourcing.
///
/// An aggregate is a domain object that:
/// 1. Has a unique identity (OrderId)
/// 2. Maintains state (OrderState)
/// 3. Handles commands (Place, Confirm, Ship)
/// 4. Raises events (OrderPlaced, OrderConfirmed, etc.)
///
/// This file is compiled and run by the test suite, so it only uses the public API.
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

    // State transitions are pure functions that create new state.
    // Use a 'with' expression for immutable updates. The methods must be internal:
    // the source generator emits the aggregate's ApplyEvent switch from them.
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

// Step 4: Define the aggregate (inherits from Aggregate<TId, TState>).
// The class is partial: the source generator adds the ApplyEvent override, which routes
// each event to the matching Apply method above, and an OrderEventTypeRegistry.
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Command: Place an order
    // This command validates and raises an event
    public void Place(string orderNumber, decimal total)
    {
        if (total <= 0)
            throw new InvalidOperationException("Order total must be positive");

        // Raise an event
        // This updates state immediately but doesn't persist anything yet
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
}

// Step 5: Use the aggregate
public static class CreateFirstAggregateExample
{
    public static async Task<OrderState> RunAsync()
    {
        // Create an in-memory event store (for this example).
        // OrderEventTypeRegistry is emitted by the source generator for Order.
        var eventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new OrderEventTypeRegistry());

        // The repository loads aggregates by replaying their stream
        // and saves their uncommitted events with optimistic concurrency.
        var repository = new AggregateRepository<Order, OrderId>(
            eventStore,
            () => new Order(),
            id => new StreamId($"order-{id.Value}"));

        // Create a new order aggregate
        var orderId = new OrderId(Guid.NewGuid());
        using var order = new Order();

        // Execute commands
        order.Place("ORD-001", 1500m);      // Raises OrderPlacedEvent
        order.Confirm();                     // Raises OrderConfirmedEvent
        order.Ship("TRACK-ABC123");          // Raises OrderShippedEvent

        // Version counts every applied event, OriginalVersion only the persisted ones
        Console.WriteLine($"Raised {order.Version.Value - order.OriginalVersion.Value} events");

        // Persist the uncommitted events. The repository appends them at OriginalVersion,
        // so a concurrent writer causes a CONFLICT error instead of a lost update.
        var result = await repository.SaveAsync(order, orderId);

        if (result.IsSuccess)
        {
            Console.WriteLine($"\nSuccessfully saved order {orderId.Value}");
            Console.WriteLine($"Stream version is now {result.Value.NextExpectedVersion.Value}");
        }
        else
        {
            Console.WriteLine($"Error saving events: {result.Error}");
        }

        // Read the persisted events back
        await foreach (var envelope in eventStore.ReadAsync(new StreamId($"order-{orderId.Value}")))
        {
            Console.WriteLine($"  - {envelope.Position.Value}: {envelope.Event.GetType().Name}");
        }

        // View final state
        Console.WriteLine($"\nFinal aggregate state:");
        Console.WriteLine($"  IsPlaced: {order.State.IsPlaced}");
        Console.WriteLine($"  IsConfirmed: {order.State.IsConfirmed}");
        Console.WriteLine($"  IsShipped: {order.State.IsShipped}");
        Console.WriteLine($"  Total: {order.State.Total}");
        Console.WriteLine($"  TrackingNumber: {order.State.TrackingNumber}");

        return order.State;
    }

    // A reflection-based JSON serializer keeps the example short. In an application, use the
    // built-in ZeroAllocEventSerializer that AddEventSourcing() registers; it is AOT-safe.
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType());

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
            => JsonSerializer.Deserialize(payload.Span, eventType)!;
    }
}
