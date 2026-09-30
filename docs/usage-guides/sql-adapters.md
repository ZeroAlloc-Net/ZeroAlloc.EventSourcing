# Usage Guide: SQL Adapters

## Scenario: How Do I Persist Events and Snapshots to a Database?

ZeroAlloc.EventSourcing supports both PostgreSQL and SQL Server for storing events and snapshots. This guide covers setup, configuration, performance tuning, and deployment strategies. A SQLite adapter ships in `ZeroAlloc.EventSourcing.Sqlite` as well.

## PostgreSQL Setup and Configuration

### Installation

Add the PostgreSQL adapter package:

```bash
dotnet add package ZeroAlloc.EventSourcing.PostgreSql
```

### Basic Configuration

For the event type registry and the serializer, see [Building Aggregates](./building-aggregates.md)
and [Events](../core-concepts/events.md):

```csharp
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.PostgreSql;

var connectionString = "Host=localhost;Database=EventStore;Username=postgres;Password=password";

// The adapter takes an NpgsqlDataSource, which owns the connection pool: create one per
// database and keep it for the lifetime of the application
var dataSource = NpgsqlDataSource.Create(connectionString);
var adapter = new PostgreSqlEventStoreAdapter(dataSource);

// Create the event_store table if it does not exist; safe to run on every start
await adapter.EnsureSchemaAsync();

// Create event store
var registry = new OrderEventTypeRegistry();  // generated for your Order aggregate
var eventStore = new EventStore(adapter, serializer, registry);
```

### Connection Pooling

PostgreSQL uses connection pooling by default; the `NpgsqlDataSource` owns the pool. Configure it for high throughput:

```csharp
var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = "localhost",
    Database = "EventStore",
    Username = "postgres",
    Password = "password",

    // Connection pooling
    MaxPoolSize = 100,           // Max connections
    MinPoolSize = 10,            // Min connections
    CommandTimeout = 30,         // Command timeout (seconds)
    ConnectionIdleLifetime = 300 // Idle connection timeout (seconds)
}.ConnectionString;

var adapter = new PostgreSqlEventStoreAdapter(NpgsqlDataSource.Create(connectionString));
```

### Schema Creation

`EnsureSchemaAsync()` creates the table below if it does not exist, and migrates tables created by
older versions. Run it at startup or in your deployment; the dependency-injection registrations do
not run it for you.

```sql
CREATE TABLE IF NOT EXISTS event_store (
    stream_id       TEXT          NOT NULL,
    position        BIGINT        NOT NULL,
    global_position BIGSERIAL     NOT NULL,
    event_type      TEXT          NOT NULL,
    event_id        UUID          NOT NULL,
    occurred_at     TIMESTAMPTZ   NOT NULL,
    correlation_id  UUID          NULL,
    causation_id    UUID          NULL,
    payload         BYTEA         NOT NULL,
    PRIMARY KEY (stream_id, position)
);

CREATE INDEX IF NOT EXISTS event_store_global_position_idx ON event_store (global_position);
```

The snapshot, checkpoint, projection and dead-letter stores in `ZeroAlloc.EventSourcing.Sql` each
have their own `EnsureSchemaAsync()`. The snapshot table:

```sql
CREATE TABLE IF NOT EXISTS snapshots (
    stream_id   VARCHAR(255)   NOT NULL,
    position    BIGINT         NOT NULL,
    state_type  VARCHAR(500)   NOT NULL,
    payload     BYTEA          NOT NULL,
    created_at  TIMESTAMPTZ    NOT NULL,
    PRIMARY KEY (stream_id)
);
```

### Connection String Patterns

**Local Development:**
```
Host=localhost;Database=EventStore;Username=postgres;Password=password
```

**Staging:**
```
Host=staging-db.example.com;Database=EventStore;Username=app_user;Password=SecurePassword123;SSL Mode=Require
```

**Production:**
```
Host=prod-db.example.com;Database=EventStore;Username=app_user;Password=SecurePassword123;SSL Mode=Require;Application Name=EventSourcingApp
```

## SQL Server Setup and Configuration

### Installation

Add the SQL Server adapter package:

```bash
dotnet add package ZeroAlloc.EventSourcing.SqlServer
```

### Basic Configuration

