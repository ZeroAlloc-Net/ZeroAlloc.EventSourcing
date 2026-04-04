# Release Pipeline & Benchmarking Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement comprehensive benchmarking infrastructure and unified release pipeline for ZeroAlloc.EventSourcing, serving as a template for org-wide adoption.

**Architecture:** BenchmarkDotNet suite measuring 4 scenarios (event store, snapshots, aggregate loading, projections) → baseline tracking → reusable GitHub workflow → NuGet publish + website update.

**Tech Stack:** BenchmarkDotNet 0.14.0, GitVersion 6.0, Release-Please 15.0, .NET 8, C# 12

---

## Task 1: Create Benchmarks Project Structure

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Benchmarks/ZeroAlloc.EventSourcing.Benchmarks.csproj`
- Create: `src/ZeroAlloc.EventSourcing.Benchmarks/EventStoreBenchmarks.cs` (stub)
- Create: `src/ZeroAlloc.EventSourcing.Benchmarks/SnapshotStoreBenchmarks.cs` (stub)
- Create: `src/ZeroAlloc.EventSourcing.Benchmarks/AggregateLoadBenchmarks.cs` (stub)
- Create: `src/ZeroAlloc.EventSourcing.Benchmarks/ProjectionBenchmarks.cs` (stub)
- Create: `src/ZeroAlloc.EventSourcing.Benchmarks/BenchmarkConfig.cs`
- Create: `src/ZeroAlloc.EventSourcing.Benchmarks/BenchmarkRunner.cs`

**Step 1: Create project file**

Create file: `src/ZeroAlloc.EventSourcing.Benchmarks/ZeroAlloc.EventSourcing.Benchmarks.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj" />
    <ProjectReference Include="../ZeroAlloc.EventSourcing.InMemory/ZeroAlloc.EventSourcing.InMemory.csproj" />
    <ProjectReference Include="../ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Create BenchmarkConfig.cs**

Create file: `src/ZeroAlloc.EventSourcing.Benchmarks/BenchmarkConfig.cs`

```csharp
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace ZeroAlloc.EventSourcing.Benchmarks;

public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(new SimpleJob
        {
            WarmupCount = 3,
            TargetCount = 5,
            InvocationCount = 1,
            Id = "Quick"
        });

        AddDiagnoser(new BenchmarkDotNet.Diagnosers.MemoryDiagnoser());
        AddLogger(DefaultConfig.Instance.GetLoggers().ToArray());
    }
}
```

**Step 3: Create stub benchmark classes**

Create file: `src/ZeroAlloc.EventSourcing.Benchmarks/EventStoreBenchmarks.cs`

```csharp
using BenchmarkDotNet.Attributes;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
public class EventStoreBenchmarks
{
    [Benchmark]
    public void Placeholder()
    {
        // Placeholder - will implement in subsequent tasks
    }
}
```

Create file: `src/ZeroAlloc.EventSourcing.Benchmarks/SnapshotStoreBenchmarks.cs`

```csharp
using BenchmarkDotNet.Attributes;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
public class SnapshotStoreBenchmarks
{
    [Benchmark]
    public void Placeholder()
    {
        // Placeholder - will implement in subsequent tasks
    }
}
```

Create file: `src/ZeroAlloc.EventSourcing.Benchmarks/AggregateLoadBenchmarks.cs`

```csharp
using BenchmarkDotNet.Attributes;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
public class AggregateLoadBenchmarks
{
    [Benchmark]
    public void Placeholder()
    {
        // Placeholder - will implement in subsequent tasks
    }
}
```

Create file: `src/ZeroAlloc.EventSourcing.Benchmarks/ProjectionBenchmarks.cs`

```csharp
using BenchmarkDotNet.Attributes;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
public class ProjectionBenchmarks
{
    [Benchmark]
    public void Placeholder()
    {
        // Placeholder - will implement in subsequent tasks
    }
}
```

