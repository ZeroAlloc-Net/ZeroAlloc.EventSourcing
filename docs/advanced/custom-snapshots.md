# Custom Snapshot Store Implementations

**Version:** 1.0  
**Last Updated:** 2026-04-04

## Overview

Snapshot stores persist periodic snapshots of aggregate state to avoid replaying entire event histories. While the library provides an in-memory implementation, you may need custom storage (SQL, Redis, etc.).

This guide shows how to implement `ISnapshotStore<TState>` for your storage backend.

## ISnapshotStore<TState> Interface

```csharp
public interface ISnapshotStore<TState> where TState : struct
{
    /// <summary>Reads the most recent snapshot, or null if none exists.</summary>
    ValueTask<(StreamPosition Position, TState State)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default);

    /// <summary>Saves a snapshot at the given position.</summary>
    ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        TState state,
        CancellationToken ct = default);
}
```

Key points:

- Generic on state type `TState` (must be a struct)
- Read returns tuple of position + state (or null)
- Write is last-write-wins (no versioning)
- Snapshots are optional (can always reload from events)

## Example: SQL Server Implementation

```csharp
using System.Data.SqlClient;
using System.Text.Json;
using ZeroAlloc.EventSourcing;

namespace MyApp.EventSourcing;

public class SqlServerSnapshotStore<TState> : ISnapshotStore<TState>
    where TState : struct
{
    private readonly string _connectionString;
    private readonly string _tableName;

    public SqlServerSnapshotStore(string connectionString, string tableName = "Snapshots")
    {
        _connectionString = connectionString;
        _tableName = tableName;
    }

    /// <summary>Creates the snapshots table if it doesn't exist.</summary>
    public async ValueTask InitializeAsync()
    {
        var sql = $@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{_tableName}')
            BEGIN
                CREATE TABLE {_tableName} (
                    Id BIGINT PRIMARY KEY IDENTITY(1,1),
                    StreamId NVARCHAR(256) NOT NULL UNIQUE,
                    Position INT NOT NULL,
                    StateJson NVARCHAR(MAX) NOT NULL,
                    CreatedAt DATETIMEOFFSET NOT NULL,
                    INDEX IX_StreamId (StreamId)
                );
            END
        ";

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand(sql, connection);
        await connection.OpenAsync();
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask<(StreamPosition, TState)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default)
    {
        var sql = $@"
            SELECT Position, StateJson
            FROM {_tableName}
            WHERE StreamId = @StreamId
        ";

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@StreamId", streamId.Value);

        await connection.OpenAsync(ct);
        using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow, ct);

        if (!await reader.ReadAsync(ct))
            return null;  // No snapshot found

        var position = new StreamPosition(reader.GetInt32(0));
        var stateJson = reader.GetString(1);
        var state = JsonSerializer.Deserialize<TState>(stateJson)!;

        return (position, state);
    }

    public async ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        TState state,
        CancellationToken ct = default)
    {
        var stateJson = JsonSerializer.Serialize(state);

        var sql = $@"
            MERGE INTO {_tableName} AS target
            USING (SELECT @StreamId AS StreamId) AS source
            ON target.StreamId = source.StreamId
            WHEN MATCHED THEN
                UPDATE SET Position = @Position, StateJson = @StateJson, CreatedAt = @CreatedAt
            WHEN NOT MATCHED THEN
                INSERT (StreamId, Position, StateJson, CreatedAt)
                VALUES (@StreamId, @Position, @StateJson, @CreatedAt);
        ";

        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@StreamId", streamId.Value);
        command.Parameters.AddWithValue("@Position", position.Value);
        command.Parameters.AddWithValue("@StateJson", stateJson);
        command.Parameters.AddWithValue("@CreatedAt", DateTimeOffset.UtcNow);

        await connection.OpenAsync(ct);
        await command.ExecuteNonQueryAsync(ct);
    }
}

// Usage
var snapshotStore = new SqlServerSnapshotStore<OrderState>(
    "Server=.;Database=EventStore;Integrated Security=true"
);
await snapshotStore.InitializeAsync();
```

## Example: Redis Implementation

For high-performance snapshot caching:

