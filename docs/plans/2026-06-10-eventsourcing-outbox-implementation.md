# `ZeroAlloc.EventSourcing.Outbox` v0.1 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (or superpowers:subagent-driven-development) to implement this plan task-by-task.

**Goal:** Ship `ZeroAlloc.EventSourcing.Outbox` v0.1 — a thin orchestration layer wrapping `StreamConsumer("*")` + `INotificationDispatcher` for at-least-once cross-aggregate event dispatch via ZA.Mediator.

**Architecture:** Single `OutboxDispatcher : IHostedService` reuses `StreamConsumer` for polling/checkpoint/retry/DLQ and `INotificationDispatcher` (from `ZA.EventSourcing.Mediator`) for `INotification` fan-out. Type-level exclusion via `OutboxOptions.Exclude<TEvent>()`. Registered through `EventSourcingBuilder.AddOutbox(...)`. No new attribute, no separate outbox table, polling-only for v0.1.

**Tech Stack:** .NET 8/9/10 multi-target (mirrors sibling packages), xUnit + FluentAssertions, BenchmarkDotNet (deferred to optional Task 8), `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`.

**Design reference:** [docs/plans/2026-06-10-eventsourcing-outbox-design.md](2026-06-10-eventsourcing-outbox-design.md) (commit `f4d8e10` + design-correction commit on top).

**Branch:** `feat/eventsourcing-outbox` off `main`.

---

## Conventions (apply to every task)

- **Co-Authored-By trailer on every commit:** `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`
- **AOT-clean.** No reflection-based DI scanning, no `Activator.CreateInstance`, no `Type.GetMethod`. The AOT smoke project (Task 7) is the gate.
- **`ConfigureAwait(false)` on every `await`.** Repo convention.
- **TDD discipline.** Failing test FIRST, then implementation, then green, then commit. Never write implementation before the test exists.
- **Commit per logical step.** Don't batch unrelated changes. Commit subjects use the `feat(outbox):` / `test(outbox):` / `docs(outbox):` / `chore(outbox):` scopes — release-please will map these per the existing config.
- **Mirror existing packages.** When in doubt, copy the shape from `src/ZeroAlloc.EventSourcing.Mediator/` (csproj, version.txt, PublicAPI.Shipped.txt). Don't invent new conventions.
- **No `[OutboxEvent]` attribute.** Per the design — `INotification` is the marker, `Exclude<T>()` is the opt-out.

---

## Task 1 — Scaffolding + release-please mapping

**Goal:** Create empty `src/ZeroAlloc.EventSourcing.Outbox/`, test, and AOT smoke projects with correct package metadata. Wire release-please. Verify `dotnet build` of all three projects succeeds with no source files yet (just csprojs and assembly attributes).

**Files:**

- Create: `src/ZeroAlloc.EventSourcing.Outbox/ZeroAlloc.EventSourcing.Outbox.csproj`
- Create: `src/ZeroAlloc.EventSourcing.Outbox/version.txt` (content: `0.1.0`)
- Create: `src/ZeroAlloc.EventSourcing.Outbox/CHANGELOG.md` (content: `# Changelog\n`)
- Create: `src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Shipped.txt` (empty)
- Create: `src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt` (empty)
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/ZeroAlloc.EventSourcing.Outbox.Tests.csproj`
- Create: `samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/ZeroAlloc.EventSourcing.Outbox.AotSmoke.csproj`
- Modify: `release-please-config.json` (add new package entry)

**Step 1: Create the main package csproj**

Mirror `src/ZeroAlloc.EventSourcing.Mediator/ZeroAlloc.EventSourcing.Mediator.csproj` minus the source-generator bundling block (no generator in this package). Specifically:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <PackageId>ZeroAlloc.EventSourcing.Outbox</PackageId>
    <Description>At-least-once cross-aggregate event dispatch from the ZeroAlloc event store. Thin orchestration over StreamConsumer + INotificationDispatcher with type-level exclusion. Polling-based, in-process Mediator dispatch.</Description>
    <PackageTags>$(PackageTags);outbox;mediator;dispatch</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ZeroAlloc.EventSourcing\ZeroAlloc.EventSourcing.csproj" />
    <ProjectReference Include="..\ZeroAlloc.EventSourcing.Mediator\ZeroAlloc.EventSourcing.Mediator.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>

  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>ZeroAlloc.EventSourcing.Outbox.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>

  <ItemGroup Condition="'$(IsRoslynComponent)' != 'true'">
    <PackageReference Include="Meziantou.Analyzer" PrivateAssets="all" />
    <PackageReference Include="Roslynator.Analyzers" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

**Step 2: Create version.txt + CHANGELOG.md + PublicAPI files**

```
src/ZeroAlloc.EventSourcing.Outbox/version.txt          → "0.1.0\n"
src/ZeroAlloc.EventSourcing.Outbox/CHANGELOG.md         → "# Changelog\n"
src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Shipped.txt    → ""
src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt  → "" (empty file — entries added in later tasks)
```

**Step 3: Create the tests csproj**

Mirror `tests/ZeroAlloc.EventSourcing.Mediator.Tests/ZeroAlloc.EventSourcing.Mediator.Tests.csproj` but drop the bridge-generator analyzer references (this package has no generator). Keep:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="ZeroAlloc.Mediator" />
    <PackageReference Include="ZeroAlloc.Mediator.Generator" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.Outbox\ZeroAlloc.EventSourcing.Outbox.csproj" />
    <ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.InMemory\ZeroAlloc.EventSourcing.InMemory.csproj" />
    <ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.Mediator.Generator\ZeroAlloc.EventSourcing.Mediator.Generator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

</Project>
```

The Mediator.Generator analyzer reference is needed for tests to declare `INotification` types AND have the generator emit a dispatcher into the test assembly.

**Step 4: Create the AOT smoke csproj**

