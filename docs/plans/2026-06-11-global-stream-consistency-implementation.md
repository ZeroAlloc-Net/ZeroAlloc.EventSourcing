# `*` Global Stream Consistency + SQLite Adapter Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (or superpowers:subagent-driven-development) to implement this plan task-by-task.

**Goal:** Ship a new `ZeroAlloc.EventSourcing.Sqlite` adapter AND fix `*` global stream semantics across the 3 existing adapters (`InMemory`, `PostgreSql`, `SqlServer`) so the just-shipped Outbox v0.1 works correctly with every backing store.

**Architecture:** Add `global_position` column to all event_store tables, populated by each provider's native auto-increment. Add `StreamId.Global` singleton + `IsGlobal` to core. Adapters dispatch reads on `id.IsGlobal` — global reads `ORDER BY global_position`; per-stream reads unchanged. Migrate Postgres + SqlServer existing tables via upgrade-aware `EnsureSchemaAsync`.

**Tech Stack:** .NET 8/9/10 multi-target, `Microsoft.Data.Sqlite` (AOT-friendly), `Npgsql`, `Microsoft.Data.SqlClient`, xUnit + FluentAssertions, BenchmarkDotNet (deferred), testcontainers for Postgres + SqlServer migration tests.

**Design reference:** [2026-06-11-global-stream-consistency-design.md](2026-06-11-global-stream-consistency-design.md) (commit `474c80b`)
**Branch:** `feat/eventsourcing-sqlite` off `main`.

---

## Conventions (apply to every task)

- **Co-Authored-By trailer on every commit:** `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`
- **AOT-clean.** No reflection in dispatch paths; new `ZeroAlloc.EventSourcing.Sqlite.AotSmoke` project gates IL2026/2067/2075/2091/3050/3051 as errors.
- **`ConfigureAwait(false)`** on every await.
- **TDD discipline.** Failing test first.
- **Mirror existing adapters.** When in doubt, copy the shape from `src/ZeroAlloc.EventSourcing.PostgreSql/` (primary reference) or `src/ZeroAlloc.EventSourcing.SqlServer/` (for IDENTITY migration patterns).
- **Migration idempotency.** Re-running `EnsureSchemaAsync` on an already-upgraded table must be a no-op.
- **PublicAPI tracking.** RS0016 must be clean before commit.
- **Pre-existing orphan modifications** in `samples/ZeroAlloc.EventSourcing.Mediator.AotSmoke/...csproj` and `tests/ZeroAlloc.EventSourcing.Mediator.*.csproj` must NOT be staged in this PR.

---

## Task 1 — Core: `StreamId.Global` singleton + `IsGlobal`