```csharp
using System.Text.Json;
using StackExchange.Redis;
using ZeroAlloc.EventSourcing;

namespace MyApp.EventSourcing;

public class RedisSnapshotStore<TState> : ISnapshotStore<TState>
    where TState : struct
{
    private readonly IDatabase _db;
    private readonly string _keyPrefix;

    public RedisSnapshotStore(IConnectionMultiplexer redis, string keyPrefix = "snapshot:")
    {
        _db = redis.GetDatabase();
        _keyPrefix = keyPrefix;
    }

    public async ValueTask<(StreamPosition, TState)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default)
    {
        var key = $"{_keyPrefix}{streamId.Value}";

        // Redis value format: "position|stateJson"
        var value = await _db.StringGetAsync(key);

        if (!value.HasValue)
            return null;

        var parts = value.ToString().Split('|');
        if (parts.Length != 2)
            return null;

        if (!int.TryParse(parts[0], out var positionValue))
            return null;

        var position = new StreamPosition(positionValue);
        var state = JsonSerializer.Deserialize<TState>(parts[1])!;

        return (position, state);
    }

    public async ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        TState state,
        CancellationToken ct = default)
    {
        var key = $"{_keyPrefix}{streamId.Value}";
        var stateJson = JsonSerializer.Serialize(state);
        var value = $"{position.Value}|{stateJson}";

        // Store with 24-hour expiration (optional)
        await _db.StringSetAsync(key, value, expiry: TimeSpan.FromHours(24));
    }
}

// Usage
var redis = ConnectionMultiplexer.Connect("localhost:6379");
var snapshotStore = new RedisSnapshotStore<OrderState>(redis);
```

## Example: PostgreSQL with JSONB

For powerful querying:

```csharp
using Npgsql;
using System.Text.Json;
using ZeroAlloc.EventSourcing;

namespace MyApp.EventSourcing;

public class PostgreSqlSnapshotStore<TState> : ISnapshotStore<TState>
    where TState : struct
{
    private readonly string _connectionString;
    private readonly string _tableName;

    public PostgreSqlSnapshotStore(string connectionString, string tableName = "snapshots")
    {
        _connectionString = connectionString;
        _tableName = tableName;
    }

    public async ValueTask InitializeAsync()
    {
        var sql = $@"
            CREATE TABLE IF NOT EXISTS {_tableName} (
                id SERIAL PRIMARY KEY,
                stream_id VARCHAR(256) NOT NULL UNIQUE,
                position INT NOT NULL,
                state JSONB NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                INDEX ix_{_tableName}_stream_id ON {_tableName}(stream_id)
            );
        ";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask<(StreamPosition, TState)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default)
    {
        var sql = $"SELECT position, state FROM {_tableName} WHERE stream_id = @stream_id";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@stream_id", streamId.Value);

        using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow, ct);

        if (!await reader.ReadAsync(ct))
            return null;

        var position = new StreamPosition(reader.GetInt32(0));
        var stateJson = reader.GetString(1);
        var state = JsonSerializer.Deserialize<TState>(stateJson)!;

        return (position, state);
    }

    public async ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        TState state,
        CancellationToken ct = default)
    {
        var stateJson = JsonSerializer.Serialize(state);

        var sql = $@"
            INSERT INTO {_tableName} (stream_id, position, state)
            VALUES (@stream_id, @position, @state::jsonb)
            ON CONFLICT (stream_id) DO UPDATE SET
                position = EXCLUDED.position,
                state = EXCLUDED.state
        ";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@stream_id", streamId.Value);
        command.Parameters.AddWithValue("@position", position.Value);
        command.Parameters.AddWithValue("@state", stateJson);

        await command.ExecuteNonQueryAsync(ct);
    }
}
```

## Snapshot Strategy: When and How Often

`SnapshotCachingRepositoryDecorator` decides when to write a snapshot with an `ISnapshotPolicy`,
asked after each successful save. It gets the stream position after the save and the position
of the last snapshot, if any:

```csharp
public interface ISnapshotPolicy
{
    bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition);
}
```