Mirror `samples/ZeroAlloc.EventSourcing.Mediator.AotSmoke/ZeroAlloc.EventSourcing.Mediator.AotSmoke.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks></TargetFrameworks>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <RootNamespace>ZeroAlloc.EventSourcing.Outbox.AotSmoke</RootNamespace>
    <IsPackable>false</IsPackable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <OptimizationPreference>Size</OptimizationPreference>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <WarningsAsErrors>$(WarningsAsErrors);IL2026;IL2067;IL2075;IL2091;IL3050;IL3051</WarningsAsErrors>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.Outbox\ZeroAlloc.EventSourcing.Outbox.csproj" SetTargetFramework="TargetFramework=net10.0" />
    <ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.InMemory\ZeroAlloc.EventSourcing.InMemory.csproj" SetTargetFramework="TargetFramework=net10.0" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ZeroAlloc.Mediator" />
    <PackageReference Include="ZeroAlloc.Mediator.Generator">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>
</Project>
```

A minimal `Program.cs` is needed so the project builds — for Task 1 just stub it (Task 7 lands the real smoke logic):

```csharp
// samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/Program.cs
System.Console.WriteLine("Outbox AOT smoke placeholder. Real wiring lands in Task 7.");
return 0;
```

**Step 5: Update release-please-config.json**

Add the new entry alphabetically (between `.InMemory` and `.Kafka`):

```json
"src/ZeroAlloc.EventSourcing.Outbox": {
  "package-name": "ZeroAlloc.EventSourcing.Outbox",
  "release-type": "simple",
  "changelog-path": "CHANGELOG.md"
},
```

**Step 6: Build everything; verify**

```powershell
cd c:/Projects/Prive/ZeroAlloc/ZeroAlloc.EventSourcing
dotnet restore
dotnet build src/ZeroAlloc.EventSourcing.Outbox/ZeroAlloc.EventSourcing.Outbox.csproj -c Release -v minimal
dotnet build tests/ZeroAlloc.EventSourcing.Outbox.Tests/ZeroAlloc.EventSourcing.Outbox.Tests.csproj -c Release -v minimal
dotnet build samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/ZeroAlloc.EventSourcing.Outbox.AotSmoke.csproj -c Release -v minimal
```

Expected: all three build with 0 errors. Test project will have 0 tests; the main package will have 0 public types (just the `InternalsVisibleTo` assembly attribute).

**Step 7: Commit**

```powershell
git add src/ZeroAlloc.EventSourcing.Outbox/ tests/ZeroAlloc.EventSourcing.Outbox.Tests/ samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/ release-please-config.json
git commit -m @'
chore(outbox): scaffold ZeroAlloc.EventSourcing.Outbox package projects

Adds empty src/tests/samples csprojs for the Outbox package, release-please
mapping for v0.1.0, version.txt + CHANGELOG + PublicAPI tracking files.
No source types yet — Task 2 lands OutboxOptions.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 2 — `OutboxOptions` with `Exclude<T>()`

**Goal:** Public options record with the configuration knobs from the design + the type-level opt-out method. TDD: tests first.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Outbox/OutboxOptions.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/OutboxOptionsTests.cs`
- Modify: `src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.EventSourcing.Outbox.Tests/OutboxOptionsTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Outbox;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class OutboxOptionsTests
{
    [Fact]
    public void Defaults_match_design_contract()
    {
        var opts = new OutboxOptions();

        opts.ConsumerId.Should().Be("outbox");
        opts.BatchSize.Should().Be(100);
        opts.ErrorStrategy.Should().Be(ErrorHandlingStrategy.DeadLetter);
        opts.CommitStrategy.Should().Be(CommitStrategy.AfterEvent);
        opts.PollInterval.Should().Be(TimeSpan.FromSeconds(1));
        opts.ExcludedTypes.Should().BeEmpty();
    }

    [Fact]
    public void Exclude_adds_type_and_returns_options_for_chaining()
    {
        var opts = new OutboxOptions();
        var returned = opts.Exclude<NotifA>();

        returned.Should().BeSameAs(opts);
        opts.ExcludedTypes.Should().ContainSingle().Which.Should().Be(typeof(NotifA));
    }

    [Fact]
    public void Exclude_is_idempotent()
    {
        var opts = new OutboxOptions();
        opts.Exclude<NotifA>().Exclude<NotifA>();

        opts.ExcludedTypes.Should().ContainSingle().Which.Should().Be(typeof(NotifA));
    }

    [Fact]
    public void Exclude_supports_multiple_distinct_types()
    {
        var opts = new OutboxOptions();
        opts.Exclude<NotifA>().Exclude<NotifB>();

        opts.ExcludedTypes.Should().BeEquivalentTo([typeof(NotifA), typeof(NotifB)]);
    }

    private sealed class NotifA { }
    private sealed class NotifB { }
}
```

**Step 2: Run; verify it fails**

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests --filter "FullyQualifiedName~OutboxOptionsTests" -c Release
```
Expected: 4 failures (`OutboxOptions` does not exist).

**Step 3: Implement `OutboxOptions`**

```csharp
// src/ZeroAlloc.EventSourcing.Outbox/OutboxOptions.cs
using System;
using System.Collections.Generic;

namespace ZeroAlloc.EventSourcing.Outbox;

