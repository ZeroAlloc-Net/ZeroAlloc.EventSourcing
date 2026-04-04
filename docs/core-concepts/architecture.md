# Core Concepts: Architecture

This page explores the high-level design of ZeroAlloc.EventSourcing: its philosophy, design decisions, and how all the core concepts fit together into a coherent system.

## Design Philosophy: Zero Allocation

The name "ZeroAlloc.EventSourcing" reflects a core design philosophy: **minimize heap allocations where possible**.

In event sourcing, the most expensive operation is often **loading an aggregate**:

```csharp
// Load aggregate by replaying events
var order = new Order();
order.SetId(orderId);

// Replay 1,000 events
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    order.ApplyHistoric(envelope.Event, envelope.Position);
    // Each event application creates a new state
    // If TState were a class, that's 1,000 heap allocations!
}
```

If the aggregate's state were a class, replaying 1,000 events would create 1,000 objects on the heap, triggering garbage collection and hurting performance.

**ZeroAlloc.EventSourcing solves this by using value types (structs):**

```csharp
// State is a struct (value type, stack-allocated)
public partial struct OrderState : IAggregateState<OrderState>
{
    public bool IsPlaced { get; private set; }
    public decimal Total { get; private set; }
    
    // Apply returns a new OrderState (struct, not class)
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };
}

// Replaying 1,000 events = 1,000 lightweight struct updates
// No heap allocations, no GC pressure
```

This design choice enables **high-throughput event sourcing** without pausing for garbage collection.

## Core Design: Aggregate<TId, TState>

The library centers on a single, elegant pattern:

```csharp
public abstract class Aggregate<TId, TState> : IAggregate
    where TId : struct
    where TState : struct, IAggregateState<TState>
{
    public TId Id { get; protected set; }
    public TState State { get; private set; }
    public StreamPosition Version { get; private set; }
    
    protected void Raise<TEvent>(TEvent @event) where TEvent : notnull
    {
        _uncommitted.Add(@event);
        State = ApplyEvent(State, @event);
        Version = Version.Next();
    }
    
    protected abstract TState ApplyEvent(TState state, object @event);
}
```

**Benefits of this design:**

1. **Type safety** — `Aggregate<OrderId, OrderState>` is explicit about ID and state types
2. **Value type constraints** — `where TId : struct` and `where TState : struct` ensure allocation-free operations
3. **Immutable state transitions** — State updates via pure `Apply` methods
4. **No persistence coupling** — Aggregates are pure domain logic, unaware of storage
5. **Testability** — No mocks needed; test state transitions directly

## Value Types for IDs and State

Using value types (structs) throughout the system provides substantial benefits:

### Aggregate IDs

```csharp
// ✓ Good: Value type ID
public readonly record struct OrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);

// The ID is stack-allocated, hashable, and immutable
var id = new OrderId(Guid.NewGuid());
var order = await repository.LoadAsync(id);
```

**Benefits:**
- No heap allocations
- Value equality (two OrderIds with the same Guid compare equal)
- Hashable (can be dictionary keys)
- Explicitly typed (OrderId, not just Guid)

### Aggregate State

```csharp
// ✓ Good: Struct state (value type)
public partial struct OrderState : IAggregateState<OrderState>
{
    public bool IsPlaced { get; private set; }
    public decimal Total { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };
}

// Replaying events doesn't allocate on the heap
```

**Benefits:**
- Each `with` expression creates a stack value, not a heap object
- No GC pressure during replay
- Immutable updates are natural and efficient
- Memory layout is cache-friendly

### When NOT to Use Value Types

Value types have a cost: copying. For large state objects (100KB+), struct copying can be expensive:

```csharp
// ✗ Bad: Very large struct (expensive to copy)
public struct HugeState
{
    public byte[] LargeArray;  // 100KB+
    
    public HugeState Apply(Event e)
    {
        var copy = this;       // Copy entire 100KB
        copy.LargeArray[0] = 1;
        return copy;
    }
}
```

