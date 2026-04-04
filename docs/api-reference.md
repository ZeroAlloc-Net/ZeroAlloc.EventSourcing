# API Reference

**Note:** This document serves as a navigation hub for the ZeroAlloc.EventSourcing API. For complete, auto-generated API documentation with full signatures and remarks, see the [Generated API Reference](#generated-api-reference) section below.

## Table of Contents

1. [Overview](#overview)
2. [Core Interfaces](#core-interfaces)
3. [Built-in Implementations](#built-in-implementations)
4. [Source Generators](#source-generators)
5. [Extension Points](#extension-points)
6. [How to Navigate](#how-to-navigate)
7. [Generated API Reference](#generated-api-reference)

---

## Overview

The ZeroAlloc.EventSourcing API is designed around **zero-allocation** principles and **type safety**. The public API is organized into:

- **Core abstractions** (`IEventStore`, `ISnapshotStore`, `IProjection`, etc.) for storage and read models
- **Aggregate framework** (`Aggregate<TId,TState>`) for domain logic
- **Built-in implementations** for in-memory and SQL databases (PostgreSQL, SQL Server)
- **Source generators** for compile-time event dispatch (eliminating reflection)

### How API Docs Are Generated

The complete API reference is **auto-generated from XML comments** in the source code using DocFX or similar tooling. This document provides:

- **Quick reference** for common interfaces and classes
- **Links** to detailed API documentation
- **Usage patterns** to get started quickly
- **Navigation guides** for discovering relevant APIs

For the **exhaustive, auto-generated reference** (parameter details, return values, exceptions), navigate to the [Generated API Reference](#generated-api-reference) section or build the docs locally with `docfx build`.

### API Organization

```
ZeroAlloc.EventSourcing/
├── Core: Event store, snapshots, projections, serialization
├── Aggregates: Domain logic, aggregate root patterns
└── Platform-specific:
    ├── InMemory: Testing and demos
    ├── PostgreSQL: Production event store & snapshots
    └── SqlServer: Production event store & snapshots

ZeroAlloc.EventSourcing.Generators/
├── AggregateDispatchGenerator: Compile event dispatch for aggregates
├── ProjectionDispatchGenerator: Compile event dispatch for projections
└── EventTypeRegistryGenerator: Build event type mappings
```

---

## Core Interfaces

These interfaces define the contracts for event sourcing infrastructure. Most applications will depend on these, but won't implement them directly (implementations are provided).

### IEventStore

**Namespace:** `ZeroAlloc.EventSourcing`

The primary API for persisting and reading events.

**Key Methods:**
- `AppendAsync(StreamId, events, expectedVersion, ct)` — Append new events with optimistic concurrency
- `ReadAsync(StreamId, from, ct)` — Stream events from a specific position
- `SubscribeAsync(StreamId, from, handler, ct)` — Subscribe to new events

**Usage Pattern:**

```csharp
// Append events (optimistic concurrency)
var result = await eventStore.AppendAsync(
    streamId: orderId,
    events: new object[] { orderPlacedEvent, orderConfirmedEvent },
    expectedVersion: StreamPosition.Start // Use 0 for new streams
);

if (result.IsError)
{
    // Handle StoreError.Conflict (someone else updated the stream)
}

// Read all events
await foreach (var envelope in eventStore.ReadAsync(orderId))
{
    Console.WriteLine($"Event {envelope.Position}: {envelope.Event}");
}

// Subscribe to new events
var subscription = await eventStore.SubscribeAsync(
    streamId: orderId,
    from: StreamPosition.AtVersion(10),
    handler: async (envelope, ct) => {
        Console.WriteLine($"New event: {envelope.Event}");
        await Task.CompletedTask;
    }
);
```

**See Also:**
- [Event Store Concepts](core-concepts/event-store.md) — Understand events, versioning, and concurrency
- [Custom Event Stores](advanced/custom-event-store.md) — Implement for other databases

---

### ISnapshotStore<TState>

**Namespace:** `ZeroAlloc.EventSourcing`

Optimizes loading of large aggregates by storing periodic checkpoints.

**Type Parameter:** `TState` — The aggregate state struct type

**Key Methods:**
- `ReadAsync(StreamId, ct)` — Load the most recent snapshot (or null)
- `WriteAsync(StreamId, position, state, ct)` — Save a snapshot

**Usage Pattern:**

```csharp
// Load snapshot (if one exists)
var snapshot = await snapshotStore.ReadAsync(orderId);

if (snapshot != null)
{
    var (position, state) = snapshot.Value;
    // Load remaining events from position onwards
    // Faster than replaying entire event history
}

// After aggregates completes, save a snapshot
await snapshotStore.WriteAsync(
    streamId: orderId,
    position: aggregate.Version,
    state: aggregate.State
);
```

**See Also:**
- [Snapshots Concepts](core-concepts/snapshots.md) — When and why to use snapshots
- [Custom Snapshot Stores](advanced/custom-snapshots.md) — Implement for Redis, S3, etc.

---

### IProjection (base: Projection<TReadModel>)

**Namespace:** `ZeroAlloc.EventSourcing`

Base class for building read models from event streams.

**Type Parameter:** `TReadModel` — Your read model type (record, struct, or class)

**Key Methods:**
- `Apply(TReadModel, EventEnvelope)` → `TReadModel` — Route events to handlers
- `HandleAsync(EventEnvelope)` — Process a single event

**Key Properties:**
- `Current` — The current read model state

**Usage Pattern:**

```csharp
public record OrderSummary(string OrderId, decimal Amount, string? TrackingCode);

public class OrderProjection : Projection<OrderSummary>
{
    protected override OrderSummary Apply(OrderSummary current, EventEnvelope envelope)
    {
        return envelope.Event switch
        {
            OrderPlacedEvent e => new OrderSummary(e.OrderId, e.Amount, null),
            OrderShippedEvent e => current with { TrackingCode = e.TrackingCode },
            _ => current
        };
    }
}

// Usage
var projection = new OrderProjection();

await foreach (var envelope in eventStore.ReadAsync(orderId))
{
    await projection.HandleAsync(envelope);
}

var summary = projection.Current; // Read model is ready
```

**See Also:**
- [Projections Concepts](core-concepts/projections.md) — Understand read models and materialized views
- [Advanced Projections](advanced/custom-projections.md) — 10 patterns including filtering, batching, side effects

---

### IEventSerializer

**Namespace:** `ZeroAlloc.EventSourcing`

Converts events to/from byte representation for storage.

**Key Methods:**
- `Serialize<TEvent>(event)` → `ReadOnlyMemory<byte>` — Serialize an event
- `Deserialize(bytes, type)` → `object` — Deserialize bytes to an event

**Implementation Note:** Typically wraps ZeroAlloc.Serialisation or a JSON serializer (System.Text.Json, Newtonsoft.Json, MessagePack, Protobuf, etc.).

**Usage Pattern:**

```csharp
// Typically configured at application startup
var serializer = new JsonEventSerializer(); // Your implementation

var eventStore = new PostgreSqlEventStore(
    connectionString: "...",
    serializer: serializer
);
```

**See Also:**
- Configuration and wiring in [Getting Started: Installation](getting-started/installation.md)

---

## Built-in Implementations

ZeroAlloc.EventSourcing provides ready-made implementations for common scenarios.

### InMemoryEventStoreAdapter

**Namespace:** `ZeroAlloc.EventSourcing.InMemory`

In-memory event store. Perfect for testing, demos, and learning.

**Characteristics:**
- All events stored in memory (not persisted)
- Thread-safe (lock-based)
- Supports optimistic concurrency
- Minimal allocation

**Example:**

```csharp
var adapter = new InMemoryEventStoreAdapter();
var serializer = new JsonEventSerializer();
var eventStore = new EventStore(adapter, serializer);

// Use like any other event store
await eventStore.AppendAsync(streamId, events, version);
```

**See Also:**
- Testing patterns in [Testing Examples](examples/03-testing/)

---

### PostgreSqlEventStoreAdapter & PostgreSqlSnapshotStore

**Namespace:** `ZeroAlloc.EventSourcing.PostgreSql`

Production-grade PostgreSQL persistence for events and snapshots.

**Characteristics:**
- Append-only event log with optimistic concurrency
- Efficient range queries and subscriptions
- Built-in snapshot support
- Handles connection pooling and transaction management

**Example:**

```csharp
var eventStoreAdapter = new PostgreSqlEventStoreAdapter("Host=localhost;Database=events");
var snapshotStore = new PostgreSqlSnapshotStore<OrderState>("Host=localhost;Database=events");

var eventStore = new EventStore(eventStoreAdapter, serializer);

// Use together for optimized loading
var snapshot = await snapshotStore.ReadAsync(orderId);
var events = eventStore.ReadAsync(orderId, 
    from: snapshot?.Position ?? StreamPosition.Start);
```

**See Also:**
- [Custom Event Stores](advanced/custom-event-store.md) — Connection setup, schema details

---

### SqlServerEventStoreAdapter & SqlServerSnapshotStore

**Namespace:** `ZeroAlloc.EventSourcing.SqlServer`

Production-grade SQL Server persistence for events and snapshots.

**Characteristics:**
- Append-only event log with optimistic concurrency
- Full-text search and indexing support
- Built-in snapshot support
- Enterprise features (clustering, replication)

**Example:**

```csharp
var eventStoreAdapter = new SqlServerEventStoreAdapter("Server=.;Database=events");
var snapshotStore = new SqlServerSnapshotStore<OrderState>("Server=.;Database=events");

var eventStore = new EventStore(eventStoreAdapter, serializer);
```

**See Also:**
- [Custom Event Stores](advanced/custom-event-store.md) — Connection setup, schema details

---

### InMemorySnapshotStore

**Namespace:** `ZeroAlloc.EventSourcing.InMemory`

In-memory snapshot storage. Useful for testing and local development.

**Characteristics:**
- Stores snapshots in memory (cleared on restart)
- Thread-safe
- No I/O overhead

**Example:**

```csharp
var snapshotStore = new InMemorySnapshotStore<OrderState>();

// Save after processing
await snapshotStore.WriteAsync(orderId, aggregate.Version, aggregate.State);

// Load before processing
var snapshot = await snapshotStore.ReadAsync(orderId);
```

---

### Aggregate<TId, TState>

**Namespace:** `ZeroAlloc.EventSourcing.Aggregates`

Base class for domain entities (aggregate roots).

**Type Parameters:**
- `TId` — Aggregate identifier (value type, e.g., `OrderId`, `CustomerId`)
- `TState` — Aggregate state (struct implementing `IAggregateState<TState>`)

**Key Properties:**
- `Id` — The aggregate identifier
- `State` — Current state (readonly externally)
- `Version` — Current version (number of events applied)
- `OriginalVersion` — Version when loaded (for optimistic concurrency)

**Key Methods:**
- `Raise<TEvent>(event)` — Raise a domain event (internal use only, called from command methods)
- `ApplyEvent(state, event)` → `TState` — Route events to state transitions (implement via source generator)

**Usage Pattern:**

```csharp
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void PlaceOrder(string customerId, decimal amount)
    {
        // Validate
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive");
        
        // Raise event(s)
        Raise(new OrderPlacedEvent(customerId, amount));
    }

    // ApplyEvent is generated by source generator
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        _ => state
    };
}
```

**See Also:**
- [Aggregates Concepts](core-concepts/aggregates.md) — Design patterns and best practices
- [Your First Aggregate](getting-started/first-aggregate.md) — Step-by-step tutorial

---

## Source Generators

ZeroAlloc.EventSourcing includes source generators to eliminate reflection and enable compile-time dispatch of events.

**Namespace:** `ZeroAlloc.EventSourcing.Generators`

### AggregateDispatchGenerator

Generates `ApplyEvent` method implementations for aggregates.

**What it does:**
- Finds all `Apply` methods on `IAggregateState<TState>` implementations
- Generates a switch expression routing each event type to the correct handler
- Eliminates reflection; purely compiled dispatch

**How to use:**

1. Mark your aggregate class as `partial`:
   ```csharp
   public sealed partial class Order : Aggregate<OrderId, OrderState>
   {
       // Your command methods here (Raise events)
   }
   ```

2. Implement state `Apply` methods:
   ```csharp
   public partial struct OrderState : IAggregateState<OrderState>
   {
       public OrderState Apply(OrderPlacedEvent e) 
           => this with { IsPlaced = true, Amount = e.Amount };
       
       public OrderState Apply(OrderShippedEvent e)
           => this with { IsShipped = true, TrackingNumber = e.TrackingNumber };
   }
   ```

3. The generator creates:
   ```csharp
   // Auto-generated in Order.g.cs
   protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
   {
       OrderPlacedEvent e => state.Apply(e),
       OrderShippedEvent e => state.Apply(e),
       _ => state
   };
   ```

**Example Generated Code:**

```csharp
// Input: partial aggregate with event handling
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // Your code here (methods that call Raise)
}

// Generated: Order.g.cs
public sealed partial class Order
{
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderConfirmedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        _ => state
    };
}
```

---

### ProjectionDispatchGenerator

Generates `Apply` method implementations for projections.

**What it does:**
- Finds all handler methods on `Projection<TReadModel>` subclasses
- Generates a switch expression routing each event type to the correct handler
- Eliminates reflection; purely compiled dispatch

**How to use:**

1. Mark your projection class as `partial`:
   ```csharp
   public sealed partial class OrderProjection : Projection<OrderSummary>
   {
       // Your handlers here
   }
   ```

2. Implement handler methods:
   ```csharp
   public sealed partial class OrderProjection : Projection<OrderSummary>
   {
       private OrderSummary Handle(OrderPlacedEvent e)
           => new(e.OrderId, e.Amount, null);
       
       private OrderSummary Handle(OrderShippedEvent e)
           => Current with { TrackingCode = e.TrackingCode };
   }
   ```

3. The generator creates:
   ```csharp
   // Auto-generated in OrderProjection.g.cs
   protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event) => @event.Event switch
   {
       OrderPlacedEvent e => Handle(e),
       OrderShippedEvent e => Handle(e),
       _ => current
   };
   ```

**Example Generated Code:**

```csharp
// Generated: OrderProjection.g.cs
public sealed partial class OrderProjection
{
    protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => Handle(e),
        OrderShippedEvent e => Handle(e),
        _ => current
    };
}
```

---

### EventTypeRegistryGenerator

**What it does:**
- Scans all event types in your assembly
- Generates a static registry mapping event type names to types
- Enables serialization/deserialization without reflection

**How to use:**

The generator runs automatically. Access the registry:

```csharp
// Generated registry
var eventType = EventTypeRegistry.GetType("OrderPlacedEvent"); // Compiled lookup
var eventName = EventTypeRegistry.GetName(typeof(OrderPlacedEvent));
```

---

## Extension Points

ZeroAlloc.EventSourcing is designed for customization. These are the primary extension points.

### Custom Event Store

**Abstract Base:** `IEventStoreAdapter`

Implement to support databases not included in the framework.

**Methods to implement:**
- `AppendAsync(...)` — Persist events with optimistic concurrency
- `ReadAsync(...)` — Retrieve events by stream and version range
- `SubscribeAsync(...)` — Subscribe to new events

**Example:** Custom MongoDB event store

```csharp
public class MongoEventStoreAdapter : IEventStoreAdapter
{
    private readonly IMongoDatabase _db;
    
    public async ValueTask<AppendResult> AppendAsync(...)
    {
        // Check optimistic concurrency
        // Insert events into MongoDB collection
    }

    public IAsyncEnumerable<EventEnvelope> ReadAsync(...)
    {
        // Query events from MongoDB
    }

    public async ValueTask<IEventSubscription> SubscribeAsync(...)
    {
        // Watch collection for changes
    }
}
```

**See Also:**
- [Custom Event Stores](advanced/custom-event-store.md) — Full walkthrough with database schema

---

### Custom Snapshot Store

**Abstract Base:** `ISnapshotStore<TState>`

Implement for snapshot persistence to custom storage (Redis, S3, DynamoDB, etc.).

**Methods to implement:**
- `ReadAsync(streamId, ct)` — Load most recent snapshot
- `WriteAsync(streamId, position, state, ct)` — Save snapshot

**Example:** Custom Redis snapshot store

```csharp
public class RedisSnapshotStore<TState> : ISnapshotStore<TState> where TState : struct
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IEventSerializer _serializer;

    public async ValueTask<(StreamPosition, TState)?> ReadAsync(StreamId streamId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var key = $"snapshot:{streamId}";
        var value = await db.StringGetAsync(key);
        if (value.IsNullOrEmpty) return null;
        
        // Deserialize and return
        var (position, state) = Deserialize(value);
        return (position, state);
    }

    public async ValueTask WriteAsync(StreamId streamId, StreamPosition position, TState state, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var key = $"snapshot:{streamId}";
        var value = Serialize(position, state);
        await db.StringSetAsync(key, value);
    }
}
```

**See Also:**
- [Custom Snapshot Stores](advanced/custom-snapshots.md) — Patterns and strategies

---

### Custom Projection Pattern

**Base Class:** `Projection<TReadModel>`

Extend for complex read models and side effects.

**Key extension points:**
- `Apply(current, envelope)` — Override to define event routing
- Constructor — Initialize read model with defaults
- Additional methods — Add query/aggregation methods to the projection

**Example:** Projection with side effects

```csharp
public class OrderProjection : Projection<OrderSummary>
{
    private readonly IEmailService _emailService;

    public OrderProjection(IEmailService emailService)
    {
        _emailService = emailService;
    }

    protected override OrderSummary Apply(OrderSummary current, EventEnvelope envelope)
    {
        var newState = envelope.Event switch
        {
            OrderPlacedEvent e => new OrderSummary(e.OrderId, e.Amount, null),
            OrderShippedEvent e => current with { TrackingCode = e.TrackingCode },
            _ => current
        };

        // Side effect: send email on order placed
        if (envelope.Event is OrderPlacedEvent orderedEvent)
        {
            _ = _emailService.SendOrderConfirmationAsync(orderedEvent.OrderId);
        }

        return newState;
    }
}
```

**See Also:**
- [Advanced Projections](advanced/custom-projections.md) — 10 patterns with examples

---

### Plugin Architecture

**Entry Point:** `IServiceCollection.AddEventSourcing()`

Use Dependency Injection to plug in implementations.

**Example:** Custom DI registration

```csharp
services.AddSingleton<IEventStore>(sp =>
{
    var adapter = new PostgreSqlEventStoreAdapter("...");
    var serializer = new JsonEventSerializer();
    return new EventStore(adapter, serializer);
});

services.AddSingleton<ISnapshotStore<OrderState>, PostgreSqlSnapshotStore<OrderState>>();
services.AddSingleton<OrderRepository>();
services.AddSingleton<OrderProjection>();
```

**See Also:**
- [Plugin Architecture](advanced/plugin-architecture.md) — Extensible system design
- [Installation Guide](getting-started/installation.md) — Dependency injection setup

---

## How to Navigate

### For Understanding Concepts

Start with **core concepts** before implementing:

1. [Event Sourcing Fundamentals](core-concepts/fundamentals.md) — Core principles
2. [Aggregates](core-concepts/aggregates.md) — Domain logic patterns
3. [Events](core-concepts/events.md) — Immutable facts
4. [Event Store](core-concepts/event-store.md) — Persistence
5. [Snapshots](core-concepts/snapshots.md) — Optimization
6. [Projections](core-concepts/projections.md) — Read models
7. [Architecture](core-concepts/architecture.md) — System design

### For Implementation Examples

Find code samples in **examples/**:

- [Getting Started Examples](examples/01-getting-started/) — CreateFirstAggregate, AppendAndRead
- [Domain Modeling Examples](examples/02-domain-modeling/) — OrderAggregate, OrderState, OrderEvents
- [Testing Examples](examples/03-testing/) — Unit and integration test patterns
- [Advanced Examples](examples/04-advanced/) — Custom stores, custom projections

### For Practical Patterns

Browse **usage guides** and **performance guides**:

- [Usage Guides](usage-guides/) — Common patterns and recipes
- [Testing](testing/) — Test strategies
- [Performance](performance/) — Characteristics and optimization
- [Advanced Patterns](advanced/) — Custom implementations, plugin design

### For API Details

Use the **auto-generated reference** (below) for:

- Complete method signatures
- Parameter descriptions
- Return types and exceptions
- Code examples in XML comments

Search the generated docs for:
- `IEventStore` — Event storage API
- `Aggregate<TId,TState>` — Base aggregate class
- `Projection<TReadModel>` — Read model base
- `ISnapshotStore<TState>` — Snapshot storage API

### If You're Stuck

- Check [Adoption Guide](ADOPTION_GUIDE.md) — Business case and rationale
- See [Contributing Guide](advanced/contributing.md) — Development setup
- Review [Architecture Guide](core-concepts/architecture.md) — System design decisions

---

## Generated API Reference

**Complete API documentation is auto-generated from source code XML comments.**

### Accessing the Full Reference

The full, exhaustive API reference includes:
- All public types and methods
- Complete method signatures with all overloads
- Parameter descriptions and constraints
- Return type details
- Exception information
- Code examples from XML comments

### How to Build Locally

1. **Clone the repository:**
   ```bash
   git clone https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing.git
   cd ZeroAlloc.EventSourcing
   ```

2. **Build API docs with DocFX:**
   ```bash
   docfx docs/docfx.json
   ```

3. **View the generated site:**
   ```bash
   docfx serve _site
   ```

### Key Interfaces to Look Up

| Interface | Purpose | Docs |
|-----------|---------|------|
| `IEventStore` | Persist and read events | [Reference](#ieventstore) |
| `IEventStoreAdapter` | Database-specific persistence | [Advanced](advanced/custom-event-store.md) |
| `ISnapshotStore<TState>` | Persist aggregate snapshots | [Reference](#isnapshotstore) |
| `IProjectionStore` | Persist read model state | [Core Concepts](core-concepts/projections.md) |
| `IEventSerializer` | Event serialization | [Reference](#ieventserializer) |
| `Aggregate<TId,TState>` | Domain entity base class | [Reference](core-concepts/aggregates.md) |
| `Projection<TReadModel>` | Read model base class | [Reference](#iprojection) |

### Namespaces

```
ZeroAlloc.EventSourcing/
├── Core interfaces and adapters
├── IEventStore, ISnapshotStore, IProjection, etc.

ZeroAlloc.EventSourcing.Aggregates/
├── Aggregate<TId,TState>, IAggregateState<TSelf>

ZeroAlloc.EventSourcing.InMemory/
├── InMemoryEventStoreAdapter, InMemorySnapshotStore, InMemoryProjectionStore

ZeroAlloc.EventSourcing.PostgreSql/
├── PostgreSqlEventStoreAdapter, PostgreSqlSnapshotStore

ZeroAlloc.EventSourcing.SqlServer/
├── SqlServerEventStoreAdapter, SqlServerSnapshotStore

ZeroAlloc.EventSourcing.Generators/
├── Source generators (AggregateDispatchGenerator, ProjectionDispatchGenerator)
```

---

## Quick Reference Cheat Sheet

### Creating and Using an Event Store

```csharp
// Setup
var adapter = new PostgreSqlEventStoreAdapter("connection");
var serializer = new JsonEventSerializer();
var eventStore = new EventStore(adapter, serializer);

// Append events
var result = await eventStore.AppendAsync(
    streamId: orderId,
    events: new object[] { @event },
    expectedVersion: StreamPosition.Start
);

// Read events
await foreach (var envelope in eventStore.ReadAsync(orderId))
{
    Console.WriteLine(envelope.Event);
}
```

### Creating an Aggregate

```csharp
// Define state
public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    
    public OrderState Apply(OrderPlacedEvent e) => this with { Amount = e.Amount };
}

// Define aggregate
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(decimal amount) => Raise(new OrderPlacedEvent(amount));
    
    // ApplyEvent generated by source generator
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        _ => state
    };
}
```

### Creating a Projection

```csharp
public record OrderSummary(decimal Amount, string? TrackingCode);

public sealed partial class OrderProjection : Projection<OrderSummary>
{
    protected override OrderSummary Apply(OrderSummary current, EventEnvelope envelope)
    {
        return envelope.Event switch
        {
            OrderPlacedEvent e => new OrderSummary(e.Amount, null),
            OrderShippedEvent e => current with { TrackingCode = e.TrackingCode },
            _ => current
        };
    }
}

// Usage
var projection = new OrderProjection();
await foreach (var envelope in eventStore.ReadAsync(orderId))
{
    await projection.HandleAsync(envelope);
}
var summary = projection.Current;
```

### Using Snapshots

```csharp
var snapshotStore = new PostgreSqlSnapshotStore<OrderState>("connection");

// Load with snapshot
var snapshot = await snapshotStore.ReadAsync(orderId);
var fromVersion = snapshot?.Position ?? StreamPosition.Start;

var events = eventStore.ReadAsync(orderId, from: fromVersion);
// ... process events ...

// Save snapshot
await snapshotStore.WriteAsync(orderId, aggregate.Version, aggregate.State);
```

---

## Additional Resources

- [Installation & Setup](getting-started/installation.md)
- [Quick Start Example](getting-started/quick-start-example.md)
- [Complete Documentation Index](INDEX.md)
- [Contributing Guide](advanced/contributing.md)

---

Last Updated: 2026-04-04
