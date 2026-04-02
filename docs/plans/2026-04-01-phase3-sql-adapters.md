# Phase 3: SQL Adapters Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Deliver a PostgreSQL adapter and a SQL Server adapter, each implementing `IEventStoreAdapter` with transactional optimistic concurrency, plus Testcontainers-based integration tests.

**Architecture:** Each adapter is a self-contained class that takes a DB connection factory, acquires a transaction per `AppendAsync` call, uses a locking SELECT to read the current stream version, and then INSERTs events at computed positions. Schema creation is a single `EnsureSchemaAsync()` method (CREATE TABLE IF NOT EXISTS — no migration framework). `SubscribeAsync` throws `NotSupportedException`; subscriptions are Phase 4. Tests use Testcontainers to spin up real database containers.

**Tech Stack:** .NET 9/10 multi-target for libraries, net9.0 for tests. Npgsql 9.x (PostgreSQL driver), Microsoft.Data.SqlClient 5.x (SQL Server driver), Testcontainers.PostgreSql 4.x + Testcontainers.MsSql 4.x for integration tests. xUnit + FluentAssertions. Central Package Management (no `Version` on `<PackageReference>`).

---

## Pre-flight

```bash
cd c:/Projects/Prive/ZeroAlloc.EventSourcing
dotnet test ZeroAlloc.EventSourcing.slnx --framework net9.0  # must be 39/39 green
```

---

### Task 1: Wire project dependencies

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/ZeroAlloc.EventSourcing.PostgreSql/ZeroAlloc.EventSourcing.PostgreSql.csproj`
- Modify: `src/ZeroAlloc.EventSourcing.SqlServer/ZeroAlloc.EventSourcing.SqlServer.csproj`
- Modify: `tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/ZeroAlloc.EventSourcing.PostgreSql.Tests.csproj`
- Modify: `tests/ZeroAlloc.EventSourcing.SqlServer.Tests/ZeroAlloc.EventSourcing.SqlServer.Tests.csproj`

**Step 1: Add package versions to `Directory.Packages.props`**

Add these four entries inside the existing `<ItemGroup>`:

```xml
<PackageVersion Include="Npgsql"                       Version="9.0.2" />
<PackageVersion Include="Microsoft.Data.SqlClient"     Version="5.2.2" />
<PackageVersion Include="Testcontainers.PostgreSql"    Version="4.1.0" />
<PackageVersion Include="Testcontainers.MsSql"         Version="4.1.0" />
```

**Step 2: Update `ZeroAlloc.EventSourcing.PostgreSql.csproj`**

Replace the entire file content with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <Description>PostgreSQL event store implementation for ZeroAlloc.EventSourcing.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ZeroAlloc.EventSourcing\ZeroAlloc.EventSourcing.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Npgsql" />
  </ItemGroup>

</Project>
```

**Step 3: Update `ZeroAlloc.EventSourcing.SqlServer.csproj`**

Replace the entire file content with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <Description>SQL Server event store implementation for ZeroAlloc.EventSourcing.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ZeroAlloc.EventSourcing\ZeroAlloc.EventSourcing.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>

</Project>
```

**Step 4: Update `ZeroAlloc.EventSourcing.PostgreSql.Tests.csproj`**

Replace the entire file content with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Testcontainers.PostgreSql" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.PostgreSql\ZeroAlloc.EventSourcing.PostgreSql.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

**Step 5: Update `ZeroAlloc.EventSourcing.SqlServer.Tests.csproj`**

Replace the entire file content with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Testcontainers.MsSql" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.SqlServer\ZeroAlloc.EventSourcing.SqlServer.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

**Step 6: Restore and verify build**

```bash
dotnet restore ZeroAlloc.EventSourcing.slnx
dotnet build ZeroAlloc.EventSourcing.slnx
```

Expected: 0 errors, 0 warnings.

**Step 7: Commit**

```bash
git add Directory.Packages.props \
        src/ZeroAlloc.EventSourcing.PostgreSql/ \
        src/ZeroAlloc.EventSourcing.SqlServer/ \
        tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/ \
        tests/ZeroAlloc.EventSourcing.SqlServer.Tests/
git commit -m "chore(phase3): wire SQL adapter and test project dependencies"
```

---

### Task 2: PostgreSQL adapter

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.PostgreSql/PostgreSqlEventStoreAdapter.cs`

