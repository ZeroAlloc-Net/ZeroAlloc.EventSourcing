# Phase 1 Completion Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Complete the five half-finished items that block production use: `SqlServerCheckpointStore`, `ISnapshotPolicy`, `IDeadLetterStore` (with SQL implementations + consumer wiring), SQL-backed `IProjectionStore`, and multi-partition Kafka support.

**Architecture:** Each task follows the same pattern as existing implementations — new abstraction in `ZeroAlloc.EventSourcing` core, concrete implementations in the relevant adapter package, contract tests via abstract base class. No cross-task dependencies except Task 3 (DeadLetter) which requires the new `IDeadLetterStore` interface before `StreamConsumer` can be wired.

**Tech Stack:** C# 13, .NET 9, `Microsoft.Data.SqlClient`, `Npgsql`, `Confluent.Kafka`, xUnit, FluentAssertions, Testcontainers

---

## Task 1: SqlServerCheckpointStore

Mirror of `PostgreSqlCheckpointStore` using SQL Server's `MERGE` upsert pattern.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sql/SqlServerCheckpointStore.cs`
- Modify: `tests/ZeroAlloc.EventSourcing.Sql.Tests/ZeroAlloc.EventSourcing.Sql.Tests.csproj` (add Testcontainers.MsSql if not already present)
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerCheckpointStoreTests.cs`

### Step 1: Write the failing integration test

Create `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerCheckpointStoreTests.cs`:

```csharp
using FluentAssertions;
using Testcontainers.MsSql;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection("SqlServer")]
public sealed class SqlServerCheckpointStoreTests : CheckpointStoreContractTests, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private SqlServerCheckpointStore _store = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _store = new SqlServerCheckpointStore(_container.GetConnectionString());
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
    }

    protected override ICheckpointStore CreateStore() => _store;

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        var act = async () => await _store.EnsureSchemaAsync();
        await act.Should().NotThrowAsync();
        await act.Should().NotThrowAsync(); // second call must not throw
    }
}
```

### Step 2: Run the test to confirm it fails

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/ --filter "SqlServerCheckpointStoreTests" -v minimal
```

Expected: FAIL — `SqlServerCheckpointStore` does not exist.

### Step 3: Implement `SqlServerCheckpointStore`

Create `src/ZeroAlloc.EventSourcing.Sql/SqlServerCheckpointStore.cs`:

```csharp
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// SQL Server checkpoint store for persisting consumer positions.
/// Uses atomic MERGE for last-write-wins semantics.
/// </summary>
public sealed class SqlServerCheckpointStore : ICheckpointStore
{
    private readonly string _connectionString;
    private const string TableName = "consumer_checkpoints";

    /// <summary>
    /// Create checkpoint store with a SQL Server connection string.
    /// </summary>
    public SqlServerCheckpointStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>
    /// Create consumer_checkpoints table if it does not already exist.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables
                WHERE name = '{TableName}' AND schema_id = SCHEMA_ID('dbo')
            )
            BEGIN
                CREATE TABLE dbo.{TableName} (
                    consumer_id  VARCHAR(256)    NOT NULL,
                    position     BIGINT          NOT NULL,
                    updated_at   DATETIME2       NOT NULL,
                    CONSTRAINT PK_{TableName} PRIMARY KEY (consumer_id)
                )
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"SELECT position FROM dbo.{TableName} WHERE consumer_id = @consumer_id";
        cmd.Parameters.AddWithValue("@consumer_id", consumerId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null && result != DBNull.Value
            ? new StreamPosition((long)result)
            : null;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string consumerId, StreamPosition position, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"""
            MERGE INTO dbo.{TableName} AS target
            USING (SELECT @consumer_id AS consumer_id) AS source
            ON target.consumer_id = source.consumer_id
            WHEN MATCHED THEN UPDATE SET
                position   = @position,
                updated_at = @updated_at
            WHEN NOT MATCHED THEN INSERT (consumer_id, position, updated_at)
                VALUES (@consumer_id, @position, @updated_at);
            """;
        cmd.Parameters.AddWithValue("@consumer_id", consumerId);
        cmd.Parameters.AddWithValue("@position", position.Value);
        cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string consumerId, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"DELETE FROM dbo.{TableName} WHERE consumer_id = @consumer_id";
        cmd.Parameters.AddWithValue("@consumer_id", consumerId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void ValidateConsumerId(string consumerId)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));
        if (consumerId.Length > 256)
            throw new ArgumentException("Consumer ID cannot exceed 256 characters", nameof(consumerId));
    }
}
```

