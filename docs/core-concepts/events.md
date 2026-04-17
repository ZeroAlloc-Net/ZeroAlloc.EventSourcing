# Core Concepts: Events

Events are the foundation of event sourcing. An event is an immutable record of something that happened in your domain. Unlike commands (which request a change), events are facts—they represent what actually occurred, and they can never be undone or changed.

## What is an Event?

An event captures a domain-significant occurrence at a specific point in time. It answers the question: "What happened?"

**Example Order events:**

```csharp
// An order was placed with a specific amount
public record OrderPlacedEvent(string OrderId, decimal Total);

// An order was confirmed by a user
public record OrderConfirmedEvent;

// An order was shipped with tracking information
public record OrderShippedEvent(string TrackingNumber);
```

Each event is:
- **Immutable** — Once recorded, it can never be changed
- **Named in past tense** — `OrderPlaced`, not `PlaceOrder`
- **Self-contained** — Contains all data needed to understand what happened
- **Domain-focused** — Expresses business concepts, not technical details

Events are typically represented as C# records because records are:
- Immutable by design
- Serializable (work well with JSON, protobuf, etc.)
- Comparable and hashable
- Concise to define

## Event Immutability and Why It Matters

Immutability is non-negotiable in event sourcing. Once an event is appended to the event store, it is permanent. This has profound implications:

### 1. Audit Trail Integrity

If events could be modified, the audit trail would be meaningless. You could "fix" a payment event by changing the amount, making it impossible to trace what actually happened. Immutable events guarantee that recorded facts cannot be altered after the fact.

### 2. Replay Safety

Event sourcing relies on the ability to replay events to reconstruct state. If events could change, replaying at different times might produce different results:

```csharp
// Event sourcing promises: replaying these events today produces the same
// state as replaying them tomorrow (or in 5 years)
var events = new[] {
    new OrderPlacedEvent("ORD-001", 1500m),
    new OrderShippedEvent("TRACK-123")
};

var stateToday = Replay(events);      // Produces same state...
var stateInFiveYears = Replay(events); // ...because events are immutable
```

If `OrderPlacedEvent` could be modified to change the amount, these replays would produce different results, breaking the fundamental promise of event sourcing.

### 3. Concurrency Safety

Immutability eliminates entire classes of concurrency bugs. Multiple threads or processes can safely read the same events without synchronization, locks, or fear of race conditions. The guarantee is baked into the type system.

### 4. Event Versioning

When you need to evolve your events (add fields, rename properties, etc.), immutability forces you to create new event versions rather than mutating old ones. This preserves history:

```csharp
// Original event type
public record OrderPlacedEvent(string OrderId, decimal Total);

// Years later, you need to track who placed the order
// Instead of modifying OrderPlacedEvent (which would break old events),
// create a new version:
public record OrderPlacedEventV2(string OrderId, decimal Total, string CustomerId);

// Your apply logic handles both versions gracefully
```

## Event Metadata

Events carry two types of information: **payload** (what happened) and **metadata** (context about what happened).

### Event Payload

The payload is the domain data — what users care about:

```csharp
public record OrderPlacedEvent(string OrderId, decimal Total);
//                             ^^^^^^^^^^^^^^  ^^^^^  
//                             payload data
```

### Event Metadata

Metadata enriches events with operational context. The event store captures:

```csharp
public record EventEnvelope(
    StreamId StreamId,          // Which aggregate's stream this belongs to
    StreamPosition Position,    // Where in the stream this event lives
    object Event,               // The event payload (e.g., OrderPlacedEvent)
    EventMetadata Metadata      // Context about what happened
);

public record EventMetadata(
    Guid EventId,              // Unique identifier for this event
    string EventType,          // Type name for deserialization
    DateTimeOffset OccurredAt, // When the event occurred
    Guid? CorrelationId,       // Links related events across boundaries
    Guid? CausationId          // Identifies the command or event that caused this
);
```

Metadata is optional but powerful for:
- **Auditing** — Who made this change and when?
- **Debugging** — Trace requests across services via CorrelationId
- **Compliance** — Track user actions for regulatory reporting
- **Analytics** — Analyze event patterns over time

## Event Naming Conventions

Good event names tell a story. They use past tense and clearly express what happened:

**Good:**
```csharp
public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderShippedEvent(string TrackingNumber);
public record PaymentProcessedEvent(decimal Amount);
public record CustomerSubscribedEvent(string CustomerId);
```

**Problematic:**
```csharp
public record PlaceOrder(string OrderId, decimal Total);           // Present tense, confusing
public record OrderShip(string TrackingNumber);                    // Verb form, not a fact
public record Process(decimal Amount);                             // Too generic, unclear domain
public record CustomerEvent(string CustomerId, string Action);     // Generic, doesn't express what happened
```

### Naming Strategy

- Use full domain language: `OrderPlacedEvent`, not `OrdPlc`
- Be specific: `OrderShippedEvent`, not `OrderChangedEvent`
- Use past tense consistently
- Include the domain concept in the name (Order, Payment, Customer)

## Event Definition Patterns

Events in ZeroAlloc.EventSourcing are immutable C# records. Here are common patterns:

### Simple Value Events

Events with just a fact, no additional data:

```csharp
public record OrderConfirmedEvent;

public record OrderCancelledEvent;
```

### Events with Single Data Field

```csharp
public record OrderShippedEvent(string TrackingNumber);
```

### Events with Multiple Fields

```csharp
public record OrderPlacedEvent(string OrderId, decimal Total);

public record RefundProcessedEvent(decimal Amount, string Reason);
```

### Events with Nested Data

For complex data, include structured information:

```csharp
public record LineItem(string ProductId, int Quantity, decimal Price);

public record OrderPlacedEvent(string OrderId, decimal Total, LineItem[] Items);
```

### Optional Fields (Using Nullable Reference Types)

```csharp
public record OrderShippedEvent(
    string TrackingNumber,
    string? Carrier = null,
    DateTime? EstimatedDelivery = null
);
```

## Event Versioning and Evolution

Events are immutable, but requirements change. When you need to add fields to an event, create a new version and handle both gracefully.

### Scenario: Adding a Required Field

**Original:**
```csharp
public record OrderPlacedEvent(string OrderId, decimal Total);
```

**Later, you realize you need to track the customer:**
```csharp
public record OrderPlacedEventV2(string OrderId, decimal Total, string CustomerId);
```

Your aggregate's `ApplyEvent` dispatcher handles both:

```csharp
protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
{
    OrderPlacedEvent e => state.Apply(e),  // Old events (no CustomerId)
    OrderPlacedEventV2 e => state.Apply(e), // New events (with CustomerId)
    _ => state
};

// OrderState.Apply methods handle both
internal OrderState Apply(OrderPlacedEvent e) =>
    this with { IsPlaced = true, Total = e.Total, CustomerId = null };

internal OrderState Apply(OrderPlacedEventV2 e) =>
    this with { IsPlaced = true, Total = e.Total, CustomerId = e.CustomerId };
```

### When to Introduce Versions

- **New required field** — Create V2
- **Renamed field** — Create V2 with new name, apply old name to new field
- **Deleted field** — Keep accepting old version, ignore the field
- **Restructured data** — Create V2, write migration logic if needed

### Migration Strategy

For long-lived systems, you might want to migrate old events to new versions. However, immutability means you can't change old events. Instead:

1. **Keep both versions forever** — Simple, no migration risk
2. **Replay and rewrite** — Periodically read old events, emit new versions, store both
3. **Accept null/defaults** — For missing data, use reasonable defaults

The simplest and safest approach is to keep both versions and let your apply logic handle the differences.

## Event Type Discovery and Serialization

Events must be serializable to be stored. ZeroAlloc.EventSourcing requires:

1. An **IEventSerializer** — Converts events to/from bytes
2. An **IEventTypeRegistry** — Maps event type names to C# types

```csharp
public interface IEventTypeRegistry
{
    bool TryGetType(string eventType, out Type? type);
    string GetTypeName(Type type);
}

public interface IEventSerializer
{
    ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull;
    object Deserialize(ReadOnlyMemory<byte> payload, Type eventType);
}
```

### Built-in: ZeroAllocEventSerializer (recommended)

The `ZeroAlloc.Serialisation` package provides `ZeroAllocEventSerializer` — a built-in
implementation of `IEventSerializer` that uses source-generated, reflection-free dispatch.

