# Implementing Custom Event Stores

**Version:** 1.0  
**Last Updated:** 2026-04-04

## Overview

While ZeroAlloc.EventSourcing provides an in-memory event store implementation, you may need to use a different storage backend: SQL Server, PostgreSQL, MongoDB, or a custom solution.

This guide shows how to implement a custom event store by implementing the `IEventStoreAdapter` interface.

## Architecture: IEventStoreAdapter

The `IEventStore` (public API) wraps an `IEventStoreAdapter` (SPI). The adapter handles the low-level storage operations:

```
┌─────────────────────────────┐
│ Your Application            │
└──────────────┬──────────────┘
               │ uses
┌──────────────▼──────────────┐
│ IEventStore (public)        │
│ - AppendAsync               │
│ - ReadAsync                 │
│ - SubscribeAsync            │
└──────────────┬──────────────┘
               │ delegates to
┌──────────────▼──────────────┐
│ IEventStoreAdapter (SPI)   │
│ (your implementation)       │
│ - AppendAsync               │
│ - ReadAsync                 │
│ - SubscribeAsync            │
└─────────────────────────────┘
               │ uses
┌──────────────▼──────────────┐
│ SQL Server / PostgreSQL     │
│ MongoDB / Custom Storage    │
└─────────────────────────────┘
```

The `IEventStore` handles:
- Event serialization (JSON/binary)
- Event envelope construction
- Type dispatch

Your adapter handles:
- Raw event persistence
- Position/version management
- Stream subscription logic

## IEventStoreAdapter Interface

```csharp
public interface IEventStoreAdapter
{
    // Appends raw events to a stream with optimistic concurrency check.
    ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default);

    // Reads raw events from a stream starting after 'from'.
    IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        CancellationToken ct = default);

    // Subscribes to events appended to a stream.
    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}

// What the adapter stores: the type name, the serialized payload and the metadata
public readonly record struct RawEvent(
    StreamPosition Position,
    string EventType,
    ReadOnlyMemory<byte> Payload,
    EventMetadata Metadata);
```

### The Contract

Every adapter that ships with the library follows these rules, and `EventStore`,
`AggregateRepository` and `StreamConsumer` rely on them:

- **Versions count events.** A stream's version is the number of events in it. A new stream is at
  `StreamPosition.Start` (0).
- **Positions are 1-based and assigned by the adapter.** The first event of a stream is at
  position 1. `EventStore` fills in `RawEvent.Position`, but only as a hint: the adapter decides.
- **Appends are optimistic.** When `expectedVersion` is not the stream's current version, return
  `StoreError.Conflict(id, expectedVersion, actualVersion)` and store nothing. On success return
  `new AppendResult(id, newVersion)`, where `newVersion` is the version after the append.
- **Reads exclude `from`.** `ReadAsync(id, from)` returns the events with a position greater than
  `from`, so `StreamPosition.Start` reads the whole stream and a consumer that passes its last
  checkpoint does not get that event again.
- **Metadata round-trips.** Store `RawEvent.Metadata` (event ID, type, time, correlation and
  causation IDs) and return it on reads.
- **The global stream.** `StreamConsumer` reads `StreamId.Global` ("*") by default: all events of
  all streams, in append order, with a global position. Support it if your adapter should serve
  stream consumers; `InMemoryEventStoreAdapter` shows how.

## Example: A Dictionary Adapter

`docs/examples/04-advanced/CustomEventStore.cs` holds a complete adapter that keeps the events in
a dictionary, with a subscription and a usage example that the test suite runs. Its append and
read show the contract:

```csharp
/// <summary>
/// Appends events to a stream with optimistic concurrency.
/// The stream's version is the number of events in it; a new stream is at
/// <see cref="StreamPosition.Start"/>. Positions are 1-based: the first event is at 1.
/// </summary>
public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
    StreamId id,
    ReadOnlyMemory<RawEvent> events,
    StreamPosition expectedVersion,
    CancellationToken ct = default)
{
    RawEvent[] appended;
    Subscription[] subscribers;
    StreamPosition newVersion;

    lock (_lock)
    {
        if (!_streams.TryGetValue(id.Value, out var stream))
        {
            stream = new List<RawEvent>();
            _streams[id.Value] = stream;
        }

        // Check the optimistic lock: the caller must have seen the current version
        var currentVersion = new StreamPosition(stream.Count);
        if (currentVersion != expectedVersion)
        {
            return Result<AppendResult, StoreError>.Failure(
                StoreError.Conflict(id, expectedVersion, currentVersion));
        }

        // The adapter owns the positions: assign each event the next one
        appended = new RawEvent[events.Length];
        for (var i = 0; i < appended.Length; i++)
        {
            appended[i] = events.Span[i] with { Position = new StreamPosition(stream.Count + 1) };
            stream.Add(appended[i]);
        }

        newVersion = new StreamPosition(stream.Count);

        subscribers = _subscriptions.Where(s => s.StreamId == id && s.IsRunning).ToArray();
    }

    // Notify live subscribers outside the lock
    foreach (var subscriber in subscribers)
    {
        foreach (var e in appended)
            await subscriber.Handler(e, ct);
    }

    return Result<AppendResult, StoreError>.Success(new AppendResult(id, newVersion));
}

/// <summary>
/// Reads the events of a stream that come after <paramref name="from"/>. The bound is
/// exclusive: <see cref="StreamPosition.Start"/> reads the whole stream, and a consumer that
/// passes the position of the last event it handled gets only the events after it.
/// </summary>
public async IAsyncEnumerable<RawEvent> ReadAsync(
    StreamId id,
    StreamPosition from,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    // Copy under the lock, then yield outside it
    List<RawEvent> snapshot;
    lock (_lock)
    {
        snapshot = _streams.TryGetValue(id.Value, out var stream)
            ? stream.Where(e => e.Position.Value > from.Value).ToList()
            : new List<RawEvent>();
    }

    foreach (var e in snapshot)
    {
        ct.ThrowIfCancellationRequested();
        yield return e;
    }
}
```

