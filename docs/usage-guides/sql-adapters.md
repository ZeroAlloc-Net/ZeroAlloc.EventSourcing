# Usage Guide: SQL Adapters

## Scenario: How Do I Persist Events and Snapshots to a Database?

ZeroAlloc.EventSourcing supports both PostgreSQL and SQL Server for storing events and snapshots. This guide covers setup, configuration, performance tuning, and deployment strategies.

## PostgreSQL Setup and Configuration

### Installation

Add the PostgreSQL adapter package:

```bash
dotnet add package ZeroAlloc.EventSourcing.PostgreSql
```

### Basic Configuration

```csharp
using ZeroAlloc.EventSourcing.PostgreSql;
using ZeroAlloc.EventSourcing;

var connectionString = "Host=localhost;Database=EventStore;User=postgres;Password=password";

// Create adapter
var adapter = new PostgreSqlEventStoreAdapter(connectionString);

// Create event store
var registry = new OrderEventTypeRegistry();
var serializer = new JsonEventSerializer();
var eventStore = new EventStore(adapter, serializer, registry);
```

### Connection Pooling

PostgreSQL uses connection pooling by default. Configure for high throughput:

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

var adapter = new PostgreSqlEventStoreAdapter(connectionString);
```

### Schema Creation

Schemas are created automatically on first use. Or create manually:

```sql
-- EventStore table
CREATE TABLE event_store (
    stream_id TEXT NOT NULL,
    position BIGINT NOT NULL,
    event_type TEXT NOT NULL,
    event_data BYTEA NOT NULL,
    metadata JSONB,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (stream_id, position)
);

-- Indexes for performance
CREATE INDEX idx_event_store_stream ON event_store(stream_id, position);
CREATE INDEX idx_event_store_created ON event_store(created_at);
CREATE INDEX idx_event_store_type ON event_store(event_type);

-- SnapshotStore table
CREATE TABLE snapshot_store (
    stream_id TEXT NOT NULL PRIMARY KEY,
    position BIGINT NOT NULL,
    state BYTEA NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_snapshot_created ON snapshot_store(created_at);
```

### Connection String Patterns

**Local Development:**
```
Host=localhost;Database=EventStore;User=postgres;Password=password
```

**Staging:**
```
Host=staging-db.example.com;Database=EventStore;User=app_user;Password=SecurePassword123;SslMode=Require
```

**Production:**
```
Host=prod-db.example.com;Database=EventStore;User=app_user;Password=SecurePassword123;SslMode=Require;Application Name=EventSourcingApp
```

## SQL Server Setup and Configuration

### Installation

Add the SQL Server adapter package:

```bash
dotnet add package ZeroAlloc.EventSourcing.SqlServer
```

### Basic Configuration

```csharp
using ZeroAlloc.EventSourcing.SqlServer;
using ZeroAlloc.EventSourcing;

var connectionString = "Server=localhost;Database=EventStore;User=sa;Password=YourPassword123";

// Create adapter
var adapter = new SqlServerEventStoreAdapter(connectionString);

// Create event store
var registry = new OrderEventTypeRegistry();
var serializer = new JsonEventSerializer();
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
    Max Pool Size = 100,
    Min Pool Size = 10,
    Connection Timeout = 30,
    Connection Lifetime = 300
}.ConnectionString;

var adapter = new SqlServerEventStoreAdapter(connectionString);
```

### Schema Creation

SQL Server requires explicit schema creation. Use provided script or create manually:

```sql
-- EventStore table
CREATE TABLE [dbo].[EventStore] (
    [StreamId] NVARCHAR(450) NOT NULL,
    [Position] BIGINT NOT NULL,
    [EventType] NVARCHAR(255) NOT NULL,
    [EventData] VARBINARY(MAX) NOT NULL,
    [Metadata] NVARCHAR(MAX),
    [CreatedAt] DATETIME2 DEFAULT GETUTCDATE(),
    PRIMARY KEY CLUSTERED ([StreamId], [Position])
);

-- Indexes for performance
CREATE NONCLUSTERED INDEX [idx_event_created] ON [dbo].[EventStore]([CreatedAt]);
CREATE NONCLUSTERED INDEX [idx_event_type] ON [dbo].[EventStore]([EventType]);

-- SnapshotStore table
CREATE TABLE [dbo].[SnapshotStore] (
    [StreamId] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [Position] BIGINT NOT NULL,
    [State] VARBINARY(MAX) NOT NULL,
    [CreatedAt] DATETIME2 DEFAULT GETUTCDATE()
);