### Step 4: Run tests to verify they pass

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/ --filter "SqlServerCheckpointStoreTests" -v minimal
```

Expected: All tests PASS.

### Step 5: Commit

```bash
git add src/ZeroAlloc.EventSourcing.Sql/SqlServerCheckpointStore.cs \
        tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerCheckpointStoreTests.cs
git commit -m "feat: add SqlServerCheckpointStore for SQL Server consumer position tracking"
```

---

## Task 2: ISnapshotPolicy

A pluggable policy abstraction controlling when `SnapshotCachingRepositoryDecorator` writes snapshots. Currently callers manage this manually.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/ISnapshotPolicy.cs`
- Create: `src/ZeroAlloc.EventSourcing/SnapshotPolicies.cs`
- Modify: `src/ZeroAlloc.EventSourcing.Aggregates/SnapshotCachingRepositoryDecorator.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Tests/SnapshotPolicyTests.cs`

### Step 1: Write the failing unit tests

Create `tests/ZeroAlloc.EventSourcing.Tests/SnapshotPolicyTests.cs`:

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class SnapshotPolicyTests
{
    [Theory]
    [InlineData(10, null, 10, true)]    // No previous snapshot, 10 events → snapshot at 10
    [InlineData(10, 5, 15, true)]       // Last at 5, now at 15 → 10 events since → snapshot
    [InlineData(10, 5, 14, false)]      // Last at 5, now at 14 → only 9 events since → no snapshot
    [InlineData(10, 10, 19, false)]     // Last at 10, now at 19 → 9 events since → no snapshot
    [InlineData(10, 10, 20, true)]      // Last at 10, now at 20 → exactly 10 → snapshot
    public void EveryNEvents_ShouldSnapshot_ReturnsExpected(
        int n, long? lastSnapshot, long current, bool expected)
    {
        var policy = SnapshotPolicy.EveryNEvents(n);
        var last = lastSnapshot.HasValue ? new StreamPosition(lastSnapshot.Value) : (StreamPosition?)null;
        policy.ShouldSnapshot(new StreamPosition(current), last).Should().Be(expected);
    }

    [Fact]
    public void Always_ShouldSnapshot_AlwaysTrue()
    {
        var policy = SnapshotPolicy.Always;
        policy.ShouldSnapshot(new StreamPosition(1), null).Should().BeTrue();
        policy.ShouldSnapshot(new StreamPosition(100), new StreamPosition(99)).Should().BeTrue();
    }

    [Fact]
    public void Never_ShouldSnapshot_AlwaysFalse()
    {
        var policy = SnapshotPolicy.Never;
        policy.ShouldSnapshot(new StreamPosition(1), null).Should().BeFalse();
        policy.ShouldSnapshot(new StreamPosition(1000), new StreamPosition(1)).Should().BeFalse();
    }

    [Fact]
    public void EveryNEvents_NLessThanOne_Throws()
    {
        var act = () => SnapshotPolicy.EveryNEvents(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

### Step 2: Run to verify failure

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "SnapshotPolicyTests" -v minimal
```

Expected: FAIL — `ISnapshotPolicy`, `SnapshotPolicy` do not exist.

### Step 3: Define the interface

Create `src/ZeroAlloc.EventSourcing/ISnapshotPolicy.cs`:

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Controls when the snapshot caching repository should persist a new snapshot.
/// </summary>
public interface ISnapshotPolicy
{
    /// <summary>
    /// Returns true if a snapshot should be written at the current position.
    /// </summary>
    /// <param name="currentPosition">The aggregate's current stream position after saving.</param>
    /// <param name="lastSnapshotPosition">The position of the last written snapshot, or null if none.</param>
    bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition);
}
```

### Step 4: Implement the built-in policies

Create `src/ZeroAlloc.EventSourcing/SnapshotPolicies.cs`:

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Factory for built-in <see cref="ISnapshotPolicy"/> implementations.
/// </summary>
public static class SnapshotPolicy
{
    /// <summary>Write a snapshot every time an aggregate is saved.</summary>
    public static ISnapshotPolicy Always { get; } = new AlwaysPolicy();

    /// <summary>Never write automatic snapshots (manual only).</summary>
    public static ISnapshotPolicy Never { get; } = new NeverPolicy();

    /// <summary>
    /// Write a snapshot when at least <paramref name="n"/> events have been appended
    /// since the last snapshot (or since stream start if no snapshot exists).
    /// </summary>
    /// <param name="n">Minimum number of events between snapshots. Must be ≥ 1.</param>
    public static ISnapshotPolicy EveryNEvents(int n)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n), "Snapshot interval must be at least 1");
        return new EveryNEventsPolicy(n);
    }

    private sealed class AlwaysPolicy : ISnapshotPolicy
    {
        public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition)
            => true;
    }

    private sealed class NeverPolicy : ISnapshotPolicy
    {
        public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition)
            => false;
    }

    private sealed class EveryNEventsPolicy : ISnapshotPolicy
    {
        private readonly int _n;
        public EveryNEventsPolicy(int n) => _n = n;

        public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition)
        {
            var baseline = lastSnapshotPosition?.Value ?? 0L;
            return (currentPosition.Value - baseline) >= _n;
        }
    }
}
```

### Step 5: Run tests to verify they pass

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "SnapshotPolicyTests" -v minimal
```