**Guidelines:**
- **Small structs (< 64 bytes)** — Use structs, value type semantics
- **Medium structs (64-1KB)** — Can use structs, but measure performance
- **Large objects (> 1KB)** — Consider using classes for state, sacrifice allocation-free replay

For most business domains, structs are the right choice.

## Struct Allocation and Performance

Understanding struct allocation is critical:

### On the Stack

When you create a local variable, it lives on the stack:

```csharp
void ProcessOrder(OrderId orderId)
{
    var order = new Order();           // Stack allocated
    var state = new OrderState();      // Stack allocated
    
    await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    {
        order.ApplyHistoric(envelope.Event, envelope.Position);
        // order.State = ApplyEvent(state, event) creates a new struct
        // Struct is created on the stack, not the heap
    }
}
```

### On the Heap (Boxed)

When you box a struct, it allocates on the heap:

```csharp
object boxedState = order.State;  // ✗ Allocates! Struct is boxed to object

// Avoid boxing in hot paths
```

### Performance Impact

For replaying 10,000 events:

| Implementation | Allocations | Time |
|---|---|---|
| Class state | 10,000 allocations | 1,000ms + GC |
| Struct state | 0 allocations | 100ms (no GC) |
| Boxed struct | 10,000 allocations | 1,000ms + GC |

The difference is substantial. Use structs and avoid boxing.

## Source Generators for Dispatch

Event dispatch (routing events to the correct `Apply` method) is typically hand-written:

```csharp
protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
{
    OrderPlacedEvent e => state.Apply(e),
    OrderConfirmedEvent e => state.Apply(e),
    OrderShippedEvent e => state.Apply(e),
    _ => state
};
```

ZeroAlloc.EventSourcing includes optional source generators to automate this:

```csharp
// Decorate with [AggregateDispatch]
[AggregateDispatch]
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Source generator creates the ApplyEvent implementation
}
```

**Benefits:**
- No boilerplate to maintain
- Compiler-verified event handling
- Zero runtime overhead (generated at compile time)
- Type-safe dispatch

The generators are entirely optional; you can hand-write dispatch if you prefer.

## Memory Efficiency of Events

Events are immutable records, which are themselves designed for efficiency:

```csharp
public record OrderPlacedEvent(string OrderId, decimal Total);

// Records are:
// - Immutable by default
// - Automatically serializable
// - Comparable (equality by value)
// - Lightweight (no virtual method overhead)
```

Events are appended to the event store and never modified. This allows:

1. **Cheap copying** — The store can copy events without worrying about external mutations
2. **Safe sharing** — Multiple threads can safely read the same event
3. **Deduplication** — Identical events can be detected and optimized
4. **Compression** — Events can be compressed for storage

## Threading and Concurrency Model

ZeroAlloc.EventSourcing is designed for single-threaded aggregate ownership:

```csharp
// Each aggregate is owned by a single thread
var order = await repository.LoadAsync(orderId);
order.Place("ORD-001", 1500m);
order.Confirm();
await repository.SaveAsync(order);

// If another thread tries to modify the same order, it must load its own instance
// Conflicts are detected at save time (optimistic locking)
```

**Benefits:**
- No locks needed within an aggregate
- Simple reasoning about state
- High performance (no synchronization overhead)

**Limitations:**
- Can't modify the same aggregate from multiple threads
- Must use optimistic locking for concurrency detection

**Scaling to multiple threads:**

```csharp
// Thread 1
var order1 = await repository.LoadAsync(orderId);
order1.Ship("TRACK-123");

// Thread 2
var order2 = await repository.LoadAsync(orderId);  // Separate instance
order2.Confirm();

// Both call repository.SaveAsync()
// Whichever saves first succeeds
// The second gets StoreError.Conflict and must retry
```

This model works well for most systems. For extreme concurrency, consider:
- Sharding aggregates across threads
- Using message queues to serialize commands per aggregate
- Accepting eventual consistency for cross-aggregate operations

## Comparison with Traditional ORM Approaches

