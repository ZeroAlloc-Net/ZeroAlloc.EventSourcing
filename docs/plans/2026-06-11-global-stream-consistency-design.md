# `*` Global Stream Consistency + SQLite Adapter — Design

**Status:** approved 2026-06-11
**Scope:** Five-package coordinated change in `ZeroAlloc.EventSourcing` — new `.Sqlite` adapter + `*` global stream consistency fix across `.InMemory`, `.PostgreSql`, `.SqlServer`.
**Branch:** `feat/eventsourcing-sqlite` off `main`.
**Estimated scope:** 3-5 days of focused work, multi-task PR.

## Background

The `ZA.EventSourcing.Outbox` v0.1 just shipped (PR #185), depending on the `*` pseudo-stream for global event subscription via `StreamConsumer("*", ...)`. Exploration during Session C revealed that **`*` is inconsistently supported across the four event-store adapters**:

- `InMemoryEventStoreAdapter` — supports `*` but has the cursor-conflation bug documented in `tests/ZeroAlloc.EventSourcing.Outbox.Tests/CrossAggregateIntegrationTests.cs`. The `*` pseudo-stream reads use the consumer's global cursor against per-stream offsets, silently skipping events.
- `PostgreSqlEventStoreAdapter` — does NOT support `*`. The `ReadAsync` filter `WHERE stream_id = @id` with `id = "*"` matches zero rows.
- `SqlServerEventStoreAdapter` — same as Postgres: zero rows returned.
- `SqliteEventStoreAdapter` — does not exist yet.

Outbox + Postgres / SqlServer is therefore **silently broken** in production today. The Outbox tests pass because they run against the (buggy) InMemory adapter.

This design fixes `*` consistently across the ecosystem AND adds the missing SQLite adapter as part of the same PR.

## Decision

Adopt **Option B from the Session C brainstorm**: fix `*` everywhere. Five-package coordinated change.

**Rejected alternatives:**

- **Option A (SQLite-only):** SQLite ships with proper `*` support, Postgres/SqlServer/InMemory left broken. Fastest path to unblock `za-cqrs-es` template, but leaves silent footguns for adopters mixing the Outbox with non-SQLite stores.
- **Option C (SQLite + InMemory only):** compromise that fixes the test path but leaves Postgres/SqlServer Outbox broken. Defers the same problem.

**Adopted schema design** (Section 1 brainstorm — Option A): add `global_position` column to all event-store tables, populated by each provider's native auto-increment mechanism (SQLite hand-managed inside the write transaction; Postgres `BIGSERIAL`; SqlServer `IDENTITY(1,1)`; InMemory `Interlocked.Increment`).

## Architecture

### Invariant

Every event has a stream-local `position` (1-based, monotonic per stream) AND a global `global_position` (monotonic across the whole store). Reads from `*` use `ORDER BY global_position`; reads from a specific stream use `ORDER BY position` (unchanged from today). Both cursor types use the same `StreamPosition` (long) — semantically overloaded by which `streamId` is passed.

### Per-adapter mechanism

| Adapter | global_position mechanism | Migration |
|---|---|---|
| **SQLite (new)** | `global_position INTEGER NOT NULL` populated by `MAX(global_position) + 1` in the same `BEGIN IMMEDIATE` transaction as the per-stream INSERT | n/a — ships with column from day one |
| **PostgreSql** | `global_position BIGSERIAL NOT NULL` | upgrade-aware `EnsureSchemaAsync`: detect missing column → `ALTER TABLE ADD COLUMN` → backfill via `ROW_NUMBER()` → `SET DEFAULT nextval(...)` → `SET NOT NULL` |
| **SqlServer** | `global_position BIGINT IDENTITY(1,1) NOT NULL` | upgrade-aware `EnsureSchemaAsync`: `ALTER COLUMN ... IDENTITY` is not supported → must use sp_rename + new IDENTITY column dance (drop+recreate would lose data; we preserve it) |
| **InMemory** | `Interlocked.Increment(ref _globalCounter)` inside the existing write lock | trivial fix — replace the buggy `*` branch with a global-counter ordered read |

### Concurrency under load

- **Postgres**: per-stream advisory locks unchanged — different streams append in parallel; same stream serializes
- **SqlServer**: `WITH (UPDLOCK, HOLDLOCK)` range lock on `stream_id` index unchanged
- **SQLite**: single-writer transaction lock — ALL appends serialize globally (SQLite's fundamental constraint, appropriate for embedded single-app use)
- **InMemory**: single `lock` — same serialization as SQLite

## Public API surface

### New: `StreamId.Global` singleton

```csharp
public readonly partial record struct StreamId(string Value)
{
    public static StreamId Global { get; } = new("*");
    public bool IsGlobal => Value == "*";
}
```

All 4 adapters dispatch on `id.IsGlobal` instead of literal string compare. Users who already pass `new StreamId("*")` keep working — no breaking change. New code prefers `StreamId.Global` for discoverability + intent clarity.

### New package: `ZeroAlloc.EventSourcing.Sqlite`

```csharp
public sealed class SqliteEventStoreAdapter : IEventStoreAdapter
{
    public SqliteEventStoreAdapter(string connectionString);   // mirrors SqlServer pattern
    public ValueTask EnsureSchemaAsync(CancellationToken ct = default);
    // IEventStoreAdapter contract — unchanged signatures
}

public static class EventSourcingBuilderSqliteExtensions
{
    public static EventSourcingBuilder UseSqliteEventStore(
        this EventSourcingBuilder builder,
        string connectionString,
        bool ensureSchema = true);
}

public sealed class SqliteEventStoreHealthCheck : IHealthCheck { /* mirrors SqlServer */ }
```

Provider: `Microsoft.Data.Sqlite` (matches za-clean template precedent, AOT-friendly).

### Existing 3 adapters — no public API change

- `PostgreSqlEventStoreAdapter` — internal SQL changes only; `EnsureSchemaAsync` becomes upgrade-aware
- `SqlServerEventStoreAdapter` — same
- `InMemoryEventStoreAdapter` — internal counter swap; `*` branch rewritten; no signature change

The `RawEvent`, `EventEnvelope`, serializer, registry, etc. are untouched.

## Data flow

### Write path

```
Client: store.AppendAsync(StreamId("order-123"), [event], expectedVersion: 0)
  ↓ EventStore.AppendAsync (core, serializes → RawEvent)
  ↓ IEventStoreAdapter.AppendAsync

  SQLite:
    BEGIN IMMEDIATE
    SELECT COALESCE(MAX(position), 0) FROM event_store WHERE stream_id = @id
    if (current != expectedVersion) → rollback + StoreError.Conflict
    SELECT COALESCE(MAX(global_position), 0) FROM event_store
    for each event:
      INSERT (stream_id, position, global_position, ...)
        VALUES (@id, @current+i+1, @globalCurrent+i+1, ...)
    COMMIT

  Postgres:
    BEGIN
    SELECT pg_advisory_xact_lock(hashtextextended(@id, 0))
    SELECT COALESCE(MAX(position), 0) FROM event_store WHERE stream_id = @id
    if (current != expectedVersion) → rollback + Conflict
    for each event:
      INSERT (stream_id, position, ...) — global_position auto-assigned by BIGSERIAL
    COMMIT

  SqlServer:
    BEGIN
    SELECT ISNULL(MAX(position), 0) WITH (UPDLOCK, HOLDLOCK) WHERE stream_id = @id
    if (current != expectedVersion) → rollback + Conflict
    for each event:
      INSERT (stream_id, position, ...) — global_position auto-assigned by IDENTITY
    COMMIT

  InMemory:
    lock (_writeLock):
      if (currentVersion(@id) != expectedVersion) → Conflict
      for each event:
        var globalPos = Interlocked.Increment(ref _globalCounter);
        _events.Add(new StoredEvent(@id, expectedVersion + i + 1, globalPos, ...))
```

**SQLite subtlety:** unlike Postgres/SqlServer where the provider's auto-increment fires per row, SQLite requires the adapter to hand-compute `global_position` because we keep `(stream_id, position)` as the composite PK (can't also be AUTOINCREMENT). The `SELECT MAX(global_position)` plus `INSERT` happens inside `BEGIN IMMEDIATE`; SQLite's single-writer model guarantees no other writer interleaves. An index on `global_position DESC` makes `MAX` O(1).

### Read path

**Per-stream** (unchanged):

```sql
SELECT position, event_type, ..., payload, global_position
FROM event_store
WHERE stream_id = @id AND position >= @from
ORDER BY position ASC
```

**Global** (new branch keyed on `id.IsGlobal`):

```sql
SELECT position, event_type, ..., payload, global_position
FROM event_store
WHERE global_position >= @from
ORDER BY global_position ASC
```

The `EventEnvelope` returned carries the GLOBAL position in its `Position` field when reading global. This is the semantic overload: same `long` type, different domain depending on the streamId passed.

**Cursor implication:** `StreamConsumer`/`Outbox` checkpoint storage works correctly without any change — `ICheckpointStore` stores `lastEnvelope.Position`, which is `global_position` for global subscribers. Next poll resumes from that value. No checkpoint-store changes needed.

### Subscription path

`PollingEventSubscription` (in core) polls `ReadAsync` repeatedly. Global subscriptions work automatically once `ReadAsync` is fixed in each adapter.

## Migration semantics

`EnsureSchemaAsync` becomes upgrade-aware for Postgres + SqlServer. Pseudocode:

```
CREATE TABLE IF NOT EXISTS event_store (...)   ← fresh install includes global_position

IF NOT EXISTS column 'global_position' in event_store:
  ALTER TABLE event_store ADD COLUMN global_position BIGINT NULL
  UPDATE event_store
    SET global_position = ROW_NUMBER() OVER (
      ORDER BY occurred_at ASC, stream_id ASC, position ASC
    )
  ALTER TABLE event_store ALTER COLUMN global_position SET NOT NULL
  -- Postgres: ALTER COLUMN ... SET DEFAULT nextval('event_store_global_position_seq'::regclass)
  -- SqlServer: drop+recreate the column with IDENTITY via sp_rename + new column dance
  CREATE INDEX event_store_global_position_idx ON event_store (global_position)
```

**Backfill ordering**: `ORDER BY occurred_at ASC, stream_id ASC, position ASC` — chronological with deterministic tiebreaks. Operators with co-incident timestamps get reproducible global positions.

**Concurrent-startup safety**: each migration wraps in a transaction with the appropriate lock (Postgres advisory, SqlServer `sp_getapplock`, SQLite `BEGIN EXCLUSIVE`). Two app instances calling `EnsureSchemaAsync` simultaneously won't corrupt each other.

**SqlServer caveat**: `ALTER COLUMN ... IDENTITY` is not supported. The migration:
1. `ALTER TABLE ... ADD global_position_new BIGINT IDENTITY(1, 1)` (high seed value avoiding any future contention with backfilled values)
2. Backfill `global_position_new` with the `ROW_NUMBER()` pass
3. `EXEC sp_rename 'event_store.global_position_new', 'global_position', 'COLUMN'`
4. `DBCC CHECKIDENT` resets the IDENTITY seed to `MAX(global_position) + 1`

Documented carefully in the implementation plan — this is the gnarliest part.

## Error handling

- **Migration failure mid-flight**: transaction rolls back, leaving the table in its prior state. `EnsureSchemaAsync` re-raises. Operators diagnose and re-run.
- **Append after migration**: works immediately — the BIGSERIAL/IDENTITY/SQLite-MAX sequence picks up from `MAX(global_position) + 1`.
- **Conflict on optimistic-concurrency check**: unchanged — `StoreError.Conflict` returned, no migration involvement.
- **Existing InMemory checkpoint values become invalid** for users who were using `StreamConsumer("*", ...)`: their checkpoints pointed to per-stream positions but the new `*` semantics use `global_position`. Documented breaking-change in InMemory's CHANGELOG; recommend `IStreamConsumer.ResetPositionAsync(StreamPosition.Start)` on upgrade. This is the consequence of fixing a real bug.
- **Postgres/SqlServer**: nobody was using `*` (silently broken), so no checkpoints exist to invalidate. Migration is silent.

## Testing strategy

**Per-adapter** (4 test projects — 1 new SQLite, 3 existing updated):

- Single-stream append + read round-trip — verify per-stream cursor still works
- Concurrent appends to different streams — verify parallelism (Postgres/SqlServer) or proper serialization (SQLite/InMemory)
- Optimistic concurrency conflict — unchanged behavior
- **New**: global stream read after multi-stream appends — assert events appear in `(occurred_at, stream_id, position)` order
- **New**: subscription on `StreamId.Global` — assert all events flow through
- **New**: migration test — pre-seed table without `global_position` column, run `EnsureSchemaAsync`, assert column added + backfilled in correct order + new appends auto-assign
- **New (SQLite only)**: `BEGIN IMMEDIATE` write lock behavior — concurrent connections serialize

**Cross-package integration** (in `ZA.EventSourcing.Outbox.Tests`):

- The current `CrossAggregateIntegrationTests` Task 5 placeholder workaround **gets removed** — the InMemory adapter's `*` now works correctly, so no pre-seed placeholder needed. Update the test + delete the TODO breadcrumb at `InMemoryEventStoreAdapter.cs:45-49`.
- Add an equivalent integration test using `SqliteEventStoreAdapter` to prove Outbox + SQLite works end-to-end
- Postgres/SqlServer integration tests stay in their respective `.Tests` projects (they already use test containers)

**AOT smoke**: new `ZeroAlloc.EventSourcing.Sqlite.AotSmoke` project, follows the Postgres/SqlServer smoke pattern. Validates the `Microsoft.Data.Sqlite` provider is AOT-clean in the dispatch path.

## Out of scope (deferrals)

- Cross-process broker dispatch via Outbox — that's Outbox v0.3 work
- LIVE subscription path via `IEventStore.SubscribeAsync` for SQLite — polling-only for v0.1 (matches Postgres/SqlServer)
- Connection pooling beyond what `Microsoft.Data.Sqlite` provides natively
- Multi-database tenancy (one DB per tenant) — separate concern, future work
- `StreamPosition` becoming a strongly-typed union of `PerStream(long)` / `Global(long)` — defer to v1.0 API freeze; for v0.1 the semantic overload is documented

## Risk

- **SqlServer IDENTITY migration** is the gnarliest part. The sp_rename + DBCC CHECKIDENT dance is well-documented but operators get nervous. The migration test in `ZA.EventSourcing.SqlServer.Tests` must cover both fresh-install and upgrade paths against a real SqlServer test container, with idempotency verification (re-running `EnsureSchemaAsync` on an already-upgraded table is a no-op).
- **Existing InMemory adopters** of `*` get a breaking checkpoint-semantic change. Mitigation: documented `ResetPositionAsync` recommendation in the CHANGELOG. Low risk — InMemory was for tests/dev; few real adopters depended on its (broken) `*` behavior.
- **SQLite global_position contention**: every append needs `SELECT MAX(global_position)` inside the write transaction. With an index on `global_position DESC`, this is O(1) — but it's worth a benchmark to confirm. Add to the Sqlite.Benchmarks suite alongside the AppendAsync throughput case.
- **Cross-version mixing** during rolling deployment: app v_n+1 starts writing with `global_position`, app v_n is still reading from the old schema. The `ALTER ... ADD COLUMN` doesn't break v_n's `SELECT * FROM event_store` queries IF they don't use `*` (positional schema dependence is the risk). Document the migration as best run during scheduled downtime.

## Versioning

The PR causes release-please to bump four packages:

- `ZeroAlloc.EventSourcing.Sqlite v0.1.0` — new package (feat)
- `ZeroAlloc.EventSourcing.InMemory v?.?+1` — minor bump (`feat:` — `*` semantics now work correctly; existing behavior preserved for non-`*` paths)
- `ZeroAlloc.EventSourcing.PostgreSql v?.?+1` — minor bump (`feat:` — `*` newly supported)
- `ZeroAlloc.EventSourcing.SqlServer v?.?+1` — minor bump (`feat:` — `*` newly supported)
- `ZeroAlloc.EventSourcing` core — patch bump (`feat:` — adds `StreamId.Global` + `IsGlobal` properties, additive)

Squash subjects per the implementation plan must use the correct conventional-commit scope so release-please routes the bumps correctly. The final merge subject will be coordinated.

## Roadmap (post v0.1)

- LIVE subscription path for all SQL adapters (Postgres `LISTEN/NOTIFY`, SqlServer Service Broker, SQLite `data_change_notification_callback`) — separate work
- `StreamPosition` strongly-typed union — v1.0 API freeze candidate
- Snapshot integration with `*` — currently snapshots are per-stream, may want a global snapshot for fast outbox catch-up