Expected: All tests PASS.

### Step 6: Wire `ISnapshotPolicy` into `SnapshotCachingRepositoryDecorator`

Modify `src/ZeroAlloc.EventSourcing.Aggregates/SnapshotCachingRepositoryDecorator.cs`:

Add a `_snapshotPolicy` field and an optional constructor parameter (default: `SnapshotPolicy.Never` to preserve existing behaviour). Then in `SaveAsync`, after delegating to the inner repository, read the current snapshot position and call `_snapshotPolicy.ShouldSnapshot(...)` before writing:

```csharp
// Add field:
private readonly ISnapshotPolicy _snapshotPolicy;
private readonly Func<TAggregate, TState> _extractState;

// Add to constructor parameters (after aggregateFactory):
ISnapshotPolicy? snapshotPolicy = null,
Func<TAggregate, TState>? extractState = null

// Assign in constructor:
_snapshotPolicy = snapshotPolicy ?? SnapshotPolicy.Never;
_extractState = extractState ?? throw new ArgumentNullException(nameof(extractState),
    "extractState is required when a snapshotPolicy other than Never is used");

// Replace SaveAsync body:
public override async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default)
{
    var result = await _innerRepository.SaveAsync(aggregate, id, ct).ConfigureAwait(false);
    if (!result.IsSuccess) return result;

    var streamId = _streamIdFactory(id);
    var snapshot = await _snapshotStore.ReadAsync(streamId, ct).ConfigureAwait(false);
    var lastPosition = snapshot?.Position;

    if (_snapshotPolicy.ShouldSnapshot(result.Value.NextExpectedVersion, lastPosition))
    {
        var state = _extractState(aggregate);
        await _snapshotStore.WriteAsync(streamId, result.Value.NextExpectedVersion, state, ct).ConfigureAwait(false);
    }

    return result;
}
```

> Note: `extractState` is only required if `snapshotPolicy != Never`. You can guard this in the constructor.