**Context:**

- `IEventStoreAdapter` is in `ZeroAlloc.EventSourcing` — see `src/ZeroAlloc.EventSourcing/IEventStoreAdapter.cs`
- `RawEvent` has: `Position`, `EventType`, `Payload` (`ReadOnlyMemory<byte>`), `Metadata` (`EventId`, `EventType`, `OccurredAt`, `CorrelationId`, `CausationId`)
- `AppendResult` has: `StreamId`, `NextExpectedVersion`
- `StoreError` factory methods: `StoreError.Conflict(id, expected, actual)`, `StoreError.Unknown(message)`
- `Result<T,E>.Success(value)` / `Result<T,E>.Failure(error)` — from `ZeroAlloc.Results`
- Positions are 1-based. `StreamPosition.Start.Value == 0`. First event in a new stream is at position 1.
- `SubscribeAsync` is Phase 4 — throw `NotSupportedException`.

**Optimistic concurrency strategy:**
1. Begin a transaction.
2. Use `pg_advisory_xact_lock(hashtext(@streamId))` to exclusively lock the logical stream (auto-released at transaction end).
3. `SELECT COALESCE(MAX(position), 0) FROM event_store WHERE stream_id = @streamId` to get the current version.
4. If current != expectedVersion → rollback, return `CONFLICT`.
5. INSERT each event at `expectedVersion.Value + i + 1` (i = 0-based event index).
6. Commit.

**Step 1: Write the failing tests** *(Skip — tests are in Task 3. Implement the adapter directly.)*

**Step 2: Create `PostgreSqlEventStoreAdapter.cs`**

```csharp
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.PostgreSql;

/// <summary>
/// PostgreSQL <see cref="IEventStoreAdapter"/> backed by a single <c>event_store</c> table.
/// Uses advisory transaction locks for optimistic concurrency — no separate streams table required.
/// </summary>
public sealed class PostgreSqlEventStoreAdapter : IEventStoreAdapter
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Initialises the adapter with a pre-built <see cref="NpgsqlDataSource"/>.</summary>
    public PostgreSqlEventStoreAdapter(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Creates the <c>event_store</c> table if it does not already exist.
    /// Call once during application startup or test setup.
    /// </summary>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS event_store (
                stream_id      TEXT         NOT NULL,
                position       BIGINT       NOT NULL,
                event_type     TEXT         NOT NULL,
                event_id       UUID         NOT NULL,
                occurred_at    TIMESTAMPTZ  NOT NULL,
                correlation_id UUID         NULL,
                causation_id   UUID         NULL,
                payload        BYTEA        NOT NULL,
                PRIMARY KEY (stream_id, position)
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        if (events.Length == 0)
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, expectedVersion));

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // Acquire an exclusive advisory lock scoped to this transaction.
            // hashtext() maps the stream_id string to an int4 key; collisions are astronomically rare.
            await using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.Transaction = tx;
                lockCmd.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@streamId))";
                lockCmd.Parameters.AddWithValue("streamId", id.Value);
                await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Read current version (0 if stream does not exist yet).
            long current;
            await using (var versionCmd = conn.CreateCommand())
            {
                versionCmd.Transaction = tx;
                versionCmd.CommandText = "SELECT COALESCE(MAX(position), 0) FROM event_store WHERE stream_id = @streamId";
                versionCmd.Parameters.AddWithValue("streamId", id.Value);
                current = (long)(await versionCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            }

            if (current != expectedVersion.Value)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return Result<AppendResult, StoreError>.Failure(
                    StoreError.Conflict(id, expectedVersion, new StreamPosition(current)));
            }

            // Insert events at successive positions.
            var span = events.Span;
            for (var i = 0; i < span.Length; i++)
            {
                var e = span[i];
                var position = expectedVersion.Value + i + 1;
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO event_store (stream_id, position, event_type, event_id, occurred_at, correlation_id, causation_id, payload)
                    VALUES (@streamId, @position, @eventType, @eventId, @occurredAt, @correlationId, @causationId, @payload)
                    """;
                ins.Parameters.AddWithValue("streamId", id.Value);
                ins.Parameters.AddWithValue("position", position);
                ins.Parameters.AddWithValue("eventType", e.EventType);
                ins.Parameters.AddWithValue("eventId", e.Metadata.EventId);
                ins.Parameters.AddWithValue("occurredAt", e.Metadata.OccurredAt.UtcDateTime);
                ins.Parameters.Add(new NpgsqlParameter("correlationId", NpgsqlDbType.Uuid) { Value = (object?)e.Metadata.CorrelationId ?? DBNull.Value });
                ins.Parameters.Add(new NpgsqlParameter("causationId", NpgsqlDbType.Uuid) { Value = (object?)e.Metadata.CausationId ?? DBNull.Value });
                ins.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea) { Value = e.Payload.ToArray() });
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            var nextVersion = new StreamPosition(expectedVersion.Value + span.Length);
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, nextVersion));
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload
            FROM event_store
            WHERE stream_id = @streamId AND position >= @from
            ORDER BY position ASC
            """;
        cmd.Parameters.AddWithValue("streamId", id.Value);
        cmd.Parameters.AddWithValue("from", from.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var position    = new StreamPosition(reader.GetInt64(0));
            var eventType   = reader.GetString(1);
            var eventId     = reader.GetGuid(2);
            var occurredAt  = new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero);
            var correlationId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
            var causationId   = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
            var payloadBytes  = (byte[])reader.GetValue(6);

            var metadata = new EventMetadata(eventId, eventType, occurredAt, correlationId, causationId);
            yield return new RawEvent(position, eventType, payloadBytes.AsMemory(), metadata);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Subscriptions are implemented in Phase 4.</exception>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => throw new NotSupportedException("Subscriptions are not yet implemented for the PostgreSQL adapter. See Phase 4.");
}
```