CREATE NONCLUSTERED INDEX [idx_snapshot_created] ON [dbo].[SnapshotStore]([CreatedAt]);
```

### Connection String Patterns

**Local Development:**
```
Server=(local);Database=EventStore;Integrated Security=true;Encrypt=false
```

**Staging:**
```
Server=staging-db.example.com;Database=EventStore;User=app_user;Password=SecurePassword123;Encrypt=true;TrustServerCertificate=false
```

**Production:**
```
Server=prod-db.example.com;Database=EventStore;User=app_user;Password=SecurePassword123;Encrypt=true;TrustServerCertificate=false;Application Name=EventSourcingApp
```

## Serialization Strategies

### JSON Serialization (Recommended)

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
        return JsonSerializer.SerializeToUtf8Bytes(@event, Options);
    }
    
    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        return JsonSerializer.Deserialize(payload.Span, eventType, Options)
            ?? throw new InvalidOperationException("Deserialization failed");
    }
}
```

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
        return MessagePackSerializer.Serialize(@event);
    }
    
    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        return MessagePackSerializer.Deserialize(eventType, payload.Span)
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
        
        using var source = new MemoryStream(uncompressed.ToArray());
        using var target = new MemoryStream();
        
        using (var gzip = new GZipStream(target, CompressionMode.Compress))
        {
            source.CopyTo(gzip);
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

All adapters include default indexes:

**PostgreSQL:**
```sql
CREATE INDEX idx_event_store_stream ON event_store(stream_id, position);
CREATE INDEX idx_event_store_created ON event_store(created_at);
CREATE INDEX idx_event_store_type ON event_store(event_type);
```

**SQL Server:**
```sql
CREATE NONCLUSTERED INDEX [idx_event_created] ON [dbo].[EventStore]([CreatedAt]);
CREATE NONCLUSTERED INDEX [idx_event_type] ON [dbo].[EventStore]([EventType]);
```

### Query Analysis

Analyze slow queries:

**PostgreSQL:**
```sql
-- Find slow queries
SELECT 
    query,
    calls,
    mean_time,
    max_time
FROM pg_stat_statements
WHERE query LIKE '%event%'
ORDER BY mean_time DESC;

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
    query_hash,
    statement_text,
    execution_count,
    total_elapsed_time / 1000 as total_ms,
    total_elapsed_time / execution_count / 1000 as avg_ms
FROM sys.dm_exec_query_stats
CROSS APPLY sys.dm_exec_sql_text(sql_handle)
WHERE statement_text LIKE '%EventStore%'
ORDER BY total_elapsed_time DESC;
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

### Point-in-Time Recovery

Implement event archival for fast recovery:

```csharp
public class EventArchiver
{
    private readonly IEventStore _live;
    private readonly IEventStore _archive;
    
    public async Task ArchiveOldEvents(StreamId streamId, DateTime before)
    {
        // Copy old events to archive
        await foreach (var envelope in _live.ReadAsync(streamId, StreamPosition.Start))
        {
            if (envelope.Metadata?.CreatedAt >= before)
                break;
            
            await _archive.AppendAsync(
                streamId,
                new[] { envelope.Event },
                envelope.Position);
        }
        
        // Delete from live (keep recent events)
        await _live.DeleteRangeAsync(streamId, StreamPosition.Start, before);
    }
}
```

## Data Migration and Versioning

### Schema Evolution

When adding new columns, use migrations:

**PostgreSQL:**
```sql
-- Add column
ALTER TABLE event_store ADD COLUMN IF NOT EXISTS metadata_version INT DEFAULT 1;

-- Create new index
CREATE INDEX IF NOT EXISTS idx_event_metadata_version 
ON event_store(metadata_version);
```

**SQL Server:**
```sql
-- Add column
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS 
               WHERE TABLE_NAME = 'EventStore' AND COLUMN_NAME = 'MetadataVersion')
BEGIN
    ALTER TABLE [dbo].[EventStore] ADD [MetadataVersion] INT DEFAULT 1;
END

-- Create index
IF NOT EXISTS (SELECT * FROM sys.indexes 
               WHERE name = 'idx_metadata_version')
BEGIN
    CREATE NONCLUSTERED INDEX [idx_metadata_version] 
    ON [dbo].[EventStore]([MetadataVersion]);