### Step 7: Run the full aggregates test suite

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ -v minimal
```

Expected: All existing tests PASS (no regressions — default is `Never`).

### Step 8: Commit

```bash
git add src/ZeroAlloc.EventSourcing/ISnapshotPolicy.cs \
        src/ZeroAlloc.EventSourcing/SnapshotPolicies.cs \
        src/ZeroAlloc.EventSourcing.Aggregates/SnapshotCachingRepositoryDecorator.cs \
        tests/ZeroAlloc.EventSourcing.Tests/SnapshotPolicyTests.cs
git commit -m "feat: add ISnapshotPolicy with EveryNEvents, Always, Never implementations"
```

---

## Task 3: IDeadLetterStore + StreamConsumer wiring

Defines the dead-letter abstraction, provides InMemory and SQL implementations, and replaces the `NotSupportedException` in `StreamConsumer`.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/IDeadLetterStore.cs`
- Create: `src/ZeroAlloc.EventSourcing/DeadLetterEntry.cs`
- Create: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryDeadLetterStore.cs`
- Modify: `src/ZeroAlloc.EventSourcing/StreamConsumer.cs` (wire DeadLetter path)
- Create: `src/ZeroAlloc.EventSourcing.Sql/PostgreSqlDeadLetterStore.cs`
- Create: `src/ZeroAlloc.EventSourcing.Sql/SqlServerDeadLetterStore.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Tests/DeadLetterStoreContractTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerDeadLetterTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlDeadLetterStoreTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerDeadLetterStoreTests.cs`

### Step 1: Define `DeadLetterEntry` and `IDeadLetterStore`

Create `src/ZeroAlloc.EventSourcing/DeadLetterEntry.cs`:

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Represents a single failed event that was routed to the dead-letter store.
/// </summary>
public sealed record DeadLetterEntry(
    EventEnvelope Envelope,
    string ConsumerId,
    string ExceptionType,
    string ExceptionMessage,
    DateTimeOffset FailedAt);
```

Create `src/ZeroAlloc.EventSourcing/IDeadLetterStore.cs`:

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Stores events that have permanently failed processing after all retries are exhausted.
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>
    /// Persist a failed event with its exception details.
    /// </summary>
    ValueTask WriteAsync(string consumerId, EventEnvelope envelope, Exception exception, CancellationToken ct = default);

    /// <summary>
    /// Read all dead-letter entries. Primarily for monitoring and manual replay.
    /// </summary>
    IAsyncEnumerable<DeadLetterEntry> ReadAllAsync(CancellationToken ct = default);
}
```

### Step 2: Write contract tests for `IDeadLetterStore`

Create `tests/ZeroAlloc.EventSourcing.Tests/DeadLetterStoreContractTests.cs`:

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public abstract class DeadLetterStoreContractTests
{
    protected abstract IDeadLetterStore CreateStore();

    private static EventEnvelope MakeEnvelope() =>
        new(new StreamId("test-stream"), new StreamPosition(1), new object(),
            new EventMetadata(Guid.NewGuid(), "TestEvent", DateTimeOffset.UtcNow, null, null));

    [Fact]
    public async Task ReadAllAsync_Empty_ReturnsNothing()
    {
        var store = CreateStore();
        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_SingleEntry_CanBeRead()
    {
        var store = CreateStore();
        var envelope = MakeEnvelope();
        var ex = new InvalidOperationException("boom");

        await store.WriteAsync("consumer-1", envelope, ex);

        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);

        results.Should().HaveCount(1);
        results[0].ConsumerId.Should().Be("consumer-1");
        results[0].ExceptionType.Should().Be(nameof(InvalidOperationException));
        results[0].ExceptionMessage.Should().Be("boom");
        results[0].Envelope.Metadata.EventId.Should().Be(envelope.Metadata.EventId);
    }

    [Fact]
    public async Task WriteAsync_MultipleEntries_AllReadBack()
    {
        var store = CreateStore();
        await store.WriteAsync("c1", MakeEnvelope(), new Exception("e1"));
        await store.WriteAsync("c2", MakeEnvelope(), new Exception("e2"));

        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);

        results.Should().HaveCount(2);
    }
}
```

