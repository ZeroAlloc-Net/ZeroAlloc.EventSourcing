# Quick Start Example

Get event sourcing working in 5-10 minutes with a complete, runnable example. This guide walks through a real Order aggregate demonstrating a multi-step state workflow, saving events, and reconstructing the aggregate from history.

By the end, you'll understand the full cycle: creating aggregates, raising events through multiple state transitions, persisting to a store, and loading aggregates back from their event history.

## The Complete Example

Here's a complete, copy-paste ready program that demonstrates event sourcing with the Order domain:

```csharp
using System.Text.Json;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

// Top-level program statements come first; the types they use follow.

// ============ Main Program ============

var orderId = new OrderId(Guid.NewGuid());
Console.WriteLine($"Creating order with ID: {orderId.Value}");
Console.WriteLine();

// === Set up the event store and repository ===
var adapter = new InMemoryEventStoreAdapter();
var registry = new OrderEventTypeRegistry();
var serializer = new JsonAggregateSerializer();
var eventStore = new EventStore(adapter, serializer, registry);

var repository = new AggregateRepository<Order, OrderId>(
    eventStore,
    () => new Order(),
    id => new StreamId($"order-{id.Value}"));

// === Create and modify an aggregate ===
using var order = new Order();

Console.WriteLine("Step 1: Commanding the aggregate");
order.Place("ORD-001", 1500m);
Console.WriteLine("  → Order placed for $1500");

order.Confirm();
Console.WriteLine("  → Order confirmed");

order.Ship("TRACK-XYZ-789");
Console.WriteLine("  → Order shipped with tracking TRACK-XYZ-789");

// Version counts every applied event; OriginalVersion only the saved ones
Console.WriteLine($"  → Generated {order.Version.Value - order.OriginalVersion.Value} events");
Console.WriteLine();

// === Persist the events ===
Console.WriteLine("Step 2: Persisting events to store");
var saved = await repository.SaveAsync(order, orderId);
if (saved.IsFailure)
    throw new InvalidOperationException(saved.Error.ToString());
Console.WriteLine("  → Events saved to event store");
Console.WriteLine();

// === Load aggregate from history ===
Console.WriteLine("Step 3: Loading aggregate from event history");
var streamId = new StreamId($"order-{orderId.Value}");
int eventCount = 0;
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    eventCount++;

// LoadAsync replays every event in the stream onto a new Order
var loaded = await repository.LoadAsync(orderId);
using var loadedOrder = loaded.Value;

Console.WriteLine($"  → Loaded {eventCount} events from stream");
Console.WriteLine();

// === Verify loaded state ===
Console.WriteLine("Step 4: Verifying reconstructed state");
Console.WriteLine($"  IsPlaced: {loadedOrder.State.IsPlaced}");
Console.WriteLine($"  IsConfirmed: {loadedOrder.State.IsConfirmed}");
Console.WriteLine($"  IsShipped: {loadedOrder.State.IsShipped}");
Console.WriteLine($"  Total: ${loadedOrder.State.Total}");
Console.WriteLine($"  TrackingNumber: {loadedOrder.State.TrackingNumber}");
Console.WriteLine($"  Version: {loadedOrder.Version.Value}");
Console.WriteLine();

Console.WriteLine("✓ Complete! The aggregate was reconstructed exactly from its event history.");

// ============ Domain Model ============

public readonly record struct OrderId(Guid Value);

public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderConfirmedEvent;
public record OrderShippedEvent(string TrackingNumber);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }

    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };

    internal OrderState Apply(OrderConfirmedEvent e) =>
        this with { IsConfirmed = true };

    internal OrderState Apply(OrderShippedEvent e) =>
        this with { IsShipped = true, TrackingNumber = e.TrackingNumber };
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
            throw new InvalidOperationException("Cannot confirm an order that hasn't been placed");
        
        Raise(new OrderConfirmedEvent());
    }

    public void Ship(string trackingNumber)
    {
        if (!State.IsConfirmed)
            throw new InvalidOperationException("Cannot ship an order that hasn't been confirmed");
        
        Raise(new OrderShippedEvent(trackingNumber));
    }

    // Written by hand here because this single-file program also hand-writes the
    // registry below. With the source generator, leave both out: it emits ApplyEvent
    // and an OrderEventTypeRegistry for a partial aggregate.
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderConfirmedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        _ => state
    };
}

// ============ Event Store Infrastructure ============

internal sealed class JsonAggregateSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => JsonSerializer.Deserialize(payload.Span, eventType)!;
}

internal sealed class OrderEventTypeRegistry : IEventTypeRegistry
{
    private static readonly Dictionary<string, Type> Map = new()
    {
        [nameof(OrderPlacedEvent)] = typeof(OrderPlacedEvent),
        [nameof(OrderConfirmedEvent)] = typeof(OrderConfirmedEvent),
        [nameof(OrderShippedEvent)] = typeof(OrderShippedEvent),
    };

    public bool TryGetType(string eventType, out Type? type) => Map.TryGetValue(eventType, out type);
    public string GetTypeName(Type type) => type.Name;
}
```