/// <summary>
/// Configuration for the <see cref="OutboxDispatcher"/>. Construct directly and mutate
/// via the chainable <see cref="Exclude{TEvent}"/> method or assign properties.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Consumer identifier used as the checkpoint-store key. Defaults to <c>"outbox"</c>.</summary>
    public string ConsumerId { get; set; } = "outbox";

    /// <summary>Maximum events read per poll iteration. Defaults to 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Delay between empty-batch polls. Defaults to 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How to handle handler exceptions after retry exhaustion. Defaults to <see cref="ErrorHandlingStrategy.DeadLetter"/>.</summary>
    public ErrorHandlingStrategy ErrorStrategy { get; set; } = ErrorHandlingStrategy.DeadLetter;

    /// <summary>When to write the checkpoint position. Defaults to <see cref="CommitStrategy.AfterEvent"/>.</summary>
    public CommitStrategy CommitStrategy { get; set; } = CommitStrategy.AfterEvent;

    /// <summary>Maximum retry attempts before applying <see cref="ErrorStrategy"/>. Defaults to 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Retry-delay policy. Defaults to exponential backoff.</summary>
    public IRetryPolicy RetryPolicy { get; set; } = new ExponentialBackoffRetryPolicy(
        initialDelay: TimeSpan.FromMilliseconds(100),
        maxDelay: TimeSpan.FromSeconds(10));

    /// <summary>
    /// Event types that should NOT be dispatched through the outbox, even if they implement
    /// <c>ZeroAlloc.Mediator.INotification</c>. Use for events that exist only for
    /// aggregate-internal reasons (snapshot markers, etc.).
    /// </summary>
    public IReadOnlyCollection<Type> ExcludedTypes => _excluded;

    private readonly HashSet<Type> _excluded = new();

    /// <summary>Adds <typeparamref name="TEvent"/> to <see cref="ExcludedTypes"/>. Idempotent. Returns <c>this</c> for chaining.</summary>
    public OutboxOptions Exclude<TEvent>() where TEvent : class
    {
        _excluded.Add(typeof(TEvent));
        return this;
    }
}
```

**Step 4: Run; verify it passes**

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests --filter "FullyQualifiedName~OutboxOptionsTests" -c Release
```
Expected: 4 passed, 0 failed.

**Step 5: Update PublicAPI.Unshipped.txt**

Add entries for the new public surface. Run `dotnet build` to surface RS0016 warnings telling you exactly what to add, OR add manually:

```
#nullable enable
ZeroAlloc.EventSourcing.Outbox.OutboxOptions
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.BatchSize.get -> int
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.BatchSize.set -> void
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.CommitStrategy.get -> ZeroAlloc.EventSourcing.CommitStrategy
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.CommitStrategy.set -> void
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.ConsumerId.get -> string!
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.ConsumerId.set -> void
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.ErrorStrategy.get -> ZeroAlloc.EventSourcing.ErrorHandlingStrategy
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.ErrorStrategy.set -> void
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.Exclude<TEvent>() -> ZeroAlloc.EventSourcing.Outbox.OutboxOptions!
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.ExcludedTypes.get -> System.Collections.Generic.IReadOnlyCollection<System.Type!>!
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.MaxRetries.get -> int
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.MaxRetries.set -> void
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.OutboxOptions() -> void
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.PollInterval.get -> System.TimeSpan
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.PollInterval.set -> void
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.RetryPolicy.get -> ZeroAlloc.EventSourcing.IRetryPolicy!
ZeroAlloc.EventSourcing.Outbox.OutboxOptions.RetryPolicy.set -> void
```

Re-run `dotnet build` — RS0016 warnings should disappear.

**Step 6: Commit**

```powershell
git add src/ZeroAlloc.EventSourcing.Outbox/OutboxOptions.cs src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt tests/ZeroAlloc.EventSourcing.Outbox.Tests/OutboxOptionsTests.cs
git commit -m @'
feat(outbox): OutboxOptions with Exclude<TEvent>() type-level opt-out

Surface for the dispatcher's configuration knobs (ConsumerId, BatchSize,
PollInterval, ErrorStrategy, CommitStrategy, MaxRetries, RetryPolicy)
plus the chainable Exclude<T>() method for opting out specific event
types from outbox dispatch. AOT-clean — typeof(T) is statically known.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 3 — `OutboxDispatcher` core

**Goal:** The hosted-service dispatcher itself. Wraps a `StreamConsumer` and dispatches each event via `INotificationDispatcher` with filtering + retry + DLQ via the underlying `StreamConsumer` machinery. TDD with one behavior per fact.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Outbox/OutboxDispatcher.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/OutboxDispatcherTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/TestNotifications.cs` (shared test events)
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/RecordingDispatcher.cs` (test double for `INotificationDispatcher`)
- Modify: `src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt`

This is the largest task. Each sub-step is one fact + minimal-impl iteration. Commit ONCE at the end (the dispatcher is one coherent unit).

### Step 1 — Shared test helpers

```csharp
// tests/ZeroAlloc.EventSourcing.Outbox.Tests/TestNotifications.cs
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public sealed record TestEventA(int Value) : INotification;
public sealed record TestEventB(string Value) : INotification;
public sealed record TestEventNotNotification(int Value); // does NOT implement INotification
```

```csharp
// tests/ZeroAlloc.EventSourcing.Outbox.Tests/RecordingDispatcher.cs
using System.Collections.Concurrent;
using ZeroAlloc.EventSourcing.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

/// <summary>
/// Test double for <see cref="INotificationDispatcher"/>. Records every dispatched event
/// for assertion; optionally throws to simulate handler failures.
/// </summary>
public sealed class RecordingDispatcher : INotificationDispatcher
{
    public ConcurrentBag<object> Dispatched { get; } = new();
    public Func<object, Exception?>? ThrowFor { get; set; }

    public ValueTask DispatchAsync(object @event, CancellationToken ct)
    {
        var ex = ThrowFor?.Invoke(@event);
        if (ex is not null) throw ex;
        Dispatched.Add(@event);
        return ValueTask.CompletedTask;
    }
}
```

### Step 2 — First failing test: happy path

```csharp
// tests/ZeroAlloc.EventSourcing.Outbox.Tests/OutboxDispatcherTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Mediator;
using ZeroAlloc.EventSourcing.Outbox;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class OutboxDispatcherTests
{
    private static (IEventStore store, ICheckpointStore checkpoints, RecordingDispatcher dispatcher) NewHarness()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = new ZeroAllocEventSerializer( /* test registry — see existing tests */);
        var store = new EventStore(adapter, serializer);
        var checkpoints = new InMemoryCheckpointStore();
        var dispatcher = new RecordingDispatcher();
        return (store, checkpoints, dispatcher);
    }

    [Fact]
    public async Task Dispatcher_invokes_handler_for_INotification_event_then_advances_checkpoint()
    {
        var (store, checkpoints, recorder) = NewHarness();
        var opts = new OutboxOptions { ConsumerId = "test-1", PollInterval = TimeSpan.FromMilliseconds(50) };
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(42) }, StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, deadLetters: null, opts, NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().ContainSingle().Which.Should().BeEquivalentTo(new TestEventA(42));
        var pos = await checkpoints.ReadAsync("test-1");
        pos.Should().NotBeNull();
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        if (!predicate())
            throw new TimeoutException($"Predicate did not become true within {timeout}");
    }
}
```

Note: the exact `EventStore` + `ZeroAllocEventSerializer` constructor signatures may differ — read the existing tests in `tests/ZeroAlloc.EventSourcing.Tests/` for the canonical harness. Adopt that pattern verbatim. Do NOT invent a new harness shape.

### Step 3 — Run; verify it fails

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests -c Release
```
Expected: fail. `OutboxDispatcher` doesn't exist.