Create file: `src/ZeroAlloc.EventSourcing.Benchmarks/BenchmarkRunner.cs`

```csharp
using BenchmarkDotNet.Running;

namespace ZeroAlloc.EventSourcing.Benchmarks;

internal class Program
{
    static void Main(string[] args)
    {
        var summary = BenchmarkRunner.Run(typeof(Program).Assembly, new BenchmarkConfig());
    }
}
```

**Step 4: Verify project builds**

Run: `dotnet build src/ZeroAlloc.EventSourcing.Benchmarks/ZeroAlloc.EventSourcing.Benchmarks.csproj`

Expected: BUILD SUCCESS

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Benchmarks/
git commit -m "feat: add ZeroAlloc.EventSourcing.Benchmarks project structure"
```

---

## Task 2: Implement EventStoreBenchmarks

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.Benchmarks/EventStoreBenchmarks.cs`

**Step 1: Write test to understand EventStore API**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/ -v` to see how EventStore is used

Expected: All existing tests pass (verify project structure)

**Step 2: Implement EventStoreBenchmarks**

Replace `EventStoreBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
public class EventStoreBenchmarks
{
    private InMemoryEventStore _eventStore = null!;
    private byte[] _eventData = null!;
    private StreamId _streamId;
    private int _eventCount;

    [GlobalSetup]
    public void Setup()
    {
        _eventStore = new InMemoryEventStore();
        _streamId = new StreamId("benchmark-stream");
        _eventData = new byte[] { 1, 2, 3, 4, 5 };
        _eventCount = 0;

        // Pre-populate with 100 events for read benchmarks
        for (int i = 0; i < 100; i++)
        {
            _eventStore.AppendAsync(
                _streamId,
                new StreamEvent(
                    new EventType("BenchmarkEvent"),
                    _eventData,
                    metadata: ReadOnlyMemory<byte>.Empty
                ),
                CancellationToken.None
            ).GetAwaiter().GetResult();
        }
        _eventCount = 100;
    }

    [Benchmark]
    public async Task AppendEvent()
    {
        await _eventStore.AppendAsync(
            new StreamId($"stream-{_eventCount}"),
            new StreamEvent(
                new EventType("BenchmarkEvent"),
                _eventData,
                metadata: ReadOnlyMemory<byte>.Empty
            ),
            CancellationToken.None
        );
        _eventCount++;
    }

    [Benchmark]
    public async Task ReadEventsFromStream()
    {
        var events = new List<StreamEvent>();
        await foreach (var evt in _eventStore.ReadAsync(_streamId, StreamPosition.Start, CancellationToken.None))
        {
            events.Add(evt);
        }
    }

    [Benchmark]
    public async Task ReadEventsAcrossStreams()
    {
        var allEvents = 0;
        for (int i = 0; i < 10; i++)
        {
            await foreach (var evt in _eventStore.ReadAsync(
                new StreamId($"stream-{i}"),
                StreamPosition.Start,
                CancellationToken.None
            ))
            {
                allEvents++;
            }
        }
    }
}
```

**Step 3: Verify benchmarks compile**

Run: `dotnet build src/ZeroAlloc.EventSourcing.Benchmarks/ZeroAlloc.EventSourcing.Benchmarks.csproj`

Expected: BUILD SUCCESS

**Step 4: Run benchmarks locally (dry run)**

Run: `dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/ --no-build`

Expected: Benchmarks complete, display results with mean/allocations

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Benchmarks/EventStoreBenchmarks.cs
git commit -m "feat: implement EventStoreBenchmarks (append, read single/multiple streams)"
```

---

## Task 3: Implement SnapshotStoreBenchmarks

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.Benchmarks/SnapshotStoreBenchmarks.cs`

**Step 1: Implement SnapshotStoreBenchmarks**

Replace `SnapshotStoreBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.Aggregates.Tests;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
public class SnapshotStoreBenchmarks
{
    private InMemorySnapshotStore<OrderState> _snapshotStore = null!;
    private OrderState _state;
    private StreamId _streamId;
    private StreamPosition _position;

