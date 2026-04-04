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
    /// <summary>
    /// Appends serialized events to a stream.
    /// Must return StoreError.Conflict if expectedVersion mismatches.
    /// </summary>
    ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId streamId,
        ReadOnlyMemory<SerializedEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Reads serialized events from a stream.
    /// </summary>
    IAsyncEnumerable<SerializedEvent> ReadAsync(
        StreamId streamId,
        StreamPosition from = default,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes to events appended to a stream.
    /// Must call handler immediately for historical events from 'from'.
    /// Then notify handler for new events as they arrive.
    /// </summary>
    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId streamId,
        StreamPosition from,
        Func<SerializedEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
```

## Example: SQL Server Implementation

Here's a complete SQL Server implementation:

```csharp
using System.Data;
using System.Data.SqlClient;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace MyApp.EventSourcing;

public class SqlServerEventStoreAdapter : IEventStoreAdapter
{
    private readonly string _connectionString;
    private readonly string _tableName;

    public SqlServerEventStoreAdapter(string connectionString, string tableName = "Events")
    {
        _connectionString = connectionString;
        _tableName = tableName;
    }

    /// <summary>Creates the events table if it doesn't exist.</summary>
    public async ValueTask InitializeAsync()
    {
        var sql = $@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{_tableName}')
            BEGIN
                CREATE TABLE {_tableName} (
                    Id BIGINT PRIMARY KEY IDENTITY(1,1),
                    StreamId NVARCHAR(256) NOT NULL,
                    Position INT NOT NULL,
                    EventType NVARCHAR(256) NOT NULL,
                    EventData NVARCHAR(MAX) NOT NULL,
                    Timestamp DATETIMEOFFSET NOT NULL,
                    CONSTRAINT UK_StreamId_Position UNIQUE (StreamId, Position),
                    INDEX IX_StreamId (StreamId)
                );
            END
        ";

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand(sql, connection);
        await connection.OpenAsync();
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId streamId,
        ReadOnlyMemory<SerializedEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);

        try
        {
            // 1. Get current version
            var currentVersionSql = $"SELECT ISNULL(MAX(Position), -1) FROM {_tableName} WHERE StreamId = @StreamId";
            using var versionCmd = new SqlCommand(currentVersionSql, connection, transaction);
            versionCmd.Parameters.AddWithValue("@StreamId", streamId.Value);
            
            var currentVersion = (int)await versionCmd.ExecuteScalarAsync(ct);
            var currentPosition = new StreamPosition(currentVersion);

            // 2. Check for optimistic lock conflict
            if (currentPosition != expectedVersion)
            {
                return Result.Error<AppendResult, StoreError>(
                    StoreError.Conflict("Expected version {0}, but current is {1}",
                        expectedVersion.Value, currentPosition.Value));
            }

            // 3. Insert events
            var nextPosition = currentPosition.Next();
            var insertSql = $@"
                INSERT INTO {_tableName} (StreamId, Position, EventType, EventData, Timestamp)
                VALUES (@StreamId, @Position, @EventType, @EventData, @Timestamp)
            ";

            foreach (var @event in events.Span)
            {
                using var insertCmd = new SqlCommand(insertSql, connection, transaction);
                insertCmd.Parameters.AddWithValue("@StreamId", streamId.Value);
                insertCmd.Parameters.AddWithValue("@Position", nextPosition.Value);
                insertCmd.Parameters.AddWithValue("@EventType", @event.Type);
                insertCmd.Parameters.AddWithValue("@EventData", @event.Data);
                insertCmd.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow);

                await insertCmd.ExecuteNonQueryAsync(ct);
                nextPosition = nextPosition.Next();
            }

            await transaction.CommitAsync(ct);

            return Result.Ok<AppendResult, StoreError>(
                new AppendResult(currentPosition, nextPosition - 1));
        }
        catch (SqlException ex) when (ex.Number == 2627)  // Unique constraint violation
        {
            return Result.Error<AppendResult, StoreError>(
                StoreError.Conflict("Optimistic lock conflict"));
        }
    }

    public async IAsyncEnumerable<SerializedEvent> ReadAsync(
        StreamId streamId,
        StreamPosition from = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $@"
            SELECT EventType, EventData, Position
            FROM {_tableName}
            WHERE StreamId = @StreamId AND Position >= @FromPosition
            ORDER BY Position ASC
        ";

        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@StreamId", streamId.Value);
        command.Parameters.AddWithValue("@FromPosition", from.Value);

        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

        while (await reader.ReadAsync(ct))
        {
            var eventType = reader.GetString(0);
            var eventData = reader.GetString(1);
            var position = new StreamPosition(reader.GetInt32(2));

            yield return new SerializedEvent(eventType, eventData, position);
        }
    }

    public async ValueTask<IEventSubscription> SubscribeAsync(
        StreamId streamId,
        StreamPosition from,
        Func<SerializedEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        // 1. Read all existing events
        await foreach (var @event in ReadAsync(streamId, from, ct))
        {
            await handler(@event, ct);
        }

        // 2. Poll for new events (simple implementation)
        // In production, use SQL Server change tracking or notifications
        return new PollingSubscription(streamId, this, handler, ct);
    }

    private class PollingSubscription : IEventSubscription
    {
        private readonly StreamId _streamId;
        private readonly SqlServerEventStoreAdapter _store;
        private readonly Func<SerializedEvent, CancellationToken, ValueTask> _handler;
        private readonly CancellationToken _ct;
        private StreamPosition _lastPosition;
        private Task? _pollTask;

        public PollingSubscription(
            StreamId streamId,
            SqlServerEventStoreAdapter store,
            Func<SerializedEvent, CancellationToken, ValueTask> handler,
            CancellationToken ct)
        {
            _streamId = streamId;
            _store = store;
            _handler = handler;
            _ct = ct;
            _lastPosition = StreamPosition.Start - 1;

            // Start polling
            _pollTask = PollAsync();
        }

        private async Task PollAsync()
        {
            while (!_ct.IsCancellationRequested)
            {
                try
                {
                    var nextPosition = _lastPosition.Next();
                    await foreach (var @event in _store.ReadAsync(_streamId, nextPosition, _ct))
                    {
                        await _handler(@event, _ct);
                        _lastPosition = @event.Position;
                    }

                    // Poll every 100ms
                    await Task.Delay(100, _ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_pollTask != null)
                await _pollTask;
        }
    }
}

// Usage
var adapter = new SqlServerEventStoreAdapter("Server=.;Database=EventStore;Integrated Security=true");
await adapter.InitializeAsync();

var eventStore = new EventStore(adapter, new JsonEventSerializer());
```

## Example: PostgreSQL Implementation

For PostgreSQL, the pattern is similar but uses `NpgsqlConnection`:

```csharp
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace MyApp.EventSourcing;

public class PostgreSqlEventStoreAdapter : IEventStoreAdapter
{
    private readonly string _connectionString;
    private readonly string _tableName;

    public PostgreSqlEventStoreAdapter(string connectionString, string tableName = "events")
    {
        _connectionString = connectionString;
        _tableName = tableName;
    }

    public async ValueTask InitializeAsync()
    {
        var sql = $@"
            CREATE TABLE IF NOT EXISTS {_tableName} (
                id BIGSERIAL PRIMARY KEY,
                stream_id VARCHAR(256) NOT NULL,
                position INT NOT NULL,
                event_type VARCHAR(256) NOT NULL,
                event_data TEXT NOT NULL,
                timestamp TIMESTAMPTZ NOT NULL,
                UNIQUE(stream_id, position),
                CREATE INDEX IF NOT EXISTS ix_{_tableName}_stream_id ON {_tableName}(stream_id)
            );
        ";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId streamId,
        ReadOnlyMemory<SerializedEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Get current position
            var sql = $"SELECT COALESCE(MAX(position), -1) FROM {_tableName} WHERE stream_id = @stream_id";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@stream_id", streamId.Value);
            
            var currentVersion = (int)await cmd.ExecuteScalarAsync(ct) ?? -1;
            var currentPosition = new StreamPosition(currentVersion);

            // Check optimistic lock
            if (currentPosition != expectedVersion)
            {
                return Result.Error<AppendResult, StoreError>(
                    StoreError.Conflict("Optimistic lock conflict"));
            }

            // Insert events using COPY for performance
            var nextPosition = currentPosition.Next();
            using var writer = connection.BeginBinaryImport(
                $"COPY {_tableName} (stream_id, position, event_type, event_data, timestamp) FROM STDIN (FORMAT BINARY)");

            foreach (var @event in events.Span)
            {
                writer.WriteRow(
                    streamId.Value,
                    nextPosition.Value,
                    @event.Type,
                    @event.Data,
                    DateTimeOffset.UtcNow
                );
                nextPosition = nextPosition.Next();
            }

            writer.Complete();
            await transaction.CommitAsync(ct);

            return Result.Ok<AppendResult, StoreError>(
                new AppendResult(currentPosition, nextPosition - 1));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")  // Unique violation
        {
            return Result.Error<AppendResult, StoreError>(
                StoreError.Conflict("Optimistic lock conflict"));
        }
    }

    public async IAsyncEnumerable<SerializedEvent> ReadAsync(
        StreamId streamId,
        StreamPosition from = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $@"
            SELECT event_type, event_data, position
            FROM {_tableName}
            WHERE stream_id = @stream_id AND position >= @from_position
            ORDER BY position ASC
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@stream_id", streamId.Value);
        command.Parameters.AddWithValue("@from_position", from.Value);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

        while (await reader.ReadAsync(ct))
        {
            yield return new SerializedEvent(
                reader.GetString(0),
                reader.GetString(1),
                new StreamPosition(reader.GetInt32(2))
            );
        }
    }

    public async ValueTask<IEventSubscription> SubscribeAsync(
        StreamId streamId,
        StreamPosition from,
        Func<SerializedEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        // Read historical events
        await foreach (var @event in ReadAsync(streamId, from, ct))
        {
            await handler(@event, ct);
        }

        // PostgreSQL LISTEN/NOTIFY for new events
        return new ListenSubscription(streamId, _connectionString, handler, ct);
    }

    // Implement ListenSubscription using PostgreSQL LISTEN/NOTIFY
}
```

## Key Design Patterns

### 1. Optimistic Locking

Always check the expected version before appending:

```csharp
// Get current position
var currentPosition = GetCurrentPosition(streamId);

// Check optimistic lock
if (currentPosition != expectedVersion)
    return Conflict();

// Append safely
AppendEvents(streamId, events);
```

### 2. Idempotency

Make append operations idempotent using unique constraints:

```sql
CONSTRAINT UK_StreamId_Position UNIQUE (StreamId, Position)
```

This ensures:
- Duplicate appends return the same position
- Network retries are safe
- No lost updates

### 3. Atomic Reads

Use transactions for consistency:

```csharp
using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);