### Step 4 — Minimal `OutboxDispatcher` implementation

```csharp
// src/ZeroAlloc.EventSourcing.Outbox/OutboxDispatcher.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing.Mediator;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox;

/// <summary>
/// Hosted service that polls the global event stream and dispatches each
/// <see cref="INotification"/> event (subject to <see cref="OutboxOptions.ExcludedTypes"/>)
/// via the bundled <see cref="INotificationDispatcher"/>. At-least-once delivery;
/// handlers must be idempotent.
/// </summary>
public sealed class OutboxDispatcher : IHostedService, IAsyncDisposable
{
    private readonly IEventStore _store;
    private readonly ICheckpointStore _checkpoints;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IDeadLetterStore? _deadLetters;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public OutboxDispatcher(
        IEventStore store,
        ICheckpointStore checkpoints,
        INotificationDispatcher dispatcher,
        IDeadLetterStore? deadLetters,
        OutboxOptions options,
        ILogger<OutboxDispatcher> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _deadLetters = deadLetters;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => RunAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopCts is not null)
            _loopCts.Cancel();

        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(default).ConfigureAwait(false);
        _loopCts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var consumerOptions = new StreamConsumerOptions
        {
            BatchSize = _options.BatchSize,
            MaxRetries = _options.MaxRetries,
            RetryPolicy = _options.RetryPolicy,
            ErrorStrategy = _options.ErrorStrategy,
            CommitStrategy = _options.CommitStrategy,
        };
        var consumer = new StreamConsumer(
            _store,
            _checkpoints,
            _options.ConsumerId,
            consumerOptions,
            streamId: new StreamId("*"),
            deadLetterStore: _deadLetters);

        while (!ct.IsCancellationRequested)
        {
            await consumer.ConsumeAsync(DispatchAsync, ct).ConfigureAwait(false);
            // ConsumeAsync returns when batch is empty. Poll-sleep, then resume.
            try { await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DispatchAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Event is not INotification)
            return;
        if (_options.ExcludedTypes.Contains(envelope.Event.GetType()))
            return;

        await _dispatcher.DispatchAsync(envelope.Event, ct).ConfigureAwait(false);
    }
}
```

### Step 5 — Run; verify pass

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests -c Release
```
Expected: 1 test passes.

### Step 6 — Layer on the remaining behaviors as additional facts

Add these tests one at a time, run between each, ensuring everything stays green:

```csharp
[Fact]
public async Task Dispatcher_skips_non_INotification_event()
{
    var (store, checkpoints, recorder) = NewHarness();
    await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventNotNotification(7) }, StreamPosition.Start);

    var sut = new OutboxDispatcher(store, checkpoints, recorder, null,
        new OutboxOptions { ConsumerId = "test-2", PollInterval = TimeSpan.FromMilliseconds(50) },
        NullLogger<OutboxDispatcher>.Instance);
    await sut.StartAsync(default);
    await Task.Delay(200);   // give it a chance to poll
    await sut.StopAsync(default);

    recorder.Dispatched.Should().BeEmpty();
}

[Fact]
public async Task Dispatcher_skips_excluded_INotification_type()
{
    var (store, checkpoints, recorder) = NewHarness();
    var opts = new OutboxOptions { ConsumerId = "test-3", PollInterval = TimeSpan.FromMilliseconds(50) }
        .Exclude<TestEventA>();
    await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(1), new TestEventB("ok") }, StreamPosition.Start);

    var sut = new OutboxDispatcher(store, checkpoints, recorder, null, opts, NullLogger<OutboxDispatcher>.Instance);
    await sut.StartAsync(default);
    await WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
    await sut.StopAsync(default);

    recorder.Dispatched.Should().ContainSingle().Which.Should().BeOfType<TestEventB>();
}

[Fact]
public async Task Dispatcher_retries_on_transient_failure_then_succeeds()
{
    var (store, checkpoints, recorder) = NewHarness();
    var attemptCount = 0;
    recorder.ThrowFor = ev =>
    {
        attemptCount++;
        return attemptCount < 2 ? new InvalidOperationException("transient") : null;
    };
    await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(99) }, StreamPosition.Start);

    var sut = new OutboxDispatcher(store, checkpoints, recorder, null,
        new OutboxOptions { ConsumerId = "test-4", PollInterval = TimeSpan.FromMilliseconds(50), MaxRetries = 3 },
        NullLogger<OutboxDispatcher>.Instance);
    await sut.StartAsync(default);
    await WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
    await sut.StopAsync(default);

    recorder.Dispatched.Should().ContainSingle();
    attemptCount.Should().Be(2);
}

[Fact]
public async Task Dispatcher_writes_to_DLQ_after_retry_exhaustion()
{
    var (store, checkpoints, recorder) = NewHarness();
    var dlq = new InMemoryDeadLetterStore();
    recorder.ThrowFor = _ => new InvalidOperationException("poison");
    await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(666) }, StreamPosition.Start);

    var sut = new OutboxDispatcher(store, checkpoints, recorder, dlq,
        new OutboxOptions { ConsumerId = "test-5", PollInterval = TimeSpan.FromMilliseconds(50), MaxRetries = 2 },
        NullLogger<OutboxDispatcher>.Instance);
    await sut.StartAsync(default);
    await WaitUntil(() => (await dlq.ReadAllAsync("test-5")).Count > 0, TimeSpan.FromSeconds(2));
    await sut.StopAsync(default);

    recorder.Dispatched.Should().BeEmpty();
    var entries = await dlq.ReadAllAsync("test-5");
    entries.Should().ContainSingle();
}