### Step 3: Run to confirm failure

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "DeadLetterStoreContractTests" -v minimal
```

Expected: FAIL — types do not exist yet.

### Step 4: Implement `InMemoryDeadLetterStore`

Create `src/ZeroAlloc.EventSourcing.InMemory/InMemoryDeadLetterStore.cs`:

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// In-memory dead-letter store for testing. Not suitable for production.
/// </summary>
public sealed class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly ConcurrentQueue<DeadLetterEntry> _entries = new();

    /// <inheritdoc/>
    public ValueTask WriteAsync(string consumerId, EventEnvelope envelope, Exception exception, CancellationToken ct = default)
    {
        _entries.Enqueue(new DeadLetterEntry(
            envelope,
            consumerId,
            exception.GetType().Name,
            exception.Message,
            DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DeadLetterEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in _entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
```

### Step 5: Run contract tests against InMemoryDeadLetterStore

Add a concrete contract test class in the InMemory tests project to run the contracts. Then:

```bash
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/ -v minimal
```

Expected: Contract tests PASS.

### Step 6: Wire `IDeadLetterStore` into `StreamConsumer`

Modify `src/ZeroAlloc.EventSourcing/StreamConsumer.cs`:

Add an optional `IDeadLetterStore? _deadLetterStore` field and constructor parameter. Replace the `NotSupportedException` in `ProcessEventWithRetryAsync`:

```csharp
// Constructor addition:
public StreamConsumer(
    IEventStore eventStore,
    ICheckpointStore checkpointStore,
    string consumerId,
    StreamConsumerOptions? options = null,
    StreamId? streamId = null,
    IDeadLetterStore? deadLetterStore = null)   // ← add this
{
    // existing validation...
    _deadLetterStore = deadLetterStore;
}

// Replace DeadLetter case:
case ErrorHandlingStrategy.DeadLetter:
    if (_deadLetterStore == null)
        throw new InvalidOperationException(
            "ErrorHandlingStrategy.DeadLetter requires a dead-letter store. " +
            "Pass an IDeadLetterStore to the StreamConsumer constructor.");
    await _deadLetterStore.WriteAsync(ConsumerId, envelope, ex, cancellationToken).ConfigureAwait(false);
    return; // skip event, continue consuming
```

### Step 7: Write consumer dead-letter test

Create `tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerDeadLetterTests.cs`:

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerDeadLetterTests
{
    [Fact]
    public async Task ConsumeAsync_WithDeadLetterStrategy_RoutesFailedEventToStore()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = new TestEventSerializer();   // use existing test serializer
        var registry = new TestEventTypeRegistry();   // use existing test registry
        var eventStore = new EventStore(adapter, serializer, registry);
        var checkpoint = new InMemoryCheckpointStore();
        var deadLetter = new InMemoryDeadLetterStore();

        var streamId = new StreamId("orders");
        await eventStore.AppendAsync(streamId, new[] { new TestEvent("e1") }, StreamPosition.Start);

        var consumer = new StreamConsumer(
            eventStore, checkpoint, "test-consumer",
            new StreamConsumerOptions
            {
                MaxRetries = 0,
                ErrorStrategy = ErrorHandlingStrategy.DeadLetter,
                CommitStrategy = CommitStrategy.AfterEvent
            },
            streamId,
            deadLetterStore: deadLetter);

        await consumer.ConsumeAsync((_, _) => throw new InvalidOperationException("poison"));

        var entries = new List<DeadLetterEntry>();
        await foreach (var e in deadLetter.ReadAllAsync())
            entries.Add(e);

        entries.Should().HaveCount(1);
        entries[0].ExceptionMessage.Should().Be("poison");
        entries[0].ConsumerId.Should().Be("test-consumer");
    }

    [Fact]
    public async Task ConsumeAsync_DeadLetterWithoutStore_Throws()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var checkpoint = new InMemoryCheckpointStore();

        var consumer = new StreamConsumer(
            eventStore, checkpoint, "test-consumer",
            new StreamConsumerOptions { MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.DeadLetter });

        var act = async () => await consumer.ConsumeAsync((_, _) => throw new Exception("oops"));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*dead-letter store*");
    }
}
```

### Step 8: Run all consumer tests

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "DeadLetter" -v minimal
```