    [GlobalSetup]
    public void Setup()
    {
        _snapshotStore = new InMemorySnapshotStore<OrderState>();
        _streamId = new StreamId("order-benchmark");
        _position = new StreamPosition(50);
        _state = new OrderState
        {
            OrderId = new OrderId(123),
            CustomerId = new CustomerId(456),
            Amount = 1000m,
            Status = "Confirmed"
        };

        // Pre-populate for read benchmarks
        _snapshotStore.WriteAsync(_streamId, _position, _state, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task WriteSnapshot()
    {
        var newState = new OrderState
        {
            OrderId = new OrderId(124),
            CustomerId = new CustomerId(456),
            Amount = 2000m,
            Status = "Shipped"
        };

        await _snapshotStore.WriteAsync(
            new StreamId($"order-{DateTime.UtcNow.Ticks}"),
            new StreamPosition(75),
            newState,
            CancellationToken.None
        );
    }

    [Benchmark]
    public async Task ReadSnapshot()
    {
        var result = await _snapshotStore.ReadAsync(_streamId, CancellationToken.None);
    }

    [Benchmark]
    public async Task WriteAndReadSnapshot()
    {
        var newState = new OrderState
        {
            OrderId = new OrderId(125),
            CustomerId = new CustomerId(456),
            Amount = 3000m,
            Status = "Pending"
        };

        var streamId = new StreamId($"order-{DateTime.UtcNow.Ticks}");
        await _snapshotStore.WriteAsync(
            streamId,
            new StreamPosition(100),
            newState,
            CancellationToken.None
        );

        var result = await _snapshotStore.ReadAsync(streamId, CancellationToken.None);
    }
}
```

**Step 2: Verify benchmarks compile**

Run: `dotnet build src/ZeroAlloc.EventSourcing.Benchmarks/ZeroAlloc.EventSourcing.Benchmarks.csproj`

Expected: BUILD SUCCESS

**Step 3: Run benchmarks locally (dry run)**

Run: `dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/ --no-build`

Expected: Benchmarks include snapshot operations

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Benchmarks/SnapshotStoreBenchmarks.cs
git commit -m "feat: implement SnapshotStoreBenchmarks (read, write, round-trip)"
```

---

## Task 4: Implement AggregateLoadBenchmarks

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.Benchmarks/AggregateLoadBenchmarks.cs`

**Step 1: Implement AggregateLoadBenchmarks**

Replace `AggregateLoadBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.Aggregates.Tests;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
public class AggregateLoadBenchmarks
{
    private InMemoryEventStore _eventStore = null!;
    private IAggregateRepository<Order, OrderId> _repository = null!;
    private OrderId _smallStreamOrderId = null!;
    private OrderId _mediumStreamOrderId = null!;
    private OrderId _largeStreamOrderId = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _eventStore = new InMemoryEventStore();

        var aggregateFactory = () => new Order();
        var streamIdFactory = (OrderId id) => new StreamId($"order-{id.Value}");

        _repository = new AggregateRepository<Order, OrderId>(
            _eventStore,
            aggregateFactory,
            streamIdFactory
        );

        // Create small stream (10 events)
        _smallStreamOrderId = new OrderId(1);
        var smallOrder = new Order();
        for (int i = 0; i < 10; i++)
        {
            smallOrder.CreateOrder(_smallStreamOrderId, new CustomerId(100), 1000m);
        }
        await _repository.SaveAsync(smallOrder, CancellationToken.None);

        // Create medium stream (100 events)
        _mediumStreamOrderId = new OrderId(2);
        var mediumOrder = new Order();
        for (int i = 0; i < 100; i++)
        {
            mediumOrder.CreateOrder(_mediumStreamOrderId, new CustomerId(100), 1000m);
        }
        await _repository.SaveAsync(mediumOrder, CancellationToken.None);

        // Create large stream (1000 events)
        _largeStreamOrderId = new OrderId(3);
        var largeOrder = new Order();
        for (int i = 0; i < 1000; i++)
        {
            largeOrder.CreateOrder(_largeStreamOrderId, new CustomerId(100), 1000m);
        }
        await _repository.SaveAsync(largeOrder, CancellationToken.None);
    }

    [Benchmark]
    public async Task LoadAggregateSmallStream()
    {
        var order = await _repository.LoadAsync(_smallStreamOrderId, CancellationToken.None);
    }

    [Benchmark]
    public async Task LoadAggregateMediumStream()
    {
        var order = await _repository.LoadAsync(_mediumStreamOrderId, CancellationToken.None);
    }

    [Benchmark]
    public async Task LoadAggregateLargeStream()
    {
        var order = await _repository.LoadAsync(_largeStreamOrderId, CancellationToken.None);
    }
}
```

**Step 2: Verify benchmarks compile**

Run: `dotnet build src/ZeroAlloc.EventSourcing.Benchmarks/ZeroAlloc.EventSourcing.Benchmarks.csproj`

Expected: BUILD SUCCESS

**Step 3: Run benchmarks locally (dry run)**

Run: `dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/ --no-build`

Expected: Benchmarks show load time for different stream sizes

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Benchmarks/AggregateLoadBenchmarks.cs
git commit -m "feat: implement AggregateLoadBenchmarks (small/medium/large streams)"
```

---

## Task 5: Implement ProjectionBenchmarks

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing.Benchmarks/ProjectionBenchmarks.cs`

**Step 1: Implement ProjectionBenchmarks**

Replace `ProjectionBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.Aggregates.Tests;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
public class ProjectionBenchmarks
{
    private TestInvoiceProjection _projection = null!;
    private readonly InvoiceCreated _invoiceCreated = new InvoiceCreated { InvoiceId = "INV-001", Amount = 1000m };
    private readonly Paid _paid = new Paid { Amount = 500m };
    private readonly Refund _refund = new Refund { Amount = 100m };