### Traditional ORM (Entity Framework, NHibernate)

```csharp
// Entity is a mutable class
public class Order
{
    public Guid Id { get; set; }
    public string Status { get; set; }
    public decimal Total { get; set; }
    
    public void Ship(string tracking)
    {
        this.Status = "Shipped";     // Direct mutation
        this.TrackingNumber = tracking;
    }
}

using (var db = new OrderDb())
{
    var order = db.Orders.Find(id);
    order.Ship("TRACK-123");
    db.SaveChanges();                // UPDATE statement
}
```

**Tradeoffs:**
- Simpler mental model (mutate objects directly)
- Excellent for CRUD operations
- No audit trail without external logging
- Concurrency conflicts are hard to detect
- Temporal queries require special setup (temporal tables, archival)
- Changed tracking at the ORM layer adds complexity

### Event-Sourced Approach (ZeroAlloc)

```csharp
// Aggregate raises events, never mutates state directly
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Ship(string tracking)
    {
        if (!State.IsConfirmed)
            throw new InvalidOperationException("Cannot ship unconfirmed");
        Raise(new OrderShippedEvent(tracking));
    }
}

var order = await repository.LoadAsync(orderId);
order.Ship("TRACK-123");
await repository.SaveAsync(order);
```

**Tradeoffs:**
- More explicit (commands → events → state)
- Perfect audit trail is automatic
- Concurrency conflicts are detected immediately
- Temporal queries are trivial
- Read models require explicit projections
- More ceremony for simple operations

## Design Decisions and Rationale

### Why Aggregate<TId, TState> Instead of IAggregate<T>?

**Decision:** Use abstract base class with two type parameters.

```csharp
// ✓ Chosen: Base class with two type parameters
public abstract class Aggregate<TId, TState> where TId : struct where TState : struct
```

**Rationale:**
- Enforces value type constraints at compile time
- Clear separation of ID and state types
- Enables type-safe repository patterns
- Base class provides infrastructure (Raise, ApplyHistoric, etc.)
- Easier than interface-based approach with multiple constraint combinations

### Why Events Are Immutable

**Decision:** All events are immutable (records, no setters).

**Rationale:**
- Audit trail integrity — events are permanent facts
- Replay safety — same events always produce same results
- Concurrency — no synchronization needed for reads
- Versioning — forces new versions instead of mutation

### Why Optimistic Locking Over Pessimistic?

**Decision:** Detect conflicts at save time (optimistic), not at load time (pessimistic).

```csharp
// Optimistic: Load, modify, save with version check
var order = await repository.LoadAsync(id);      // No lock acquired
order.Ship("TRACK-123");                         // No lock held
await repository.SaveAsync(order);               // Check version here
```

**Rationale:**
- Works well for event sourcing (you always replay to reconstruct state)
- Avoids long-lived locks (better throughput)
- Naturally supports asynchronous operations
- Conflicts are rare in well-designed systems (different aggregates or disjoint operations)

### Why No Distributed Transactions

**Decision:** Each stream is independent; no cross-stream transactions.

**Rationale:**
- Event sourcing assumes autonomous aggregates
- Transactions across streams are rare (use sagas for coordination)
- No need for distributed consensus (events are appended independently)
- Simpler architecture (no global locks or 2-phase commit)

For operations spanning multiple aggregates, use the Saga pattern:

```csharp
// Saga: Order aggregate commands Payment aggregate
// If order fails, saga handles payment cancellation
public class OrderPlacementSaga
{
    public async Task PlaceOrder(OrderId orderId, decimal amount)
    {
        var order = await repository.LoadAsync(orderId);
        order.Place("ORD-001", amount);
        await repository.SaveAsync(order);
        
        // Separately command payment system
        var paymentResult = await paymentService.ChargeAsync(amount);
        if (!paymentResult.Success)
        {
            // Compensate: cancel the order
            order = await repository.LoadAsync(orderId);
            order.Cancel();
            await repository.SaveAsync(order);
        }
    }
}
```

