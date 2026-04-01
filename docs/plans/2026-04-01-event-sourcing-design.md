# ZeroAlloc.EventSourcing — Design Document

**Date:** 2026-04-01  
**Status:** Approved

---

## Overview

A zero-allocation, high-performance event sourcing library for .NET. Layered architecture with pluggable storage, a source-generated aggregate model, and full integration with the ZeroAlloc ecosystem.

**Target frameworks:** `net9.0`, `net10.0`  
**Repo structure:** Monorepo, separate NuGet packages per concern.

---

## Solution Structure

```
ZeroAlloc.EventSourcing/
├── src/
│   ├── ZeroAlloc.EventSourcing/                   ← core primitives
│   ├── ZeroAlloc.EventSourcing.Aggregates/         ← DDD aggregate layer
│   ├── ZeroAlloc.EventSourcing.Generators/         ← Roslyn source generator
│   ├── ZeroAlloc.EventSourcing.InMemory/           ← in-memory adapter
│   ├── ZeroAlloc.EventSourcing.SqlServer/          ← SQL Server adapter
│   └── ZeroAlloc.EventSourcing.PostgreSql/         ← PostgreSQL adapter
├── tests/
│   ├── ZeroAlloc.EventSourcing.Tests/
│   ├── ZeroAlloc.EventSourcing.Aggregates.Tests/
│   ├── ZeroAlloc.EventSourcing.InMemory.Tests/
│   ├── ZeroAlloc.EventSourcing.SqlServer.Tests/
│   └── ZeroAlloc.EventSourcing.PostgreSql.Tests/
├── docs/
│   └── plans/
└── ZeroAlloc.EventSourcing.sln
```

### ZeroAlloc Package Dependencies

| Project | ZeroAlloc deps |
|---|---|
| Core | `Results`, `Collections`, `AsyncEvents`, `ValueObjects` |
| Aggregates | Core + `Mediator`, `Validation` |
| Generators | `Pipeline`, `Analyzers` |
| Adapters | Core + `Serialisation` |

---

## Layer 1: Core Primitives (`ZeroAlloc.EventSourcing`)

### Key Types

```csharp
// Zero-alloc identifiers
public readonly record struct StreamId(string Value);
public readonly record struct StreamPosition(long Value)
{
    public static readonly StreamPosition Start = new(0);
    public static readonly StreamPosition End = new(-1);
}

// Event envelope — carries event + metadata, no boxing
public readonly record struct EventEnvelope<TEvent>(
    StreamId StreamId,
    StreamPosition Position,
    TEvent Event,
    EventMetadata Metadata);

public readonly record struct EventMetadata(
    Guid EventId,       // Guid.CreateVersion7() — time-ordered, no external dep
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? CorrelationId,
    Guid? CausationId);

// Core store interface
public interface IEventStore
{
    ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<object> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default);

    IAsyncEnumerable<EventEnvelope<object>> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        CancellationToken ct = default);

    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        CancellationToken ct = default);
}

// Implemented by storage backends
public interface IEventStoreAdapter
{
    ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default);

    IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        CancellationToken ct = default);
}
```

### ID Generation

`EventMetadata.EventId`, `CorrelationId`, and `CausationId` all use `Guid.CreateVersion7()` — time-ordered, available on net9.0+, no external dependency required.

### Optimistic Concurrency

`expectedVersion` is passed on every append. The adapter enforces it; conflicts are mapped to `StoreError.Conflict` and surfaced via `Result<AppendResult, StoreError>` (via `ZeroAlloc.Results`).

---

## Layer 2: Aggregate Layer (`ZeroAlloc.EventSourcing.Aggregates`)

### Aggregate Base

```csharp
public interface IAggregateState<TSelf> where TSelf : struct, IAggregateState<TSelf>
{
    static abstract TSelf Initial { get; }
}

public abstract class Aggregate<TId, TState>
    where TId : struct
    where TState : struct, IAggregateState<TState>
{
    public TId Id { get; protected set; }
    public StreamPosition Version { get; private set; }
    public StreamPosition OriginalVersion { get; private set; }

    protected TState State { get; private set; }

    // Pooled via ZeroAlloc.Collections — no List<T> per aggregate
    private PooledList<object> _uncommitted;

    protected void Raise<TEvent>(TEvent @event) where TEvent : notnull
    {
        _uncommitted.Add(@event);
        State = ApplyEvent(State, @event); // source-generated dispatch
    }

    // Emitted by ZeroAlloc.EventSourcing.Generators
    protected abstract TState ApplyEvent(TState state, object @event);

    internal ReadOnlySpan<object> DequeueUncommitted() { ... }
}
```

### State Definition (consumer code)