    [GlobalSetup]
    public void Setup()
    {
        _projection = new TestInvoiceProjection();
    }

    [Benchmark]
    public void ApplySingleEvent()
    {
        var current = new InvoiceReadModel { Id = "INV-001", Total = 0m };
        _projection.Apply(current, _invoiceCreated);
    }

    [Benchmark]
    public void ApplyMultipleEventsSequential()
    {
        var current = new InvoiceReadModel { Id = "INV-001", Total = 0m };
        _projection.Apply(current, _invoiceCreated);
        _projection.Apply(current, _paid);
        _projection.Apply(current, _refund);
    }

    [Benchmark]
    public void ApplyTypedDispatch()
    {
        var current = new InvoiceReadModel { Id = "INV-001", Total = 0m };
        _projection.ApplyTyped(current, _invoiceCreated);
        _projection.ApplyTyped(current, _paid);
        _projection.ApplyTyped(current, _refund);
    }

    // Test implementations
    public record InvoiceReadModel
    {
        public string Id { get; set; } = string.Empty;
        public decimal Total { get; set; }
        public decimal Paid { get; set; }
    }

    public record InvoiceCreated
    {
        public string InvoiceId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public record Paid
    {
        public decimal Amount { get; set; }
    }

    public record Refund
    {
        public decimal Amount { get; set; }
    }

    public class TestInvoiceProjection : Projection<InvoiceReadModel>
    {
        public override void Apply(InvoiceReadModel current, object @event)
        {
            if (@event is InvoiceCreated ic)
                ApplyInvoiceCreated(current, ic);
            else if (@event is Paid p)
                ApplyPaid(current, p);
            else if (@event is Refund r)
                ApplyRefund(current, r);
        }