```csharp
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

var connectionString = "Server=localhost;Database=EventStore;User Id=sa;Password=YourPassword123;TrustServerCertificate=true";

// Create adapter
var adapter = new SqlServerEventStoreAdapter(connectionString);

// Create the dbo.event_store table if it does not exist; safe to run on every start
await adapter.EnsureSchemaAsync();

// Create event store
var registry = new OrderEventTypeRegistry();  // generated for your Order aggregate
var eventStore = new EventStore(adapter, serializer, registry);
```

### Connection Pooling

SQL Server pools connections by default. Configure for high throughput:

```csharp
var connectionString = new SqlConnectionStringBuilder
{
    DataSource = "localhost",
    InitialCatalog = "EventStore",
    UserID = "sa",
    Password = "YourPassword123",

    // Connection pooling
    Pooling = true,
    MaxPoolSize = 100,
    MinPoolSize = 10,
    ConnectTimeout = 30,       // Connection timeout (seconds)
    LoadBalanceTimeout = 300   // Connection lifetime in the pool (seconds)
}.ConnectionString;

var adapter = new SqlServerEventStoreAdapter(connectionString);
```

### Schema Creation

`EnsureSchemaAsync()` creates the table below if it does not exist, and migrates tables created by
older versions:

```sql
CREATE TABLE dbo.event_store (
    stream_id       NVARCHAR(255)     NOT NULL,
    position        BIGINT            NOT NULL,
    global_position BIGINT            IDENTITY(1,1) NOT NULL,
    event_type      NVARCHAR(500)     NOT NULL,
    event_id        UNIQUEIDENTIFIER  NOT NULL,
    occurred_at     DATETIMEOFFSET    NOT NULL,
    correlation_id  UNIQUEIDENTIFIER  NULL,
    causation_id    UNIQUEIDENTIFIER  NULL,
    payload         VARBINARY(MAX)    NOT NULL,
    CONSTRAINT PK_event_store PRIMARY KEY (stream_id, position)
);

CREATE INDEX event_store_global_position_idx ON dbo.event_store (global_position);
```

The snapshot table, `dbo.snapshots`, has the same columns as the PostgreSQL one.

### Connection String Patterns

**Local Development:**
```
Server=(local);Database=EventStore;Integrated Security=true;Encrypt=false
```

**Staging:**
```
Server=staging-db.example.com;Database=EventStore;User Id=app_user;Password=SecurePassword123;Encrypt=true;TrustServerCertificate=false
```

**Production:**
```
Server=prod-db.example.com;Database=EventStore;User Id=app_user;Password=SecurePassword123;Encrypt=true;TrustServerCertificate=false;Application Name=EventSourcingApp
```

## Serialization Strategies

The adapters store whatever bytes the `IEventSerializer` produces. The recommended serializer is the
AOT-safe `ZeroAllocEventSerializer`; see [Events](../core-concepts/events.md). The serializers below
are alternatives.

### JSON Serialization

```csharp
public class JsonEventSerializer : IEventSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
    {
        return JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), Options);
    }

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        return JsonSerializer.Deserialize(payload.Span, eventType, Options)
            ?? throw new InvalidOperationException("Deserialization failed");
    }
}
```

This one uses reflection; under NativeAOT, use `ZeroAllocEventSerializer` or pass `JsonTypeInfo`
from a source-generated `JsonSerializerContext`.

**Advantages:**
- Human-readable (useful for debugging)
- Language-agnostic (other services can read)
- Handles schema evolution easily

**Disadvantages:**
- Larger storage (vs. binary)
- Slower serialization (vs. binary)

### Binary Serialization (MessagePack)

```csharp
using MessagePack;

public class MessagePackEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
    {
        return MessagePackSerializer.Serialize(@event.GetType(), @event);
    }

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        return MessagePackSerializer.Deserialize(eventType, payload)
            ?? throw new InvalidOperationException("Deserialization failed");
    }
}
```

**Advantages:**
- Compact (smaller storage)
- Fast (good for high throughput)

**Disadvantages:**
- Binary format (harder to debug)
- Requires schema coordination

### Compression

Compress large events:

```csharp
public class CompressedEventSerializer : IEventSerializer
{
    private readonly IEventSerializer _inner;

    public CompressedEventSerializer(IEventSerializer inner)
    {
        _inner = inner;
    }

    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
    {
        var uncompressed = _inner.Serialize(@event);

        using var target = new MemoryStream();
        using (var gzip = new GZipStream(target, CompressionMode.Compress))
        {
            gzip.Write(uncompressed.Span);
        }

        return target.ToArray();
    }

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        using var source = new MemoryStream(payload.ToArray());
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();

        gzip.CopyTo(target);
        return _inner.Deserialize(target.ToArray(), eventType);
    }
}
```