[Fact]
public async Task Dispatcher_resumes_from_checkpoint_after_restart()
{
    var (store, checkpoints, recorder) = NewHarness();
    await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(1), new TestEventA(2), new TestEventA(3) }, StreamPosition.Start);

    // First run — drain
    var sut1 = new OutboxDispatcher(store, checkpoints, recorder, null,
        new OutboxOptions { ConsumerId = "test-6", PollInterval = TimeSpan.FromMilliseconds(50) },
        NullLogger<OutboxDispatcher>.Instance);
    await sut1.StartAsync(default);
    await WaitUntil(() => recorder.Dispatched.Count == 3, TimeSpan.FromSeconds(2));
    await sut1.StopAsync(default);

    // Second run on the SAME stores — should deliver 0 new events
    var sut2 = new OutboxDispatcher(store, checkpoints, recorder, null,
        new OutboxOptions { ConsumerId = "test-6", PollInterval = TimeSpan.FromMilliseconds(50) },
        NullLogger<OutboxDispatcher>.Instance);
    await sut2.StartAsync(default);
    await Task.Delay(200);
    await sut2.StopAsync(default);

    recorder.Dispatched.Count.Should().Be(3);   // unchanged
}
```

The implementation already covers all of these via the `StreamConsumer` underneath — these tests should ALL pass without further `OutboxDispatcher` edits. If one fails, the gap is in the dispatcher's option-to-consumer mapping; debug there.

### Step 7 — Update `PublicAPI.Unshipped.txt`

Add the dispatcher's public surface:
```
ZeroAlloc.EventSourcing.Outbox.OutboxDispatcher
ZeroAlloc.EventSourcing.Outbox.OutboxDispatcher.DisposeAsync() -> System.Threading.Tasks.ValueTask
ZeroAlloc.EventSourcing.Outbox.OutboxDispatcher.OutboxDispatcher(ZeroAlloc.EventSourcing.IEventStore! store, ZeroAlloc.EventSourcing.ICheckpointStore! checkpoints, ZeroAlloc.EventSourcing.Mediator.INotificationDispatcher! dispatcher, ZeroAlloc.EventSourcing.IDeadLetterStore? deadLetters, ZeroAlloc.EventSourcing.Outbox.OutboxOptions! options, Microsoft.Extensions.Logging.ILogger<ZeroAlloc.EventSourcing.Outbox.OutboxDispatcher!>! logger) -> void
ZeroAlloc.EventSourcing.Outbox.OutboxDispatcher.StartAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
ZeroAlloc.EventSourcing.Outbox.OutboxDispatcher.StopAsync(System.Threading.CancellationToken cancellationToken) -> System.Threading.Tasks.Task!
```

### Step 8 — Commit

```powershell
git add src/ZeroAlloc.EventSourcing.Outbox/OutboxDispatcher.cs src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt tests/ZeroAlloc.EventSourcing.Outbox.Tests/
git commit -m @'
feat(outbox): OutboxDispatcher with at-least-once dispatch via StreamConsumer

Hosted service polling the global stream ("*"), dispatching INotification
events through INotificationDispatcher with type-level exclusion. Reuses
StreamConsumer for batching, checkpointing, retry, and DLQ — the dispatcher
itself is ~80 LOC of orchestration.

Six tests cover the happy path, non-INotification skip, excluded-type skip,
transient retry, DLQ on poison, and checkpoint-resume after restart.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 4 — `EventSourcingBuilder.AddOutbox(...)` registration extension

**Goal:** One-line wiring of the dispatcher into the DI container. Mirrors the existing `PublishViaMediator(...)` extension shape.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Outbox/EventSourcingBuilderOutboxExtensions.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/EventSourcingBuilderOutboxExtensionsTests.cs`
- Modify: `src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.EventSourcing.Outbox.Tests/EventSourcingBuilderOutboxExtensionsTests.cs
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Mediator;
using ZeroAlloc.EventSourcing.Outbox;
using ZeroAlloc.EventSourcing.Outbox.Tests;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class EventSourcingBuilderOutboxExtensionsTests
{
    [Fact]
    public void AddOutbox_registers_dispatcher_as_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationDispatcher, RecordingDispatcher>();
        services.AddLogging();
        services.AddSingleton<ICheckpointStore, InMemoryCheckpointStore>();
        services.AddEventSourcing()
                .UseInMemoryAdapter()
                .AddOutbox(opts => opts.ConsumerId = "test-app");

        using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>().ToList();
        hosted.Should().ContainSingle(h => h is OutboxDispatcher);

        var resolved = (OutboxDispatcher)hosted.Single(h => h is OutboxDispatcher);
        resolved.Should().NotBeNull();
    }

    [Fact]
    public void AddOutbox_applies_configure_action_to_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationDispatcher, RecordingDispatcher>();
        services.AddLogging();
        services.AddSingleton<ICheckpointStore, InMemoryCheckpointStore>();
        services.AddEventSourcing()
                .UseInMemoryAdapter()
                .AddOutbox(opts =>
                {
                    opts.ConsumerId = "custom";
                    opts.BatchSize = 50;
                    opts.Exclude<TestEventA>();
                });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<OutboxOptions>();
        opts.ConsumerId.Should().Be("custom");
        opts.BatchSize.Should().Be(50);
        opts.ExcludedTypes.Should().ContainSingle().Which.Should().Be(typeof(TestEventA));
    }
}
```

(The exact `UseInMemoryAdapter()` extension may differ — check `tests/ZeroAlloc.EventSourcing.Tests/` for the canonical wiring. If a different shape exists, adopt it.)

**Step 2: Run; verify fails (`AddOutbox` does not exist)**

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests --filter "FullyQualifiedName~EventSourcingBuilderOutboxExtensionsTests" -c Release
```