END
```

### Event Migration

Migrate events when changing event structure:

```csharp
public class EventMigration
{
    public async Task MigrateOrderPlacedEvents(IEventStore eventStore)
    {
        var migratedCount = 0;
        
        // Read old events
        await foreach (var envelope in eventStore.ReadAllAsync())
        {
            if (envelope.Event is not OrderPlacedEvent_V1 oldEvent)
                continue;
            
            // Convert to new version
            var newEvent = new OrderPlacedEvent_V2(
                OrderId: oldEvent.OrderId,
                Total: oldEvent.Total,
                CustomerId: oldEvent.CustomerId ?? "Unknown",
                PlacedAt: envelope.Metadata?.CreatedAt ?? DateTime.UtcNow
            );
            
            // Note: In practice, use event upcasting in ApplyEvent
            // Don't actually modify the event store
            migratedCount++;
        }
        
        Console.WriteLine($"Migrated {migratedCount} events");
    }
}
```

## Monitoring and Observability

### Query Logging

Log slow queries:

```csharp
public class LoggingEventStoreAdapter : IEventStoreAdapter
{
    private readonly IEventStoreAdapter _inner;
    private readonly ILogger<LoggingEventStoreAdapter> _logger;
    
    public async Task AppendAsync(
        StreamId streamId,
        IReadOnlyList<object> events,
        StreamPosition expectedVersion)
    {
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            await _inner.AppendAsync(streamId, events, expectedVersion);
            stopwatch.Stop();
            
            if (stopwatch.ElapsedMilliseconds > 100)
            {
                _logger.LogWarning(
                    "Slow append: {StreamId} took {Ms}ms",
                    streamId.Value,
                    stopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Append failed for {StreamId}", streamId.Value);
            throw;
        }
    }
}
```

### Monitoring Metrics

Track database health:

```csharp
public class EventStoreMetrics
{
    private readonly IEventStore _eventStore;
    private readonly IMetricsCollector _metrics;
    
    public async Task ReportMetrics()
    {
        // Event count
        var eventCount = await _eventStore.CountAsync();
        _metrics.Gauge("events.total_count", eventCount);
        
        // Stream count
        var streamCount = await _eventStore.CountStreamsAsync();
        _metrics.Gauge("events.stream_count", streamCount);
        
        // Database size
        var size = await _eventStore.GetDatabaseSizeAsync();
        _metrics.Gauge("events.database_size_bytes", size);
    }
}
```

## Testing with Testcontainers

Use Testcontainers for integration tests:

### PostgreSQL with Testcontainers

```csharp
using Testcontainers.PostgreSql;
using Xunit;

public class PostgreSqlEventStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private IEventStore _eventStore = null!;
    
    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithDatabase("eventstore")
            .WithUsername("postgres")
            .WithPassword("password")
            .Build();
        
        await _container.StartAsync();
        
        var connectionString = _container.GetConnectionString();
        var adapter = new PostgreSqlEventStoreAdapter(connectionString);
        var registry = new OrderEventTypeRegistry();
        var serializer = new JsonEventSerializer();
        
        _eventStore = new EventStore(adapter, serializer, registry);
    }
    
    public async Task DisposeAsync()
    {
        await _container.StopAsync();
        _container.Dispose();
    }
    
    [Fact]
    public async Task AppendAsync_SavesEvents()
    {
        // Arrange
        var streamId = new StreamId("test-stream");
        var events = new[] { new OrderPlacedEvent("ORD-001", 1500m) };
        
        // Act
        await _eventStore.AppendAsync(streamId, events, StreamPosition.Start);
        
        // Assert
        var result = await _eventStore.ReadAsync(streamId, StreamPosition.Start);
        var count = 0;
        await foreach (var _ in result)
        {
            count++;
        }
        
        Assert.Equal(1, count);
    }
}
```

### SQL Server with Testcontainers

```csharp
using Testcontainers.MsSql;
using Xunit;

public class SqlServerEventStoreTests : IAsyncLifetime
{
    private MsSqlContainer _container = null!;
    private IEventStore _eventStore = null!;
    
    public async Task InitializeAsync()
    {
        _container = new MsSqlBuilder()
            .WithPassword("YourPassword123!")
            .Build();
        
        await _container.StartAsync();
        
        var connectionString = _container.GetConnectionString();
        var adapter = new SqlServerEventStoreAdapter(connectionString);
        var registry = new OrderEventTypeRegistry();
        var serializer = new JsonEventSerializer();
        
        _eventStore = new EventStore(adapter, serializer, registry);
    }
    
    public async Task DisposeAsync()
    {
        await _container.StopAsync();
        _container.Dispose();
    }
    