// All reads within this transaction are consistent
var currentVersion = GetVersion(streamId);
var events = ReadEvents(streamId);

transaction.Commit();
```

### 4. Subscription Implementation

For subscriptions, use one of:

1. **Polling** (simplest)
```csharp
while (true)
{
    var newEvents = ReadAsync(streamId, lastPosition);
    await handler(newEvents);
    await Task.Delay(100);
}
```

2. **Database notifications** (PostgreSQL LISTEN, SQL Server Service Broker)
3. **Change streams** (MongoDB)
4. **Message queues** (publish events to Kafka/RabbitMQ)

## Testing Your Adapter

```csharp
[TestClass]
public class SqlServerEventStoreAdapterTests
{
    private SqlServerEventStoreAdapter _adapter;

    [TestInitialize]
    public async Task Setup()
    {
        _adapter = new SqlServerEventStoreAdapter("Server=.;Database=EventStoreTest;");
        await _adapter.InitializeAsync();
    }

    [TestMethod]
    public async Task AppendAsync_AppendsEvents()
    {
        var streamId = new StreamId("order-123");
        var @event = new SerializedEvent("OrderPlaced", "{\"total\":1000}", StreamPosition.Start);

        var result = await _adapter.AppendAsync(
            streamId,
            new[] { @event },
            StreamPosition.Start
        );

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Value.FirstPosition.Value);
    }

    [TestMethod]
    public async Task AppendAsync_DetectsOptimisticLockConflict()
    {
        var streamId = new StreamId("order-456");
        var @event = new SerializedEvent("OrderPlaced", "{}", StreamPosition.Start);

        // First append succeeds
        await _adapter.AppendAsync(streamId, new[] { @event }, StreamPosition.Start);

        // Second append with wrong version fails
        var result = await _adapter.AppendAsync(
            streamId,
            new[] { @event },
            StreamPosition.Start  // Wrong: should be the position after first append
        );

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(StoreError.Type.Conflict, result.Error.Type);
    }

    [TestMethod]
    public async Task ReadAsync_ReadsAllEvents()
    {
        var streamId = new StreamId("order-789");
        var events = new[]
        {
            new SerializedEvent("OrderPlaced", "{\"total\":1000}"),
            new SerializedEvent("OrderShipped", "{\"tracking\":\"ABC123\"}"),
        };

        await _adapter.AppendAsync(streamId, events, StreamPosition.Start);

        var readEvents = new List<SerializedEvent>();
        await foreach (var @event in _adapter.ReadAsync(streamId))
        {
            readEvents.Add(@event);
        }

        Assert.AreEqual(2, readEvents.Count);
        Assert.AreEqual("OrderPlaced", readEvents[0].Type);
        Assert.AreEqual("OrderShipped", readEvents[1].Type);
    }
}
```

## Common Pitfalls

### 1. Not Handling Serialization Errors

Always wrap serialization in try-catch:

```csharp
try
{
    var eventData = JsonSerializer.Serialize(@event);
    await AppendAsync(streamId, eventData);
}
catch (JsonException ex)
{
    return StoreError.SerializationFailed(ex.Message);
}
```

### 2. Ignoring Position Management

Always increment positions correctly:

```csharp
// Wrong: All events get same position
for (int i = 0; i < events.Length; i++)
{
    await AppendAsync(streamId, events[i], currentPosition);  // ✗
}