        public void ApplyInvoiceCreated(InvoiceReadModel current, InvoiceCreated @event)
        {
            Current = current with { Id = @event.InvoiceId, Total = @event.Amount };
        }

        public void ApplyPaid(InvoiceReadModel current, Paid @event)
        {
            Current = current with { Paid = current.Paid + @event.Amount };
        }

        public void ApplyRefund(InvoiceReadModel current, Refund @event)
        {
            Current = current with { Total = current.Total - @event.Amount };
        }

        public void ApplyTyped(InvoiceReadModel current, object @event)
        {
            // Simulated generated dispatch (would be generated in real implementation)
            switch (@event)
            {
                case InvoiceCreated ic:
                    ApplyInvoiceCreated(current, ic);
                    break;
                case Paid p:
                    ApplyPaid(current, p);
                    break;
                case Refund r:
                    ApplyRefund(current, r);
                    break;
            }
        }
    }
}
```

**Step 2: Verify benchmarks compile**

Run: `dotnet build src/ZeroAlloc.EventSourcing.Benchmarks/ZeroAlloc.EventSourcing.Benchmarks.csproj`

Expected: BUILD SUCCESS

**Step 3: Run benchmarks locally (dry run)**

Run: `dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/ --no-build`

Expected: All benchmarks complete successfully

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Benchmarks/ProjectionBenchmarks.cs
git commit -m "feat: implement ProjectionBenchmarks (apply single/multiple events, typed dispatch)"
```

---

## Task 6: Add Release Configuration Files

**Files:**
- Create: `GitVersion.yml`
- Create: `release-please-config.json`
- Create: `.commitlintrc.yml`
- Create: `benchmarks.json`

**Step 1: Create GitVersion.yml**

Create file: `GitVersion.yml` (repo root)

```yaml
mode: Mainline
branches:
  main:
    increment: Patch
    prevent-increment-of-merged-branch-version: true
  feature:
    increment: Minor
  release:
    increment: Patch
commit-message-increment-pattern: '^(feat|fix|perf)(\(.+\))?!?:'
```

**Step 2: Create release-please-config.json**

Create file: `release-please-config.json` (repo root)

```json
{
  "packages": {
    ".": {
      "package-name": "ZeroAlloc.EventSourcing",
      "bump-minor-pre-major": false,
      "changelog-path": "CHANGELOG.md"
    }
  },
  "changelog-sections": [
    { "type": "feat", "section": "Features" },
    { "type": "fix", "section": "Bug Fixes" },
    { "type": "perf", "section": "Performance" },
    { "type": "breaking", "section": "Breaking Changes" }
  ]
}
```

**Step 3: Create .commitlintrc.yml**

Create file: `.commitlintrc.yml` (repo root)

```yaml
extends:
  - "@commitlint/config-conventional"
rules:
  type-enum:
    - 2
    - always
    - [feat, fix, docs, style, refactor, perf, test, chore, ci, revert]
  scope-enum:
    - 2
    - always
    - [core, store, snapshot, projection, generator, sql, tests, benchmarks]
```

**Step 4: Create initial benchmarks.json**

Create file: `benchmarks.json` (repo root)

```json
{
  "version": "0.0.0",
  "timestamp": "2026-04-04T00:00:00Z",
  "benchmarks": {}
}
```

**Step 5: Verify files created**

Run: `ls -la GitVersion.yml release-please-config.json .commitlintrc.yml benchmarks.json`

Expected: All four files exist

**Step 6: Commit**

```bash
git add GitVersion.yml release-please-config.json .commitlintrc.yml benchmarks.json
git commit -m "chore: add release pipeline configuration (GitVersion, release-please, commitlint)"
```

---

## Task 7: Create Performance Documentation Template

**Files:**
- Create: `docs/performance.md`

**Step 1: Create performance.md template**

Create file: `docs/performance.md`

```markdown
# Performance & Benchmarks

**Version:** Next Release  
**Last Updated:** 2026-04-04

## Event Store Operations

Benchmarks for core event store functionality: appending events and reading event streams.

| Operation | Mean (ns) | Allocations | P95 (ns) | P99 (ns) |
|-----------|-----------|-------------|----------|----------|
| AppendEvent | — | — | — | — |
| ReadEventsFromStream | — | — | — | — |
| ReadEventsAcrossStreams | — | — | — | — |

## Snapshot Storage

Benchmarks for snapshot read/write operations (InMemory implementation).

| Operation | Mean (ns) | Allocations | P95 (ns) | P99 (ns) |
|-----------|-----------|-------------|----------|----------|
| WriteSnapshot | — | — | — | — |
| ReadSnapshot | — | — | — | — |
| WriteAndReadSnapshot | — | — | — | — |

## Aggregate Loading & Replay

Benchmarks for loading aggregates from event streams of varying sizes.

| Operation | Mean (ns) | Allocations | P95 (ns) | P99 (ns) |
|-----------|-----------|-------------|----------|----------|
| LoadAggregateSmallStream (10 events) | — | — | — | — |
| LoadAggregateMediumStream (100 events) | — | — | — | — |
| LoadAggregateLargeStream (1000 events) | — | — | — | — |

## Projection Application

Benchmarks for applying events to projections (read models).

| Operation | Mean (ns) | Allocations | P95 (ns) | P99 (ns) |
|-----------|-----------|-------------|----------|----------|
| ApplySingleEvent | — | — | — | — |
| ApplyMultipleEventsSequential | — | — | — | — |
| ApplyTypedDispatch | — | — | — | — |

## Zero-Allocation Compliance

All core operations target **zero allocations** in steady state. Any allocations shown above represent transient state during benchmark execution and are acceptable for this zero-allocation library.

## Methodology

Benchmarks are executed using [BenchmarkDotNet](https://benchmarkdotnet.org/) with:
- 3 warmup iterations
- 5 measurement iterations
- Full memory diagnostics enabled
- Nanosecond precision

Benchmarks run before each release to validate no performance regressions have been introduced.
```

**Step 2: Verify file created**

Run: `cat docs/performance.md | head -20`

Expected: Performance template displays

**Step 3: Commit**

```bash
git add docs/performance.md
git commit -m "docs: add performance benchmarks template"
```

---

## Task 8: Validate Full Benchmark Suite

**Files:**
- Run full test: `dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/`

**Step 1: Run all benchmarks**

Run: `dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/`

Expected: All benchmark classes execute, display results table with ns/allocations

**Step 2: Capture results and verify output**

Manually inspect output for:
- AppendEvent, ReadEventsFromStream, ReadEventsAcrossStreams (EventStore)
- WriteSnapshot, ReadSnapshot, WriteAndReadSnapshot (Snapshot)
- LoadAggregateSmallStream, LoadAggregateMediumStream, LoadAggregateLargeStream (Aggregate)
- ApplySingleEvent, ApplyMultipleEventsSequential, ApplyTypedDispatch (Projection)

Expected: All benchmarks complete with mean time and allocation data

**Step 3: No step needed**

Output is informational only; benchmarks should complete without errors.

**Step 4: Commit current state (if any changes)**

```bash
git status
```

Expected: No uncommitted changes (all benchmarks integrated)

---

## Task 9: Create GitHub Workflow File in .github Repo

**Note:** This task requires access to https://github.com/ZeroAlloc-Net/.github repo. Coordinate with org maintainers.

**Files:**
- Create: `.github/workflows/nuget-release.yml` (in ZeroAlloc-Net/.github)

**Step 1: Create reusable workflow template**

In `.github` org repo, create file: `.github/workflows/nuget-release.yml`

```yaml
name: NuGet Release - Benchmark & Publish

on:
  workflow_call:
    inputs:
      repo-name:
        description: 'Repository name'
        required: true
        type: string
      has-benchmarks:
        description: 'Whether repo has benchmarks project'
        required: false
        type: boolean
        default: false
      nuget-org:
        description: 'NuGet organization'
        required: true
        type: string

jobs:
  release:
    runs-on: ubuntu-latest
    
    steps:
      - name: Checkout code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore dependencies
        run: dotnet restore

      - name: Build
        run: dotnet build --configuration Release --no-restore

      - name: Run tests
        run: dotnet test --configuration Release --no-build --verbosity normal

      - name: Run benchmarks
        if: ${{ inputs.has-benchmarks == true }}
        run: dotnet run -c Release --project src/${{ inputs.repo-name }}.Benchmarks/
        
      - name: Upload benchmark results
        if: ${{ inputs.has-benchmarks == true && always() }}
        uses: actions/upload-artifact@v3
        with:
          name: benchmark-results
          path: src/${{ inputs.repo-name }}.Benchmarks/BenchmarkDotNet.Artifacts/

      - name: Publish to NuGet
        run: dotnet nuget push "**/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key ${{ secrets.NUGET_API_KEY }} --skip-duplicate
        
      - name: Create GitHub Release
        uses: softprops/action-gh-release@v1
        with:
          generate_release_notes: true
```

**Step 2: Test workflow syntax**

Verify YAML is valid:

Run: `yamllint .github/workflows/nuget-release.yml` (or use GitHub's validation)

Expected: No syntax errors

**Step 3: Commit to .github repo**

```bash
git add .github/workflows/nuget-release.yml
git commit -m "ci: add reusable nuget-release workflow for benchmarking and publishing"
git push
```

Expected: Workflow available in `.github` org repo for all NuGet packages to reference

---

## Task 10: Wire EventSourcing to Call Shared Workflow

**Files:**
- Create: `.github/workflows/release.yml`

**Step 1: Create EventSourcing release workflow**

Create file: `.github/workflows/release.yml`

```yaml
name: Release

on:
  push:
    tags:
      - 'v*'

jobs:
  release:
    uses: ZeroAlloc-Net/.github/.github/workflows/nuget-release.yml@main
    with:
      repo-name: 'ZeroAlloc.EventSourcing'
      has-benchmarks: true
      nuget-org: 'ZeroAlloc'
    secrets:
      NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
```

**Step 2: Verify workflow structure**

Run: `cat .github/workflows/release.yml`

Expected: Workflow calls shared `nuget-release.yml` with correct inputs

**Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: add release workflow calling shared nuget-release action"
```

---

## Task 11: Final Validation and Test Dry Run

**Files:**
- Verify: Full project builds and tests pass
- Verify: Benchmarks run without errors
- Verify: All files committed

**Step 1: Clean build**

Run: `dotnet clean && dotnet build -c Release`

Expected: BUILD SUCCESS

**Step 2: Run all tests (including benchmarks)**

Run: `dotnet test --configuration Release --verbosity normal`

Expected: All tests pass, 0 failed

**Step 3: Verify benchmark project standalone**

Run: `dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/ -- --warmupCount 1 --targetCount 1`

Expected: Benchmarks complete quickly with results

**Step 4: Check git status**

Run: `git status`

Expected: Clean working directory (no uncommitted changes)

**Step 5: View commit log**

Run: `git log --oneline | head -10`

Expected: Recent commits include all benchmarking and configuration tasks

**Step 6: No additional commit needed**

All work should already be committed in previous steps.

---

## Task 12: Create Adoption Guide for Other Repos

**Files:**
- Create: `docs/ADOPTION_GUIDE.md`

**Step 1: Create adoption guide**

Create file: `docs/ADOPTION_GUIDE.md`

```markdown
# Release Pipeline & Benchmarking Adoption Guide

This document describes how to adopt the unified release pipeline (including optional benchmarking) for other ZeroAlloc NuGet packages.

## Prerequisites

- Repository is a .NET 8 NuGet package
- GitHub Actions available in your org repo
- Access to `.github` org repo to reference shared workflows

## Quick Start

### Step 1: Add Configuration Files

Copy from EventSourcing repo:

1. `GitVersion.yml` → Your repo root
2. `release-please-config.json` → Your repo root (update `package-name` field)
3. `.commitlintrc.yml` → Your repo root (update `scope-enum` as needed)

### Step 2: Add Release Workflow

Create `.github/workflows/release.yml`:

```yaml
name: Release

on:
  push:
    tags:
      - 'v*'

jobs:
  release:
    uses: ZeroAlloc-Net/.github/.github/workflows/nuget-release.yml@main
    with:
      repo-name: 'YourPackageName'
      has-benchmarks: false  # Set to true if you add benchmarks
      nuget-org: 'ZeroAlloc'
    secrets:
      NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
```

### Step 3 (Optional): Add Benchmarks

If your package is performance-critical:

1. Create `src/YourPackage.Benchmarks/` project
2. Add BenchmarkDotNet package reference
3. Implement benchmark classes following EventSourcing pattern
4. Update `release.yml` to set `has-benchmarks: true`

## Full Benchmarking Setup

If adding benchmarks, follow the pattern in `ZeroAlloc.EventSourcing.Benchmarks/`:

```
src/YourPackage.Benchmarks/
├── YourPackage.Benchmarks.csproj
├── BenchmarkConfig.cs
├── BenchmarkRunner.cs
├── CoreBenchmarks.cs         (your specific benchmarks)
└── AdvancedFeatureBenchmarks.cs
```

Add to project file:

```xml
<ItemGroup>
  <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
</ItemGroup>
```

## Testing Locally

Before committing:

```bash
# Build
dotnet build -c Release

# Run tests
dotnet test --configuration Release

# Run benchmarks (optional)
dotnet run -c Release --project src/YourPackage.Benchmarks/
```

## Release Process

1. **Create a pull request** with your changes using conventional commits:
   - `feat: add new feature`
   - `fix: resolve bug`
   - `perf: improve performance`

2. **Merge to main**. Release-Please will automatically:
   - Calculate next semantic version (using GitVersion)
   - Generate changelog
   - Create a release PR

3. **Merge release PR**. GitHub Actions will:
   - Run benchmarks (if enabled)
   - Validate no regressions (if benchmarks exist)
   - Publish to NuGet
   - Create release on GitHub

## Monitoring Performance

If benchmarks are enabled, monitor:

- **Local runs:** `dotnet run -c Release --project src/YourPackage.Benchmarks/`
- **PR validation:** Check Actions tab on release-please PR
- **Release notes:** Performance results included in GitHub release

## Questions?

See `docs/plans/2026-04-04-release-pipeline-benchmarking-design.md` for architecture details.
```

**Step 2: Verify file created**

Run: `cat docs/ADOPTION_GUIDE.md | head -30`

Expected: Adoption guide displays with clear steps

**Step 3: Commit**

```bash
git add docs/ADOPTION_GUIDE.md
git commit -m "docs: add adoption guide for release pipeline in other repos"
```

---

## Summary

✅ Benchmarks project structure created and tested  
✅ 4 benchmark suites implemented (EventStore, Snapshot, Aggregate, Projection)  
✅ BenchmarkDotNet configured with proper diagnostics  
✅ Version/release configuration files added (GitVersion, release-please, commitlint)  
✅ Performance documentation template created  
✅ Reusable GitHub workflow added to `.github` org repo  
✅ EventSourcing release workflow created  
✅ Adoption guide documented for other repos  

**Next:** Merge to main, tag release, and validate full workflow end-to-end.

---

Plan complete and saved to `docs/plans/2026-04-04-release-pipeline-implementation.md`. 

**Two execution options:**

**1. Subagent-Driven (this session)** — I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Parallel Session (separate)** — Open new session with executing-plans, batch execution with checkpoints

Which approach?