**Step 3: Build**

```bash
dotnet build src/ZeroAlloc.EventSourcing.PostgreSql/ZeroAlloc.EventSourcing.PostgreSql.csproj
```

Expected: 0 errors, 0 warnings.

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.PostgreSql/
git commit -m "feat(postgres): implement PostgreSqlEventStoreAdapter with optimistic concurrency"
```

---

### Task 3: PostgreSQL adapter tests

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/PostgreSqlAdapterTests.cs`

**Context:**

- Tests require Docker to be running (Testcontainers spins up a real PostgreSQL container).
- `PostgreSqlBuilder` from `Testcontainers.PostgreSql` creates a container; `.Build()` returns a `PostgreSqlContainer`.
- `container.GetConnectionString()` returns a connection string for the container.
- `NpgsqlDataSource.Create(connectionString)` creates a data source (for test use; prefer `NpgsqlDataSourceBuilder` in production).
- `IAsyncLifetime` (xUnit) provides `InitializeAsync` (container start + schema) and `DisposeAsync` (container stop).
- One `PostgreSqlContainer` per test class — containers are slow to start; don't create one per test.
- For `AppendAsync`, pass `ReadOnlyMemory<RawEvent>`. Use a helper `MakeRaw(string eventType, string payload = "{}") ` that creates a `RawEvent` with a UTF-8-encoded payload.
- `StreamPosition.Start` has `Value == 0`. The first event lands at position 1.

**Step 1: Write the tests**