**Step 3: Implement the extension**

```csharp
// src/ZeroAlloc.EventSourcing.Outbox/EventSourcingBuilderOutboxExtensions.cs
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ZeroAlloc.EventSourcing.Outbox;

/// <summary>
/// Chainable registration for <see cref="OutboxDispatcher"/> on an
/// <see cref="EventSourcingBuilder"/>. Wires the dispatcher as a singleton
/// <see cref="IHostedService"/> and the <see cref="OutboxOptions"/> as a singleton
/// configured by <paramref name="configure"/>.
/// </summary>
public static class EventSourcingBuilderOutboxExtensions
{
    /// <summary>
    /// Registers the <see cref="OutboxDispatcher"/> hosted service that polls the global
    /// stream and dispatches <c>INotification</c> events via <c>INotificationDispatcher</c>.
    /// </summary>
    /// <param name="builder">The event-sourcing DI builder.</param>
    /// <param name="configure">Optional <see cref="OutboxOptions"/> mutation. Called once at registration time.</param>
    /// <returns>The same builder for chaining.</returns>
    public static EventSourcingBuilder AddOutbox(
        this EventSourcingBuilder builder,
        Action<OutboxOptions>? configure = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        var options = new OutboxOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton(options);
        builder.Services.AddSingleton<IHostedService, OutboxDispatcher>();

        return builder;
    }
}
```

**Step 4: Run; verify passes**

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests -c Release
```
Expected: all tests pass (previous 10 + 2 new = 12).

**Step 5: Update `PublicAPI.Unshipped.txt`**

```
ZeroAlloc.EventSourcing.Outbox.EventSourcingBuilderOutboxExtensions
static ZeroAlloc.EventSourcing.Outbox.EventSourcingBuilderOutboxExtensions.AddOutbox(this ZeroAlloc.EventSourcing.EventSourcingBuilder! builder, System.Action<ZeroAlloc.EventSourcing.Outbox.OutboxOptions!>? configure = null) -> ZeroAlloc.EventSourcing.EventSourcingBuilder!
```

**Step 6: Commit**

```powershell
git add src/ZeroAlloc.EventSourcing.Outbox/EventSourcingBuilderOutboxExtensions.cs src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt tests/ZeroAlloc.EventSourcing.Outbox.Tests/EventSourcingBuilderOutboxExtensionsTests.cs
git commit -m @'
feat(outbox): AddOutbox(...) registration extension on EventSourcingBuilder

One-line wiring: services.AddEventSourcing().UseInMemoryAdapter()
    .AddOutbox(opts => opts.ConsumerId = "myapp").

Registers OutboxDispatcher as IHostedService + OutboxOptions as singleton.
Mirrors the existing PublishViaMediator(...) extension shape.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 5 — Cross-aggregate integration test + idempotency demo

**Goal:** Lock in the two behaviors most likely to break in subtle ways: (a) a handler invoked by the outbox can itself call `SaveAsync`, and the resulting new event flows through the outbox on the next poll; (b) double-delivery of the same event is safe when handlers are idempotent via aggregate-version checks.

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/CrossAggregateIntegrationTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Outbox.Tests/IdempotencyDemoTests.cs`

**Step 1: Cross-aggregate integration test**

```csharp
// tests/ZeroAlloc.EventSourcing.Outbox.Tests/CrossAggregateIntegrationTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class CrossAggregateIntegrationTests
{
    [Fact]
    public async Task Handler_triggered_by_outbox_saves_new_event_that_outbox_picks_up_on_next_poll()
    {
        // Use the same harness pattern as OutboxDispatcherTests.NewHarness().
        // Set up a "credit handler" that, on receiving TestEventA, appends a TestEventB
        // to a SEPARATE stream. Assert that the outbox dispatches BOTH events.

        var (store, checkpoints, recorder) = OutboxDispatcherTests.NewHarness();
        // Make the dispatcher append a follow-up event when it sees TestEventA.
        recorder.OnDispatch = async ev =>
        {
            if (ev is TestEventA a)
                await store.AppendAsync(new StreamId("credit-1"), new object[] { new TestEventB($"credited-{a.Value}") }, StreamPosition.Start);
        };

        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(50) }, StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, null,
            new OutboxOptions { ConsumerId = "test-cross", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await WaitUntil(() => recorder.Dispatched.OfType<TestEventB>().Any(), TimeSpan.FromSeconds(3));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().Contain(e => e is TestEventA);
        recorder.Dispatched.Should().Contain(e => e is TestEventB);
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        if (!predicate())
            throw new TimeoutException();
    }
}
```

Add `OnDispatch` hook to `RecordingDispatcher`:

```csharp
public Func<object, Task>? OnDispatch { get; set; }
// In DispatchAsync, after the throw-check:
if (OnDispatch is not null) await OnDispatch(@event).ConfigureAwait(false);
```

**Step 2: Idempotency demo**

The aggregate-version-based recipe — show that a redelivered event hits `StoreError.Conflict` on `AppendAsync` and the handler swallows it:

```csharp
// tests/ZeroAlloc.EventSourcing.Outbox.Tests/IdempotencyDemoTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class IdempotencyDemoTests
{
    [Fact]
    public async Task Aggregate_version_check_makes_redelivery_a_no_op()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = /* canonical test serializer */;
        var store = new EventStore(adapter, serializer);

        // Initial state: append one event to a stream
        var append1 = await store.AppendAsync(new StreamId("acc-1"), new object[] { new TestEventA(10) }, StreamPosition.Start);
        append1.IsSuccess.Should().BeTrue();

        // Simulated redelivery: the handler tries to apply the same "credit" again,
        // but the aggregate has already been advanced. AppendAsync with expectedVersion 0
        // gets StoreError.Conflict — which the handler swallows.
        var append2 = await store.AppendAsync(new StreamId("acc-1"), new object[] { new TestEventA(10) }, StreamPosition.Start);
        append2.IsSuccess.Should().BeFalse();
        append2.Error.Should().Be(StoreError.Conflict);
    }
}
```

The point of this test is documentation-via-test: it shows the idempotency pattern users follow. It doesn't assert outbox-specific behavior.

**Step 3: Run; verify passes**

```powershell
dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests -c Release
```
Expected: all tests pass (12 from Tasks 2-4 + 2 from Task 5 = 14).

**Step 4: Commit**

```powershell
git add tests/ZeroAlloc.EventSourcing.Outbox.Tests/CrossAggregateIntegrationTests.cs tests/ZeroAlloc.EventSourcing.Outbox.Tests/IdempotencyDemoTests.cs tests/ZeroAlloc.EventSourcing.Outbox.Tests/RecordingDispatcher.cs
git commit -m @'
test(outbox): cross-aggregate integration + idempotency demos