## Indexes and Query Optimization

### Essential Indexes

The schemas above include the indexes the adapters need:

- the primary key on `(stream_id, position)`, which serves stream reads and rejects a second writer
  at the same position;
- `event_store_global_position_idx` on `global_position`, which serves reads of `StreamId.Global`.

Add indexes for your own ad-hoc queries, for example on `event_type` or `occurred_at`, if you run
them often.

### Query Analysis

Analyze slow queries:

**PostgreSQL:**
```sql
-- Find slow queries (requires the pg_stat_statements extension)
SELECT
    query,
    calls,
    mean_exec_time,
    max_exec_time
FROM pg_stat_statements
WHERE query LIKE '%event_store%'
ORDER BY mean_exec_time DESC;

-- EXPLAIN ANALYZE to understand query plan
EXPLAIN ANALYZE
SELECT * FROM event_store
WHERE stream_id = 'order-123'
ORDER BY position;
```

**SQL Server:**
```sql
-- Find slow queries
SELECT
    qs.query_hash,
    st.text AS statement_text,
    qs.execution_count,
    qs.total_elapsed_time / 1000 AS total_ms,
    qs.total_elapsed_time / qs.execution_count / 1000 AS avg_ms
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text LIKE '%event_store%'
ORDER BY qs.total_elapsed_time DESC;
```

## Backup and Disaster Recovery

### Backup Strategy

**PostgreSQL:**
```bash
# Full backup
pg_dump -h localhost -U postgres EventStore > backup.sql

# Compressed backup
pg_dump -h localhost -U postgres -F c EventStore > backup.dump

# Restore
pg_restore -h localhost -U postgres -d EventStore backup.dump
```

**SQL Server:**
```sql
-- Full backup
BACKUP DATABASE [EventStore]
TO DISK = N'D:\Backups\EventStore.bak'
WITH FORMAT, MEDIANAME = 'EventStoreBackup';

-- Restore
RESTORE DATABASE [EventStore]
FROM DISK = N'D:\Backups\EventStore.bak'
WITH REPLACE;
```

For point-in-time recovery, use the database's own tools: PostgreSQL WAL archiving, or SQL Server
full recovery model with log backups.

### Archiving Streams

Copy streams to a second event store, for example a cheaper database for closed orders:

```csharp
public class EventArchiver
{
    private readonly IEventStore _live;
    private readonly IEventStore _archive;

    public EventArchiver(IEventStore live, IEventStore archive)
    {
        _live = live;
        _archive = archive;
    }

    public async Task ArchiveStreamAsync(StreamId streamId, CancellationToken ct = default)
    {
        // Copy the stream's events to the archive
        var events = new List<object>();
        await foreach (var envelope in _live.ReadAsync(streamId, StreamPosition.Start, ct))
        {
            events.Add(envelope.Event);
        }

        // The archive gives the events new metadata: event IDs and timestamps are not copied
        var result = await _archive.AppendAsync(streamId, events.ToArray(), StreamPosition.Start, ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.Error.ToString());  // e.g. [CONFLICT]: already archived
    }
}
```

The event store is append-only: the library has no API to delete events. Removing archived events
from the live table is a database operation, and anything that still points into the stream, such
as a snapshot, a checkpoint or a projection, has to be cleaned up with it.

## Data Migration and Versioning

### Schema Evolution

The adapters own their table layout. `EnsureSchemaAsync()` creates it and migrates tables created by
older versions; for example it adds and backfills `global_position` in tables that predate global
reads. Don't change the adapter tables yourself; keep your own data in your own tables.

### Event Migration

Don't rewrite stored events when an event's shape changes. Register an upcaster: events are
upgraded as they are read.

```csharp
// The old shape stays readable: keep it, and its entry in the event type registry
public record OrderPlacedEvent_V1(string OrderId, decimal Total);
public record OrderPlacedEvent_V2(string OrderId, decimal Total, string CustomerId);
services
    .AddEventSourcing()
    .UseSqlServerEventStore(connectionString)
    // Every V1 event read from the store is handed to your code as V2
    .AddUpcaster<OrderPlacedEvent_V1, OrderPlacedEvent_V2>(
        old => new OrderPlacedEvent_V2(old.OrderId, old.Total, CustomerId: "unknown"));
```

