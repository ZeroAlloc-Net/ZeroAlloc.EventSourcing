# Phase 4: Subscriptions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement catch-up + live subscriptions for the PostgreSQL and SQL Server adapters, replacing the Phase 3 `NotSupportedException` stubs with a working `SubscribeAsync` that replays historical events then forwards new ones as they are appended.

**Architecture:** Each SQL adapter gets a `PollingEventSubscription` class. When `StartAsync()` is called, a background loop reads events from the adapter starting at `from`, delivers them to the handler, records the last seen position, then polls for new events on a configurable interval (default 500 ms). Live delivery is poll-based (no DB triggers or LISTEN/NOTIFY) — simple, portable, and sufficient for Phase 4. Phase 5 can replace polling with LISTEN/NOTIFY for PostgreSQL if needed. The InMemory adapter already has working subscriptions (via `ZeroAlloc.AsyncEvents` broadcast); it is not changed in this phase.

**Tech Stack:** .NET 9/10 multi-target for libraries, net9.0 for tests. `System.Threading.PeriodicTimer` (no external deps), existing `IEventStoreAdapter.ReadAsync`. Tests use Testcontainers (same containers as Phase 3). xUnit + FluentAssertions.

---

## Pre-flight

```bash
cd c:/Projects/Prive/ZeroAlloc.EventSourcing
dotnet test ZeroAlloc.EventSourcing.slnx --framework net9.0  # must be green (39+ tests)
```

---

### Task 1: Shared `PollingEventSubscription` base class

**Context:**

Both SQL adapters need the same catch-up + poll loop. Rather than duplicating it, we extract it into a shared internal class in the **core** project (`ZeroAlloc.EventSourcing`) since it depends only on `IEventStoreAdapter` and the core types. Alternatively, it can live in each adapter — but sharing avoids duplication.

The cleanest home is a new file in `src/ZeroAlloc.EventSourcing/` because `IEventStoreAdapter` and `RawEvent` live there and both SQL adapters reference the core project already.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/PollingEventSubscription.cs`

**What the class does:**
1. Constructor takes `IEventStoreAdapter adapter`, `StreamId id`, `StreamPosition from`, `Func<RawEvent, CancellationToken, ValueTask> handler`, and `TimeSpan pollInterval`.
2. `IsRunning` returns true after `StartAsync` and before `DisposeAsync`.
3. `StartAsync` launches a background `Task` via `Task.Run` that:
   a. Reads all events from `from` via `adapter.ReadAsync`, delivers each to `handler`, tracks `_nextPosition`.
   b. Enters a `PeriodicTimer` loop, each tick reads events from `_nextPosition` onward, delivers them, advances `_nextPosition`.
   c. Loop exits when the `CancellationTokenSource` linked to disposal is cancelled.
4. `DisposeAsync` cancels the `CancellationTokenSource`, awaits the background task (with a `try/catch` for `OperationCanceledException`), then disposes the `CancellationTokenSource`.
5. Exceptions from the handler are NOT swallowed — they propagate via the background task and surface on the next `DisposeAsync` await. The background task stores the first non-cancellation exception and re-throws on dispose.

**Step 1: Create `src/ZeroAlloc.EventSourcing/PollingEventSubscription.cs`**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// A catch-up + polling <see cref="IEventSubscription"/> for adapters that do not support
/// push-based delivery (e.g. SQL adapters). On <see cref="StartAsync"/>, replays all events
/// from <c>from</c> then polls at <see cref="DefaultPollInterval"/> until disposed.
/// </summary>
internal sealed class PollingEventSubscription : IEventSubscription
{
    /// <summary>Default interval between poll cycles.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IEventStoreAdapter _adapter;
    private readonly StreamId _id;
    private readonly Func<RawEvent, CancellationToken, ValueTask> _handler;
    private readonly TimeSpan _pollInterval;
    private StreamPosition _nextPosition;
    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;
    private volatile bool _running;

    internal PollingEventSubscription(
        IEventStoreAdapter adapter,
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        TimeSpan pollInterval)
    {
        _adapter = adapter;
        _id = id;
        _nextPosition = from;
        _handler = handler;
        _pollInterval = pollInterval;
    }

    /// <inheritdoc/>
    public bool IsRunning => _running;

    /// <inheritdoc/>
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        _running = true;
        _backgroundTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Catch-up: deliver all events from _nextPosition onward.
            await DeliverNewEventsAsync(ct).ConfigureAwait(false);

            // Live: poll on interval until cancelled.
            using var timer = new PeriodicTimer(_pollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await DeliverNewEventsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — swallow.
        }
    }

    private async Task DeliverNewEventsAsync(CancellationToken ct)
    {
        await foreach (var e in _adapter.ReadAsync(_id, _nextPosition, ct).ConfigureAwait(false))
        {
            await _handler(e, ct).ConfigureAwait(false);
            _nextPosition = new StreamPosition(e.Position.Value + 1);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_running) return;
        _running = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_backgroundTask is not null)
        {
            try { await _backgroundTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }
        _cts.Dispose();
    }
}
```