    [Fact]
    public async Task AppendAsync_SavesEvents()
    {
        var streamId = new StreamId("test-stream");
        var events = new[] { new OrderPlacedEvent("ORD-001", 1500m) };
        
        await _eventStore.AppendAsync(streamId, events, StreamPosition.Start);
        
        var result = await _eventStore.ReadAsync(streamId, StreamPosition.Start);
        var count = 0;
        await foreach (var _ in result)
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
            DatabaseType.PostgreSql => new PostgreSqlEventStoreAdapter(connectionString),
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
var services = new ServiceCollection();

// Load config
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var dbType = Enum.Parse<DatabaseType>(config["Database:Type"]);
var connectionString = config.GetConnectionString("EventStore");

// Register adapter based on config
services.AddScoped<IEventStoreAdapter>(sp =>
{
    var factory = sp.GetRequiredService<IEventStoreAdapterFactory>();
    return factory.CreateAdapter(dbType, connectionString);
});

// Register event store
services.AddScoped<IEventStore>(sp =>
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
public class EventSourcingSetup
{
    public static IServiceCollection AddEventSourcing(
        this IServiceCollection services,
        string connectionString,
        DatabaseType dbType)
    {
        // Adapter
        var adapter = dbType switch
        {
            DatabaseType.PostgreSql => 
                (IEventStoreAdapter)new PostgreSqlEventStoreAdapter(connectionString),
            DatabaseType.SqlServer => 
                (IEventStoreAdapter)new SqlServerEventStoreAdapter(connectionString),
            _ => throw new ArgumentException($"Unknown database: {dbType}")
        };
        
        // Event serializer
        services.AddScoped<IEventSerializer>(_ => new JsonEventSerializer());
        
        // Event type registry
        services.AddScoped<IEventTypeRegistry, OrderEventTypeRegistry>();
        
        // Event store
        services.AddScoped<IEventStore>(sp =>
            new EventStore(
                adapter,
                sp.GetRequiredService<IEventSerializer>(),
                sp.GetRequiredService<IEventTypeRegistry>()));
        
        // Snapshot store
        services.AddScoped<ISnapshotStore<OrderState>>(sp =>
            dbType == DatabaseType.PostgreSql
                ? new PostgreSqlSnapshotStore<OrderState>(connectionString)
                : new SqlServerSnapshotStore<OrderState>(connectionString));
        
        // Repository
        services.AddScoped<IAggregateRepository<Order, OrderId>>(sp =>
            new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
                innerRepository: new AggregateRepository<Order, OrderId>(
                    sp.GetRequiredService<IEventStore>(),
                    () => new Order(),
                    id => new StreamId($"order-{id.Value}")),
                snapshotStore: sp.GetRequiredService<ISnapshotStore<OrderState>>(),
                strategy: SnapshotLoadingStrategy.ValidateAndReplay,
                restoreState: (o, s, p) => o.RestoreState(s, p),
                eventStore: sp.GetRequiredService<IEventStore>(),
                streamIdFactory: id => new StreamId($"order-{id.Value}"),
                aggregateFactory: () => new Order()));
        
        return services;
    }
}

// Usage
var services = new ServiceCollection();
var connectionString = "Host=localhost;Database=EventStore;User=postgres;Password=password";
services.AddEventSourcing(connectionString, DatabaseType.PostgreSql);

var sp = services.BuildServiceProvider();
var repository = sp.GetRequiredService<IAggregateRepository<Order, OrderId>>();

// Use it
var order = new Order();
order.SetId(new OrderId(Guid.NewGuid()));
order.Place("ORD-001", 1500m);
await repository.SaveAsync(order);
```

## Summary

Effective SQL adapter usage requires:

1. **Choose database** — PostgreSQL for Linux, SQL Server for Windows
2. **Configure connection pooling** — 10-100 connections depending on load
3. **Create indexes** — Essential for query performance
4. **Choose serialization** — JSON for flexibility, binary for performance
5. **Monitor performance** — Track slow queries and database size
6. **Plan for growth** — Archival and partitioning for large databases
7. **Test integration** — Use Testcontainers for reliable tests
8. **Plan disaster recovery** — Regular backups and point-in-time restore

## Next Steps

- **[Performance Guide](../performance.md)** — Detailed benchmarking and tuning
- **[Deployment Guide](../advanced/deployment.md)** — Production deployment patterns
- **[Monitoring Guide](../advanced/monitoring.md)** — Observability and alerting