## Expected Output

When you run this program, you'll see:

```
Creating order with ID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx

Step 1: Commanding the aggregate
  → Order placed for $1500
  → Order confirmed
  → Order shipped with tracking TRACK-XYZ-789
  → Generated 3 events

Step 2: Persisting events to store
  → Events saved to event store

Step 3: Loading aggregate from event history
  → Loaded 3 events from stream

Step 4: Verifying reconstructed state
  IsPlaced: True
  IsConfirmed: True
  IsShipped: True
  Total: 1500
  TrackingNumber: TRACK-XYZ-789
  Version: 3

✓ Complete! The aggregate was reconstructed exactly from its event history.
```

## What You Just Did

1. **Defined the Domain**: Created `OrderPlacedEvent`, `OrderConfirmedEvent`, `OrderShippedEvent`, and `OrderState` to model the order workflow
2. **Built an Aggregate**: Implemented `Order` with commands (`Place`, `Confirm`, `Ship`) that raise domain events
3. **Set Up the Event Store**: Created a serializer and registry so events can be stored and retrieved
4. **Saved Events**: Used `AggregateRepository.SaveAsync()` to append the aggregate's new events to its stream
5. **Loaded from History**: Used `AggregateRepository.LoadAsync()`, which reads the stream and replays every event to reconstruct the aggregate to its exact final state

## Key Concepts

- **Events are facts**: Once raised, they're immutable. They represent what actually happened in your domain.
- **State is derived**: The aggregate's state (`IsPlaced`, `IsConfirmed`, etc.) is computed by replaying events, never stored directly.
- **Audit trail**: Every state change is captured as an event, creating a complete, queryable history of the aggregate's lifetime.
- **Rebuild from history**: Because events are the source of truth, you can reconstruct any past state by replaying events up to a specific point.

### State Validation & Ordering Constraints

In this example, the Order aggregate enforces strict state ordering:
1. `IsPlaced = true` (initial state after `Place()`)
2. `IsConfirmed = true` (only allowed after `IsPlaced = true`)
3. `IsShipped = true` (only allowed after `IsConfirmed = true`)

This ordering constraint is enforced through validation checks in the `Confirm()` and `Ship()` methods. Why does this matter?

- **Prevents invalid states**: The aggregate never enters intermediate states that violate business rules (e.g., shipping before confirming)
- **Audit completeness**: The event history is a complete, unambiguous record of state transitions
- **Concurrency safety**: Commands validate against the state loaded from the stream, and the repository saves with the loaded version as the expected version, so a command decided on stale state fails with a `CONFLICT` error instead of being appended
- **Domain clarity**: Explicit constraints make business logic testable and reviewable

When you load an aggregate from its event history, events are replayed in their original order. Replay applies the recorded events through the `Apply` methods only; the command checks do not run again, because the events already happened.

## Next Steps

- **[Your First Aggregate](./first-aggregate.md)** — Detailed walkthrough of each component with design explanations
- **[Core Concepts: Aggregates](../core-concepts/aggregates.md)** — Deep dive into aggregate patterns, versioning, and concurrency control
- **[Usage Guide: Domain Modeling](../usage-guides/domain-modeling.md)** — Advanced patterns for complex domains

## Running the Example

To run this example:

1. Create a new console project: `dotnet new console`
2. Add the packages: `dotnet add package ZeroAlloc.EventSourcing.Aggregates` and `dotnet add package ZeroAlloc.EventSourcing.InMemory`
3. Copy the code above into `Program.cs`
4. Run: `dotnet run`

That's it! You now have a working event-sourced aggregate.