**Step 2: Build**

```bash
dotnet build src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj
```

Expected: 0 errors, 0 warnings.

**Step 3: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/
git commit -m "feat(subscriptions): add PollingEventSubscription for catch-up + poll-based delivery"
```

---

### Task 2: Wire `SubscribeAsync` in the PostgreSQL adapter

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.PostgreSql/PostgreSqlEventStoreAdapter.cs` (replace the `NotSupportedException` stub)

**Context:**

The current `SubscribeAsync` at the bottom of `PostgreSqlEventStoreAdapter.cs` throws `NotSupportedException`. Replace it with a call that creates a `PollingEventSubscription` using the default poll interval.

The method signature must match `IEventStoreAdapter`:
```csharp
ValueTask<IEventSubscription> SubscribeAsync(
    StreamId id,
    StreamPosition from,
    Func<RawEvent, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)
```

**Step 1: Replace the stub**

In `src/ZeroAlloc.EventSourcing.PostgreSql/PostgreSqlEventStoreAdapter.cs`, replace:

```csharp
    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Subscriptions are implemented in Phase 4.</exception>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => throw new NotSupportedException("Subscriptions are not yet implemented for the PostgreSQL adapter. See Phase 4.");
```

With:

```csharp
    /// <inheritdoc/>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        var sub = new PollingEventSubscription(this, id, from, handler, PollingEventSubscription.DefaultPollInterval);
        return ValueTask.FromResult<IEventSubscription>(sub);
    }
```

**Step 2: Build**

```bash
dotnet build src/ZeroAlloc.EventSourcing.PostgreSql/ZeroAlloc.EventSourcing.PostgreSql.csproj
```

Expected: 0 errors, 0 warnings.

**Step 3: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.PostgreSql/
git commit -m "feat(postgres): implement SubscribeAsync via PollingEventSubscription"
```

---

### Task 3: Wire `SubscribeAsync` in the SQL Server adapter

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.SqlServer/SqlServerEventStoreAdapter.cs` (replace the `NotSupportedException` stub)

**Step 1: Replace the stub**

Identical pattern to Task 2. In `src/ZeroAlloc.EventSourcing.SqlServer/SqlServerEventStoreAdapter.cs`, replace:

```csharp
    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Subscriptions are implemented in Phase 4.</exception>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => throw new NotSupportedException("Subscriptions are not yet implemented for the SQL Server adapter. See Phase 4.");
```

With:

```csharp
    /// <inheritdoc/>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        var sub = new PollingEventSubscription(this, id, from, handler, PollingEventSubscription.DefaultPollInterval);
        return ValueTask.FromResult<IEventSubscription>(sub);
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
git commit -m "feat(sqlserver): implement SubscribeAsync via PollingEventSubscription"
```

---

### Task 4: PostgreSQL subscription tests

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/PostgreSqlSubscriptionTests.cs`

**Context:**

- Class `PostgreSqlSubscriptionTests : IAsyncLifetime` — reuse the same container lifecycle pattern as `PostgreSqlAdapterTests`.
- Tests must be async-safe: use `TaskCompletionSource` for "event received" synchronisation (not `Task.Delay`), with a 10-second timeout via `.WaitAsync(TimeSpan.FromSeconds(10))`.
- `PollingEventSubscription` polls at 500 ms by default — tests must wait for a poll cycle. The `TaskCompletionSource` pattern handles this naturally without fixed sleeps.
- Subscription delivers `RawEvent` (adapter level). Tests work at the adapter level directly.

**Tests to write (5 tests):**

1. `Subscribe_CatchesUpHistoricalEvents_BeforeStartAsync` — append an event, then subscribe from `Start`, verify the event is delivered during catch-up.
2. `Subscribe_ReceivesLiveEvents_AppendedAfterStart` — subscribe, start, then append, verify event arrives.
3. `Subscribe_FromPosition_SkipsEarlierEvents` — append 3 events, subscribe from position 3 (0-based: after first two), verify only the third arrives.
4. `Subscribe_AfterDispose_StopsReceiving` — start, dispose, append, verify no further events.
5. `Subscription_IsRunning_TrueAfterStart_FalseAfterDispose` — verify `IsRunning` state transitions.

**Step 1: Create the test file**

```csharp
using System.Text;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.PostgreSql;

namespace ZeroAlloc.EventSourcing.PostgreSql.Tests;