Pass it as `snapshotPolicy:`, together with `extractState: order => order.State`
(see [Pattern 2](#pattern-2-periodic-snapshotting)).

### Strategy 1: Event-Count-Based Snapshots

Snapshot every N events. The library ships this one:

```csharp
// Snapshot once 100 events have been appended since the last snapshot
ISnapshotPolicy policy = SnapshotPolicy.EveryNEvents(100);

// Or on every save, or never
ISnapshotPolicy always = SnapshotPolicy.Always;
ISnapshotPolicy never = SnapshotPolicy.Never;
```

### Strategy 2: Time-Based Snapshots

Snapshot a stream at most once per time interval. The policy is not told which stream it is
asked about, so a time-based rule is a small wrapper around the save instead:

```csharp
public sealed class TimedSnapshotWriter<TState> where TState : struct
{
    private readonly ISnapshotStore<TState> _store;
    private readonly TimeSpan _interval;
    private readonly ConcurrentDictionary<StreamId, DateTimeOffset> _lastSnapshot = new();

    public TimedSnapshotWriter(ISnapshotStore<TState> store, TimeSpan interval)
    {
        _store = store;
        _interval = interval;
    }

    // Call after a successful save, with the saved aggregate's Version and State
    public async ValueTask SnapshotIfNeededAsync(
        StreamId streamId,
        StreamPosition position,
        TState state,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var last = _lastSnapshot.GetValueOrDefault(streamId, DateTimeOffset.MinValue);
        if (now - last < _interval)
            return;

        await _store.WriteAsync(streamId, position, state, ct);
        _lastSnapshot[streamId] = now;
    }
}

// Usage: Snapshot each stream at most every 5 minutes
var writer = new TimedSnapshotWriter<OrderState>(store, TimeSpan.FromMinutes(5));
```

### Strategy 3: Adaptive Snapshots

Snapshot short streams rarely and long streams often:

```csharp
public sealed class AdaptiveSnapshotPolicy : ISnapshotPolicy
{
    public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition)
    {
        var sinceLast = currentPosition.Value - (lastSnapshotPosition?.Value ?? 0);

        // Streams under 500 events are cheap to replay: no snapshot.
        // Beyond that, snapshot every 500 events, and every 100 past 10,000.
        if (currentPosition.Value < 500)
            return false;
        return sinceLast >= (currentPosition.Value > 10_000 ? 100 : 500);
    }
}
```

## Loading Aggregates with Snapshots

A custom store plugs into `SnapshotCachingRepositoryDecorator` like the built-in ones. The decorator
reads the snapshot, restores it onto a fresh aggregate, and replays only the events after it.
Replaying events onto an aggregate is internal to the repositories, so load through the decorator
rather than writing that loop yourself:

```csharp
public async Task<Order> LoadOrderAsync(
    OrderId orderId,
    IEventStore eventStore,
    ISnapshotStore<OrderState> snapshotStore)
{
    var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
        innerRepository: new AggregateRepository<Order, OrderId>(
            eventStore,
            () => new Order(),
            id => new StreamId($"order-{id.Value}")),
        snapshotStore: snapshotStore,  // your custom store
        strategy: SnapshotLoadingStrategy.ValidateAndReplay,
        restoreState: (order, state, pos) => order.RestoreState(state, pos),
        eventStore: eventStore,
        streamIdFactory: id => new StreamId($"order-{id.Value}"),
        aggregateFactory: () => new Order());

    // 1. Reads the snapshot, if any, and restores it
    // 2. Replays the events after the snapshot position (or all of them without a snapshot)
    var result = await repository.LoadAsync(orderId);
    if (result.IsFailure)
        throw new InvalidOperationException(result.Error.ToString());

    return result.Value;  // the caller disposes the aggregate
}
```

In an application, build the repository once (or register it in DI) rather than per load.
[`CustomSnapshotStore.cs`](../examples/04-advanced/CustomSnapshotStore.cs) is a runnable example
with a custom JSON snapshot store.

## Snapshot Rebuilding

When aggregate structure changes, rebuild all snapshots:

Load each aggregate through the plain `AggregateRepository`, which replays the full stream and never
reads snapshots, and write its state at its version. A store keeps only the latest snapshot per
stream, so one write per aggregate is enough:

```csharp
public async Task RebuildSnapshotAsync(
    AggregateRepository<Order, OrderId> fullReplayRepository,
    ISnapshotStore<OrderState> snapshotStore,
    OrderId orderId)
{
    // 1. Load aggregate from scratch: replays all events with the new logic
    var result = await fullReplayRepository.LoadAsync(orderId);
    if (result.IsFailure)
        throw new InvalidOperationException(result.Error.ToString());

    using var order = result.Value;
    if (order.Version == StreamPosition.Start)
        return;  // empty stream, nothing to snapshot

    // 2. Version is the position of the last replayed event
    var streamId = new StreamId($"order-{orderId.Value}");
    await snapshotStore.WriteAsync(streamId, order.Version, order.State);
}

// Usage: Rebuild snapshots for all orders
public async Task RebuildAllSnapshotsAsync(IEventStore eventStore, ISnapshotStore<OrderState> snapshotStore)
{
    var repository = new AggregateRepository<Order, OrderId>(
        eventStore,
        () => new Order(),
        id => new StreamId($"order-{id.Value}"));

    // Get all order IDs (implementation-specific: IEventStore reads one stream at a time)
    var orderIds = await GetAllOrderIdsAsync();

    foreach (var orderId in orderIds)
    {
        await RebuildSnapshotAsync(repository, snapshotStore, orderId);
    }
}
```

## Testing Snapshot Stores

```csharp
[TestClass]
public class SnapshotStoreTests
{
    private SqlServerSnapshotStore<OrderState> _store;

    [TestInitialize]
    public async Task Setup()
    {
        _store = new SqlServerSnapshotStore<OrderState>(
            "Server=.;Database=EventStoreTest;",
            serializer: mySerializer  // your IEventSerializer; it must handle OrderState
        );
        await _store.EnsureSchemaAsync();
    }

    [TestMethod]
    public async Task WriteAsync_WritesSnapshot()
    {
        var streamId = new StreamId("order-123");
        var state = StateAfterPlacing(1000m);
        var position = new StreamPosition(10);

        await _store.WriteAsync(streamId, position, state);

        var read = await _store.ReadAsync(streamId);
        Assert.IsTrue(read.HasValue);
        Assert.AreEqual(position, read.Value.Position);
        Assert.AreEqual(1000, read.Value.State.Total);
    }

    [TestMethod]
    public async Task ReadAsync_ReturnsNullWhenNotFound()
    {
        var streamId = new StreamId("nonexistent");
        var read = await _store.ReadAsync(streamId);
        Assert.IsFalse(read.HasValue);
    }

    [TestMethod]
    public async Task WriteAsync_OverwritesOldSnapshot()
    {
        var streamId = new StreamId("order-456");

        // Write first snapshot
        var state1 = StateAfterPlacing(1000m);
        await _store.WriteAsync(streamId, new StreamPosition(10), state1);

        // Write second snapshot
        var state2 = StateAfterPlacing(2000m);
        await _store.WriteAsync(streamId, new StreamPosition(20), state2);

        // Should have second snapshot
        var read = await _store.ReadAsync(streamId);
        Assert.AreEqual(new StreamPosition(20), read.Value.Position);
        Assert.AreEqual(2000, read.Value.State.Total);
    }

    // State has private setters: build it the way the application does, by raising events
    private static OrderState StateAfterPlacing(decimal total)
    {
        using var order = new Order();
        order.Place("ORD-001", total);
        return order.State;
    }
}
```

## Performance Considerations

1. **Serialization format** — JSON is readable but slower; MessagePack/Protobuf are faster
2. **Connection pooling** — Reuse database connections
3. **Indexing** — Index on StreamId for fast lookups
4. **Compression** — Compress state JSON for storage savings

## Common Patterns

### Pattern 1: Snapshot + Event Replay

Fastest for large aggregates:

```csharp
// SnapshotCachingRepositoryDecorator, configured as in "Loading Aggregates with Snapshots":
// 1. loads the snapshot (if it exists) and restores it with RestoreState
// 2. replays only the events after the snapshot position
var result = await snapshotRepository.LoadAsync(orderId);
using var order = result.Value;
```

### Pattern 2: Periodic Snapshotting

On aggregate save:

```csharp
// The decorator writes a snapshot after a successful save once the policy says so
var snapshotRepository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: innerRepository,
    snapshotStore: snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (order, state, pos) => order.RestoreState(state, pos),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order(),
    snapshotPolicy: SnapshotPolicy.EveryNEvents(100),  // Snapshot every 100 events
    extractState: order => order.State);

// Appends the events, then snapshots if 100+ events were appended since the last snapshot
var saved = await snapshotRepository.SaveAsync(order, orderId);
```

### Pattern 3: Lazy Snapshots

Build snapshots only for frequently-accessed aggregates:

```csharp
private readonly ConcurrentDictionary<OrderId, int> _accessCounts = new();

public async Task<Order> LoadAsync(OrderId orderId, CancellationToken ct = default)
{
    // Track access
    var accesses = _accessCounts.AddOrUpdate(orderId, 1, (_, n) => n + 1);

    // snapshotRepository: the decorator from Pattern 2, with snapshotPolicy: SnapshotPolicy.Never
    var order = (await snapshotRepository.LoadAsync(orderId, ct)).Value;

    // Snapshot only if accessed 10+ times, and only a long stream
    if (accesses >= 10 && order.Version.Value > 500)
    {
        var streamId = new StreamId($"order-{orderId.Value}");
        await snapshotStore.WriteAsync(streamId, order.Version, order.State, ct);
    }

    return order;  // the caller disposes it
}
```

## Summary

Snapshot stores enable:

- **Faster aggregate loading** (avoid replaying entire history)
- **Reduced CPU usage** (especially for large aggregates)
- **Scalability** (handle larger event streams)

Key implementation details:

1. Implement `ISnapshotStore<TState>` for your storage backend
2. Use UPSERT/MERGE for last-write-wins semantics
3. Choose snapshots strategy (time-based, count-based, adaptive)
4. Test thoroughly for consistency
5. Consider performance (serialization, compression, indexing)

## Next Steps

- **[Custom Projections](./custom-projections.md)** — Advanced projection patterns
- **[Optimization Strategies](../performance/optimization.md)** — Performance tuning
- **[Core Concepts: Snapshots](../core-concepts/snapshots.md)** — Snapshot fundamentals