```csharp
using System.Text;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.PostgreSql;

namespace ZeroAlloc.EventSourcing.PostgreSql.Tests;

public sealed class PostgreSqlAdapterTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().Build();
    private PostgreSqlEventStoreAdapter _adapter = null!;
    private NpgsqlDataSource _dataSource = null!;

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

    private static RawEvent MakeRaw(string eventType, string payload = "{}")
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return new RawEvent(StreamPosition.Start, eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    [Fact]
    public async Task Append_ToNewStream_Succeeds_AndReturnsPosition1()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[] { MakeRaw("OrderPlaced") }.AsMemory();

        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task Append_WithWrongExpectedVersion_ReturnsConflict()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[] { MakeRaw("OrderPlaced") }.AsMemory();

        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        // Second append still uses expectedVersion=0 (stale)
        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Append_MultipleEvents_AssignsConsecutivePositions()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
        }.AsMemory();

        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(2);
    }

    [Fact]
    public async Task Read_AfterAppend_ReturnsEventsInOrder()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
        }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderPlaced");
        read[0].Position.Value.Should().Be(1);
        read[1].EventType.Should().Be("OrderShipped");
        read[1].Position.Value.Should().Be(2);
    }

    [Fact]
    public async Task Read_FromPosition_ReturnsEventsFromThatPositionOnward()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
            MakeRaw("OrderDelivered"),
        }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, new StreamPosition(2)))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderShipped");
        read[1].EventType.Should().Be("OrderDelivered");
    }

    [Fact]
    public async Task Read_NonExistentStream_ReturnsEmpty()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_PreservesPayload()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var payload = """{"orderId":"abc","amount":42}""";
        var events = new[] { MakeRaw("OrderPlaced", payload) }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        RawEvent read = default;
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read = e;

        Encoding.UTF8.GetString(read.Payload.Span).Should().Be(payload);
    }

    [Fact]
    public async Task Append_EmptyEventList_Succeeds_WithUnchangedVersion()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var result = await _adapter.AppendAsync(id, ReadOnlyMemory<RawEvent>.Empty, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(0);
    }

    [Fact]
    public async Task SecondAppend_AfterSuccessfulFirst_Succeeds()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);
        var result = await _adapter.AppendAsync(id, new[] { MakeRaw("OrderShipped") }.AsMemory(), new StreamPosition(1));

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(2);
    }
}
```

**Step 2: Run tests to verify they fail** (no implementation yet — but adapter is already written; tests should pass)

```bash
dotnet test tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/ --framework net9.0 -v n
```

Expected: All tests pass (Docker must be running). If Docker is not available, tests will fail with a container startup error — that is expected in a CI environment without Docker; these are integration tests.

**Step 3: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/
git commit -m "test(postgres): add PostgreSQL adapter integration tests via Testcontainers"
```

---

### Task 4: SQL Server adapter

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.SqlServer/SqlServerEventStoreAdapter.cs`

**Context:**

Same contract as the PostgreSQL adapter (`IEventStoreAdapter`) but uses `Microsoft.Data.SqlClient` and SQL Server DDL:
- `NVARCHAR` instead of `TEXT`
- `UNIQUEIDENTIFIER` instead of `UUID`
- `DATETIMEOFFSET` instead of `TIMESTAMPTZ`
- `VARBINARY(MAX)` instead of `BYTEA`
- Schema guard: `IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'event_store')` instead of `CREATE TABLE IF NOT EXISTS`
- Optimistic concurrency: `WITH (UPDLOCK, HOLDLOCK)` table hint on the `SELECT MAX(position)` query instead of advisory locks.

**`UPDLOCK`** — upgrades shared lock to update lock, preventing another transaction from also taking an update lock on the same rows simultaneously.
**`HOLDLOCK`** — holds the lock until the transaction ends (equivalent to SERIALIZABLE for the locked range), preventing phantom inserts between the SELECT and INSERT.

The adapter takes a `string connectionString`. SQL Server has built-in ADO.NET connection pooling, so no DataSource wrapper is needed.

**Step 1: Create `SqlServerEventStoreAdapter.cs`**