Plug the adapter into an `EventStore`, which does the serialization and the type lookup:

```csharp
// JsonEventSerializer is your IEventSerializer; OrderEventTypeRegistry is the
// IEventTypeRegistry the source generator emits for your Order aggregate.
var eventStore = new EventStore(new DictionaryEventStoreAdapter(), new JsonEventSerializer(), new OrderEventTypeRegistry());
```

## Database Adapters

The SQL Server, PostgreSQL and SQLite adapters ship as packages
(`ZeroAlloc.EventSourcing.SqlServer`, `ZeroAlloc.EventSourcing.PostgreSql`,
`ZeroAlloc.EventSourcing.Sqlite`); use them, or read their source as reference implementations
when you write an adapter for another database. They follow the same shape:

1. **One table, keyed by stream and position.** A primary key or unique constraint on
   `(stream_id, position)` makes the database reject a second writer at the same position.
2. **Check and insert in one transaction.** Read the stream's current version with a lock that
   holds until commit, compare it with `expectedVersion`, and insert the events at
   `expectedVersion + 1`, `+ 2`, and so on.
3. **Map a key violation to a conflict.** Two writers that pass the check at the same time still
   collide on the key; return `StoreError.Conflict` for that case too.
4. **Read with `position > @from`,** ordered by position.
5. **Store the payload as bytes** and the metadata in their own columns.

### Subscriptions for a Database

A database cannot push new rows to you, so the shipped SQL adapters poll.
`PollingEventSubscription` does the catch-up and the polling on top of your `ReadAsync`:

```csharp
public ValueTask<IEventSubscription> SubscribeAsync(
    StreamId id,
    StreamPosition from,
    Func<RawEvent, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)
{
    // Catches up from 'from', then polls ReadAsync until the subscription is disposed
    var subscription = new PollingEventSubscription(
        this, id, from, handler, PollingEventSubscription.DefaultPollInterval);
    return ValueTask.FromResult<IEventSubscription>(subscription);
}
```

Other options are database notifications (PostgreSQL `LISTEN`/`NOTIFY`, SQL Server Service Broker),
change streams (MongoDB) or publishing appended events to a message broker.

## Key Design Patterns

### 1. Optimistic Locking

Always check the expected version before appending, inside the same transaction as the insert:

```csharp
// Pseudo-code: GetCurrentVersion and InsertEvents stand for your storage calls
var currentVersion = GetCurrentVersion(streamId);

if (currentVersion != expectedVersion)
    return Result<AppendResult, StoreError>.Failure(
        StoreError.Conflict(streamId, expectedVersion, currentVersion));

InsertEvents(streamId, events, firstPosition: expectedVersion.Value + 1);
```

### 2. Idempotency

Make append operations safe to retry using unique constraints:

```sql
CONSTRAINT PK_event_store PRIMARY KEY (stream_id, position)
```

This ensures:
- A retried append that already succeeded fails with a conflict instead of storing the events twice
- Network retries are safe
- No lost updates

### 3. Atomic Reads

Read the version and insert in one transaction, so no other writer can slip in between:

```csharp
// Pseudo-code
using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

// Lock the stream's rows until commit, e.g. SELECT ... WITH (UPDLOCK, HOLDLOCK) on SQL Server
var currentVersion = GetVersion(streamId, transaction);
InsertEvents(streamId, events, transaction);

transaction.Commit();
```

## Testing Your Adapter

Test the contract directly against the adapter, without an `EventStore`:

```csharp
public class DictionaryEventStoreAdapterTests
{
    private readonly DictionaryEventStoreAdapter _adapter = new();

    // EventStore normally builds the RawEvents; a test of the adapter alone builds them itself
    private static RawEvent Raw(string eventType, string json)
        => new(StreamPosition.Start, eventType, Encoding.UTF8.GetBytes(json), EventMetadata.New(eventType));

    [Fact]
    public async Task AppendAsync_AppendsEvents()
    {
        var streamId = new StreamId("order-123");

        var result = await _adapter.AppendAsync(
            streamId,
            new[] { Raw("OrderPlaced", "{\"total\":1000}") },
            StreamPosition.Start);

        // Positions are 1-based: one event appended to an empty stream sits at 1
        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.NextExpectedVersion.Value);
    }

    [Fact]
    public async Task AppendAsync_DetectsOptimisticLockConflict()
    {
        var streamId = new StreamId("order-456");

        // First append succeeds
        await _adapter.AppendAsync(streamId, new[] { Raw("OrderPlaced", "{}") }, StreamPosition.Start);

        // Second append with wrong version fails
        var result = await _adapter.AppendAsync(
            streamId,
            new[] { Raw("OrderShipped", "{}") },
            StreamPosition.Start);  // Wrong: the stream is at version 1 now

        Assert.False(result.IsSuccess);
        Assert.Equal("CONFLICT", result.Error.Code);
    }

    [Fact]
    public async Task ReadAsync_ReadsAllEventsInOrder()
    {
        var streamId = new StreamId("order-789");
        await _adapter.AppendAsync(
            streamId,
            new[] { Raw("OrderPlaced", "{\"total\":1000}"), Raw("OrderShipped", "{\"tracking\":\"ABC123\"}") },
            StreamPosition.Start);

        var readEvents = new List<RawEvent>();
        await foreach (var @event in _adapter.ReadAsync(streamId, StreamPosition.Start))
        {
            readEvents.Add(@event);
        }

        Assert.Equal(new[] { "OrderPlaced", "OrderShipped" }, readEvents.Select(e => e.EventType));
        Assert.Equal(new long[] { 1, 2 }, readEvents.Select(e => e.Position.Value));
    }

    [Fact]
    public async Task ReadAsync_FromAPosition_ExcludesThatPosition()
    {
        var streamId = new StreamId("order-101");
        await _adapter.AppendAsync(
            streamId,
            new[] { Raw("OrderPlaced", "{}"), Raw("OrderShipped", "{}") },
            StreamPosition.Start);

        var readEvents = new List<RawEvent>();
        await foreach (var @event in _adapter.ReadAsync(streamId, new StreamPosition(1)))
        {
            readEvents.Add(@event);
        }

        Assert.Equal("OrderShipped", Assert.Single(readEvents).EventType);
    }
}
```

## Common Pitfalls

### 1. Serializing in the Adapter

The adapter never sees your event objects. `EventStore` serializes them with the
`IEventSerializer` and hands the adapter bytes in `RawEvent.Payload`; store those bytes as they
are, and return them unchanged.

### 2. Ignoring Position Management

Always increment positions correctly:

```csharp
// Pseudo-code
// Wrong: every event gets the same position
foreach (var e in events.Span)
    Insert(streamId, position: expectedVersion.Value + 1, e);  // ✗

// Right: successive positions after the expected version
for (var i = 0; i < events.Length; i++)
    Insert(streamId, position: expectedVersion.Value + 1 + i, events.Span[i]);
```

### 3. Not Handling Concurrency

Always use transactions for optimistic locking:

```csharp
// Pseudo-code
// Wrong: No atomicity
if (GetVersion(streamId) == expectedVersion)
{
    InsertEvents(streamId, events);  // ✗ Race condition!
}

// Right: Atomic check-and-set
using var transaction = BeginTransaction();
if (GetVersion(streamId, transaction) == expectedVersion)
{
    InsertEvents(streamId, events, transaction);
}
transaction.Commit();  // The (stream_id, position) key still rejects a concurrent writer
```

## Performance Considerations

1. **Batch appends** — `AppendAsync` receives all events of a save as one `ReadOnlyMemory<RawEvent>`; insert them in one round trip
2. **Connection pooling** — Reuse database connections
3. **Indexing** — The `(stream_id, position)` key serves stream reads; index the global position for `StreamId.Global` reads
4. **Pagination** — For large streams, read in batches (e.g., 1000 events at a time)

## Summary

To implement a custom event store:

1. Implement `IEventStoreAdapter` with your storage backend
2. Follow the contract: counted versions, 1-based positions, exclusive reads
3. Handle optimistic locking correctly
4. Implement subscriptions (polling or notifications)
5. Test thoroughly for concurrency scenarios
6. Consider performance (batching, indexing, pagination)

The adapter is the core abstraction. Once implemented correctly, your custom event store works seamlessly with ZeroAlloc.EventSourcing.

## Next Steps

- **[Custom Snapshot Stores](./custom-snapshots.md)** — Implementing snapshot persistence
- **[Custom Projections](./custom-projections.md)** — Advanced projection patterns
- **[Core Concepts: Event Store](../core-concepts/event-store.md)** — Event store fundamentals
