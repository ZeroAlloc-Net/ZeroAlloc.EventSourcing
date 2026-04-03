# Phase 6: SQL Snapshot Stores — Design Document

**Date:** 2026-04-04  
**Status:** Approved

---

## Overview

Implement production-grade snapshot persistence for PostgreSQL and SQL Server. Each database gets an optimized implementation while both conform to the `ISnapshotStore<TState>` interface.

**Goal:** Enable durable snapshot storage, reducing event replay for long-lived aggregates in production.

---

## Architecture

### Core Pattern

Both implementations follow the Phase 3 (SQL Adapters) pattern:

```
ISnapshotStore<TState>
  ├─ PostgreSqlSnapshotStore<TState>
  └─ SqlServerSnapshotStore<TState>
```

No extra abstraction layers. Each DB implements the interface directly with optimized SQL.

### ISnapshotStoreProvider (Factory)

```csharp
public interface ISnapshotStoreProvider<TState> where TState : struct
{
    ValueTask<ISnapshotStore<TState>> CreateAsync(string connectionString, CancellationToken ct = default);
}
```

Enables runtime selection of database without compile-time coupling.

---

## Schema

### Shared Across Both Databases

```sql
CREATE TABLE snapshots (
    stream_id       VARCHAR(255)      NOT NULL,
    position        BIGINT            NOT NULL,
    state_type      VARCHAR(500)      NOT NULL,
    payload         VARBINARY/BYTEA   NOT NULL,
    created_at      DATETIMEOFFSET/TIMESTAMPTZ NOT NULL,
    
    PRIMARY KEY (stream_id)
);
```

**Columns:**
- `stream_id`: Stream identifier (matches event_store)
- `position`: Event position when snapshot was taken
- `state_type`: CLR type name of TState (for validation)
- `payload`: Serialized aggregate state (binary)
- `created_at`: Timestamp for auditing/cleanup

**Rationale:**
- Single PK on `stream_id` (last-write-wins semantics)
- No versioning column (snapshots are immutable after write)
- `state_type` allows schema migration detection

---

## Components

### 1. PostgreSqlSnapshotStore<TState>

Optimized for PostgreSQL:

```csharp
public sealed class PostgreSqlSnapshotStore<TState> : ISnapshotStore<TState>
    where TState : struct
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSnapshotStore(NpgsqlDataSource dataSource);

    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        // CREATE TABLE IF NOT EXISTS snapshots (...)
    }

    public async ValueTask<(StreamPosition, TState)?> ReadAsync(
        StreamId streamId, 
        CancellationToken ct = default)
    {
        // SELECT position, state_type, payload FROM snapshots WHERE stream_id = @streamId
        // Deserialize payload using IEventSerializer<TState>
    }

    public async ValueTask WriteAsync(
        StreamId streamId, 
        StreamPosition position, 
        TState state, 
        CancellationToken ct = default)
    {
        // INSERT INTO snapshots (...) VALUES (...)
        // ON CONFLICT (stream_id) DO UPDATE SET position = ..., payload = ...
    }
}
```

**PostgreSQL-Specific:**
- Use `BYTEA` for binary payload
- Use `TIMESTAMPTZ` for audit timestamp
- Use `ON CONFLICT ... DO UPDATE` for upsert (atomic, no race)
- UUID advisory locks optional (coordination if needed)

### 2. SqlServerSnapshotStore<TState>

Optimized for SQL Server:

```csharp
public sealed class SqlServerSnapshotStore<TState> : ISnapshotStore<TState>
    where TState : struct
{
    private readonly string _connectionString;

    public SqlServerSnapshotStore(string connectionString);

    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        // CREATE TABLE IF NOT EXISTS snapshots (...)
    }

    public async ValueTask<(StreamPosition, TState)?> ReadAsync(
        StreamId streamId, 
        CancellationToken ct = default)
    {
        // SELECT position, state_type, payload FROM snapshots WHERE stream_id = @streamId
        // Deserialize payload
    }

    public async ValueTask WriteAsync(
        StreamId streamId, 
        StreamPosition position, 
        TState state, 
        CancellationToken ct = default)
    {
        // MERGE INTO snapshots ... WHEN MATCHED THEN UPDATE ... WHEN NOT MATCHED THEN INSERT ...
    }
}
```

