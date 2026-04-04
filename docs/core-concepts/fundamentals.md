# Core Concepts: Event Sourcing Fundamentals

Event sourcing is a architectural pattern that fundamentally changes how you think about data. Instead of storing the current state of your application in a database, you store every action (event) that has ever happened. This simple shift unlocks powerful capabilities: perfect audit trails, time-travel debugging, event replay, and the ability to ask "what happened?" at any point in history.

## Why Event Sourcing Exists: A Motivating Scenario

Imagine you're building a banking system. A customer has a bank account with a balance of $5,000. In a traditional system, you'd store this directly in the database:

```
Account(123) -> Balance: $5,000
```

But here's the problem: if there's a discrepancy, you have no idea how it happened. Did someone debit $10 twice? Did a transfer fail? Was there unauthorized access? You only know the current state, not the history.

Now imagine storing every transaction instead:

```
Account(123):
  - Deposit: $5,000 (opening)
  - Withdrawal: $200
  - Withdrawal: $200 (duplicate?)
  - Transfer In: $500
  Balance: $5,100
```

Suddenly, everything is transparent. You can see exactly what happened, when, and in what order. You can investigate discrepancies, audit regulatory requirements, and even replay the account's history to debug issues. This is the core motivation behind event sourcing.

Event sourcing says: **store the facts (events), not the conclusions (state)**. State is always derived from events. The events are the source of truth.

## What is Event Sourcing?

Event sourcing is a pattern where:

1. **Events are the source of truth** — An event represents a fact that has happened in your domain. It is immutable, named in the past tense (OrderPlaced, PaymentProcessed), and captures all domain-significant information about what occurred.

2. **State is derived from events** — The current state of an aggregate is reconstructed by replaying all events that have occurred since its creation. State is not stored; it is calculated.

3. **Events are immutable** — Once an event is recorded, it can never be changed or deleted. If something was wrong, you record a corrective event (e.g., Refunded instead of trying to erase a Charged event).

This is the complete opposite of traditional approaches where you update a record in place:

```csharp
// Traditional: Update state directly
var order = db.Orders.Get(orderId);
order.Status = "Shipped";
order.TrackingNumber = "TRACK-123";
db.SaveChanges();

// Event Sourcing: Record what happened
var order = new Order();
order.Place("ORD-001", 1500m);
order.Ship("TRACK-123");
// Events are now: [OrderPlacedEvent, OrderShippedEvent]
// State is derived from replaying these events
```

## Traditional State vs. Event Sourcing

Let's compare these approaches using an Order aggregate throughout.

### Traditional State-Based Storage

In a traditional system, you store the current state of an order in a database table:

| OrderId | Status | Total | TrackingNumber |
|---------|--------|-------|----------------|
| 1 | Shipped | 1500 | TRACK-123 |

**How it works:**
- When you place an order, you insert a row with Status = "Placed"
- When you ship it, you update Status to "Shipped" and set TrackingNumber
- The database always reflects the current state
- To know what happened, you'd need a separate audit log (if you bothered to create one)

**Problems:**
- No history of state changes without an audit log
- Cannot answer "what did this order look like on April 1st?" without a temporal table or archival
- Concurrent modifications can lose updates (race conditions)
- Debugging is harder — you only see the current state, not how it got there
- Audit trails are an afterthought, not built-in

### Event Sourcing

In event sourcing, you store every event:

| OrderId | EventType | EventData | Position |
|---------|-----------|-----------|----------|
| 1 | OrderPlaced | {OrderId: "ORD-001", Total: 1500} | 1 |
| 1 | OrderShipped | {TrackingNumber: "TRACK-123"} | 2 |

**How it works:**
- When you place an order, you record an OrderPlacedEvent
- When you ship it, you record an OrderShippedEvent
- The current state is calculated by replaying all events in sequence
- The entire history is available for inspection, audit, or replay

**Benefits:**
- Full audit trail is built-in and unavoidable
- You can replay events to any point in time
- You can ask "what was the state at position 1?" (before shipping)
- Concurrent writes are detected via position-based optimistic locking
- Debugging is easier — the event history tells the full story

## Events are Immutable

Immutability is non-negotiable in event sourcing. Once an event is recorded, it becomes a permanent fact. You can never change it.

**Why immutability matters:**

1. **Audit Trail Integrity** — If events could be modified, the audit trail would be meaningless. You must be able to trust that recorded facts are true.

2. **Replay Safety** — If events could change, replaying them at different times might produce different results. Immutable events guarantee consistent replay.

3. **Concurrency** — Immutability eliminates entire classes of concurrency bugs. Multiple threads or processes can safely read the same events without synchronization.

4. **Event Versioning** — When you need to evolve your events, immutability forces you to add new events rather than mutate old ones. This keeps history intact.

**Example with Order:**

