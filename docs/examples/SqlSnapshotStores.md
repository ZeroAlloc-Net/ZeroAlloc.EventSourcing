# SQL Snapshot Stores

## Overview

The `ZeroAlloc.EventSourcing.Sql` package provides SQL-based implementations of the `ISnapshotStore<TState>` interface for persisting aggregate snapshots. Two implementations are available:

- **PostgreSqlSnapshotStore**: Uses PostgreSQL with BYTEA columns for efficient binary storage
- **SqlServerSnapshotStore**: Uses SQL Server with VARBINARY(MAX) columns

Both implementations use atomic upsert semantics (last-write-wins) to ensure concurrent writes don't corrupt snapshot data. Snapshots are stored with position information, allowing you to resume event replay from the snapshot point.

## PostgreSQL Basic Usage

```csharp
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

// Create a data source
var dataSource = NpgsqlDataSource.Create(
    "Host=localhost;Database=mydb;Username=postgres;Password=password");

// Create the snapshot store
var snapshotStore = new PostgreSqlSnapshotStore<OrderState>(dataSource);

// Initialize schema (idempotent - safe to call multiple times)
await snapshotStore.EnsureSchemaAsync();

// Write a snapshot
var streamId = new StreamId("order-123");
var position = new StreamPosition(50);  // Snapshot at event 50
var state = new OrderState { Amount = 1000m, Status = "Confirmed" };
await snapshotStore.WriteAsync(streamId, position, state);

// Read the snapshot back
var result = await snapshotStore.ReadAsync(streamId);
if (result.HasValue)
{
    var (snapshotPosition, snapshotState) = result.Value;
    Console.WriteLine($"Snapshot at position {snapshotPosition}: {snapshotState.Status}");
}
```

## PostgreSQL Database-Specific Details

### Connection Pool Management

PostgreSQL uses connection pooling via `NpgsqlDataSource`. Configure pooling parameters when creating the data source:

```csharp
var dataSourceBuilder = new NpgsqlDataSourceBuilder("Host=localhost;Database=mydb;Username=postgres;Password=password");
dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 20;
dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 5;

var dataSource = dataSourceBuilder.Build();
```

### Handling Serialization

If your aggregate state is not natively serializable, provide a custom serializer:

```csharp
using System.Text.Json;

public class JsonEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    public object Deserialize(ReadOnlyMemory<byte> data, Type targetType)
    {
        var json = System.Text.Encoding.UTF8.GetString(data.Span);
        return JsonSerializer.Deserialize(json, targetType)
            ?? throw new InvalidOperationException("Deserialization returned null");
    }
}

var snapshotStore = new PostgreSqlSnapshotStore<OrderState>(
    dataSource,
    serializer: new JsonEventSerializer());
```

### PostgreSQL Upsert Semantics

PostgreSQL uses `ON CONFLICT ... DO UPDATE SET` for atomic upserts:

```sql
INSERT INTO snapshots (stream_id, position, state_type, payload, created_at)
VALUES (@stream_id, @position, @state_type, @payload, @created_at)
ON CONFLICT (stream_id) DO UPDATE SET
    position = EXCLUDED.position,
    state_type = EXCLUDED.state_type,
    payload = EXCLUDED.payload,
    created_at = EXCLUDED.created_at
```

This ensures last-write-wins semantics even with concurrent writes.

## SQL Server Basic Usage

```csharp
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

// Create the snapshot store with a connection string
const string connectionString = "Server=localhost;Database=mydb;User Id=sa;Password=YourPassword123!";
var snapshotStore = new SqlServerSnapshotStore<OrderState>(connectionString);

// Initialize schema (idempotent - safe to call multiple times)
await snapshotStore.EnsureSchemaAsync();

// Write a snapshot
var streamId = new StreamId("order-456");
var position = new StreamPosition(75);  // Snapshot at event 75
var state = new OrderState { Amount = 2000m, Status = "Shipped" };
await snapshotStore.WriteAsync(streamId, position, state);

// Read the snapshot back
var result = await snapshotStore.ReadAsync(streamId);
if (result.HasValue)
{
    var (snapshotPosition, snapshotState) = result.Value;
    Console.WriteLine($"Snapshot at position {snapshotPosition}: {snapshotState.Status}");
}
```

## SQL Server Database-Specific Details

### Connection Pooling

SQL Server connection pooling is handled automatically by `SqlConnection`. Configure pooling in the connection string:

```csharp
const string connectionString = "Server=localhost;Database=mydb;" +
    "User Id=sa;Password=YourPassword123!;" +
    "Max Pool Size=20;" +
    "Min Pool Size=5;";

var snapshotStore = new SqlServerSnapshotStore<OrderState>(connectionString);
```

### Handling Serialization

Like PostgreSQL, SQL Server also requires a serializer for non-primitive state types:

```csharp
var snapshotStore = new SqlServerSnapshotStore<OrderState>(
    connectionString,
    serializer: new JsonEventSerializer());
```

### SQL Server MERGE Semantics

SQL Server uses `MERGE` for atomic upserts:

```sql
MERGE INTO snapshots AS target
USING (SELECT @stream_id AS stream_id) AS source
ON target.stream_id = source.stream_id
WHEN MATCHED THEN UPDATE SET
    position = @position,
    state_type = @state_type,
    payload = @payload,
    created_at = @created_at
WHEN NOT MATCHED THEN INSERT (stream_id, position, state_type, payload, created_at)
    VALUES (@stream_id, @position, @state_type, @payload, @created_at);
```

This provides the same last-write-wins semantics as PostgreSQL.

## Runtime Database Selection with Factory

To select between databases at runtime, use a factory pattern:

```csharp
public interface ISnapshotStoreFactory
{
    ISnapshotStore<TState> CreateSnapshotStore<TState>(string provider)
        where TState : struct;
}

public class SnapshotStoreFactory : ISnapshotStoreFactory
{
    private readonly NpgsqlDataSource _pgDataSource;
    private readonly string _sqlServerConnectionString;
    private readonly IEventSerializer _serializer;

    public SnapshotStoreFactory(
        NpgsqlDataSource pgDataSource,
        string sqlServerConnectionString,
        IEventSerializer serializer)
    {
        _pgDataSource = pgDataSource;
        _sqlServerConnectionString = sqlServerConnectionString;
        _serializer = serializer;
    }

    public ISnapshotStore<TState> CreateSnapshotStore<TState>(string provider)
        where TState : struct
    {
        return provider switch
        {
            "PostgreSQL" => new PostgreSqlSnapshotStore<TState>(_pgDataSource, _serializer),
            "SqlServer" => new SqlServerSnapshotStore<TState>(_sqlServerConnectionString, _serializer),
            _ => throw new ArgumentException($"Unknown provider: {provider}", nameof(provider))
        };
    }
}
```

## DI Configuration Examples

### Minimal with PostgreSQL

```csharp
services.AddSingleton(_ =>
    NpgsqlDataSource.Create("Host=localhost;Database=mydb;Username=postgres;Password=password"));

services.AddSingleton<IEventSerializer, JsonEventSerializer>();

services.AddScoped(sp =>
    new PostgreSqlSnapshotStore<OrderState>(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<IEventSerializer>()));

services.AddScoped<ISnapshotStore<OrderState>>(sp =>
    sp.GetRequiredService<PostgreSqlSnapshotStore<OrderState>>());
```

### Minimal with SQL Server

```csharp
const string connectionString = "Server=localhost;Database=mydb;User Id=sa;Password=YourPassword123!";

services.AddSingleton<IEventSerializer, JsonEventSerializer>();

services.AddScoped(sp =>
    new SqlServerSnapshotStore<OrderState>(
        connectionString,
        sp.GetRequiredService<IEventSerializer>()));

services.AddScoped<ISnapshotStore<OrderState>>(sp =>
    sp.GetRequiredService<SqlServerSnapshotStore<OrderState>>());
```

### Multiple Aggregate Types

```csharp
services.AddSingleton(_ =>
    NpgsqlDataSource.Create("Host=localhost;Database=mydb;Username=postgres;Password=password"));

services.AddSingleton<IEventSerializer, JsonEventSerializer>();

// Register snapshot stores for each aggregate
services.AddScoped<ISnapshotStore<OrderState>>(sp =>
    new PostgreSqlSnapshotStore<OrderState>(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<IEventSerializer>()));

services.AddScoped<ISnapshotStore<InvoiceState>>(sp =>
    new PostgreSqlSnapshotStore<InvoiceState>(
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<IEventSerializer>()));
```

### With SnapshotCachingRepositoryDecorator