public sealed class PostgreSqlSubscriptionTests : IAsyncLifetime
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

    private static RawEvent MakeRaw(string eventType = "TestEvent")
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        return new RawEvent(StreamPosition.Start, eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    [Fact]
    public async Task Subscribe_CatchesUpHistoricalEvents_BeforeStartAsync()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);

        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task Subscribe_ReceivesLiveEvents_AppendedAfterStart()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task Subscribe_FromPosition_SkipsEarlierEvents()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await _adapter.AppendAsync(id, new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
            MakeRaw("OrderDelivered"),
        }.AsMemory(), StreamPosition.Start);

        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        // Subscribe from position 3 — only "OrderDelivered" (at position 3) should arrive
        var sub = await _adapter.SubscribeAsync(id, new StreamPosition(3), (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderDelivered");
    }

    [Fact]
    public async Task Subscribe_AfterDispose_StopsReceiving()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var received = new List<RawEvent>();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start,
            (e, _) => { received.Add(e); return ValueTask.CompletedTask; });
        await sub.StartAsync();
        await sub.DisposeAsync();

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);
        await Task.Delay(1100); // wait > 1 poll cycle to confirm silence

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task Subscription_IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (_, _) => ValueTask.CompletedTask);

        sub.IsRunning.Should().BeFalse();
        await sub.StartAsync();
        sub.IsRunning.Should().BeTrue();
        await sub.DisposeAsync();
        sub.IsRunning.Should().BeFalse();
    }
}
```

**Step 2: Build**

```bash
dotnet build tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/ -q
```

Expected: 0 errors.

**Step 3: Run tests**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/ --framework net9.0 -v n
```

Expected: All tests pass (Docker must be running).

**Step 4: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/
git commit -m "test(postgres): add subscription tests for catch-up and live polling"
```

---

### Task 5: SQL Server subscription tests

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.SqlServer.Tests/SqlServerSubscriptionTests.cs`

**Context:**

Mirror of the PostgreSQL subscription tests using `MsSqlContainer` / `MsSqlBuilder`.

**Step 1: Create the test file**

```csharp
using System.Text;
using FluentAssertions;
using Testcontainers.MsSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

public sealed class SqlServerSubscriptionTests : IAsyncLifetime
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

    private static RawEvent MakeRaw(string eventType = "TestEvent")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{}");
        return new RawEvent(StreamPosition.Start, eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    [Fact]
    public async Task Subscribe_CatchesUpHistoricalEvents_BeforeStartAsync()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);

        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task Subscribe_ReceivesLiveEvents_AppendedAfterStart()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task Subscribe_FromPosition_SkipsEarlierEvents()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await _adapter.AppendAsync(id, new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
            MakeRaw("OrderDelivered"),
        }.AsMemory(), StreamPosition.Start);

        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        var sub = await _adapter.SubscribeAsync(id, new StreamPosition(3), (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderDelivered");
    }

    [Fact]
    public async Task Subscribe_AfterDispose_StopsReceiving()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var received = new List<RawEvent>();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start,
            (e, _) => { received.Add(e); return ValueTask.CompletedTask; });
        await sub.StartAsync();
        await sub.DisposeAsync();

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);
        await Task.Delay(1100); // wait > 1 poll cycle to confirm silence

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task Subscription_IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (_, _) => ValueTask.CompletedTask);

        sub.IsRunning.Should().BeFalse();
        await sub.StartAsync();
        sub.IsRunning.Should().BeTrue();
        await sub.DisposeAsync();
        sub.IsRunning.Should().BeFalse();
    }
}
```

**Step 2: Build**

```bash
dotnet build tests/ZeroAlloc.EventSourcing.SqlServer.Tests/ -q
```

Expected: 0 errors.

**Step 3: Run tests**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.SqlServer.Tests/ --framework net9.0 -v n
```

Expected: All tests pass (Docker must be running; SQL Server container takes ~30–60 s to start).

**Step 4: Run full test suite to verify no regressions**

```bash
dotnet test ZeroAlloc.EventSourcing.slnx --framework net9.0
```

Expected: All pre-existing tests (39+) still pass, plus the 10 new subscription tests.

**Step 5: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.SqlServer.Tests/
git commit -m "test(sqlserver): add subscription tests for catch-up and live polling"
```

---

## Notes for implementer

- **`PeriodicTimer`** is the idiomatic .NET 6+ replacement for `Task.Delay` polling loops. It avoids drift: the period is wall-clock-based, not `delay-after-work`. Available on `net9.0`.
- **`_cts.CancelAsync()`** is the correct cancellation call on `CancellationTokenSource` in .NET 8+ (avoids the sync `Cancel()` which can throw in certain contexts). Falls back to `.Cancel()` on older TFMs — but this project targets net9.0/net10.0 so `CancelAsync` is fine.
- **`_nextPosition` thread-safety**: the background loop is the only writer; `StartAsync` only writes `_running`. No concurrent reads of `_nextPosition` exist, so no lock is needed.
- **Catch-up ordering**: `ReadAsync` returns events `WHERE position >= @from ORDER BY position ASC`, so catch-up and live events are both in order. After delivering event at position N, `_nextPosition` is set to N+1 (exclusive lower bound for next read).
- **`Subscribe_AfterDispose_StopsReceiving`** uses `Task.Delay(1100)` — just over 2 poll cycles at 500 ms. This is the only test that uses a fixed delay and it is intentional: we need to confirm that nothing arrives, so we must wait a reasonable duration. Document this inline.
- **Central Package Management**: no `Version` attributes on `<PackageReference>`. No new packages needed for Phase 4 (core uses only BCL types).