```csharp
// Events are immutable records
public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderShippedEvent(string TrackingNumber);

var order = new Order();
order.Place("ORD-001", 1500m);  // Raises OrderPlacedEvent

// The OrderPlacedEvent is now immutable. You cannot change it.
// If you made a mistake, you record a corrective event:
order.Cancel();  // Raises OrderCancelledEvent (a new event)

// The history is: [OrderPlacedEvent, OrderCancelledEvent]
// Not: [OrderPlacedEvent with Total=0]
```

## State is Derived

In event sourcing, there is a crucial inversion of concerns: **state is not stored, it is calculated**.

When you load an order from an event store, you don't retrieve a serialized OrderState object. Instead, you:

1. Fetch all events for that order from the event store
2. Create an empty state (OrderState.Initial)
3. Replay each event by applying it to the state
4. The result is the current state

```csharp
// Loading an order from events
var order = new Order();
order.SetId(orderId);

// Read all events from the stream
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    // Apply each event to rebuild state
    order.ApplyHistoric(envelope.Event, envelope.Position);
}

// Now order.State is fully reconstructed
Console.WriteLine($"Order Total: {order.State.Total}");        // 1500
Console.WriteLine($"Tracking: {order.State.TrackingNumber}");  // TRACK-123
```

**Why derive state instead of storing it?**

- **Single Source of Truth** — Only events are stored. State is always recalculated, so they cannot diverge.
- **Temporal Queries** — You can reconstruct the state at any point in time by replaying events up to that point.
- **Rebuilding** — If you need to change how state is calculated (e.g., add a new field), you can rebuild all aggregates by replaying their events with the new state logic.
- **Eventual Consistency** — In distributed systems, derived state can be updated independently of the source events.

## The Audit Trail

Every action recorded as an event creates an automatic, built-in audit trail. This is not an optional feature you add later; it's fundamental to event sourcing.

**The audit trail answers critical questions:**

- Who performed this action? (Include user info in events)
- When did it happen? (Event timestamps)
- What changed? (Event data)
- Why did it change? (Event payload can include reason)
- In what order did events occur? (Position in stream)

**Example: Order audit trail**

```
Position 1 | OrderPlacedEvent(orderId="ORD-001", total=1500) @ 2024-04-01 10:30 by user123
Position 2 | OrderShippedEvent(trackingNumber="TRACK-123") @ 2024-04-02 14:15 by admin001
```

For compliance scenarios (banking, healthcare, finance), this audit trail is invaluable. You can prove:
- The order was placed at a specific time
- It was shipped exactly 1 day later
- Which personnel made each change
- In what sequence events occurred

This level of auditability is difficult and expensive to retrofit into traditional systems. In event sourcing, it's free.

## Replay and Rebuilding

One of the most powerful capabilities of event sourcing is the ability to replay events. You can take any point in your system's history and reconstruct exactly what it looked like.

### Event Replay

Replay means: take a sequence of events and apply them to an empty state to reconstruct a past state.

```csharp
// Reconstruct the order's state before shipping
var order = new Order();
order.SetId(orderId);

// Read only the first event (OrderPlacedEvent)
var envelope = await eventStore.ReadSingleAsync(streamId, StreamPosition.Start);
order.ApplyHistoric(envelope.Event, envelope.Position);

// Now order.State reflects the state after OrderPlaced, before OrderShipped
Console.WriteLine($"Order Status: {order.State.IsPlaced}");        // true
Console.WriteLine($"Tracking: {order.State.TrackingNumber}");      // null (not shipped yet)
```

### Rebuilding State Models

Replay also enables a powerful pattern: rebuilding read models or derived state.

Suppose you want to add a new field to OrderState:

```csharp
public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    
    public bool IsPlaced { get; private set; }
    public decimal Total { get; private set; }
    public string TrackingNumber { get; private set; }
    public DateTime PlacedAt { get; private set; }  // NEW FIELD
```

You don't need to worry about updating billions of existing records. Just:

1. Update your state structure
2. Implement the Apply method to populate PlacedAt from the event
3. Run a rebuild process that replays all events for all orders
4. The new field is populated automatically

This is impossible in traditional systems where you'd need a database migration.

## Event Versioning

Events are immutable, but the structure of events can evolve over time. When requirements change, you need a strategy for evolving events without breaking history.

### The Problem

When you first defined OrderPlacedEvent, it had two fields:

```csharp
public record OrderPlacedEvent(string OrderId, decimal Total);
```

Later, you realize you need to track the customer ID who placed the order:

```csharp
public record OrderPlacedEvent(string OrderId, decimal Total, string CustomerId);
```

But you have thousands of old events with only two fields. How do you handle them?

### The Solution: Multiple Event Versions

Keep both the old and new event types:

```csharp
public record OrderPlacedEvent(string OrderId, decimal Total);

// New version with additional data
public record OrderPlacedEventV2(string OrderId, decimal Total, string CustomerId);
```