**SQL Server-Specific:**
- Use `VARBINARY(MAX)` for binary payload
- Use `DATETIMEOFFSET` for audit timestamp
- Use `MERGE` statement for atomic upsert
- Handle NULL state_type gracefully

### 3. SnapshotDeserializer (Shared)

```csharp
public sealed class SnapshotDeserializer<TState> where TState : struct
{
    private readonly IEventSerializer _serializer;
    private readonly Type _expectedType;

    public TState Deserialize(byte[] payload, string stateType)
    {
        // Validate stateType matches expected CLR type
        if (stateType != typeof(TState).FullName)
            throw new InvalidOperationException($"Expected {_expectedType.FullName}, got {stateType}");
        
        // Deserialize using the configured serializer
        return (TState)_serializer.Deserialize(payload, typeof(TState));
    }
}
```

---

## Data Flow

### Read Path

```
ReadAsync(streamId, ct)
  │
  ├─→ Query snapshots table WHERE stream_id = @streamId
  │
  ├─→ Snapshot found?
  │    │
  │    ├─ YES → Deserialize payload
  │    │        Validate state_type
  │    │        Return (Position, State)
  │    │
  │    └─ NO → Return null
  │
  └─→ Handle cancellation / connection errors
```

### Write Path

```
WriteAsync(streamId, position, state, ct)
  │
  ├─→ Serialize state using IEventSerializer
  │
  ├─→ Get state_type name: typeof(TState).FullName
  │
  ├─→ Upsert into snapshots:
  │    ├─ PostgreSQL: ON CONFLICT ... DO UPDATE
  │    └─ SQL Server: MERGE ... WHEN MATCHED
  │
  └─→ Handle serialization errors / connection failures
```

---

## Error Handling

**Schema Not Created:**
- Both: `EnsureSchemaAsync()` creates table if not exists
- Idempotent: safe to call multiple times

**Serialization Failure:**
- Log error, propagate as `SerializationException`
- Caller (SnapshotCachingRepositoryDecorator) falls back to full replay

**Deserialization Failure:**
- Type mismatch in `state_type` column
- Log warning, return null (treat as missing snapshot)
- Caller replays from start

**Connection Failures:**
- Timeout / network error
- Log error, propagate `InvalidOperationException`
- Caller handles via fallback strategy

---

## Configuration & DI

### Dependency Injection Pattern

```csharp
services
    .AddScoped<ISnapshotStore<OrderState>>(sp => 
        new PostgreSqlSnapshotStore<OrderState>(
            sp.GetRequiredService<NpgsqlDataSource>()))
    .AddScoped<ISnapshotStore<InvoiceState>>(sp =>
        new SqlServerSnapshotStore<InvoiceState>(connectionString));
```

### Or using ISnapshotStoreProvider

```csharp
var provider = new SnapshotStoreProvider(database: "PostgreSQL", connectionString);
var store = await provider.CreateAsync<OrderState>();
```

---

## Testing Strategy

**Unit Tests:**
- Mock serializer, test read/write logic
- Null snapshot handling
- Type validation

**Integration Tests (via Testcontainers):**
- PostgreSQL: Real database, test upsert with concurrency
- SQL Server: Real database, test MERGE statement
- Both: Round-trip (write snapshot, read it back, verify state)
- Both: EnsureSchemaAsync idempotency

**Contract Tests:**
- Both stores inherit from `SnapshotStoreContractTests` (Phase 2 pattern)
- Ensures consistent behavior across databases

---

## Success Criteria

- ✅ PostgreSQL and SQL Server implementations both work
- ✅ Both conform to `ISnapshotStore<TState>` interface
- ✅ Snapshot data survives persistence and deserialization
- ✅ Last-write-wins semantics (upsert behavior)
- ✅ EnsureSchemaAsync is idempotent
- ✅ Integration tests with real databases pass
- ✅ Contract tests inherited by both implementations