## Monitoring and Observability

### Query Logging

Log slow appends by decorating the adapter:

```csharp
public class LoggingEventStoreAdapter : IEventStoreAdapter
{
    private readonly IEventStoreAdapter _inner;
    private readonly ILogger<LoggingEventStoreAdapter> _logger;

    public LoggingEventStoreAdapter(IEventStoreAdapter inner, ILogger<LoggingEventStoreAdapter> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _inner.AppendAsync(id, events, expectedVersion, ct);
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds > 100)
            {
                _logger.LogWarning(
                    "Slow append: {StreamId} took {Ms}ms",
                    id.Value,
                    stopwatch.ElapsedMilliseconds);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Append failed for {StreamId}", id.Value);
            throw;
        }
    }

    public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
        => _inner.ReadAsync(id, from, ct);

    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => _inner.SubscribeAsync(id, from, handler, ct);
}
```

### Monitoring Metrics

Register health checks for the stores:

```csharp
services.AddHealthChecks()
    .AddPostgreSqlEventStore(dataSource)               // ZeroAlloc.EventSourcing.Sql
    .AddSqlServerEventStore(sqlServerConnectionString); // ZeroAlloc.EventSourcing.SqlServer
```

The library has no counters for the number of events, streams or the database size; query the
database for them, for example `SELECT count(*) FROM event_store` and
`SELECT count(DISTINCT stream_id) FROM event_store`.

## Testing with Testcontainers

Use Testcontainers for integration tests:

### PostgreSQL with Testcontainers

```csharp
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.PostgreSql;

public class PostgreSqlEventStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        _adapter = new PostgreSqlEventStoreAdapter(_dataSource);
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task AppendAsync_SavesEvents()
    {
        // Arrange: the adapter stores serialized events; EventStore creates these for you
        var streamId = new StreamId("test-stream");
        var raw = new RawEvent(StreamPosition.Start, "OrderPlaced", Encoding.UTF8.GetBytes("{}"), EventMetadata.New("OrderPlaced"));

        // Act
        var appended = await _adapter.AppendAsync(streamId, new[] { raw }, StreamPosition.Start);

        // Assert
        Assert.True(appended.IsSuccess);

        var count = 0;
        await foreach (var _ in _adapter.ReadAsync(streamId, StreamPosition.Start))
        {
            count++;
        }

        Assert.Equal(1, count);
    }
}
```

### SQL Server with Testcontainers

```csharp
using System.Text;
using Testcontainers.MsSql;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

public class SqlServerEventStoreTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private SqlServerEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _adapter = new SqlServerEventStoreAdapter(_container.GetConnectionString());
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task AppendAsync_SavesEvents()
    {
        var streamId = new StreamId("test-stream");
        var raw = new RawEvent(StreamPosition.Start, "OrderPlaced", Encoding.UTF8.GetBytes("{}"), EventMetadata.New("OrderPlaced"));

        var appended = await _adapter.AppendAsync(streamId, new[] { raw }, StreamPosition.Start);

        Assert.True(appended.IsSuccess);

        var count = 0;
        await foreach (var _ in _adapter.ReadAsync(streamId, StreamPosition.Start))
        {
            count++;
        }

        Assert.Equal(1, count);
    }
}
```

## Switching Between Databases

### Factory Pattern

```csharp
public interface IEventStoreAdapterFactory
{
    IEventStoreAdapter CreateAdapter(DatabaseType dbType, string connectionString);
}

public class EventStoreAdapterFactory : IEventStoreAdapterFactory
{
    public IEventStoreAdapter CreateAdapter(DatabaseType dbType, string connectionString)
    {
        return dbType switch
        {
            DatabaseType.PostgreSql => new PostgreSqlEventStoreAdapter(NpgsqlDataSource.Create(connectionString)),
            DatabaseType.SqlServer => new SqlServerEventStoreAdapter(connectionString),
            _ => throw new ArgumentException($"Unknown database type: {dbType}")
        };
    }
}

public enum DatabaseType
{
    PostgreSql,
    SqlServer
}
```

### DI Configuration