**Goal:** Additive change to `StreamId` exposing the global-stream identity as a typed singleton. Backward-compatible: existing `new StreamId("*")` keeps working; `IsGlobal` is the discriminator the adapters dispatch on.

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing/StreamId.cs`
- Modify: `src/ZeroAlloc.EventSourcing/PublicAPI.Unshipped.txt`
- Create: `tests/ZeroAlloc.EventSourcing.Tests/StreamIdTests.cs` (if it doesn't already cover the new surface)

**Step 1: Failing test**

```csharp
// tests/ZeroAlloc.EventSourcing.Tests/StreamIdTests.cs
using FluentAssertions;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamIdTests
{
    [Fact]
    public void Global_is_singleton_with_star_value()
    {
        StreamId.Global.Value.Should().Be("*");
        StreamId.Global.IsGlobal.Should().BeTrue();
    }

    [Fact]
    public void IsGlobal_true_for_star_streamId_constructed_from_string()
    {
        new StreamId("*").IsGlobal.Should().BeTrue();
        new StreamId("$all").IsGlobal.Should().BeFalse();   // legacy alias not promoted — only "*"
        new StreamId("order-1").IsGlobal.Should().BeFalse();
    }
}
```

**Step 2: Verify fail**

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.Tests --filter "FullyQualifiedName~StreamIdTests" -c Release
```
Expected: compile errors (`Global` + `IsGlobal` don't exist).

**Step 3: Implement**

Modify `src/ZeroAlloc.EventSourcing/StreamId.cs`:

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>Strongly typed identifier for an event stream.</summary>
public readonly record struct StreamId
{
    /// <summary>The stream identifier string.</summary>
    public string Value { get; init; }

    /// <summary>The global stream singleton — subscribes/reads ALL events across every stream, ordered by global_position.</summary>
    public static StreamId Global { get; } = new("*");

    /// <summary>True when this stream id refers to the global stream (Value == "*").</summary>
    public bool IsGlobal => Value == "*";

    /// <summary>Initialises a <see cref="StreamId"/> with a non-null, non-empty value.</summary>
    public StreamId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>Returns the string value of this stream identifier.</summary>
    public override string ToString() => Value;
}
```

**Step 4: Verify pass + update PublicAPI**

Build will surface RS0016 warnings for the new surface. Add to `PublicAPI.Unshipped.txt`:
```
ZeroAlloc.EventSourcing.StreamId.IsGlobal.get -> bool
static ZeroAlloc.EventSourcing.StreamId.Global.get -> ZeroAlloc.EventSourcing.StreamId
```

Re-run tests; verify 3 facts pass.

**Step 5: Commit**

```powershell
git add src/ZeroAlloc.EventSourcing/StreamId.cs src/ZeroAlloc.EventSourcing/PublicAPI.Unshipped.txt tests/ZeroAlloc.EventSourcing.Tests/StreamIdTests.cs
git commit -m @'
feat(core): StreamId.Global + IsGlobal for typed global-stream identity

Adds the canonical singleton + discriminator so adapters can dispatch on
id.IsGlobal instead of magic-string-comparing "*" at the SQL layer.
Backward-compatible — new StreamId("*") still works and is still
IsGlobal-true.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 2 — InMemory: add `global_position` + fix `*` cursor

**Goal:** Replace the buggy `*` branch in `InMemoryEventStoreAdapter.cs:43-61` with proper global-position tracking. Each event gets a monotonic `globalPosition` assigned at append time via `Interlocked.Increment`. Reads from `StreamId.Global` order by global position.

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryEventStoreAdapter.cs`
- Modify: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryStream.cs` (likely needs to track both per-stream + global positions)
- Create or modify: `tests/ZeroAlloc.EventSourcing.InMemory.Tests/GlobalStreamTests.cs`

**Step 1: Failing tests** (write 4 facts at once)

```csharp
// tests/ZeroAlloc.EventSourcing.InMemory.Tests/GlobalStreamTests.cs
using FluentAssertions;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public class GlobalStreamTests
{
    [Fact]
    public async Task Global_read_returns_events_from_all_streams_in_append_order()
    {
        var adapter = new InMemoryEventStoreAdapter();
        await adapter.AppendAsync(new StreamId("order-1"), Raw("A"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("customer-1"), Raw("B"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("order-1"), Raw("C"), new StreamPosition(1));

        var events = new List<string>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            events.Add(e.EventType);

        events.Should().Equal("A", "B", "C");   // append order
    }

    [Fact]
    public async Task Global_read_with_from_returns_only_subsequent_events()
    {
        var adapter = new InMemoryEventStoreAdapter();
        await adapter.AppendAsync(new StreamId("s1"), Raw("A"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s2"), Raw("B"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s3"), Raw("C"), StreamPosition.Start);

        var events = new List<string>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, new StreamPosition(2)))
            events.Add(e.EventType);

        events.Should().Equal("B", "C");
    }

    [Fact]
    public async Task Per_stream_read_unaffected_by_global_counter()
    {
        var adapter = new InMemoryEventStoreAdapter();
        await adapter.AppendAsync(new StreamId("s1"), Raw("A1"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s2"), Raw("B1"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s1"), Raw("A2"), new StreamPosition(1));

        var events = new List<string>();
        await foreach (var e in adapter.ReadAsync(new StreamId("s1"), StreamPosition.Start))
            events.Add(e.EventType);

        events.Should().Equal("A1", "A2");
    }

    [Fact]
    public async Task Global_position_starts_at_1_and_increments_monotonically()
    {
        var adapter = new InMemoryEventStoreAdapter();
        await adapter.AppendAsync(new StreamId("s1"), Raw("A"), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s2"), Raw("B"), StreamPosition.Start);

        var positions = new List<long>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            positions.Add(e.Position.Value);

        positions.Should().Equal(1, 2);
    }

    private static ReadOnlyMemory<RawEvent> Raw(string eventType)
        => new[] { new RawEvent(StreamPosition.Start, eventType, new byte[] { 0 }, EventMetadata.New(eventType)) }.AsMemory();
}
```

**Step 2: Verify fail**

Expected: facts 1, 2, 4 fail (current `*` branch yields per-stream events ordered by per-stream position — not in append order). Fact 3 may pass since it's per-stream.

**Step 3: Implement**

Approach: add a `long _globalCounter` field on `InMemoryEventStoreAdapter`. Modify `InMemoryStream` to store `(perStreamPos, globalPos, RawEvent)` tuples. Append assigns global via `Interlocked.Increment(ref _globalCounter)`. The `*` read iterates `_streams.Values`, collects all stored entries, sorts by `globalPos`, filters by `>= from`, yields a `RawEvent` whose `Position` is the global position (semantic overload per the design).

Key: when reading per-stream, yield `RawEvent` with per-stream position. When reading global, yield `RawEvent` with global position. These are distinct values stored side-by-side.

Pseudocode for the new `*` branch:

```csharp
if (id.IsGlobal)
{
    var allEntries = new List<StoredEntry>();
    foreach (var stream in _streams.Values)
        allEntries.AddRange(stream.Entries);   // (perStreamPos, globalPos, RawEvent)

    allEntries.Sort((a, b) => a.GlobalPosition.CompareTo(b.GlobalPosition));

    foreach (var entry in allEntries)
    {
        if (entry.GlobalPosition < from.Value) continue;
        ct.ThrowIfCancellationRequested();
        yield return entry.RawEvent with { Position = new StreamPosition(entry.GlobalPosition) };
    }
    yield break;
}
```

**Step 4: Verify pass**

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests -c Release
```

Expected: 4 new facts pass + all pre-existing InMemory tests still pass.

**Step 5: Update PublicAPI if needed**

The change is internal — `InMemoryEventStoreAdapter` public surface is unchanged. PublicAPI.Unshipped.txt should require no edits. If RS0016 surfaces, investigate.

**Step 6: Commit**

```powershell
git add src/ZeroAlloc.EventSourcing.InMemory/ tests/ZeroAlloc.EventSourcing.InMemory.Tests/
git commit -m @'
feat(inmemory): fix * global stream cursor semantics

Replaces the broken * branch that conflated per-stream position with the
consumer's global cursor (silently skipping events from streams whose
positions hadn't yet caught up to the cursor). Adds an
Interlocked.Increment _globalCounter populated at append time;
StreamId.Global reads order by globalPosition.

Per-stream read paths unchanged. RawEvent.Position is the global position
when reading global and the per-stream position when reading specific
streams — semantic overload documented in the design.

Closes the test workaround in Outbox.Tests/CrossAggregateIntegrationTests.cs
that Task 8 of this plan removes.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 3 — SQLite package scaffolding

**Goal:** Create the empty `src/ZeroAlloc.EventSourcing.Sqlite/` + tests + AOT smoke project shells. Wire release-please. Mirrors Outbox Task 1's scaffolding shape exactly.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/ZeroAlloc.EventSourcing.Sqlite.csproj`
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/version.txt` (content: `0.1.0\n`)
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/CHANGELOG.md` (content: `# Changelog\n`)
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/PublicAPI.Shipped.txt` (empty)
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/PublicAPI.Unshipped.txt` (empty)
- Create: `tests/ZeroAlloc.EventSourcing.Sqlite.Tests/ZeroAlloc.EventSourcing.Sqlite.Tests.csproj`
- Create: `samples/ZeroAlloc.EventSourcing.Sqlite.AotSmoke/ZeroAlloc.EventSourcing.Sqlite.AotSmoke.csproj`
- Create: `samples/ZeroAlloc.EventSourcing.Sqlite.AotSmoke/Program.cs` (placeholder — real wiring in Task 5)
- Modify: `release-please-config.json` (add entry alphabetically between `.SqlServer` and `.Telemetry`)
- Modify: `Directory.Packages.props` (add `Microsoft.Data.Sqlite` pin if not present)

**Main csproj** mirrors `ZeroAlloc.EventSourcing.PostgreSql.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>ZeroAlloc.EventSourcing.Sqlite</PackageId>
    <Description>SQLite IEventStoreAdapter for ZeroAlloc.EventSourcing. AOT-clean via Microsoft.Data.Sqlite. Supports the * global stream via global_position column.</Description>
    <PackageTags>$(PackageTags);sqlite;adapter</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ZeroAlloc.EventSourcing\ZeroAlloc.EventSourcing.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>ZeroAlloc.EventSourcing.Sqlite.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
  <ItemGroup Condition="'$(IsRoslynComponent)' != 'true'">
    <PackageReference Include="Meziantou.Analyzer" PrivateAssets="all" />
    <PackageReference Include="Roslynator.Analyzers" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

**Tests csproj** mirrors `ZeroAlloc.EventSourcing.PostgreSql.Tests` (single-TFM net10.0).

**AOT smoke csproj** mirrors `ZeroAlloc.EventSourcing.PostgreSql` smoke pattern with `<PublishAot>true</PublishAot>` + IL warnings-as-errors gate.

**Release-please entry:**

```json
"src/ZeroAlloc.EventSourcing.Sqlite": {
  "package-name": "ZeroAlloc.EventSourcing.Sqlite",
  "release-type": "simple",
  "changelog-path": "CHANGELOG.md"
},
```

**Verify all three build clean** (no source files yet → 0 errors, possibly RS0017 if PublicAPI declares a missing surface — ignore for now):

```powershell
dotnet build src/ZeroAlloc.EventSourcing.Sqlite -c Release -v minimal
dotnet build tests/ZeroAlloc.EventSourcing.Sqlite.Tests -c Release -v minimal
dotnet build samples/ZeroAlloc.EventSourcing.Sqlite.AotSmoke -c Release -v minimal
```

**Commit subject:** `chore(sqlite): scaffold ZeroAlloc.EventSourcing.Sqlite package projects`

---

## Task 4 — SQLite: `SqliteEventStoreAdapter` core (the big one)

**Goal:** Implement the full SQLite adapter — schema, `AppendAsync`, `ReadAsync` (both per-stream + global), `SubscribeAsync`, `EnsureSchemaAsync`. TDD-driven with ~12 facts.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/SqliteEventStoreAdapter.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Sqlite.Tests/SqliteEventStoreAdapterTests.cs`
- Modify: `src/ZeroAlloc.EventSourcing.Sqlite/PublicAPI.Unshipped.txt`

### Test scaffolding

Use a fresh in-memory SQLite database per test for isolation. Pattern:

```csharp
private static SqliteEventStoreAdapter NewAdapter()
{
    // Each test gets its own in-memory database; keep-alive connection prevents GC reclaim.
    var connectionString = $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
    var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
    keepAlive.Open();   // disposed by the test class; keeps the in-memory DB alive
    return new SqliteEventStoreAdapter(connectionString);
}
```

### Tests to write (one fact at a time, run between each)

1. `EnsureSchemaAsync_creates_table_with_global_position_column`
2. `EnsureSchemaAsync_is_idempotent`
3. `AppendAsync_to_empty_stream_succeeds_with_position_1`
4. `AppendAsync_wrong_expectedVersion_returns_Conflict`
5. `AppendAsync_assigns_monotonic_global_position`
6. `ReadAsync_per_stream_returns_events_in_position_order`
7. `ReadAsync_per_stream_with_from_skips_earlier_events`
8. `ReadAsync_global_returns_events_from_all_streams_in_global_position_order`
9. `ReadAsync_global_with_from_skips_earlier_global_positions`
10. `ReadAsync_unknown_stream_yields_nothing`
11. `SubscribeAsync_returns_PollingEventSubscription`
12. `Concurrent_appends_to_different_streams_serialize_safely` (SQLite single-writer test)

### Implementation outline

```csharp
public sealed class SqliteEventStoreAdapter : IEventStoreAdapter
{
    private readonly string _connectionString;

    public SqliteEventStoreAdapter(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS event_store (
                stream_id        TEXT     NOT NULL,
                position         INTEGER  NOT NULL,
                global_position  INTEGER  NOT NULL,
                event_type       TEXT     NOT NULL,
                event_id         TEXT     NOT NULL,
                occurred_at      TEXT     NOT NULL,
                correlation_id   TEXT     NULL,
                causation_id     TEXT     NULL,
                payload          BLOB     NOT NULL,
                PRIMARY KEY (stream_id, position)
            );
            CREATE INDEX IF NOT EXISTS event_store_global_position_idx ON event_store (global_position);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
    {
        if (events.Length == 0)
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, expectedVersion));

        var eventsArray = events.ToArray();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

        try
        {
            // Acquire write lock via BEGIN IMMEDIATE — done implicitly by Serializable isolation.
            // Verify by reading current per-stream version.
            var current = await ReadCurrentVersionAsync(conn, tx, id, ct).ConfigureAwait(false);
            if (current != expectedVersion.Value)
                return Result<AppendResult, StoreError>.Failure(StoreError.Conflict(id, expectedVersion, new StreamPosition(current)));

            var globalCurrent = await ReadCurrentGlobalPositionAsync(conn, tx, ct).ConfigureAwait(false);

            for (var i = 0; i < eventsArray.Length; i++)
                await InsertEventAsync(conn, tx, id, eventsArray[i], expectedVersion.Value + i + 1, globalCurrent + i + 1, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, new StreamPosition(expectedVersion.Value + eventsArray.Length)));
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* connection may be dead */ }
            throw;
        }
    }

    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id, StreamPosition from, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        if (id.IsGlobal)
        {
            cmd.CommandText = """
                SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload, global_position
                FROM event_store
                WHERE global_position >= @from
                ORDER BY global_position ASC
                """;
            cmd.Parameters.AddWithValue("@from", from.Value);
        }
        else
        {
            cmd.CommandText = """
                SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload, global_position
                FROM event_store
                WHERE stream_id = @streamId AND position >= @from
                ORDER BY position ASC
                """;
            cmd.Parameters.AddWithValue("@streamId", id.Value);
            cmd.Parameters.AddWithValue("@from", from.Value);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var perStreamPos = new StreamPosition(reader.GetInt64(0));
            var eventType    = reader.GetString(1);
            var eventId      = Guid.Parse(reader.GetString(2));
            var occurredAt   = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
            var correlationId = reader.IsDBNull(4) ? (Guid?)null : Guid.Parse(reader.GetString(4));
            var causationId   = reader.IsDBNull(5) ? (Guid?)null : Guid.Parse(reader.GetString(5));
            var payload       = (byte[])reader.GetValue(6);
            var globalPos     = reader.GetInt64(7);

            var metadata = new EventMetadata(eventId, eventType, occurredAt, correlationId, causationId);
            // When global read, RawEvent.Position carries global_position. When per-stream, per-stream position.
            var position = id.IsGlobal ? new StreamPosition(globalPos) : perStreamPos;
            yield return new RawEvent(position, eventType, payload.AsMemory(), metadata);
        }
    }

    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sub = new PollingEventSubscription(this, id, from, handler, PollingEventSubscription.DefaultPollInterval);
        return ValueTask.FromResult<IEventSubscription>(sub);
    }

    private async ValueTask<long> ReadCurrentVersionAsync(SqliteConnection conn, SqliteTransaction tx, StreamId id, CancellationToken ct) { /* SELECT COALESCE(MAX(position), 0) ... */ }
    private async ValueTask<long> ReadCurrentGlobalPositionAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct) { /* SELECT COALESCE(MAX(global_position), 0) FROM event_store */ }
    private async ValueTask InsertEventAsync(SqliteConnection conn, SqliteTransaction tx, StreamId id, RawEvent e, long position, long globalPosition, CancellationToken ct) { /* INSERT INTO event_store ... */ }
}
```

**`MAX(global_position)` performance:** the `event_store_global_position_idx` makes this O(log n) (descending index lookup). For high-throughput cases, the SQLite single-writer is the bottleneck anyway.

**Step 5: Update PublicAPI.Unshipped.txt** with the adapter's public surface.

**Step 6: Commit subject:** `feat(sqlite): SqliteEventStoreAdapter with per-stream + global * support`

---

## Task 5 — SQLite: builder extensions + HealthCheck + AOT smoke + README

**Goal:** Ship-ready wrappers around the adapter so adopters can write `services.AddEventSourcing().UseSqliteEventStore("Data Source=app.db")` in one line. Plus the AOT smoke + README + freeze PublicAPI.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/EventSourcingBuilderExtensions.cs`
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/SqliteEventStoreHealthCheck.cs`
- Create: `src/ZeroAlloc.EventSourcing.Sqlite/README.md`
- Modify: `src/ZeroAlloc.EventSourcing.Sqlite/ZeroAlloc.EventSourcing.Sqlite.csproj` (add `<PackageReadmeFile>`)
- Modify: `samples/ZeroAlloc.EventSourcing.Sqlite.AotSmoke/Program.cs` (real wiring replacing Task 3's placeholder)
- Create: `tests/ZeroAlloc.EventSourcing.Sqlite.Tests/EventSourcingBuilderExtensionsTests.cs` (~3 facts)
- Modify: `src/ZeroAlloc.EventSourcing.Sqlite/PublicAPI.Shipped.txt` (move all Unshipped → Shipped — freeze v0.1)
- Modify: `src/ZeroAlloc.EventSourcing.Sqlite/PublicAPI.Unshipped.txt` (clear to empty)

**Builder extension** mirrors PostgreSQL's pattern:

```csharp
public static class EventSourcingBuilderSqliteExtensions
{
    public static EventSourcingBuilder UseSqliteEventStore(
        this EventSourcingBuilder builder,
        string connectionString,
        bool ensureSchema = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.Services.AddSingleton<IEventStoreAdapter>(_ =>
        {
            var adapter = new SqliteEventStoreAdapter(connectionString);
            if (ensureSchema)
                adapter.EnsureSchemaAsync().AsTask().GetAwaiter().GetResult();   // bootstrap-only sync wait
            return adapter;
        });
        return builder;
    }
}
```

**HealthCheck** mirrors `SqlServerEventStoreHealthCheck.cs` — opens a connection, runs `SELECT 1`, returns healthy or unhealthy.

**README** ~100-150 lines:
1. What it is
2. Install
3. Quick start (DI wiring + EnsureSchemaAsync semantics)
4. `*` global stream support — explicit callout (this is the differentiator)
5. AOT-clean attestation
6. Connection-string examples (file-based + `:memory:`)
7. Concurrency notes (SQLite is single-writer; appropriate for embedded use)
8. Migration story — n/a, ships with `global_position` from day one
9. Roadmap (LIVE subscription via `data_change_notification_callback` deferred)

**AOT smoke Program.cs** wires the adapter against an in-memory SQLite DB, appends an event, reads it back, exits 0.

**Move Unshipped → Shipped**.

**Commit subject:** `feat(sqlite): builder extension, health check, AOT smoke + README — v0.1.0 ready`

---

## Task 6 — Postgres: `global_position BIGSERIAL` + upgrade-aware EnsureSchemaAsync + `*` read

**Goal:** Add `global_position BIGSERIAL NOT NULL` to the Postgres event_store table. Make `EnsureSchemaAsync` upgrade-aware for existing tables. Add `id.IsGlobal` branch to `ReadAsync`.

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.PostgreSql/PostgreSqlEventStoreAdapter.cs`
- Create: `tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/GlobalStreamTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/MigrationTests.cs`

### Test additions

Use the existing testcontainers Postgres fixture (mirror what `tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/` already does).

```csharp
public class GlobalStreamTests : IAsyncLifetime  // existing testcontainer pattern
{
    [Fact]
    public async Task Global_read_returns_events_from_all_streams_in_global_position_order() { /* ... */ }

    [Fact]
    public async Task Global_position_assigned_by_BIGSERIAL_is_monotonic() { /* ... */ }

    [Fact]
    public async Task Concurrent_appends_to_different_streams_get_distinct_global_positions() { /* ... */ }
}

public class MigrationTests : IAsyncLifetime
{
    [Fact]
    public async Task EnsureSchema_on_fresh_database_creates_table_with_global_position()
    {
        // Standard EnsureSchemaAsync + assert column exists via information_schema.
    }

    [Fact]
    public async Task EnsureSchema_on_legacy_table_without_global_position_migrates_in_place()
    {
        // Manually CREATE TABLE event_store WITHOUT global_position (legacy schema).
        // Pre-seed events in 3 streams.
        // Run EnsureSchemaAsync.
        // Assert: column added, ROW_NUMBER backfill ordered by (occurred_at, stream_id, position),
        // NOT NULL constraint applied, sequence ownership wired so new INSERT auto-assigns next value.
    }

    [Fact]
    public async Task EnsureSchema_is_idempotent_on_already_upgraded_table()
    {
        // Run EnsureSchemaAsync twice on an already-upgraded table. Second call should be a no-op.
    }
}
```

### Schema migration logic

```sql
-- Fresh install (CREATE TABLE IF NOT EXISTS):
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

-- Upgrade path (detected by checking information_schema):
SELECT column_name FROM information_schema.columns
  WHERE table_name = 'event_store' AND column_name = 'global_position';
-- If empty:
BEGIN;
LOCK TABLE event_store IN EXCLUSIVE MODE;
ALTER TABLE event_store ADD COLUMN global_position BIGINT NULL;
UPDATE event_store SET global_position = sub.rn FROM (
  SELECT stream_id, position,
         ROW_NUMBER() OVER (ORDER BY occurred_at ASC, stream_id ASC, position ASC) AS rn
  FROM event_store
) sub
WHERE event_store.stream_id = sub.stream_id AND event_store.position = sub.position;
ALTER TABLE event_store ALTER COLUMN global_position SET NOT NULL;
-- Create sequence + wire as default:
CREATE SEQUENCE event_store_global_position_seq;
SELECT setval('event_store_global_position_seq', COALESCE((SELECT MAX(global_position) FROM event_store), 0));
ALTER TABLE event_store ALTER COLUMN global_position SET DEFAULT nextval('event_store_global_position_seq');
ALTER SEQUENCE event_store_global_position_seq OWNED BY event_store.global_position;
CREATE INDEX IF NOT EXISTS event_store_global_position_idx ON event_store (global_position);
COMMIT;
```

The `LOCK TABLE ... IN EXCLUSIVE MODE` prevents concurrent writers during migration.

### Read branch addition

```csharp
public async IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, [EnumeratorCancellation] CancellationToken ct = default)
{
    // ... existing connection setup ...
    if (id.IsGlobal)
    {
        cmd.CommandText = """
            SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload, global_position
            FROM event_store
            WHERE global_position >= @from
            ORDER BY global_position ASC
            """;
        cmd.Parameters.AddWithValue("from", from.Value);
    }
    else
    {
        // ... existing per-stream branch with global_position selected for envelope consistency ...
    }
    // ... yield with global_position as Position when global, per-stream position otherwise ...
}
```

### Append behavior

The INSERT does NOT pass `global_position` — `BIGSERIAL` auto-assigns. No other change to AppendAsync.

**Commit subject:** `feat(postgres): global_position BIGSERIAL + upgrade-aware EnsureSchemaAsync + * support`

---

## Task 7 — SqlServer: `global_position IDENTITY` + sp_rename migration dance + `*` read

**Goal:** Add `global_position BIGINT IDENTITY(1,1) NOT NULL` to the SqlServer event_store table. Make `EnsureSchemaAsync` upgrade-aware — handle the gnarly sp_rename + DBCC CHECKIDENT dance because `ALTER COLUMN ... IDENTITY` isn't supported. Add `id.IsGlobal` branch to `ReadAsync`.

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.SqlServer/SqlServerEventStoreAdapter.cs`
- Create: `tests/ZeroAlloc.EventSourcing.SqlServer.Tests/GlobalStreamTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.SqlServer.Tests/MigrationTests.cs`

### Test additions

Mirror Postgres Task 6 — global stream tests + 3 migration tests (fresh, legacy, idempotency).

### Schema migration logic

```sql
-- Fresh install:
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'event_store' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
  CREATE TABLE dbo.event_store (
    stream_id       NVARCHAR(255)    NOT NULL,
    position        BIGINT           NOT NULL,
    global_position BIGINT           IDENTITY(1,1) NOT NULL,
    event_type      NVARCHAR(500)    NOT NULL,
    event_id        UNIQUEIDENTIFIER NOT NULL,
    occurred_at     DATETIMEOFFSET   NOT NULL,
    correlation_id  UNIQUEIDENTIFIER NULL,
    causation_id    UNIQUEIDENTIFIER NULL,
    payload         VARBINARY(MAX)   NOT NULL,
    CONSTRAINT PK_event_store PRIMARY KEY (stream_id, position)
  );
  CREATE INDEX event_store_global_position_idx ON dbo.event_store (global_position);
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.event_store') AND name = 'global_position')
BEGIN
  -- Upgrade path: add column WITHOUT IDENTITY first, backfill, then dance into IDENTITY.

  -- 1. Acquire app-level lock so two starting instances don't race.
  EXEC sp_getapplock @Resource = 'event_store_migration', @LockMode = 'Exclusive', @LockOwner = 'Transaction';

  BEGIN TRANSACTION;

  -- 2. Add backfill column (no IDENTITY).
  ALTER TABLE dbo.event_store ADD global_position_tmp BIGINT NULL;

  -- 3. Backfill with ROW_NUMBER ordered by (occurred_at, stream_id, position).
  WITH ordered AS (
    SELECT stream_id, position,
           ROW_NUMBER() OVER (ORDER BY occurred_at ASC, stream_id ASC, position ASC) AS rn
    FROM dbo.event_store
  )
  UPDATE e SET global_position_tmp = o.rn
  FROM dbo.event_store e
  INNER JOIN ordered o ON e.stream_id = o.stream_id AND e.position = o.position;

  -- 4. Drop the temp column. Create the real IDENTITY column.
  --    SqlServer cannot promote an existing column to IDENTITY in-place, so we use a sp_rename + new column dance.
  ALTER TABLE dbo.event_store DROP COLUMN global_position_tmp;
  ALTER TABLE dbo.event_store ADD global_position BIGINT IDENTITY(1,1) NOT NULL;

  -- 5. Realign the IDENTITY seed to MAX(global_position) so new INSERTs continue cleanly.
  DECLARE @seed BIGINT = (SELECT ISNULL(MAX(global_position), 0) FROM dbo.event_store);
  DBCC CHECKIDENT('dbo.event_store', RESEED, @seed);

  CREATE INDEX event_store_global_position_idx ON dbo.event_store (global_position);

  COMMIT TRANSACTION;
END
```

**Important caveat:** the dance above DROPS THE BACKFILL VALUES and re-IDENTITY-assigns, which means historical events get NEW global_position values that DON'T match the backfill order. **This is wrong.** Need a smarter approach:

**Corrected dance:**

```sql
-- 1. Add new column without IDENTITY, backfill with ROW_NUMBER (preserves chronological order).
ALTER TABLE dbo.event_store ADD global_position BIGINT NULL;

-- 2. Backfill.
WITH ordered AS (...) UPDATE ...;

-- 3. Make NOT NULL.
ALTER TABLE dbo.event_store ALTER COLUMN global_position BIGINT NOT NULL;

-- 4. Future appends need to auto-assign. Since IDENTITY can't be added to existing column,
--    we EITHER use a SEQUENCE+DEFAULT (modern SqlServer 2012+) OR a trigger.
--    SEQUENCE+DEFAULT is cleaner:
CREATE SEQUENCE dbo.event_store_global_position_seq AS BIGINT START WITH ?;
-- ? = MAX(global_position) + 1 — compute dynamically
DECLARE @next BIGINT = (SELECT ISNULL(MAX(global_position), 0) + 1 FROM dbo.event_store);
EXEC ('CREATE SEQUENCE dbo.event_store_global_position_seq AS BIGINT START WITH ' + CAST(@next AS NVARCHAR(20)));
ALTER TABLE dbo.event_store ADD CONSTRAINT DF_event_store_global_position
  DEFAULT (NEXT VALUE FOR dbo.event_store_global_position_seq) FOR global_position;

CREATE INDEX event_store_global_position_idx ON dbo.event_store (global_position);
```

This preserves backfill values AND auto-assigns new ones via SEQUENCE. The fresh-install path still uses `IDENTITY(1,1)` (simpler when the table is empty); the upgrade path uses SEQUENCE+DEFAULT. **Document this divergence** — fresh installs use IDENTITY, upgrades use SEQUENCE+DEFAULT. Behavior is identical from the AppendAsync perspective (INSERT doesn't pass global_position; both mechanisms auto-assign).

### Read branch addition

Same as Postgres — `if (id.IsGlobal)` branch with `ORDER BY global_position`.

### Append behavior

INSERT does NOT pass `global_position` — IDENTITY (fresh) or SEQUENCE DEFAULT (upgraded) auto-assigns. No other change.

**Commit subject:** `feat(sqlserver): global_position auto-assign + upgrade-aware migration + * support`

---

## Task 8 — Outbox tests cleanup + SQLite cross-aggregate integration test

**Goal:** Now that `*` works correctly in InMemory, the placeholder workaround in Outbox's CrossAggregateIntegrationTests can be removed. Also delete the InMemoryEventStoreAdapter TODO breadcrumb (Task 5 of Outbox plan). Add a SQLite-backed cross-aggregate integration test so adopters see the full Outbox + SQLite recipe.

**Files:**
- Modify: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/CrossAggregateIntegrationTests.cs`
- Modify: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryEventStoreAdapter.cs` (remove TODO breadcrumb lines 45-49)
- Modify: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/IdempotencyDemoTests.cs` (the magic-string comment if still relevant)
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/SqliteCrossAggregateIntegrationTests.cs`
- Modify: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/ZeroAlloc.EventSourcing.Outbox.Tests.csproj` (add `ZeroAlloc.EventSourcing.Sqlite` ProjectReference)

### Cleanup work

1. Remove the `TestEventNotNotification` placeholder seeding in `CrossAggregateIntegrationTests.cs` — the InMemory adapter now handles `*` correctly. Verify the test still passes without the placeholder by re-running.
2. Delete the v0.2 TODO comment block at `InMemoryEventStoreAdapter.cs:45-49` (lines from the Outbox Task 5 fix — the bug those lines flagged is fixed in Task 2 of this plan).
3. Optional: if Task 6 (Postgres) and Task 7 (SqlServer) introduce a way to expose the `Conflict` code as a typed constant (`StoreError.ConflictCode`), update `IdempotencyDemoTests.cs` to use it instead of the magic string. If `StoreError` isn't touched in this PR, leave the comment.

### New SQLite cross-aggregate integration test

Mirror `CrossAggregateIntegrationTests.cs`'s shape but use the SQLite adapter via `SqliteEventStoreAdapter`. Demonstrates Outbox + SQLite works end-to-end. ~30 lines.

**Commit subject:** `test(outbox): drop InMemory workaround + add SQLite cross-aggregate integration`

---

## Acceptance criteria (whole PR)

Before opening the PR:

- [ ] `dotnet build -c Release` of the whole repo: 0 errors, 0 warnings
- [ ] `dotnet test` of all affected test projects: all green
  - `ZeroAlloc.EventSourcing.Tests` — StreamId additions
  - `ZeroAlloc.EventSourcing.InMemory.Tests` — global stream + pre-existing
  - `ZeroAlloc.EventSourcing.Sqlite.Tests` — new (~15 facts)
  - `ZeroAlloc.EventSourcing.PostgreSql.Tests` — testcontainer migration + global stream + pre-existing
  - `ZeroAlloc.EventSourcing.SqlServer.Tests` — same shape as Postgres
  - `ZeroAlloc.EventSourcing.Outbox.Tests` — 22 + new SQLite cross-aggregate test
- [ ] `dotnet publish samples/ZeroAlloc.EventSourcing.Sqlite.AotSmoke -c Release -r win-x64 --self-contained`: 0 IL warnings
- [ ] Published Sqlite AOT smoke binary exits 0 with `Sqlite AOT smoke PASS`
- [ ] `dotnet pack` of all 4 affected packages succeeds + READMEs embedded
- [ ] PublicAPI.Unshipped.txt for `.Sqlite` is empty (everything moved to Shipped)
- [ ] release-please-config.json includes the new `.Sqlite` entry
- [ ] Branch: `feat/eventsourcing-sqlite`
- [ ] PR title: `feat: SQLite adapter + * global stream consistency across the ecosystem`

After merge:

- [ ] release-please opens `chore(main): release` PR bumping 4 packages: `.Sqlite v0.1.0` (new), `.InMemory`, `.PostgreSql`, `.SqlServer` (minor bumps for the `*` fix), `ZeroAlloc.EventSourcing` (patch for `StreamId.Global`)

## Out of scope (deferrals)

- LIVE subscription paths via provider-native push (Postgres `LISTEN/NOTIFY`, SqlServer Service Broker, SQLite `data_change_notification_callback`)
- Connection pooling beyond what providers offer natively
- `StreamPosition` strong typing (PerStream vs Global) — v1.0 API freeze candidate
- Cross-version rolling deployment safety — document as scheduled-downtime migration

## Risk

- **SqlServer migration dance** is the gnarliest. The SEQUENCE+DEFAULT approach is provably correct but unfamiliar to operators expecting IDENTITY. README + CHANGELOG must call this out explicitly. Migration test against testcontainer must verify backfill ordering AND subsequent INSERT auto-assignment.
- **Postgres `LOCK TABLE EXCLUSIVE` during migration** blocks all readers for the migration duration. For tables with millions of events, the `ROW_NUMBER` backfill may take minutes. Document the downtime expectation.
- **SQLite `MAX(global_position)` read inside every append transaction** is O(log n) with the index but still a non-zero cost. Add a benchmark to track regression.
- **InMemory checkpoint invalidation**: existing test code or smoke apps using `StreamConsumer("*", ...)` with InMemory will have invalidated checkpoints after upgrade. Documented as deliberate fix-the-bug breaking change in the InMemory CHANGELOG.

## Notes

- The Outbox PR (#185) needs to land BEFORE this PR opens — release-please-friendly cadence. Once Outbox v0.1.0 is on NuGet, this PR can ship with confidence that adopters can `dotnet add package ZeroAlloc.EventSourcing.Outbox` + `dotnet add package ZeroAlloc.EventSourcing.Sqlite` and get a working pair.
- Each task's commit subject matters for release-please routing. The `feat(<pkgscope>):` prefix routes the bump to the right package. Verify the existing `.release-please-manifest.json` post-merge.
