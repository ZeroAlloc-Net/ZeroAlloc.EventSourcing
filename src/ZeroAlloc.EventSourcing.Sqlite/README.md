# ZeroAlloc.EventSourcing.Sqlite

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.EventSourcing.Sqlite.svg)](https://www.nuget.org/packages/ZeroAlloc.EventSourcing.Sqlite)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)

A SQLite-backed `IEventStoreAdapter` for [ZeroAlloc.EventSourcing](https://www.nuget.org/packages/ZeroAlloc.EventSourcing). Designed for embedded scenarios, single-node services, test harnesses, and AOT-published binaries.

## What it is

- A drop-in `IEventStoreAdapter` implementation that persists events to a single `event_store` table in a SQLite database.
- **AOT-clean**: ships with `PublishAot=true` compatibility verified by a dedicated AOT smoke project. No reflection, no dynamic code generation.
- **`*` global stream support**: every event is assigned a monotonic `global_position`, enabling cross-stream reads via `StreamId.Global` — the same shape as the PostgreSQL and SQL Server adapters.
- **Optimistic concurrency**: enforced via `expectedVersion` checks inside a `BEGIN IMMEDIATE` transaction; conflicts return `StoreError.Conflict(...)` rather than throwing.

## Install

```bash
dotnet add package ZeroAlloc.EventSourcing.Sqlite
```

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sqlite;

var services = new ServiceCollection();

services
    .AddEventSourcing()
    .UseSqliteEventStore("Data Source=events.db");

// IEventTypeRegistry must be registered separately — see the core package docs.
```

`UseSqliteEventStore` registers `SqliteEventStoreAdapter` as `IEventStoreAdapter` and `EventStore` as `IEventStore`. By default it also calls `EnsureSchemaAsync()` once during the singleton factory so the `event_store` table exists before first use. Pass `ensureSchema: false` if you prefer to control schema bootstrap yourself (e.g. via a migration runner).

```csharp
// Opt out of automatic schema bootstrap
services.AddEventSourcing().UseSqliteEventStore(connectionString, ensureSchema: false);

// Then run it manually at a time of your choosing
var adapter = sp.GetRequiredService<IEventStoreAdapter>() as SqliteEventStoreAdapter;
await adapter!.EnsureSchemaAsync();
```

## The `*` global stream

Reading from `StreamId.Global` (alias `*`) returns events from **every** stream in commit order:

```csharp
await foreach (var raw in adapter.ReadAsync(StreamId.Global, StreamPosition.Start, ct))
{
    // raw.Position is the global_position (monotonic across all streams)
    // raw.EventType, raw.Payload, raw.Metadata are the usual per-event values
}
```

Inside a write transaction the adapter reads `MAX(global_position)` and assigns the next contiguous values. SQLite is single-writer, so global ordering is deterministic and gap-free — no skip-locks or sequence-cache surprises.

## Health checks

Add a health check that probes the database with `SELECT 1`:

```csharp
services.AddHealthChecks()
    .AddSqliteEventStore("Data Source=events.db");
```

Default registration name is `sqlite-event-store`. Override via the `name` parameter.

## AOT attestation

This package is exercised by the `ZeroAlloc.EventSourcing.Sqlite.AotSmoke` project, which:
- Publishes with `PublishAot=true` and `InvariantGlobalization=true`.
- Gates `IL2026`, `IL2067`, `IL2075`, `IL2091`, `IL3050`, and `IL3051` as build errors.
- At runtime, appends an event and reads it back via both per-stream and `*` global reads.

A clean publish + exit-0 run is required to ship a new version.

## Connection strings

```csharp
// File-based (typical for embedded production)
"Data Source=events.db"

// File-based with WAL mode (better concurrency on multi-reader workloads)
"Data Source=events.db;Cache=Shared"
// Then run "PRAGMA journal_mode=WAL;" once after EnsureSchemaAsync.

// In-memory, shared cache (typical for tests)
"Data Source=file:test-abc?mode=memory&cache=shared"
// Hold a keep-alive SqliteConnection open for the lifetime of the test so the
// shared-cache backing store is not collected when transient connections close.

// Strictly per-connection in-memory (not useful — each connection sees its own DB)
"Data Source=:memory:"
```

## Concurrency model

SQLite is a single-writer database. The adapter issues `BEGIN IMMEDIATE` (via `IsolationLevel.Serializable` on `Microsoft.Data.Sqlite`) so concurrent appenders queue cleanly at the connection layer instead of racing into "database is locked" errors. For high-throughput workloads, prefer the PostgreSQL or SQL Server adapter; SQLite shines for embedded apps, single-process services, edge deployments, and integration tests.

## Roadmap

- **Live subscriptions**: the current `SubscribeAsync` returns a polling subscription. A future release may use SQLite's `data_change_notification_callback` (the C-level `update_hook`) to push real-time notifications without polling. Tracked separately.
- **WAL bootstrap helper**: an opt-in `pragma_journal_mode=WAL` switch on `UseSqliteEventStore` to enable WAL automatically.

## Versioning

This package follows the ZeroAlloc.EventSourcing repository's SemVer + Conventional Commits + release-please workflow. The `PublicAPI.Shipped.txt` file in this project is the authoritative list of the public surface — anything not declared there is unshipped or internal.