## How Concepts Fit Together

Here's the complete event sourcing workflow:

```
1. APPLICATION LAYER
   ↓ Application wants to do something
   ↓

2. AGGREGATE COMMAND
   Order.Ship("TRACK-123")
   ↓ Aggregate validates and raises event
   ↓

3. EVENT (Immutable fact)
   OrderShippedEvent(TrackingNumber: "TRACK-123")
   ↓ Applied to aggregate state immediately
   ↓

4. STATE (Derived from events)
   OrderState { Status = "Shipped", TrackingNumber = "TRACK-123" }
   ↓ Queued in aggregate's uncommitted list
   ↓

5. EVENT STORE (Append-only log)
   Append events → Versioning via StreamPosition
   ↓ Events persisted, order.OriginalVersion updated
   ↓

6. SNAPSHOTS (Optional optimization)
   Save state periodically → Skip early events on next load
   ↓ Makes replay faster for large streams
   ↓

7. SUBSCRIPTIONS (Real-time processing)
   Events subscribed by projections and handlers
   ↓

8. PROJECTIONS (Read models)
   Transform events into query-optimized views
   ↓ Materialized in databases, search indices, etc.
   ↓

9. QUERIES (From application)
   Application queries projections, not aggregates
   ↓ Returns fast, denormalized results
```

Each concept has a clear responsibility:

| Concept | Responsibility | Type |
|---|---|---|
| **Events** | Immutable facts of what happened | Payload |
| **Aggregates** | Enforce consistency, raise events | Command handler |
| **Event Store** | Persist events, provide ordering | Infrastructure |
| **Snapshots** | Optimize replay performance | Infrastructure |
| **Projections** | Build read models from events | Query handler |

## Tradeoffs: Simplicity vs. Efficiency

ZeroAlloc.EventSourcing trades some simplicity for efficiency:

### Complexity Added

1. **Two models per aggregate** — State struct + aggregate class
2. **Manual state application** — Implement `Apply` methods
3. **Event versioning** — Multiple event versions to handle
4. **Projection management** — Maintain read models separately
5. **Eventual consistency** — Projections may lag behind events

### Efficiency Gained

1. **Zero-allocation replay** — Struct-based state, no GC pressure
2. **Built-in audit trail** — No additional logging needed
3. **Temporal queries** — Easy to ask "what was state at position X?"
4. **Concurrency safety** — Optimistic locking prevents data loss
5. **Flexible read models** — Project data however you need

## When to Use Event Sourcing

**Use event sourcing when:**

- **Audit trail is critical** — Banking, healthcare, compliance
- **Temporal queries matter** — "What was inventory on March 1?"
- **Concurrency safety matters** — High-frequency concurrent updates
- **Complex state machines** — State has many valid paths
- **Event replay is valuable** — Debugging, testing, rebuilding

**Avoid event sourcing when:**

- **CRUD simplicity is paramount** — Basic blog engine, simple CRUD
- **Event volume is massive** — High-throughput systems (trillions of events)
- **Read models are identical to write models** — No divergence benefit
- **Team unfamiliarity is high** — Learning curve is real
- **System is greenfield and domain is uncertain** — Wait until domain stabilizes

## Summary

ZeroAlloc.EventSourcing's architecture reflects a careful balance:

- **Zero allocations** — Use value types (structs) throughout
- **Type safety** — Aggregate<TId, TState> with constraints
- **Immutability** — Events are facts, state transitions are pure
- **Autonomy** — Each aggregate is independent, optimistic locking at save
- **Flexibility** — Projections decoupled from aggregates
- **Simplicity** — Clear separation of concerns, no hidden magic

The design enables high-performance event sourcing without sacrificing clarity or correctness.

## Next Steps

- **[Getting Started: Quick Start Example](../getting-started/quick-start-example.md)** — See architecture in action
- **[Usage Guide: Domain Modeling](../usage-guides/domain-modeling.md)** — Practical patterns
- **[Performance Guide](../performance.md)** — Benchmarks and optimization