// Right: Increment position for each event
var nextPosition = currentPosition;
for (int i = 0; i < events.Length; i++)
{
    var result = await AppendAsync(streamId, events[i], nextPosition);
    nextPosition = result.Value.LastPosition.Next();
}
```

### 3. Not Handling Concurrency

Always use transactions for optimistic locking:

```csharp
// Wrong: No atomicity
if (GetVersion(streamId) == expectedVersion)
{
    AppendEvents(streamId, events);  // ✗ Race condition!
}

// Right: Atomic check-and-set
using var transaction = BeginTransaction();
if (GetVersion(streamId) == expectedVersion)
{
    AppendEvents(streamId, events);
}
transaction.Commit();  // Only succeeds if version still matches
```

## Performance Considerations

1. **Batch appends** — Use `ReadOnlyMemory<SerializedEvent>` to append multiple events at once
2. **Connection pooling** — Reuse database connections
3. **Indexing** — Add index on StreamId for fast reads
4. **Pagination** — For large streams, read in batches (e.g., 1000 events at a time)

## Summary

To implement a custom event store:

1. Implement `IEventStoreAdapter` with your storage backend
2. Handle optimistic locking correctly
3. Implement subscriptions (polling or notifications)
4. Test thoroughly for concurrency scenarios
5. Consider performance (batching, indexing, pagination)

The adapter is the core abstraction. Once implemented correctly, your custom event store works seamlessly with ZeroAlloc.EventSourcing.

## Next Steps

- **[Custom Snapshot Stores](./custom-snapshots.md)** — Implementing snapshot persistence
- **[Custom Projections](./custom-projections.md)** — Advanced projection patterns
- **[Core Concepts: Event Store](../core-concepts/event-store.md)** — Event store fundamentals