When you read old OrderPlacedEvent instances from the store, apply them as usual. When you emit new events, emit OrderPlacedEventV2. The aggregate's ApplyEvent dispatcher handles both:

```csharp
protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
{
    OrderPlacedEvent e => state.Apply(e),
    OrderPlacedEventV2 e => state.Apply(e),  // Handle both versions
    OrderShippedEvent e => state.Apply(e),
    _ => state
};
```

For old events without CustomerId, you can either:
- Leave it null
- Use a reasonable default
- Store a mapping table of historical customer IDs (if available)

**Key principle:** Events are immutable, so you cannot fix old events. Instead, you add new versions and handle both gracefully in your state apply logic.

## Eventual Consistency in Distributed Systems

Event sourcing naturally supports eventual consistency patterns, useful when building distributed systems.

### The Scenario

Your order system is split into two services:
- **Order Service** — Manages orders (event source)
- **Fulfillment Service** — Manages shipments (reads from orders)

When an order is placed, the Fulfillment Service needs to know about it. But it doesn't need the information immediately. It's fine if it learns about the order 100 milliseconds later.

### The Solution: Event Subscribers

```csharp
// Order Service publishes events
await eventStore.AppendAsync(streamId, [orderPlacedEvent], position);

// Fulfillment Service subscribes and learns about it asynchronously
fulfillmentService.OnOrderPlaced(orderPlacedEvent);  // May arrive later
```

This decouples services and improves resilience:
- If Fulfillment Service is temporarily down, it can catch up by replaying events
- If an event fails to deliver, it can be retried independently
- Services can process events at their own pace

The tradeoff is that the Fulfillment Service's view of orders may temporarily lag behind the Order Service's truth. This is "eventual consistency" — the system will reach consistency given time, but it's not immediately consistent.

For most business applications, this is a worthwhile tradeoff for the improved resilience and scalability.

## Implications and Tradeoffs

Event sourcing is powerful, but it's not a silver bullet. Adopting it comes with tradeoffs.

### Increased Complexity

Your application now needs to think about:
- **Event versioning** — How to evolve events as requirements change
- **Replay logic** — Ensuring state is correctly derived from events
- **Concurrency control** — Using position-based optimistic locking
- **Snapshots** (optional) — For performance, you might periodically save state to avoid replaying thousands of events

### Eventual Consistency

In distributed systems, different services may temporarily see different versions of the truth. This requires application logic to handle:
- Idempotent event handlers (handling the same event twice should be safe)
- Compensating transactions (if one service fails, how do you undo its work?)

### Storage and Performance

- **Storage** — You store every event forever (for audit), not just current state. This can use more storage than traditional systems.
- **Performance** — Replaying thousands of events to load a single aggregate can be slow. Mitigation: snapshots, caching, or periodically archiving old events.

### Testing Complexity

Event sourcing requires thinking differently about tests:
- State transitions must be testable as pure functions (event -> state)
- Aggregate behavior must be testable by verifying emitted events
- Replay must be tested to ensure idempotency

But this also makes testing easier in some ways — you can test behavior by specifying input events and verifying output events, with no need for database mocks.

### When to Use Event Sourcing

Event sourcing is ideal for:
- **High-value operations** — Banking, trading, healthcare (where audit trails are critical)
- **Complex domains** — Systems where understanding "how we got here" is important for debugging
- **Regulatory requirements** — Compliance-heavy systems that need comprehensive audit trails
- **Time-sensitive queries** — "Show me sales from the last hour" or "What was the inventory level yesterday?"

Event sourcing is overkill for:
- **Simple CRUD applications** — If you never need to ask "how did we get here?", traditional systems are simpler
- **Real-time systems** — With extremely high throughput where storage/replay overhead matters
- **Prototype/MVP** — If you don't yet know your domain, the added complexity isn't worth it

## Summary

Event sourcing inverts traditional data storage: instead of storing state, you store events (immutable facts), and derive state by replaying them. This shift provides:

- **Perfect audit trails** — Every action is recorded
- **Temporal queries** — You can ask what the state was at any point in time
- **Rebuilding capability** — You can change state logic and rebuild all aggregates
- **Debugging clarity** — The event history explains how the system got to its current state
- **Concurrency safety** — Immutable events eliminate races and lost updates

The tradeoff is increased complexity. Event sourcing requires thinking differently about data, testing, and concurrency. But for systems where correctness and auditability matter, the benefits are substantial.

## Next Steps

- **[Your First Aggregate](../getting-started/first-aggregate.md)** — Build a working example step by step
- **[Core Concepts: Aggregates](./aggregates.md)** — Deeper dive into aggregate design patterns
- **[Usage Guide: Domain Modeling](../usage-guides/domain-modeling.md)** — Advanced patterns and best practices
