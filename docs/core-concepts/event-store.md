# Core Concepts: Event Store

The event store is the persistent backbone of event sourcing. It's an append-only log where every event in your system is recorded. Once an event is appended, it can never be changed or deleted—only new events can be added.

## What is an Event Store?

An event store is a database optimized for storing immutable, ordered sequences of events. Instead of storing the current state of your entities (like traditional databases), it stores the complete history of all state-changing events.

**Conceptually:**

```
Event Store = Immutable append-only log of all domain events

Stream 1 (Order-001):
  Position 1: OrderPlacedEvent(total=1500)
  Position 2: OrderConfirmedEvent
  Position 3: OrderShippedEvent(tracking=TRACK-123)

Stream 2 (Order-002):
  Position 1: OrderPlacedEvent(total=2500)
  Position 2: OrderShippedEvent(tracking=TRACK-456)
```

Each stream is identified by a `StreamId` (like `order-{id}` or `customer-{id}`) and contains an ordered sequence of events. Events are numbered by `StreamPosition` (1, 2, 3...).

## IEventStore Interface

ZeroAlloc.EventSourcing provides a clean, async-first API:

```csharp
public interface IEventStore
{
    // Append events to a stream
    ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<object> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default);

    // Read events from a stream
    IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        CancellationToken ct = default);

    // Subscribe to new events on a stream
    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
```

### AppendAsync: Saving Events

Save one or more events to a stream atomically:

```csharp
var events = new object[]
{
    new OrderPlacedEvent("ORD-001", 1500m),
    new OrderConfirmedEvent()
};

var streamId = new StreamId("order-12345");
var result = await eventStore.AppendAsync(
    streamId,
    events,
    expectedVersion: StreamPosition.Start  // First append
);

if (result.IsSuccess)
{
    var appendResult = result.Value;
    Console.WriteLine($"Stream: {appendResult.StreamId}");
    Console.WriteLine($"Next expected version: {appendResult.NextExpectedVersion}");
}
else if (result.Error == StoreError.Conflict)
{
    // Another process modified this stream since we loaded it
    // Reload and retry
}
```

**Key parameters:**

- `id` — The stream identifier (e.g., "order-abc123")
- `events` — Array of event objects to append
- `expectedVersion` — Optimistic locking: the version we expect before appending
  - `StreamPosition.Start` for new streams
  - `order.OriginalVersion` when updating an existing aggregate

**Return value:**

```csharp
public record AppendResult(
    StreamId StreamId,                      // The stream identifier
    StreamPosition NextExpectedVersion      // Position after append (e.g., 5)
);
```

### ReadAsync: Loading Events

Read all events from a stream or starting from a specific position:

```csharp
// Read all events from the beginning
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    Console.WriteLine($"Position {envelope.Position}: {envelope.Event}");
}

// Read from position 50 onward (useful for resuming subscriptions)
await foreach (var envelope in eventStore.ReadAsync(streamId, new StreamPosition(50)))
{
    // Process events 50, 51, 52, ...
}
```

Each `EventEnvelope` contains:

```csharp
public record EventEnvelope(
    StreamId StreamId,          // Which stream this event belongs to
    StreamPosition Position,    // Where in the stream (for versioning)
    object Event,               // The event payload (e.g., OrderPlacedEvent)
    EventMetadata Metadata      // Metadata (event ID, type, timestamp, etc.)
);
```

### SubscribeAsync: Real-Time Event Notifications

Subscribe to events as they're appended to a stream:

```csharp
var subscription = await eventStore.SubscribeAsync(
    streamId,
    from: new StreamPosition(0),  // Start from the beginning
    handler: async (envelope, ct) =>
    {
        // Process the event
        Console.WriteLine($"New event: {envelope.Event}");
        await Task.Delay(100, ct);  // Simulate processing
    },
    ct: cancellationToken
);

// Later, stop the subscription
await subscription.DisposeAsync();
```

Subscriptions are useful for:
- Building read models (projections) in real-time
- Sending notifications to other services
- Publishing events to message buses
- Maintaining consistency between systems

## Streams and Stream Identity

A stream is a sequence of events belonging to a single aggregate. Each stream has:

- **StreamId** — A unique identifier (string-based)
- **Ordering** — Events are ordered by position
- **Immutability** — Events can never be changed or deleted