Locks in the two behaviors most likely to break in subtle ways:
- A handler triggered by outbox can append new events, which the outbox
  picks up on the next poll cycle (forms a clean fan-out chain).
- Redelivery of the same event hits StoreError.Conflict on AppendAsync —
  the canonical aggregate-version-based idempotency recipe.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 6 — AOT smoke

**Goal:** Validate that the dispatch path has no reflection escape under NativeAOT. CI runs `dotnet publish` on this project; if any IL2026/IL2067/IL2075/IL2091/IL3050/IL3051 surfaces, it fails the build (per the csproj's `WarningsAsErrors`).

**Files:**
- Modify: `samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/Program.cs` (replace Task 1's stub)

**Step 1: Replace `Program.cs` with the real smoke wiring**

```csharp
// samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Mediator;
using ZeroAlloc.EventSourcing.Outbox;
using ZeroAlloc.Mediator;

// Domain event for the smoke test
public sealed record SmokeEvent(int Value) : INotification;

public sealed class SmokeHandler : INotificationHandler<SmokeEvent>
{
    public static int Calls;
    public ValueTask Handle(SmokeEvent notification, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return ValueTask.CompletedTask;
    }
}

var host = Host.CreateDefaultBuilder()
    .ConfigureServices((_, services) =>
    {
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<ICheckpointStore, InMemoryCheckpointStore>();
        services.AddMediator();                     // ZA.Mediator generator-emitted dispatcher
        services.PublishViaMediator(new StreamId("*"));  // wires INotificationDispatcher (bridge generator)
        services.AddSingleton<INotificationHandler<SmokeEvent>, SmokeHandler>();
        services.AddEventSourcing()
                .UseInMemoryAdapter()
                .AddOutbox(opts => opts.ConsumerId = "aot-smoke");
    })
    .Build();

var store = host.Services.GetRequiredService<IEventStore>();
await store.AppendAsync(new StreamId("test-1"), new object[] { new SmokeEvent(42) }, StreamPosition.Start);

await host.StartAsync();
await Task.Delay(2000);  // give the dispatcher a moment to poll
await host.StopAsync();

if (SmokeHandler.Calls != 1)
{
    Console.Error.WriteLine($"AOT smoke FAIL: expected 1 handler call, got {SmokeHandler.Calls}");
    return 1;
}

Console.WriteLine("Outbox AOT smoke PASS");
return 0;
```

(The exact `services.AddMediator()` / `services.PublishViaMediator(streamId)` extension shapes need cross-check against `EventSourcingBuilderMediatorExtensions` — adopt the actual extension names. If `PublishViaMediator` requires a per-stream subscription that conflicts with the outbox's global stream, simply skip registering the bridge here: only register `INotificationDispatcher` directly. The bridge is for LIVE delivery; the outbox does not need it.)

**Step 2: Publish AOT; verify exit-code-0**

```powershell
dotnet publish samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke -c Release -r linux-x64 --self-contained
$out = "samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/bin/Release/net10.0/linux-x64/publish/ZeroAlloc.EventSourcing.Outbox.AotSmoke"
& $out
$LASTEXITCODE   # should be 0
```

On Windows host you may need to publish for `win-x64` instead and run the .exe. The CI configuration in `.github/workflows/` already covers per-OS publish — for local sanity-check `win-x64` is fine.

Expected output: `Outbox AOT smoke PASS`, exit code 0. ANY IL warnings → fix them (they are gated as errors).

**Step 3: Commit**

```powershell
git add samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/Program.cs
git commit -m @'
feat(outbox): AOT smoke validates no reflection in dispatch path

PublishAot=true console app wires the dispatcher against InMemoryEventStoreAdapter
+ ZA.Mediator + a SmokeHandler<SmokeEvent>, raises one event, asserts the
handler ran exactly once. Gated as warnings-as-errors on IL2026/IL2067/
IL2075/IL2091/IL3050/IL3051 — any reflection escape fails the publish.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 7 — README + finalize PublicAPI shipping + version bump verification

**Goal:** Ship-ready docs + freeze the API surface for v0.1.0. After this task, the merge to main triggers release-please.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Outbox/README.md`
- Modify: `src/ZeroAlloc.EventSourcing.Outbox/ZeroAlloc.EventSourcing.Outbox.csproj` (add `PackageReadmeFile`)
- Move all `PublicAPI.Unshipped.txt` entries into `PublicAPI.Shipped.txt` (since this is the v0.1 ship)

**Step 1: README.md (~150 LOC)**

Sections:
1. **What it is** — one-paragraph elevator pitch from the design doc
2. **Install** — `dotnet add package ZeroAlloc.EventSourcing.Outbox`
3. **Quick start** — the za-cqrs-es Task 5 example: `AddOutbox(...)` + `INotificationHandler<OrderShipped>`
4. **Configuration** — table of every `OutboxOptions` property + defaults
5. **At-least-once contract** — load-bearing — handlers MUST be idempotent
6. **Idempotency recipes**:
   - Aggregate-version-based (the recommended path for handlers that mutate aggregates)
   - Dedup-table-based (for handlers without an aggregate — email senders, webhook publishers)
7. **Excluding event types** — `opts.Exclude<T>()`
8. **Error handling** — Skip / DeadLetter / Stop (FailFast)
9. **Roadmap** — LIVE subscription (v0.2), cross-process broker (v0.3)

Keep it under 200 lines. Follow the style of `src/ZeroAlloc.EventSourcing.Mediator/README.md` if it exists; otherwise match the top-level `README.md`.

**Step 2: Add PackageReadmeFile to csproj**

In `src/ZeroAlloc.EventSourcing.Outbox/ZeroAlloc.EventSourcing.Outbox.csproj`, add to the main PropertyGroup:

```xml
<PackageReadmeFile>README.md</PackageReadmeFile>
```

And add:

```xml
<ItemGroup>
  <None Include="README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

**Step 3: Move Unshipped → Shipped**

```powershell
$shipped = "src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Shipped.txt"
$unshipped = "src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt"
Get-Content $unshipped | Add-Content $shipped
Set-Content $unshipped -Value ""
```

(Or do it manually — preserve `#nullable enable` at the top of `Shipped.txt`.)

**Step 4: Build + test everything one last time**

```powershell
dotnet build -c Release -v minimal
dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests -c Release
dotnet publish samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke -c Release -r win-x64 --self-contained
& "samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/bin/Release/net10.0/win-x64/publish/ZeroAlloc.EventSourcing.Outbox.AotSmoke.exe"
```

All should succeed. AOT smoke exit code 0.

**Step 5: Commit**

```powershell
git add src/ZeroAlloc.EventSourcing.Outbox/README.md src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Shipped.txt src/ZeroAlloc.EventSourcing.Outbox/PublicAPI.Unshipped.txt src/ZeroAlloc.EventSourcing.Outbox/ZeroAlloc.EventSourcing.Outbox.csproj
git commit -m @'
docs(outbox): README + finalize public API for v0.1.0

Ships the package README with installation, quick start, the at-least-once
contract, idempotency recipes (aggregate-version + dedup-table), error
handling strategies, and the v0.2/v0.3 roadmap. Moves all PublicAPI
entries from Unshipped → Shipped — the v0.1 surface is frozen.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 8 (optional, post-v0.1) — Benchmarks

**Goal:** BDN suite measuring dispatcher overhead. Ship the suite, don't gate the release on a perf number.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Outbox.Benchmarks/ZeroAlloc.EventSourcing.Outbox.Benchmarks.csproj`
- Create: `src/ZeroAlloc.EventSourcing.Outbox.Benchmarks/Program.cs`
- Create: `src/ZeroAlloc.EventSourcing.Outbox.Benchmarks/OutboxDispatchBenchmark.cs`

Three benchmarks per the design:
1. Single-event end-to-end dispatch throughput (1 event per iteration)
2. Batch throughput at `BatchSize = 100` (100 events per iteration)
3. Exclusion-set check overhead on a no-handler event (filtered path)

Target: ≤ 5µs per event on top of `StreamConsumer` baseline. **Don't gate the release on hitting this number** — capture the data, document in the README's Performance section in a follow-up.

Commit subject: `perf(outbox): BDN suite measuring dispatcher overhead`. Note: per-repo empirical mapping, `perf:` cuts a **patch** release (e.g. v0.1.1). If you want this in v0.1.0, do it BEFORE Task 7's release-cutting merge.

---

## Acceptance criteria (whole package)

Before opening the PR:

- [ ] `dotnet build -c Release` of the whole repo succeeds with 0 errors
- [ ] `dotnet test tests/ZeroAlloc.EventSourcing.Outbox.Tests -c Release` passes 14 tests
- [ ] `dotnet publish samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke -c Release -r win-x64 --self-contained` succeeds with 0 IL warnings
- [ ] Published AOT binary runs and prints `Outbox AOT smoke PASS` (exit code 0)
- [ ] `PublicAPI.Unshipped.txt` is empty (everything moved to Shipped)
- [ ] `release-please-config.json` includes `src/ZeroAlloc.EventSourcing.Outbox`
- [ ] `version.txt` reads `0.1.0`
- [ ] README.md exists + is referenced from the csproj as `PackageReadmeFile`
- [ ] Branch name: `feat/eventsourcing-outbox`
- [ ] Squash-merge subject: `feat(outbox): cross-aggregate event dispatch via Mediator (v0.1.0)`

After the squash-merge to main:

- [ ] release-please opens a `chore(main): release` PR including `ZeroAlloc.EventSourcing.Outbox 0.1.0`
- [ ] Merging that release PR publishes the package to NuGet

## Out of scope (deferrals)

- LIVE subscription path via `IEventStore.SubscribeAsync` — v0.2
- Cross-process broker dispatch (Kafka, RabbitMQ, gRPC) — v0.3+
- DLQ replay endpoint / admin tools — pattern documented in README, not shipped
- Snapshot integration (skipping events before a snapshot horizon) — future opt-in
- Multiple outbox instances per app (sharded consumers) — future, requires consumer-id strategy work

## Risk

- **`StreamConsumer.ConsumeAsync` returns when batch is empty.** The outer `while (!ct.IsCancellationRequested)` loop must catch this and poll-sleep. If the dispatcher returns early without re-polling, events appended after the first batch never get delivered. Task 3 Step 4's implementation has this loop — review it carefully during code review.
- **`InMemoryEventStoreAdapter` ordering.** Multiple appends across different streams interleave at the `*` global-stream level in the order they hit the adapter. Tests must not assume a specific cross-stream order; assert by content, not by position.
- **`PublishViaMediator` vs direct `INotificationDispatcher` registration in the AOT smoke.** The bridge generator emits a per-stream `EventStoreMediatorBridge` subscription; the outbox doesn't need that subscription, only the dispatcher. If the AOT smoke registers both, the smoke might dispatch every event twice (once via bridge, once via outbox). Task 6 Step 1's note covers this — register only the dispatcher in the smoke, not the bridge.
- **`Microsoft.CodeAnalysis.PublicApiAnalyzers` strictness.** Every public surface change must update `PublicAPI.Unshipped.txt` in the same commit. Treat RS0016 as a forcing function.