Mark your event types with `[ZeroAllocSerializable]` and a serializer adapter attribute, then
wire up DI:

```csharp
// Mark your event types
[ZeroAllocSerializable]
[JsonSerializable(typeof(OrderPlacedEvent))]  // adapter-specific attribute
public record OrderPlacedEvent(string OrderId, decimal Total);

// In your composition root
services
    .AddSerializerDispatcher()  // generated at compile time — no reflection
    .AddEventSourcing();        // registers IEventSerializer → ZeroAllocEventSerializer
```

`AddSerializerDispatcher()` is emitted by the `ZeroAlloc.Serialisation` source generator: a
compile-time switch over every `[ZeroAllocSerializable]` type in your assembly. AOT-safe, zero
allocations on the dispatch path.

### Custom: implementing IEventSerializer yourself

For full control, implement `IEventSerializer` and `IEventTypeRegistry` manually:

```csharp
public class OrderEventTypeRegistry : IEventTypeRegistry
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

public class JsonEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => JsonSerializer.Deserialize(payload.Span, eventType)!;
}
```

This approach uses reflection — useful for prototyping but not recommended for production hot
paths. Prefer `ZeroAllocEventSerializer` when throughput matters.

## Good vs. Problematic Event Definitions

### Good: Clear, Self-Contained Events

```csharp
// ✓ Good: Expresses exactly what happened
public record OrderPlacedEvent(string OrderId, decimal Total, string CustomerId);

// ✓ Good: Each event is a distinct business fact
public record OrderConfirmedEvent;
public record OrderShippedEvent(string TrackingNumber);

// ✓ Good: Event names clearly describe the action
public record PaymentProcessedEvent(decimal Amount, DateTime ProcessedAt);
```

### Problematic: Vague or Over-Generic Events

```csharp
// ✗ Bad: Too generic, doesn't say what happened
public record OrderEvent(string Data);

// ✗ Bad: Mixing multiple facts in one event
public record OrderChangedEvent(string? NewStatus, string? TrackingNumber, decimal? NewTotal);
// What actually changed? All three? Just one?

// ✗ Bad: Present tense, sounds like a command
public record ProcessPayment(decimal Amount);

// ✗ Bad: Business context lost
public record DataChanged(object Value);
```

### Good: Events with Rich Domain Information

```csharp
// ✓ Good: Includes all context needed to understand the business fact
public record RefundInitiatedEvent(
    string OrderId,
    decimal Amount,
    string Reason,
    DateTime RequestedAt,
    string RequestedByUserId
);

// ✓ Good: Separate events for different domain facts
public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderConfirmedEvent;
public record OrderShippedEvent(string TrackingNumber);
// (not one big "OrderUpdated" event)
```

## Events in the Event Sourcing Workflow

Understanding where events fit in the full workflow helps clarify their role:

```
1. User Command (e.g., "Place this order")
   ↓
2. Aggregate processes command:
   - Validates business rules
   - Raises domain event(s)
   ↓
3. Events applied to aggregate state immediately
   ↓
4. Events queued as "uncommitted"
   ↓
5. Application persists events to event store
   ↓
6. Events are immutable facts in the store
   ↓
7. Other services/projections read events and update read models
```

Events sit at step 4-6: they're the bridge between the command handler (aggregate) and the event store (persistent truth).

## Summary

Events are:
- **Immutable facts** — What happened, captured forever
- **Named in past tense** — OrderPlaced, not PlaceOrder
- **Domain-focused** — Express business concepts
- **Self-contained** — Include all data needed to understand the fact
- **Versioned** — New versions handle evolution without losing history
- **Serializable** — Via IEventSerializer and IEventTypeRegistry
- **The source of truth** — State is always derived from events

Events are the core building block of event sourcing. All other concepts (aggregates, event stores, snapshots, projections) exist to manage and leverage the immutable event stream.

## Next Steps

- **[Core Concepts: Aggregates](./aggregates.md)** — How aggregates raise and apply events
- **[Core Concepts: Event Store](./event-store.md)** — How events are persisted and retrieved
- **[Usage Guide: Domain Modeling](../usage-guides/domain-modeling.md)** — Advanced event design patterns