```csharp
public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public OrderStatus Status { get; private set; }

    // Source generator picks up Apply(TEvent) by naming convention
    private OrderState Apply(OrderPlaced e) => this with { Status = OrderStatus.Placed };
    private OrderState Apply(OrderShipped e) => this with { Status = OrderStatus.Shipped };
}
```

### Repository

```csharp
public interface IAggregateRepository<TAggregate, TId>
    where TId : struct
{
    ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default);
    ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, CancellationToken ct = default);
}
```

### Snapshots

`ISnapshotStore<TState>` is optional. `IAggregateRepository` checks for a snapshot before reading the full stream, reducing read amplification for long-lived aggregates.

---

## Layer 3: Source Generator (`ZeroAlloc.EventSourcing.Generators`)

Built on `ZeroAlloc.Pipeline`. Emits two artifacts at compile time:

1. **`ApplyEvent` dispatch** — scans for `Apply(TEvent)` methods on aggregate state structs, emits a switch expression routing by type. No reflection, no dictionaries, no virtual dispatch. Same pattern as `ZeroAlloc.Mediator`.

2. **`IEventTypeRegistry`** — maps `event_type` strings to CLR types for deserialization. Generated from all event types referenced in `Apply` methods. Used by storage adapters at read time.

---

## Layer 4: Storage Adapters

### InMemory (`ZeroAlloc.EventSourcing.InMemory`)

- Backed by `ConcurrentDictionary<StreamId, PooledList<RawEvent>>`
- Optimistic concurrency via `Interlocked` version check
- Live subscriptions via `ZeroAlloc.AsyncEvents` in-process broadcast
- No external dependencies — primary use is unit and integration testing

### SQL Server + PostgreSQL

Shared schema, separate adapter packages.

```sql
CREATE TABLE event_store (
    stream_id       VARCHAR(255)      NOT NULL,
    position        BIGINT            NOT NULL,
    event_type      VARCHAR(500)      NOT NULL,
    event_id        UNIQUEIDENTIFIER  NOT NULL,
    occurred_at     DATETIMEOFFSET    NOT NULL,
    correlation_id  UNIQUEIDENTIFIER  NULL,
    causation_id    UNIQUEIDENTIFIER  NULL,
    payload         VARBINARY(MAX)    NOT NULL,
    PRIMARY KEY (stream_id, position)   -- enforces optimistic concurrency at DB level
);

CREATE TABLE snapshots (
    stream_id   VARCHAR(255)   NOT NULL PRIMARY KEY,
    position    BIGINT         NOT NULL,
    state_type  VARCHAR(500)   NOT NULL,
    payload     VARBINARY(MAX) NOT NULL
);
```

**Serialization** is injected via `ZeroAlloc.Serialisation`'s `ISerializer<T>`. Consumers choose MemoryPack, MessagePack, or System.Text.Json. Adapters write/read `byte[]` blobs; `IEventTypeRegistry` (source-generated) maps `event_type` strings to CLR types at read time.

---

## Layer 5: Subscriptions & Projections

### Subscriptions

```csharp
public interface IEventSubscription : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken ct = default);
}

public interface IEventHandler<TEvent>
{
    ValueTask HandleAsync(EventEnvelope<TEvent> envelope, CancellationToken ct = default);
}

// Fluent builder
eventStore
    .Subscribe(fromPosition: StreamPosition.Start)
    .Handle<OrderPlaced>(handler)
    .Handle<OrderShipped>(handler)
    .StartAsync(ct);
```

Two modes:
- **Catch-up** — reads historical events from storage, then switches to live
- **Live-only** — only new events via `ZeroAlloc.AsyncEvents` in-process broadcast

### Projections

Thin layer on subscriptions. Source generator emits the event dispatch — no reflection.

```csharp
public abstract class Projection<TReadModel>
{
    protected abstract TReadModel Apply(TReadModel current, EventEnvelope<object> envelope);
}
```

Projection persistence is intentionally out of scope — consumers own their read model stores.

---

## Feature Backlog (delivery phases)

Given the scope, implementation is split into phases. Each phase is its own implementation plan.

| Phase | Scope |
|---|---|
| **1 — Foundation** | Solution scaffold, CI, core abstractions, `InMemory` adapter, core tests |
| **2 — Aggregate Layer** | Source generator, `Aggregate<TId,TState>`, `IAggregateRepository`, aggregate tests |
| **3 — SQL Adapters** | PostgreSQL adapter, SQL Server adapter, migration scripts, adapter tests |
| **4 — Subscriptions** | Catch-up + live subscriptions, `IEventHandler`, subscription builder |
| **5 — Projections & Snapshots** | `Projection<TReadModel>`, `ISnapshotStore`, snapshot integration in repository |