```csharp
// dbType and connectionString come from your configuration, e.g.
// Enum.Parse<DatabaseType>(config["Database:Type"]) and config.GetConnectionString("EventStore")

// Register the adapter based on config. A singleton: the adapter holds the connection pool
services.AddSingleton<IEventStoreAdapterFactory, EventStoreAdapterFactory>();
services.AddSingleton<IEventStoreAdapter>(sp =>
    sp.GetRequiredService<IEventStoreAdapterFactory>().CreateAdapter(dbType, connectionString));

// Register event store
services.AddSingleton<IEventStore>(sp =>
    new EventStore(
        sp.GetRequiredService<IEventStoreAdapter>(),
        new JsonEventSerializer(),
        new OrderEventTypeRegistry()));

// Register repository
services.AddScoped<IAggregateRepository<Order, OrderId>>(sp =>
    new AggregateRepository<Order, OrderId>(
        sp.GetRequiredService<IEventStore>(),
        () => new Order(),
        id => new StreamId($"order-{id.Value}")));
```

## Performance Benchmarks

### Append Performance

| Operation | PostgreSQL | SQL Server |
|---|---|---|
| 1 event | ~1ms | ~1ms |
| 10 events | ~2ms | ~2ms |
| 100 events | ~10ms | ~10ms |
| 1,000 events | ~50ms | ~60ms |

### Read Performance

| Events | PostgreSQL | SQL Server |
|---|---|---|
| 10 | ~1ms | ~1ms |
| 100 | ~5ms | ~5ms |
| 1,000 | ~20ms | ~25ms |
| 10,000 | ~150ms | ~200ms |

### Snapshot Store Performance

| Operation | PostgreSQL | SQL Server |
|---|---|---|
| Read | ~0.5ms | ~0.5ms |
| Write | ~1ms | ~1ms |

**Lessons:**
- Both databases perform similarly for typical workloads
- PostgreSQL slightly faster for bulk operations
- SQL Server better integration with Windows infrastructure
- Snapshots dramatically reduce read latency

## Complete Setup Example

```csharp
// PostgreSQL
services
    .AddSingleton<IEventTypeRegistry, OrderEventTypeRegistry>()
    .AddSingleton<IEventSerializer, JsonEventSerializer>()  // or ZeroAlloc.Serialisation, see events.md
    .AddEventSourcing()
    .UsePostgreSqlEventStore("Host=localhost;Database=EventStore;Username=postgres;Password=password")
    .UsePostgreSqlSnapshotStore<OrderState>("Host=localhost;Database=EventStore;Username=postgres;Password=password")
    .UseAggregateRepository<Order, OrderId>(
        () => new Order(),
        id => new StreamId($"order-{id.Value}"));

// SQL Server: the same, with
//     .UseSqlServerEventStore(cs)
//     .UseSqlServerSnapshotStore<OrderState>(cs)

var sp = services.BuildServiceProvider();

// The registrations do not create tables: create them once at startup
await ((PostgreSqlEventStoreAdapter)sp.GetRequiredService<IEventStoreAdapter>()).EnsureSchemaAsync();
await ((PostgreSqlSnapshotStore<OrderState>)sp.GetRequiredService<ISnapshotStore<OrderState>>()).EnsureSchemaAsync();

var repository = sp.GetRequiredService<IAggregateRepository<Order, OrderId>>();

var orderId = new OrderId(Guid.NewGuid());
using var order = new Order();
order.Place("alice");
var saved = await repository.SaveAsync(order, orderId);
if (saved.IsFailure)
    throw new InvalidOperationException(saved.Error.ToString());  // e.g. [CONFLICT] on a concurrent write
```

> `AddEventSourcing()` returns `EventSourcingBuilder`. Chain `.Use*()` calls to register store adapters.
> Call `.Services` on the builder when you need to return to `IServiceCollection`.

## Summary

Effective SQL adapter usage requires:

1. **Choose database** — PostgreSQL for Linux, SQL Server for Windows
2. **Configure connection pooling** — 10-100 connections depending on load
3. **Create the schema** — Run `EnsureSchemaAsync()` at startup or deployment
4. **Choose serialization** — JSON for flexibility, binary for performance
5. **Monitor performance** — Track slow queries and database size
6. **Plan for growth** — Archival and partitioning for large databases
7. **Test integration** — Use Testcontainers for reliable tests
8. **Plan disaster recovery** — Regular backups and point-in-time restore

## Next Steps

- **[Performance Guide](../performance.md)** — Detailed benchmarking and tuning
- **[Deployment Guide](../DEPLOYMENT.md)** — Production deployment patterns
- **[Snapshots Usage](./snapshots-usage.md)** — Snapshot stores and loading strategies