**Common stream ID patterns:**

```csharp
// By aggregate type and ID
var streamId = new StreamId($"order-{orderId.Value}");
var streamId = new StreamId($"customer-{customerId.Value}");
var streamId = new StreamId($"invoice-{invoiceId.Value}");

// Hierarchical naming
var streamId = new StreamId($"company-{companyId.Value}/department-{deptId.Value}");
var streamId = new StreamId($"customer-{customerId.Value}/orders");

// Single stream for all events (event bus pattern)
var streamId = new StreamId("$all");  // Special stream containing all events
```

Choose stream IDs that:
- Clearly identify the aggregate
- Are deterministic (can be reconstructed from aggregate ID)
- Make sense for your domain

## StreamPosition and Versioning

Every event in a stream has a position. Positions are immutable and sequential:

```
Position 0: (start, used for new streams)
Position 1: First event
Position 2: Second event
Position 3: Third event
```

**Important:** Position 0 doesn't represent an event; it means "no events appended yet."

```csharp
public readonly record struct StreamPosition(long Value)
{
    public static StreamPosition Start => new(0);

    public StreamPosition Next() => new(Value + 1);
}
```

Positions are used for:

1. **Optimistic concurrency control** — Detecting conflicting modifications
2. **Resuming subscriptions** — "Give me events after position 50"
3. **Temporal queries** — "What was the state at position 10?"
4. **Ordering guarantees** — Proving the exact order events occurred

## Append-Only Semantics

The append-only guarantee is fundamental:

1. **You can only append** — Never update or delete existing events
2. **Appends are atomic** — Either all events append, or none do
3. **Positions never change** — An event at position 5 will always be at position 5
4. **Order is preserved** — Events are always retrieved in the order they were appended

**Example: Why append-only matters**

```csharp
// This is INVALID — you can't update an event
// eventStore.UpdateAsync(streamId, position: 2, newEvent: OrderCancelledEvent);

// Instead, append a new event
// This creates a complete audit trail:
// Position 1: OrderPlacedEvent(total=1500)
// Position 2: OrderShippedEvent(tracking=TRACK-123)
// Position 3: OrderCancelledEvent()  // Not undoing, adding a correction

// The history tells the truth: order was placed, shipped, then cancelled
```

## Optimistic Locking with StreamPosition

Concurrency safety is built in. When saving an aggregate, the event store checks if the stream version matches expectations:

```csharp
// Load aggregate from store
var order = await repository.LoadAsync(orderId);
// At this point, order.OriginalVersion = 3

// Modify it
order.Ship("TRACK-789");
var newEvents = order.DequeueUncommitted();

// Try to save
var result = await eventStore.AppendAsync(
    streamId,
    newEvents,
    expectedVersion: order.OriginalVersion  // We expect version 3
);

if (result.IsSuccess)
{
    // No other process modified this stream
    // Version is now 4 (3 + 1 new event)
}
else if (result.Error == StoreError.Conflict)
{
    // Another process appended to this stream since we loaded it
    // Our expectedVersion (3) didn't match the actual version (e.g., 4)
    // Reload and retry
    order = await repository.LoadAsync(orderId);
    order.Ship("TRACK-789");
    // Try again...
}
```

This pattern prevents "lost updates" — the silent data loss that happens in traditional concurrent systems.

## Built-in Adapters

ZeroAlloc.EventSourcing includes adapters for different storage backends:

### InMemoryEventStoreAdapter

For development and testing:

```csharp
var adapter = new InMemoryEventStoreAdapter();
var serializer = new JsonEventSerializer();
var registry = new OrderEventTypeRegistry();
var eventStore = new EventStore(adapter, serializer, registry);

// Use like any other event store
await eventStore.AppendAsync(streamId, events, StreamPosition.Start);
```

**Characteristics:**
- Events stored in memory (lost on process restart)
- Fast for testing and development
- No external dependencies
- Single-threaded (not thread-safe without external synchronization)

### PostgreSQL Adapter

For production with PostgreSQL:

```csharp
var adapter = new PostgreSqlEventStoreAdapter(
    connectionString: "Server=localhost;Database=events;..."
);
var eventStore = new EventStore(adapter, serializer, registry);
```