Expected: All PASS.

### Step 9: Implement `PostgreSqlDeadLetterStore`

Create `src/ZeroAlloc.EventSourcing.Sql/PostgreSqlDeadLetterStore.cs`:

Schema: `dead_letters(id BIGSERIAL PK, consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at)`

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL implementation of <see cref="IDeadLetterStore"/>.
/// </summary>
public sealed class PostgreSqlDeadLetterStore : IDeadLetterStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEventSerializer _serializer;

    public PostgreSqlDeadLetterStore(NpgsqlDataSource dataSource, IEventSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(serializer);
        _dataSource = dataSource;
        _serializer = serializer;
    }

    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        #pragma warning disable MA0004
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS dead_letters (
                id               BIGSERIAL       PRIMARY KEY,
                consumer_id      VARCHAR(256)    NOT NULL,
                stream_id        VARCHAR(255)    NOT NULL,
                position         BIGINT          NOT NULL,
                event_type       VARCHAR(500)    NOT NULL,
                payload          BYTEA           NOT NULL,
                exception_type   VARCHAR(500)    NOT NULL,
                exception_message TEXT           NOT NULL,
                failed_at        TIMESTAMPTZ     NOT NULL
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string consumerId, EventEnvelope envelope, Exception exception, CancellationToken ct = default)
    {
        var payload = _serializer.Serialize(envelope.Event);

        #pragma warning disable MA0004
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dead_letters
                (consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at)
            VALUES
                (@consumer_id, @stream_id, @position, @event_type, @payload, @exception_type, @exception_message, @failed_at)
            """;
        cmd.Parameters.AddWithValue("@consumer_id", consumerId);
        cmd.Parameters.AddWithValue("@stream_id", envelope.StreamId.Value);
        cmd.Parameters.AddWithValue("@position", envelope.Position.Value);
        cmd.Parameters.AddWithValue("@event_type", envelope.Metadata.EventType);
        cmd.Parameters.Add("@payload", NpgsqlDbType.Bytea).Value = payload.ToArray();
        cmd.Parameters.AddWithValue("@exception_type", exception.GetType().Name);
        cmd.Parameters.AddWithValue("@exception_message", exception.Message);
        cmd.Parameters.AddWithValue("@failed_at", DateTimeOffset.UtcNow);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DeadLetterEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        #pragma warning disable MA0004
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at FROM dead_letters ORDER BY id";

        #pragma warning disable MA0004
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var streamId = new StreamId(reader.GetString(1));
            var position = new StreamPosition(reader.GetInt64(2));
            var eventType = reader.GetString(3);
            var payload = (byte[])reader.GetValue(4);
            var metadata = new EventMetadata(Guid.NewGuid(), eventType, DateTimeOffset.UtcNow, null, null);
            var envelope = new EventEnvelope(streamId, position, payload, metadata);

            yield return new DeadLetterEntry(
                envelope,
                reader.GetString(0),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7));
        }
    }
}
```

Implement `SqlServerDeadLetterStore` using the same pattern with `SqlConnection` and `IDENTITY` column instead of `BIGSERIAL`.

### Step 10: Run integration tests for SQL dead-letter stores

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/ --filter "DeadLetter" -v minimal
```

Expected: All PASS.

### Step 11: Commit

```bash
git add src/ tests/
git commit -m "feat: implement IDeadLetterStore with InMemory/PostgreSql/SqlServer and wire into StreamConsumer"
```

---

## Task 4: SQL-backed IProjectionStore

`IProjectionStore` stores and loads string-serialized read models. Follows the same upsert pattern as snapshot stores. Stores a `projection_states` table keyed by projection name.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sql/PostgreSqlProjectionStore.cs`
- Create: `src/ZeroAlloc.EventSourcing.Sql/SqlServerProjectionStore.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Tests/ProjectionStoreContractTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlProjectionStoreTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerProjectionStoreTests.cs`

### Step 1: Write contract tests

Create `tests/ZeroAlloc.EventSourcing.Tests/ProjectionStoreContractTests.cs`:

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public abstract class ProjectionStoreContractTests
{
    protected abstract IProjectionStore CreateStore();

    [Fact]
    public async Task Load_NonExistentKey_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.LoadAsync("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", """{"count":5}""");
        var result = await store.LoadAsync("orders");
        result.Should().Be("""{"count":5}""");
    }

    [Fact]
    public async Task Save_Overwrites_ExistingState()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", """{"count":1}""");
        await store.SaveAsync("orders", """{"count":2}""");
        var result = await store.LoadAsync("orders");
        result.Should().Be("""{"count":2}""");
    }

    [Fact]
    public async Task Clear_RemovesState()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", """{"count":1}""");
        await store.ClearAsync("orders");
        var result = await store.LoadAsync("orders");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Clear_NonExistentKey_DoesNotThrow()
    {
        var store = CreateStore();
        var act = async () => await store.ClearAsync("nonexistent");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MultipleKeys_AreIndependent()
    {
        var store = CreateStore();
        await store.SaveAsync("a", "state-a");
        await store.SaveAsync("b", "state-b");
        (await store.LoadAsync("a")).Should().Be("state-a");
        (await store.LoadAsync("b")).Should().Be("state-b");
    }
}
```

### Step 2: Run to verify failure

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "ProjectionStoreContractTests" -v minimal
```

Expected: FAIL (no SQL implementations to test).

### Step 3: Implement `PostgreSqlProjectionStore`

Create `src/ZeroAlloc.EventSourcing.Sql/PostgreSqlProjectionStore.cs`:

```csharp
using Npgsql;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL implementation of <see cref="IProjectionStore"/>.
/// Stores projection state as text with atomic upsert.
/// </summary>
public sealed class PostgreSqlProjectionStore : IProjectionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlProjectionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        #pragma warning disable MA0004
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS projection_states (
                projection_key  VARCHAR(256)  NOT NULL,
                state           TEXT          NOT NULL,
                updated_at      TIMESTAMPTZ   NOT NULL,
                CONSTRAINT PK_projection_states PRIMARY KEY (projection_key)
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask SaveAsync(string key, string state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        #pragma warning disable MA0004
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projection_states (projection_key, state, updated_at)
            VALUES (@key, @state, CURRENT_TIMESTAMP)
            ON CONFLICT (projection_key) DO UPDATE SET
                state      = EXCLUDED.state,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@state", state);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<string?> LoadAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        #pragma warning disable MA0004
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state FROM projection_states WHERE projection_key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    /// <inheritdoc/>
    public async ValueTask ClearAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        #pragma warning disable MA0004
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM projection_states WHERE projection_key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
```

Implement `SqlServerProjectionStore` identically but with `SqlConnection` and `MERGE` upsert.

### Step 4: Write integration test classes

Create `tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlProjectionStoreTests.cs` — extends `ProjectionStoreContractTests`, spins up a `PostgreSqlContainer`, calls `EnsureSchemaAsync`.

Create `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerProjectionStoreTests.cs` — same but with `MsSqlContainer`.

### Step 5: Run all projection store tests

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/ --filter "ProjectionStore" -v minimal
```

Expected: All PASS.

### Step 6: Commit

```bash
git add src/ZeroAlloc.EventSourcing.Sql/ tests/
git commit -m "feat: add PostgreSqlProjectionStore and SqlServerProjectionStore"
```

---

## Task 5: Multi-partition Kafka support

Changes `KafkaConsumerOptions.Partition` from `int` to `int[]`, updates `KafkaStreamConsumer` to assign all requested partitions, and keys checkpoints per `consumer_id + partition`.

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.Kafka/KafkaConsumerOptions.cs`
- Modify: `src/ZeroAlloc.EventSourcing.Kafka/KafkaStreamConsumer.cs`
- Modify: `tests/ZeroAlloc.EventSourcing.Kafka.Tests/KafkaStreamConsumerTests.cs`

> This is the most complex task. Read `KafkaStreamConsumer.cs` in full before starting. The key change is replacing `consumer.Assign(new TopicPartition(topic, partition))` with `consumer.Assign(partitions.Select(p => new TopicPartition(topic, p)))`.

### Step 1: Read `KafkaStreamConsumer.cs` fully

```bash
cat src/ZeroAlloc.EventSourcing.Kafka/KafkaStreamConsumer.cs
```

Note how `Partition` is used — specifically in `Assign()` and checkpoint key construction.

### Step 2: Write failing unit tests for multi-partition config

Add to `tests/ZeroAlloc.EventSourcing.Kafka.Tests/KafkaConsumerOptionsTests.cs`:

```csharp
[Fact]
public void KafkaConsumerOptions_DefaultPartitions_IsZeroOnly()
{
    var opts = new KafkaConsumerOptions
    {
        BootstrapServers = "localhost:9092",
        Topic = "events",
        GroupId = "g1"
    };
    opts.Partitions.Should().Equal(0);
}

[Fact]
public void KafkaConsumerOptions_SetMultiplePartitions_Accepted()
{
    var opts = new KafkaConsumerOptions
    {
        BootstrapServers = "localhost:9092",
        Topic = "events",
        GroupId = "g1",
        Partitions = new[] { 0, 1, 2 }
    };
    opts.Partitions.Should().Equal(0, 1, 2);
}
```

### Step 3: Update `KafkaConsumerOptions`

Modify `src/ZeroAlloc.EventSourcing.Kafka/KafkaConsumerOptions.cs`:

- Rename `Partition` to `Partitions`
- Change type from `int` to `int[]`
- Default: `new[] { 0 }` (preserves single-partition behaviour)

```csharp
/// <summary>
/// Kafka partitions to consume from. Defaults to partition 0 only.
/// </summary>
public int[] Partitions { get; set; } = [0];
```

### Step 4: Update `KafkaStreamConsumer` to assign all partitions

In `KafkaStreamConsumer.cs`, find the `Assign` call and update to:

```csharp
var topicPartitions = _options.Partitions
    .Select(p => new TopicPartition(_options.Topic, new Partition(p)))
    .ToList();
_consumer.Assign(topicPartitions);
```

For checkpoint keys, change from `ConsumerId` to `$"{ConsumerId}:p{partition}"` per-partition, so each partition tracks its own position independently:

```csharp
private string CheckpointKey(int partition) => $"{ConsumerId}:p{partition}";
```

The consumer loop must then read/write checkpoint per partition in the `ConsumeResult`.

### Step 5: Run all Kafka tests

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Kafka.Tests/ -v minimal
```

Expected: All PASS. The single-partition behaviour is preserved by the `[0]` default.

### Step 6: Commit

```bash
git add src/ZeroAlloc.EventSourcing.Kafka/ tests/ZeroAlloc.EventSourcing.Kafka.Tests/
git commit -m "feat: add multi-partition Kafka support via Partitions int[] in KafkaConsumerOptions"
```

---

## Completion Checklist

- [ ] Task 1: `SqlServerCheckpointStore` — all contract tests pass with real SQL Server
- [ ] Task 2: `ISnapshotPolicy` — unit tests pass; `SnapshotCachingRepositoryDecorator` wired; no regressions
- [ ] Task 3: `IDeadLetterStore` — contract tests, consumer wiring, SQL integration tests all pass
- [ ] Task 4: SQL `IProjectionStore` — contract tests pass with PostgreSQL and SQL Server
- [ ] Task 5: Multi-partition Kafka — existing tests pass; multi-partition config accepted; checkpoint keyed per partition

Run the full suite before pushing:

```bash
dotnet build ZeroAlloc.EventSourcing.sln
dotnet test ZeroAlloc.EventSourcing.sln -v minimal
```
