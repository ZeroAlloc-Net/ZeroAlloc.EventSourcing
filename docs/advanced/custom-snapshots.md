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

### Strategy 1: Time-Based Snapshots

Snapshot every N time units:

```csharp
public class TimedSnapshotStrategy<TState> where TState : struct
{
    private readonly ISnapshotStore<TState> _store;
    private readonly TimeSpan _interval;
    private DateTime _lastSnapshot = DateTime.MinValue;

    public async ValueTask<bool> ShouldSnapshotAsync()
    {
        return DateTime.UtcNow - _lastSnapshot >= _interval;
    }

    public async ValueTask SnapshotIfNeededAsync(
        StreamId streamId,
        StreamPosition position,
        TState state)
    {
        if (await ShouldSnapshotAsync())
        {
            await _store.WriteAsync(streamId, position, state);
            _lastSnapshot = DateTime.UtcNow;
        }
    }
}

// Usage: Snapshot every 5 minutes
var strategy = new TimedSnapshotStrategy<OrderState>(store, TimeSpan.FromMinutes(5));
```

### Strategy 2: Event-Count-Based Snapshots

Snapshot every N events:

```csharp
public class CountBasedSnapshotStrategy<TState> where TState : struct
{
    private readonly ISnapshotStore<TState> _store;
    private readonly int _eventInterval;
    private int _eventsSinceLastSnapshot = 0;

    public async ValueTask<bool> ShouldSnapshotAsync()
    {
        return _eventsSinceLastSnapshot >= _eventInterval;
    }

    public async ValueTask SnapshotIfNeededAsync(
        StreamId streamId,
        StreamPosition position,
        TState state)
    {
        _eventsSinceLastSnapshot++;

        if (await ShouldSnapshotAsync())
        {
            await _store.WriteAsync(streamId, position, state);
            _eventsSinceLastSnapshot = 0;
        }
    }
}

// Usage: Snapshot every 100 events
var strategy = new CountBasedSnapshotStrategy<OrderState>(store, eventInterval: 100);
```

### Strategy 3: Adaptive Snapshots

Snapshot only when aggregate becomes large:

```csharp
public class AdaptiveSnapshotStrategy<TState> where TState : struct
{
    private readonly ISnapshotStore<TState> _store;
    private StreamPosition _lastSnapshotPosition = StreamPosition.Start;

    public async ValueTask<bool> ShouldSnapshotAsync(StreamPosition currentPosition)
    {
        // Snapshot if 500+ events since last snapshot
        return currentPosition.Value - _lastSnapshotPosition.Value >= 500;
    }

    public async ValueTask SnapshotIfNeededAsync(
        StreamId streamId,
        StreamPosition position,
        TState state)
    {
        if (await ShouldSnapshotAsync(position))
        {
            await _store.WriteAsync(streamId, position, state);
            _lastSnapshotPosition = position;
        }
    }
}
```

## Loading Aggregates with Snapshots

```csharp
public async Task<Order> LoadOrderAsync(
    OrderId orderId,
    IEventStore eventStore,
    ISnapshotStore<OrderState> snapshotStore)
{
    var order = new Order();
    order.SetId(orderId);

    var streamId = new StreamId($"order-{orderId.Value}");

    // 1. Try to load snapshot
    var snapshot = await snapshotStore.ReadAsync(streamId);

    StreamPosition startPosition;
    if (snapshot.HasValue)
    {
        // Load state from snapshot
        order.LoadSnapshot(snapshot.Value.State);
        startPosition = snapshot.Value.Position.Next();
    }
    else
    {
        // Load from beginning
        startPosition = StreamPosition.Start;
    }

    // 2. Replay remaining events
    await foreach (var envelope in eventStore.ReadAsync(streamId, startPosition))
    {
        order.ApplyHistoric(envelope.Event, envelope.Position);
    }

    return order;
}
```

## Snapshot Rebuilding

When aggregate structure changes, rebuild all snapshots:

```csharp
public async Task RebuildSnapshotsAsync<TState>(
    IEventStore eventStore,
    ISnapshotStore<TState> snapshotStore,
    StreamId streamId)
    where TState : struct
{
    // 1. Load aggregate from scratch
    var order = new Order();
    order.SetId(orderId);

    var eventCount = 0;
    StreamPosition? lastPosition = null;

    // 2. Replay all events
    await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
    {
        order.ApplyHistoric(envelope.Event, envelope.Position);
        lastPosition = envelope.Position;
        eventCount++;

        // 3. Snapshot every 100 events
        if (eventCount % 100 == 0 && lastPosition.HasValue)
        {
            await snapshotStore.WriteAsync(streamId, lastPosition.Value, order.State);
        }
    }
}

// Usage: Rebuild snapshots for all orders
public async Task RebuildAllSnapshotsAsync(IEventStore eventStore, ISnapshotStore<OrderState> snapshotStore)
{
    // Get all stream IDs (implementation-specific)
    var streamIds = await GetAllOrderStreamIdsAsync();

    foreach (var streamId in streamIds)
    {
        await RebuildSnapshotsAsync(eventStore, snapshotStore, streamId);
    }
}
```

## Testing Snapshot Stores

```csharp
[TestClass]
public class SnapshotStoreTests
{
    private ISnapshotStore<OrderState> _store;

    [TestInitialize]
    public async Task Setup()
    {
        _store = new SqlServerSnapshotStore<OrderState>(
            "Server=.;Database=EventStoreTest;"
        );
        await _store.InitializeAsync();
    }

    [TestMethod]
    public async Task WriteAsync_WritesSnapshot()
    {
        var streamId = new StreamId("order-123");
        var state = new OrderState { Total = 1000, IsPlaced = true };
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
        var state1 = new OrderState { Total = 1000 };
        await _store.WriteAsync(streamId, new StreamPosition(10), state1);

        // Write second snapshot
        var state2 = new OrderState { Total = 2000 };
        await _store.WriteAsync(streamId, new StreamPosition(20), state2);

        // Should have second snapshot
        var read = await _store.ReadAsync(streamId);
        Assert.AreEqual(new StreamPosition(20), read.Value.Position);
        Assert.AreEqual(2000, read.Value.State.Total);
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
// Load snapshot (if exists)
var snapshot = await snapshotStore.ReadAsync(streamId);
if (snapshot.HasValue)
{
    aggregate.LoadSnapshot(snapshot.Value.State);
    startPosition = snapshot.Value.Position.Next();
}

// Replay recent events
await foreach (var e in eventStore.ReadAsync(streamId, startPosition))
{
    aggregate.ApplyHistoric(e.Event, e.Position);
}
```

### Pattern 2: Periodic Snapshotting

On aggregate save:

```csharp
public async Task SaveAsync(Order order)
{
    // Append events
    var result = await eventStore.AppendAsync(...);

    // Snapshot every 100 events
    if (order.Version % 100 == 0)
    {
        await snapshotStore.WriteAsync(streamId, order.Version, order.State);
    }
}
```

### Pattern 3: Lazy Snapshots

Build snapshots only for frequently-accessed aggregates:

```csharp
private readonly Dictionary<StreamId, int> _accessCounts = new();

public async Task<Order> LoadAsync(StreamId streamId)
{
    // Track access
    if (!_accessCounts.ContainsKey(streamId))
        _accessCounts[streamId] = 0;
    _accessCounts[streamId]++;

    var order = await LoadFromEventStore(streamId);

    // Snapshot only if accessed 10+ times
    if (_accessCounts[streamId] >= 10 && order.Version > 500)
    {
        await snapshotStore.WriteAsync(streamId, order.Version, order.State);
    }

    return order;
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