**Characteristics:**
- Persistent storage across restarts
- ACID guarantees (atomicity, consistency, isolation, durability)
- Highly available and scalable
- Supports concurrent reads and writes

### SQL Server Adapter

For production with SQL Server:

```csharp
var adapter = new SqlServerEventStoreAdapter(
    connectionString: "Server=localhost;Database=events;..."
);
var eventStore = new EventStore(adapter, serializer, registry);
```

**Characteristics:**
- Similar to PostgreSQL
- Enterprise-grade reliability
- Built-in replication and high availability
- Integrates with existing SQL Server infrastructure

## Query Capabilities

The event store provides several ways to query events:

### Single Stream Queries

Read events from a specific aggregate's stream:

```csharp
var streamId = new StreamId($"order-{orderId.Value}");

// All events
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    // Process envelope.Event
}

// From a specific position onward
await foreach (var envelope in eventStore.ReadAsync(streamId, new StreamPosition(10)))
{
    // Process events 10, 11, 12, ...
}
```

### Metadata Queries

Access event metadata for auditing:

```csharp
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    var metadata = envelope.Metadata;
    Console.WriteLine($"Event: {envelope.Event.GetType().Name}");
    Console.WriteLine($"EventId: {metadata.EventId}");
    Console.WriteLine($"OccurredAt: {metadata.OccurredAt}");
    Console.WriteLine($"CorrelationId: {metadata.CorrelationId}");
    Console.WriteLine($"CausationId: {metadata.CausationId}");
}
```

### Cross-Aggregate Queries

To query across multiple aggregates, subscribe to events and build a read model (projection):

```csharp
// Read all Order events across all orders
var orderProjection = new OrderSummaryProjection();
await foreach (var envelope in eventStore.ReadAsync(new StreamId("$all"), StreamPosition.Start))
{
    if (envelope.Event is OrderPlacedEvent || envelope.Event is OrderShippedEvent)
    {
        await orderProjection.HandleAsync(envelope);
    }
}

// orderProjection.Current now contains aggregated data
```

This is the pattern for building reports and dashboards.

## Concurrency and Consistency Guarantees

**Per-stream consistency:**
- Each stream is consistent within itself
- Concurrent writes to the same stream are serialized (only one succeeds)
- Conflicting writes are detected immediately (via position check)

**Cross-stream consistency:**
- Multiple streams are eventually consistent
- No global transactions across streams
- Use sagas or process managers for coordinating changes across streams

**Example: Placing an order and charging payment:**

```csharp
// These are independent streams
var orderStreamId = new StreamId($"order-{orderId.Value}");
var paymentStreamId = new StreamId($"payment-{paymentId.Value}");

// Append to both (but they may succeed/fail independently)
var r1 = await eventStore.AppendAsync(orderStreamId, ...);
var r2 = await eventStore.AppendAsync(paymentStreamId, ...);

// If r2 fails but r1 succeeded, you have an inconsistent state
// Solution: Use a saga or process manager to coordinate
```

## Integration with Event Sourcing Workflow

The event store sits at the center of the event sourcing workflow:

```
1. Application calls aggregate command
   ↓
2. Aggregate validates and raises event
   ↓
3. Event applied to aggregate state immediately
   ↓
4. Event queued as "uncommitted"
   ↓
5. Application calls eventStore.AppendAsync()
   ↓
6. Event Store persists to storage
   ↓
7. Projections subscribe and update read models
   ↓
8. Other services read the event store for their state
```

The event store is where the immutable truth is stored.

## Summary

The event store is:
- **Append-only** — Events can only be added, never changed
- **Ordered** — Events have positions reflecting their order
- **Durable** — Persisted to reliable storage
- **Concurrent-safe** — Optimistic locking prevents lost updates
- **Query-friendly** — Supports reading by stream or subscription
- **Adapter-based** — Works with multiple storage backends (in-memory, PostgreSQL, SQL Server)

The event store transforms aggregate commands into persistent, immutable facts that drive the entire system.

## Next Steps

- **[Core Concepts: Snapshots](./snapshots.md)** — Optimizing event store reads
- **[Core Concepts: Projections](./projections.md)** — Building read models from events
- **[Usage Guide: Performance](../performance.md)** — Benchmarks and optimization strategies