```csharp
services.AddScoped<IAggregateRepository<Order, OrderId>>(sp =>
    new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
        innerRepository: new AggregateRepository<Order, OrderId>(
            sp.GetRequiredService<IEventStore>(),
            () => new Order(),
            id => new StreamId($"order-{id.Value}")),
        snapshotStore: sp.GetRequiredService<ISnapshotStore<OrderState>>(),
        strategy: SnapshotLoadingStrategy.ValidateAndReplay,
        restoreState: (order, state, pos) => order.RestoreState(state, pos),
        eventStore: sp.GetRequiredService<IEventStore>(),
        streamIdFactory: id => new StreamId($"order-{id.Value}"),
        aggregateFactory: () => new Order()));
```

## Schema Table

Both PostgreSQL and SQL Server use the same table schema:

| Column | Type | Key | Notes |
|---|---|---|---|
| `stream_id` | VARCHAR(256) | PRIMARY KEY | Identifier for the event stream |
| `position` | BIGINT | | Version/position in stream where snapshot was taken |
| `state_type` | VARCHAR(256) | | Fully-qualified type name of the state (validation) |
| `payload` | BYTEA (PG) / VARBINARY(MAX) (SS) | | Serialized aggregate state |
| `created_at` | TIMESTAMP | | When the snapshot was created (UTC) |

### Indexes

Both implementations create indexes to optimize lookups:

- `stream_id` is a PRIMARY KEY for O(1) reads
- No additional indexes are created (snapshots are accessed by stream_id only)

## Integration with Phase 5.1

Snapshot stores integrate seamlessly with the `SnapshotCachingRepositoryDecorator` from Phase 5.1:

1. **Load optimization**: When loading an aggregate, the decorator first checks for a snapshot
2. **Replay reduction**: If a snapshot exists at position N, only events after position N are replayed
3. **Validation**: The `ValidateAndReplay` strategy ensures the snapshot position exists in the event store
4. **Automatic snapshot creation**: Application code can decide when to write snapshots (e.g., every 100 events)

```csharp
// When you decide an aggregate needs a snapshot
var order = await repository.LoadAsync(new OrderId(123));
if (order.EventCount % 100 == 0)
{
    // Write snapshot
    await snapshotStore.WriteAsync(
        new StreamId($"order-{order.Id.Value}"),
        new StreamPosition(order.CurrentVersion),
        order.GetState());
}
```

## Testcontainers for Local Development

For local development and integration testing, use Testcontainers to spin up temporary database instances:

### PostgreSQL with Testcontainers

```csharp
using Testcontainers.PostgreSql;
using Xunit;

public sealed class PostgreSqlSnapshotStoreIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().Build();
    private NpgsqlDataSource _dataSource = null!;
    private ISnapshotStore<OrderState> _snapshotStore = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        _snapshotStore = new PostgreSqlSnapshotStore<OrderState>(_dataSource, new JsonEventSerializer());
        await _snapshotStore.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.StopAsync();
    }

    [Fact]
    public async Task Snapshot_RoundTrip_Succeeds()
    {
        var state = new OrderState { Amount = 500m, Status = "Confirmed" };
        await _snapshotStore.WriteAsync(new StreamId("test-1"), new StreamPosition(10), state);

        var result = await _snapshotStore.ReadAsync(new StreamId("test-1"));

        Assert.NotNull(result);
        Assert.Equal(500m, result.Value.State.Amount);
    }
}
```

### SQL Server with Testcontainers

```csharp
using Testcontainers.MsSql;
using Xunit;

public sealed class SqlServerSnapshotStoreIntegrationTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();
    private ISnapshotStore<OrderState> _snapshotStore = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _snapshotStore = new SqlServerSnapshotStore<OrderState>(
            _container.GetConnectionString(),
            new JsonEventSerializer());
        await _snapshotStore.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
    }

    [Fact]
    public async Task Snapshot_RoundTrip_Succeeds()
    {
        var state = new OrderState { Amount = 750m, Status = "Shipped" };
        await _snapshotStore.WriteAsync(new StreamId("test-2"), new StreamPosition(20), state);

        var result = await _snapshotStore.ReadAsync(new StreamId("test-2"));

        Assert.NotNull(result);
        Assert.Equal(750m, result.Value.State.Amount);
    }
}
```

### Running Integration Tests Selectively

Use xUnit collection fixtures and filtering to run integration tests selectively:

```bash
# Run only unit tests (exclude integration tests)
dotnet test --filter "FullyQualifiedName!~Integration"

# Run only integration tests
dotnet test --filter "FullyQualifiedName~Integration"

# Run specific collection
dotnet test --filter "Category=PostgreSQL"
```