```csharp
using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.SqlServer;

/// <summary>
/// SQL Server <see cref="IEventStoreAdapter"/> backed by a single <c>event_store</c> table.
/// Uses <c>WITH (UPDLOCK, HOLDLOCK)</c> for optimistic concurrency — no separate streams table required.
/// </summary>
public sealed class SqlServerEventStoreAdapter : IEventStoreAdapter
{
    private readonly string _connectionString;

    /// <summary>Initialises the adapter with a SQL Server connection string.</summary>
    public SqlServerEventStoreAdapter(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>
    /// Creates the <c>event_store</c> table if it does not already exist.
    /// Call once during application startup or test setup.
    /// </summary>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables
                WHERE name = 'event_store' AND schema_id = SCHEMA_ID('dbo')
            )
            BEGIN
                CREATE TABLE dbo.event_store (
                    stream_id       NVARCHAR(255)     NOT NULL,
                    position        BIGINT            NOT NULL,
                    event_type      NVARCHAR(500)     NOT NULL,
                    event_id        UNIQUEIDENTIFIER  NOT NULL,
                    occurred_at     DATETIMEOFFSET    NOT NULL,
                    correlation_id  UNIQUEIDENTIFIER  NULL,
                    causation_id    UNIQUEIDENTIFIER  NULL,
                    payload         VARBINARY(MAX)    NOT NULL,
                    CONSTRAINT PK_event_store PRIMARY KEY (stream_id, position)
                )
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        if (events.Length == 0)
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, expectedVersion));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        try
        {
            // UPDLOCK + HOLDLOCK: lock the current max row (or a range lock on an empty stream)
            // and hold it until commit, preventing concurrent appenders from inserting at the same position.
            long current;
            await using (var versionCmd = conn.CreateCommand())
            {
                versionCmd.Transaction = tx;
                versionCmd.CommandText = """
                    SELECT ISNULL(MAX(position), 0)
                    FROM dbo.event_store WITH (UPDLOCK, HOLDLOCK)
                    WHERE stream_id = @streamId
                    """;
                versionCmd.Parameters.AddWithValue("@streamId", id.Value);
                current = (long)(await versionCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            }

            if (current != expectedVersion.Value)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return Result<AppendResult, StoreError>.Failure(
                    StoreError.Conflict(id, expectedVersion, new StreamPosition(current)));
            }

            var span = events.Span;
            for (var i = 0; i < span.Length; i++)
            {
                var e = span[i];
                var position = expectedVersion.Value + i + 1;
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO dbo.event_store
                        (stream_id, position, event_type, event_id, occurred_at, correlation_id, causation_id, payload)
                    VALUES
                        (@streamId, @position, @eventType, @eventId, @occurredAt, @correlationId, @causationId, @payload)
                    """;
                ins.Parameters.AddWithValue("@streamId", id.Value);
                ins.Parameters.AddWithValue("@position", position);
                ins.Parameters.AddWithValue("@eventType", e.EventType);
                ins.Parameters.AddWithValue("@eventId", e.Metadata.EventId);
                ins.Parameters.AddWithValue("@occurredAt", e.Metadata.OccurredAt);
                ins.Parameters.AddWithValue("@correlationId", (object?)e.Metadata.CorrelationId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@causationId", (object?)e.Metadata.CausationId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@payload", e.Payload.ToArray());
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            var nextVersion = new StreamPosition(expectedVersion.Value + span.Length);
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, nextVersion));
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload
            FROM dbo.event_store
            WHERE stream_id = @streamId AND position >= @from
            ORDER BY position ASC
            """;
        cmd.Parameters.AddWithValue("@streamId", id.Value);
        cmd.Parameters.AddWithValue("@from", from.Value);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var position    = new StreamPosition(reader.GetInt64(0));
            var eventType   = reader.GetString(1);
            var eventId     = reader.GetGuid(2);
            var occurredAt  = reader.GetDateTimeOffset(3);
            var correlationId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
            var causationId   = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);

            // With SequentialAccess, columns must be read in order; read payload last.
            var payloadLength = (int)reader.GetBytes(6, 0, null, 0, 0);
            var payloadBytes = new byte[payloadLength];
            reader.GetBytes(6, 0, payloadBytes, 0, payloadLength);

            var metadata = new EventMetadata(eventId, eventType, occurredAt, correlationId, causationId);
            yield return new RawEvent(position, eventType, payloadBytes.AsMemory(), metadata);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Subscriptions are implemented in Phase 4.</exception>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => throw new NotSupportedException("Subscriptions are not yet implemented for the SQL Server adapter. See Phase 4.");
}
```

**Step 2: Build**

```bash
dotnet build src/ZeroAlloc.EventSourcing.SqlServer/ZeroAlloc.EventSourcing.SqlServer.csproj
```

Expected: 0 errors, 0 warnings.

**Step 3: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.SqlServer/
git commit -m "feat(sqlserver): implement SqlServerEventStoreAdapter with optimistic concurrency"
```

---

### Task 5: SQL Server adapter tests

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.SqlServer.Tests/SqlServerAdapterTests.cs`

**Context:**

Mirror of the PostgreSQL tests but using `MsSqlBuilder` from `Testcontainers.MsSql`.
- `MsSqlContainer` exposes `.GetConnectionString()`.
- SQL Server Testcontainers image is `mcr.microsoft.com/mssql/server:2022-latest` and requires ~1.5 GB of RAM — ensure Docker has sufficient memory allocated (4 GB recommended).
- `SqlDataReader.GetDateTimeOffset` is available directly; no conversion needed.

**Step 1: Write the tests**

```csharp
using System.Text;
using FluentAssertions;
using Testcontainers.MsSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

public sealed class SqlServerAdapterTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();
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

    private static RawEvent MakeRaw(string eventType, string payload = "{}")
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return new RawEvent(StreamPosition.Start, eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    [Fact]
    public async Task Append_ToNewStream_Succeeds_AndReturnsPosition1()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[] { MakeRaw("OrderPlaced") }.AsMemory();

        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task Append_WithWrongExpectedVersion_ReturnsConflict()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[] { MakeRaw("OrderPlaced") }.AsMemory();

        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Append_MultipleEvents_AssignsConsecutivePositions()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
        }.AsMemory();

        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(2);
    }

    [Fact]
    public async Task Read_AfterAppend_ReturnsEventsInOrder()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
        }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderPlaced");
        read[0].Position.Value.Should().Be(1);
        read[1].EventType.Should().Be("OrderShipped");
        read[1].Position.Value.Should().Be(2);
    }

    [Fact]
    public async Task Read_FromPosition_ReturnsEventsFromThatPositionOnward()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
            MakeRaw("OrderDelivered"),
        }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, new StreamPosition(2)))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderShipped");
        read[1].EventType.Should().Be("OrderDelivered");
    }

    [Fact]
    public async Task Read_NonExistentStream_ReturnsEmpty()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_PreservesPayload()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var payload = """{"orderId":"abc","amount":42}""";
        var events = new[] { MakeRaw("OrderPlaced", payload) }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        RawEvent read = default;
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read = e;

        Encoding.UTF8.GetString(read.Payload.Span).Should().Be(payload);
    }

    [Fact]
    public async Task Append_EmptyEventList_Succeeds_WithUnchangedVersion()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var result = await _adapter.AppendAsync(id, ReadOnlyMemory<RawEvent>.Empty, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(0);
    }

    [Fact]
    public async Task SecondAppend_AfterSuccessfulFirst_Succeeds()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);
        var result = await _adapter.AppendAsync(id, new[] { MakeRaw("OrderShipped") }.AsMemory(), new StreamPosition(1));

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(2);
    }
}
```

**Step 2: Run tests**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.SqlServer.Tests/ --framework net9.0 -v n
```

Expected: All tests pass (Docker must be running). SQL Server container takes ~20–30 s to start on first use.

**Step 3: Run full test suite to verify no regressions**

```bash
dotnet test ZeroAlloc.EventSourcing.slnx --framework net9.0
```

Expected: All pre-existing tests still pass (39+ green, plus the new SQL adapter tests).

**Step 4: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.SqlServer.Tests/
git commit -m "test(sqlserver): add SQL Server adapter integration tests via Testcontainers"
```

---

## Notes for implementer

- **Central Package Management:** Never add a `Version` attribute to `<PackageReference>`. All versions live in `Directory.Packages.props`.
- **`TreatWarningsAsErrors=true` + `GenerateDocumentationFile=true`** apply to library projects (via `Directory.Build.props`). All `public` and `protected` members must have XML doc comments.
- **`SequentialAccess` in SQL Server `ReadAsync`:** `CommandBehavior.SequentialAccess` is a performance optimization for large BLOBs. Columns **must** be read in ordinal order — payload must always be read last.
- **Npgsql TIMESTAMPTZ → DateTime:** Npgsql returns `TIMESTAMPTZ` columns as `DateTime` with `Kind = Utc`. The adapter wraps this in `new DateTimeOffset(dt, TimeSpan.Zero)`.
- **`e.Payload.ToArray()`:** The SQL drivers require `byte[]`, not `ReadOnlyMemory<byte>`. `.ToArray()` is unavoidable at this layer; it is acceptable in a database I/O context.
- **`pg_advisory_xact_lock` collision risk:** `hashtext()` maps `TEXT` to `INT4` (4 billion values). For typical event store usage (thousands of distinct stream IDs), collisions are negligible. This is a known trade-off documented at the call site.
- **Testcontainers requires Docker:** If Docker is not available, the integration tests will throw at `InitializeAsync`. This is expected and acceptable — these are integration tests, not unit tests.
