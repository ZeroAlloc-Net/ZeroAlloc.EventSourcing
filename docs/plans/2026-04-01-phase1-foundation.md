# Phase 1: Foundation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Scaffold the full solution, define all core abstractions, and deliver a working in-memory event store adapter with tests.

**Architecture:** Layered monorepo — `ZeroAlloc.EventSourcing` holds stream primitives (interfaces + value types), `ZeroAlloc.EventSourcing.InMemory` provides the first storage adapter backed by `ConcurrentDictionary` with `ZeroAlloc.AsyncEvents` for live subscriptions. All hot-path types are structs or readonly records to avoid allocations.

**Tech Stack:** .NET 9/10 multi-target (`net9.0;net10.0`), xUnit, ZeroAlloc.Results, ZeroAlloc.Collections, ZeroAlloc.AsyncEvents, Central Package Management (`Directory.Packages.props`).

---

## Pre-flight: verify tooling

Run:
```bash
dotnet --version       # expect 9.x or 10.x
gh auth status         # needed for CI task
```

---

### Task 1: Solution scaffold

**Files:**
- Create: `ZeroAlloc.EventSourcing.sln`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj`
- Create: `src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj`
- Create: `src/ZeroAlloc.EventSourcing.Generators/ZeroAlloc.EventSourcing.Generators.csproj`
- Create: `src/ZeroAlloc.EventSourcing.InMemory/ZeroAlloc.EventSourcing.InMemory.csproj`
- Create: `src/ZeroAlloc.EventSourcing.SqlServer/ZeroAlloc.EventSourcing.SqlServer.csproj`
- Create: `src/ZeroAlloc.EventSourcing.PostgreSql/ZeroAlloc.EventSourcing.PostgreSql.csproj`
- Create: `tests/ZeroAlloc.EventSourcing.Tests/ZeroAlloc.EventSourcing.Tests.csproj`
- Create: `tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ZeroAlloc.EventSourcing.Aggregates.Tests.csproj`
- Create: `tests/ZeroAlloc.EventSourcing.InMemory.Tests/ZeroAlloc.EventSourcing.InMemory.Tests.csproj`
- Create: `tests/ZeroAlloc.EventSourcing.SqlServer.Tests/ZeroAlloc.EventSourcing.SqlServer.Tests.csproj`
- Create: `tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/ZeroAlloc.EventSourcing.PostgreSql.Tests.csproj`

**Step 1: Create the solution and project tree**

```bash
cd c:/Projects/Prive/ZeroAlloc.EventSourcing

dotnet new sln -n ZeroAlloc.EventSourcing

# src projects
dotnet new classlib -n ZeroAlloc.EventSourcing           -o src/ZeroAlloc.EventSourcing           --framework net9.0
dotnet new classlib -n ZeroAlloc.EventSourcing.Aggregates -o src/ZeroAlloc.EventSourcing.Aggregates --framework net9.0
dotnet new classlib -n ZeroAlloc.EventSourcing.Generators -o src/ZeroAlloc.EventSourcing.Generators --framework net9.0
dotnet new classlib -n ZeroAlloc.EventSourcing.InMemory   -o src/ZeroAlloc.EventSourcing.InMemory   --framework net9.0
dotnet new classlib -n ZeroAlloc.EventSourcing.SqlServer  -o src/ZeroAlloc.EventSourcing.SqlServer  --framework net9.0
dotnet new classlib -n ZeroAlloc.EventSourcing.PostgreSql -o src/ZeroAlloc.EventSourcing.PostgreSql --framework net9.0

# test projects
dotnet new xunit -n ZeroAlloc.EventSourcing.Tests              -o tests/ZeroAlloc.EventSourcing.Tests              --framework net9.0
dotnet new xunit -n ZeroAlloc.EventSourcing.Aggregates.Tests   -o tests/ZeroAlloc.EventSourcing.Aggregates.Tests   --framework net9.0
dotnet new xunit -n ZeroAlloc.EventSourcing.InMemory.Tests     -o tests/ZeroAlloc.EventSourcing.InMemory.Tests     --framework net9.0
dotnet new xunit -n ZeroAlloc.EventSourcing.SqlServer.Tests    -o tests/ZeroAlloc.EventSourcing.SqlServer.Tests    --framework net9.0
dotnet new xunit -n ZeroAlloc.EventSourcing.PostgreSql.Tests   -o tests/ZeroAlloc.EventSourcing.PostgreSql.Tests   --framework net9.0

# add all to solution
dotnet sln add src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj
dotnet sln add src/ZeroAlloc.EventSourcing.Aggregates/ZeroAlloc.EventSourcing.Aggregates.csproj
dotnet sln add src/ZeroAlloc.EventSourcing.Generators/ZeroAlloc.EventSourcing.Generators.csproj
dotnet sln add src/ZeroAlloc.EventSourcing.InMemory/ZeroAlloc.EventSourcing.InMemory.csproj
dotnet sln add src/ZeroAlloc.EventSourcing.SqlServer/ZeroAlloc.EventSourcing.SqlServer.csproj
dotnet sln add src/ZeroAlloc.EventSourcing.PostgreSql/ZeroAlloc.EventSourcing.PostgreSql.csproj
dotnet sln add tests/ZeroAlloc.EventSourcing.Tests/ZeroAlloc.EventSourcing.Tests.csproj
dotnet sln add tests/ZeroAlloc.EventSourcing.Aggregates.Tests/ZeroAlloc.EventSourcing.Aggregates.Tests.csproj
dotnet sln add tests/ZeroAlloc.EventSourcing.InMemory.Tests/ZeroAlloc.EventSourcing.InMemory.Tests.csproj
dotnet sln add tests/ZeroAlloc.EventSourcing.SqlServer.Tests/ZeroAlloc.EventSourcing.SqlServer.Tests.csproj
dotnet sln add tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/ZeroAlloc.EventSourcing.PostgreSql.Tests.csproj
```

**Step 2: Create `Directory.Build.props`**

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>preview</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <Authors>ZeroAlloc-Net</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
</Project>
```

> Note: Test projects do not need multi-TFM. Override in each test `.csproj` with `<TargetFramework>net9.0</TargetFramework>` (singular) to avoid the shared `TargetFrameworks` default.

**Step 3: Create `Directory.Packages.props` (Central Package Management)**

Check NuGet.org for latest versions of each package before filling these in. As of writing, use:

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- ZeroAlloc ecosystem — check https://www.nuget.org/profiles/ZeroAlloc-Net for latest -->
    <PackageVersion Include="ZeroAlloc.Results"       Version="*" />
    <PackageVersion Include="ZeroAlloc.Collections"   Version="*" />
    <PackageVersion Include="ZeroAlloc.AsyncEvents"   Version="*" />
    <PackageVersion Include="ZeroAlloc.ValueObjects"  Version="*" />
    <PackageVersion Include="ZeroAlloc.Serialisation" Version="*" />
    <PackageVersion Include="ZeroAlloc.Pipeline"      Version="*" />
    <PackageVersion Include="ZeroAlloc.Analyzers"     Version="*" />
    <!-- Test -->
    <PackageVersion Include="xunit"                   Version="2.*" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk"  Version="*" />
    <PackageVersion Include="FluentAssertions"        Version="6.*" />
  </ItemGroup>
</Project>
```

> Replace `*` with pinned versions after checking NuGet. Using `*` during scaffolding is fine.

**Step 4: Wire up project references**

```bash
# Core — ZeroAlloc deps
dotnet add src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj package ZeroAlloc.Results
dotnet add src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj package ZeroAlloc.Collections
dotnet add src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj package ZeroAlloc.AsyncEvents
dotnet add src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj package ZeroAlloc.ValueObjects

# InMemory — depends on core
dotnet add src/ZeroAlloc.EventSourcing.InMemory/ZeroAlloc.EventSourcing.InMemory.csproj reference src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj
dotnet add src/ZeroAlloc.EventSourcing.InMemory/ZeroAlloc.EventSourcing.InMemory.csproj package ZeroAlloc.AsyncEvents

# Core tests
dotnet add tests/ZeroAlloc.EventSourcing.Tests/ZeroAlloc.EventSourcing.Tests.csproj reference src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj
dotnet add tests/ZeroAlloc.EventSourcing.Tests/ZeroAlloc.EventSourcing.Tests.csproj package FluentAssertions

# InMemory tests
dotnet add tests/ZeroAlloc.EventSourcing.InMemory.Tests/ZeroAlloc.EventSourcing.InMemory.Tests.csproj reference src/ZeroAlloc.EventSourcing.InMemory/ZeroAlloc.EventSourcing.InMemory.csproj
dotnet add tests/ZeroAlloc.EventSourcing.InMemory.Tests/ZeroAlloc.EventSourcing.InMemory.Tests.csproj package FluentAssertions
```

**Step 5: Delete generated boilerplate**

```bash
rm src/ZeroAlloc.EventSourcing/Class1.cs
rm src/ZeroAlloc.EventSourcing.Aggregates/Class1.cs
rm src/ZeroAlloc.EventSourcing.Generators/Class1.cs
rm src/ZeroAlloc.EventSourcing.InMemory/Class1.cs
rm src/ZeroAlloc.EventSourcing.SqlServer/Class1.cs
rm src/ZeroAlloc.EventSourcing.PostgreSql/Class1.cs
rm tests/ZeroAlloc.EventSourcing.Tests/UnitTest1.cs
rm tests/ZeroAlloc.EventSourcing.Aggregates.Tests/UnitTest1.cs
rm tests/ZeroAlloc.EventSourcing.InMemory.Tests/UnitTest1.cs
rm tests/ZeroAlloc.EventSourcing.SqlServer.Tests/UnitTest1.cs
rm tests/ZeroAlloc.EventSourcing.PostgreSql.Tests/UnitTest1.cs
```

**Step 6: Verify build**

```bash
dotnet build ZeroAlloc.EventSourcing.sln
```
Expected: Build succeeded, 0 errors.

**Step 7: Commit**

```bash
git add .
git commit -m "chore: scaffold solution with all src and test projects"
```

---

### Task 2: Core value types

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/StreamId.cs`
- Create: `src/ZeroAlloc.EventSourcing/StreamPosition.cs`
- Create: `src/ZeroAlloc.EventSourcing/EventMetadata.cs`
- Create: `src/ZeroAlloc.EventSourcing/EventEnvelope.cs`
- Create: `src/ZeroAlloc.EventSourcing/RawEvent.cs`
- Create: `src/ZeroAlloc.EventSourcing/AppendResult.cs`
- Create: `src/ZeroAlloc.EventSourcing/StoreError.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/StreamIdTests.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/StreamPositionTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/ZeroAlloc.EventSourcing.Tests/StreamIdTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamIdTests
{
    [Fact]
    public void TwoStreamIds_WithSameValue_AreEqual()
    {
        var a = new StreamId("orders-1");
        var b = new StreamId("orders-1");
        a.Should().Be(b);
    }

    [Fact]
    public void TwoStreamIds_WithDifferentValues_AreNotEqual()
    {
        var a = new StreamId("orders-1");
        var b = new StreamId("orders-2");
        a.Should().NotBe(b);
    }

    [Fact]
    public void StreamId_ToString_ReturnsValue()
    {
        var id = new StreamId("orders-1");
        id.ToString().Should().Be("orders-1");
    }
}

// tests/ZeroAlloc.EventSourcing.Tests/StreamPositionTests.cs
public class StreamPositionTests
{
    [Fact]
    public void Start_IsZero()
    {
        StreamPosition.Start.Value.Should().Be(0);
    }

    [Fact]
    public void End_IsMinusOne()
    {
        StreamPosition.End.Value.Should().Be(-1);
    }

    [Fact]
    public void StreamPosition_Increment_IncreasesByOne()
    {
        var pos = new StreamPosition(5);
        pos.Next().Value.Should().Be(6);
    }
}
```

**Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --framework net9.0
```
Expected: FAIL — `ZeroAlloc.EventSourcing` types not defined yet.

**Step 3: Implement value types**

```csharp
// src/ZeroAlloc.EventSourcing/StreamId.cs
namespace ZeroAlloc.EventSourcing;

public readonly record struct StreamId(string Value)
{
    public override string ToString() => Value;
}
```

```csharp
// src/ZeroAlloc.EventSourcing/StreamPosition.cs
namespace ZeroAlloc.EventSourcing;

public readonly record struct StreamPosition(long Value)
{
    public static readonly StreamPosition Start = new(0);
    public static readonly StreamPosition End = new(-1);

    public StreamPosition Next() => new(Value + 1);
}
```

```csharp
// src/ZeroAlloc.EventSourcing/EventMetadata.cs
namespace ZeroAlloc.EventSourcing;

public readonly record struct EventMetadata(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? CorrelationId,
    Guid? CausationId)
{
    public static EventMetadata New(string eventType, Guid? correlationId = null, Guid? causationId = null)
        => new(
            Guid.CreateVersion7(),
            eventType,
            DateTimeOffset.UtcNow,
            correlationId,
            causationId);
}
```

```csharp
// src/ZeroAlloc.EventSourcing/EventEnvelope.cs
namespace ZeroAlloc.EventSourcing;

public readonly record struct EventEnvelope<TEvent>(
    StreamId StreamId,
    StreamPosition Position,
    TEvent Event,
    EventMetadata Metadata);

// Non-generic version used internally for untyped reads
public readonly record struct EventEnvelope(
    StreamId StreamId,
    StreamPosition Position,
    object Event,
    EventMetadata Metadata);
```

```csharp
// src/ZeroAlloc.EventSourcing/RawEvent.cs
namespace ZeroAlloc.EventSourcing;

// Adapter-level type — holds serialized bytes + metadata, avoids deserialization until needed
public readonly record struct RawEvent(
    StreamPosition Position,
    string EventType,
    ReadOnlyMemory<byte> Payload,
    EventMetadata Metadata);
```

```csharp
// src/ZeroAlloc.EventSourcing/AppendResult.cs
namespace ZeroAlloc.EventSourcing;

public readonly record struct AppendResult(
    StreamId StreamId,
    StreamPosition NextExpectedVersion);
```

```csharp
// src/ZeroAlloc.EventSourcing/StoreError.cs
namespace ZeroAlloc.EventSourcing;

public sealed class StoreError
{
    private StoreError(string code, string message) { Code = code; Message = message; }

    public string Code { get; }
    public string Message { get; }

    public static StoreError Conflict(StreamId id, StreamPosition expected, StreamPosition actual)
        => new("CONFLICT", $"Stream '{id}' expected version {expected.Value} but was {actual.Value}.");

    public static StoreError StreamNotFound(StreamId id)
        => new("STREAM_NOT_FOUND", $"Stream '{id}' does not exist.");

    public static StoreError Unknown(string message)
        => new("UNKNOWN", message);

    public override string ToString() => $"[{Code}] {Message}";
}
```

**Step 4: Run tests to confirm they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --framework net9.0
```
Expected: PASS.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/ tests/ZeroAlloc.EventSourcing.Tests/
git commit -m "feat(core): add StreamId, StreamPosition, EventEnvelope, EventMetadata, RawEvent, AppendResult, StoreError"
```

---

### Task 3: Core interfaces

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/IEventStore.cs`
- Create: `src/ZeroAlloc.EventSourcing/IEventStoreAdapter.cs`
- Create: `src/ZeroAlloc.EventSourcing/IEventTypeRegistry.cs`
- Create: `src/ZeroAlloc.EventSourcing/IEventSubscription.cs`
- Create: `src/ZeroAlloc.EventSourcing/IEventSerializer.cs`

No TDD here — these are interface contracts only. Tests come when the InMemory adapter implements them.

**Step 1: Define interfaces**

```csharp
// src/ZeroAlloc.EventSourcing/IEventStore.cs
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

public interface IEventStore
{
    /// <summary>Appends events to a stream. Fails with StoreError.Conflict if expectedVersion mismatches.</summary>
    ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<object> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default);

    /// <summary>Reads events from a stream starting at <paramref name="from"/>.</summary>
    IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        CancellationToken ct = default);

    /// <summary>Subscribes to new events on a stream from the given position.</summary>
    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
```

```csharp
// src/ZeroAlloc.EventSourcing/IEventStoreAdapter.cs
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

/// <summary>Implemented by storage backends (InMemory, SQL Server, PostgreSQL).</summary>
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

    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
```

```csharp
// src/ZeroAlloc.EventSourcing/IEventTypeRegistry.cs
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Maps event type name strings to CLR types for deserialization.
/// Implementations are source-generated by ZeroAlloc.EventSourcing.Generators.
/// </summary>
public interface IEventTypeRegistry
{
    bool TryGetType(string eventType, out Type? type);
    string GetTypeName(Type type);
}
```

```csharp
// src/ZeroAlloc.EventSourcing/IEventSubscription.cs
namespace ZeroAlloc.EventSourcing;

public interface IEventSubscription : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken ct = default);
    bool IsRunning { get; }
}
```

```csharp
// src/ZeroAlloc.EventSourcing/IEventSerializer.cs
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Thin wrapper around ZeroAlloc.Serialisation.ISerializer{T}.
/// Adapters use this to serialize events to/from byte[].
/// </summary>
public interface IEventSerializer
{
    ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull;
    object Deserialize(ReadOnlyMemory<byte> payload, Type eventType);
}
```

**Step 2: Verify build**

```bash
dotnet build src/ZeroAlloc.EventSourcing/
```
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/
git commit -m "feat(core): add IEventStore, IEventStoreAdapter, IEventTypeRegistry, IEventSubscription, IEventSerializer"
```

---

### Task 4: InMemory adapter — append and read

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryEventStoreAdapter.cs`
- Create: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryStream.cs`
- Test: `tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemoryEventStoreAdapterTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemoryEventStoreAdapterTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public class InMemoryEventStoreAdapterTests
{
    private static RawEvent MakeRaw(StreamPosition pos, string type = "TestEvent")
        => new(pos, type, new byte[] { 1, 2, 3 }, EventMetadata.New(type));

    [Fact]
    public async Task Append_ToNewStream_Succeeds()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var events = new[] { MakeRaw(StreamPosition.Start) }.AsMemory();

        var result = await adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task Append_WithWrongExpectedVersion_ReturnsConflict()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var events = new[] { MakeRaw(StreamPosition.Start) }.AsMemory();

        await adapter.AppendAsync(id, events, StreamPosition.Start);

        // Append again with wrong expected version (still 0 instead of 1)
        var result = await adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Read_AfterAppend_ReturnsAllEvents()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var raw1 = MakeRaw(StreamPosition.Start, "OrderPlaced");
        var raw2 = MakeRaw(new StreamPosition(1), "OrderShipped");
        var events = new[] { raw1, raw2 }.AsMemory();

        await adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderPlaced");
        read[1].EventType.Should().Be("OrderShipped");
    }

    [Fact]
    public async Task Read_FromPosition_ReturnsEventsFromThatPositionOnward()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var events = new[]
        {
            MakeRaw(StreamPosition.Start, "OrderPlaced"),
            MakeRaw(new StreamPosition(1), "OrderShipped"),
            MakeRaw(new StreamPosition(2), "OrderDelivered"),
        }.AsMemory();

        await adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, new StreamPosition(1)))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderShipped");
    }

    [Fact]
    public async Task Read_FromNonExistentStream_ReturnsEmpty()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("does-not-exist");

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().BeEmpty();
    }
}
```

**Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/ --framework net9.0
```
Expected: FAIL — `InMemoryEventStoreAdapter` not defined yet.

**Step 3: Implement `InMemoryStream`**

```csharp
// src/ZeroAlloc.EventSourcing.InMemory/InMemoryStream.cs
namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>Thread-safe, append-only list of RawEvents for a single stream.</summary>
internal sealed class InMemoryStream
{
    private readonly List<RawEvent> _events = new();
    private long _version = 0;

    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Appends events if currentVersion matches expectedVersion.
    /// Returns the new version on success, -1 on conflict.
    /// </summary>
    public bool TryAppend(ReadOnlyMemory<RawEvent> incoming, long expectedVersion, out long newVersion)
    {
        lock (_events)
        {
            if (_version != expectedVersion)
            {
                newVersion = _version;
                return false;
            }

            foreach (var e in incoming.Span)
                _events.Add(e);

            _version += incoming.Length;
            newVersion = _version;
            return true;
        }
    }

    public IEnumerable<RawEvent> ReadFrom(long fromPosition)
    {
        lock (_events)
        {
            // Return a snapshot to avoid holding the lock during iteration
            return _events.Skip((int)fromPosition).ToList();
        }
    }
}
```

**Step 4: Implement `InMemoryEventStoreAdapter`**

```csharp
// src/ZeroAlloc.EventSourcing.InMemory/InMemoryEventStoreAdapter.cs
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.InMemory;

public sealed class InMemoryEventStoreAdapter : IEventStoreAdapter
{
    private readonly ConcurrentDictionary<string, InMemoryStream> _streams = new();

    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var stream = _streams.GetOrAdd(id.Value, _ => new InMemoryStream());

        if (!stream.TryAppend(events, expectedVersion.Value, out var newVersion))
        {
            var error = StoreError.Conflict(id, expectedVersion, new StreamPosition(newVersion));
            return ValueTask.FromResult(Result.Failure<AppendResult, StoreError>(error));
        }

        var result = new AppendResult(id, new StreamPosition(newVersion));
        return ValueTask.FromResult(Result.Success<AppendResult, StoreError>(result));
    }

    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_streams.TryGetValue(id.Value, out var stream))
            yield break;

        foreach (var e in stream.ReadFrom(from.Value))
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => throw new NotImplementedException("Subscriptions implemented in Task 5.");
}
```

> Note: `Result.Success` / `Result.Failure` use `ZeroAlloc.Results` API — check the package docs if the method names differ slightly.

**Step 5: Run tests to confirm they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/ --framework net9.0
```
Expected: PASS (subscription test not written yet — it throws NotImplementedException in Task 5).

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.InMemory/ tests/ZeroAlloc.EventSourcing.InMemory.Tests/
git commit -m "feat(inmemory): implement InMemoryEventStoreAdapter append and read"
```

---

### Task 5: InMemory adapter — subscriptions

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryEventSubscription.cs`
- Modify: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryEventStoreAdapter.cs`
- Modify: `src/ZeroAlloc.EventSourcing.InMemory/InMemoryStream.cs`
- Test: `tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemorySubscriptionTests.cs`

Subscriptions use `ZeroAlloc.AsyncEvents`' `AsyncEvent<RawEvent>` — an in-process, ValueTask-based broadcast. The adapter fires it on every append; subscriptions filter to the target stream.

**Step 1: Write failing subscription tests**

```csharp
// tests/ZeroAlloc.EventSourcing.InMemory.Tests/InMemorySubscriptionTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public class InMemorySubscriptionTests
{
    private static RawEvent MakeRaw(StreamPosition pos, string type = "TestEvent")
        => new(pos, type, new byte[] { 1 }, EventMetadata.New(type));

    [Fact]
    public async Task Subscribe_ReceivesEventsAppendedAfterSubscription()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var received = new List<RawEvent>();

        var sub = await adapter.SubscribeAsync(id, StreamPosition.Start,
            (e, _) => { received.Add(e); return ValueTask.CompletedTask; });
        await sub.StartAsync();

        await adapter.AppendAsync(id, new[] { MakeRaw(StreamPosition.Start) }.AsMemory(), StreamPosition.Start);

        // Give async handler a moment to fire
        await Task.Delay(50);

        received.Should().HaveCount(1);
    }

    [Fact]
    public async Task Subscribe_AfterDispose_StopsReceivingEvents()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var received = new List<RawEvent>();

        var sub = await adapter.SubscribeAsync(id, StreamPosition.Start,
            (e, _) => { received.Add(e); return ValueTask.CompletedTask; });
        await sub.StartAsync();
        await sub.DisposeAsync();

        await adapter.AppendAsync(id, new[] { MakeRaw(StreamPosition.Start) }.AsMemory(), StreamPosition.Start);
        await Task.Delay(50);

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task Subscription_IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");

        var sub = await adapter.SubscribeAsync(id, StreamPosition.Start, (_, _) => ValueTask.CompletedTask);

        sub.IsRunning.Should().BeFalse();
        await sub.StartAsync();
        sub.IsRunning.Should().BeTrue();
        await sub.DisposeAsync();
        sub.IsRunning.Should().BeFalse();
    }
}
```

**Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/ --framework net9.0 --filter "InMemorySubscriptionTests"
```
Expected: FAIL — `SubscribeAsync` throws `NotImplementedException`.

**Step 3: Add broadcast support to `InMemoryStream`**

Add a `ZeroAlloc.AsyncEvents` `AsyncEvent<RawEvent>` field to `InMemoryStream` and fire it on append:

```csharp
// Modify InMemoryStream.cs — add after existing fields:
using ZeroAlloc.AsyncEvents;

// Inside InMemoryStream class:
private readonly AsyncEvent<RawEvent> _broadcast = new();

public IDisposable Subscribe(Func<RawEvent, CancellationToken, ValueTask> handler)
    => _broadcast.Subscribe(handler);  // check ZeroAlloc.AsyncEvents API for exact subscribe method

// Modify TryAppend — after _version += incoming.Length, fire broadcast:
foreach (var e in incoming.Span)
    _ = _broadcast.InvokeAsync(e, CancellationToken.None); // fire-and-forget
```

> Check `ZeroAlloc.AsyncEvents` README for exact API — `Subscribe`, `InvokeAsync`, and the return type of `Subscribe` (likely `IDisposable` or `AsyncEventRegistration`).

**Step 4: Implement `InMemoryEventSubscription`**

```csharp
// src/ZeroAlloc.EventSourcing.InMemory/InMemoryEventSubscription.cs
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.InMemory;

internal sealed class InMemoryEventSubscription : IEventSubscription
{
    private readonly IDisposable _registration;
    private bool _running;
    private bool _disposed;

    internal InMemoryEventSubscription(IDisposable registration)
    {
        _registration = registration;
    }

    public bool IsRunning => _running && !_disposed;

    public ValueTask StartAsync(CancellationToken ct = default)
    {
        _running = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _running = false;
        _registration.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

**Step 5: Wire `SubscribeAsync` in `InMemoryEventStoreAdapter`**

Replace the `NotImplementedException` in `SubscribeAsync`:

```csharp
public ValueTask<IEventSubscription> SubscribeAsync(
    StreamId id,
    StreamPosition from,
    Func<RawEvent, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)
{
    var stream = _streams.GetOrAdd(id.Value, _ => new InMemoryStream());
    var registration = stream.Subscribe(handler);
    IEventSubscription sub = new InMemoryEventSubscription(registration);
    return ValueTask.FromResult(sub);
}
```

**Step 6: Run all InMemory tests**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests/ --framework net9.0
```
Expected: All PASS.

**Step 7: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.InMemory/ tests/ZeroAlloc.EventSourcing.InMemory.Tests/
git commit -m "feat(inmemory): add live subscriptions via ZeroAlloc.AsyncEvents"
```

---

### Task 6: EventStore facade

The `IEventStore` is the public-facing API consumers use. It wraps an `IEventStoreAdapter`, handles serialization via `IEventSerializer`, and deserializes `RawEvent` → `EventEnvelope` using `IEventTypeRegistry`.

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/EventStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/EventStoreTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/ZeroAlloc.EventSourcing.Tests/EventStoreTests.cs
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

// Simple test event
public record OrderPlaced(string OrderId, decimal Total);

// Minimal in-process serializer for tests (JSON via STJ)
internal sealed class JsonEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}

// Minimal type registry for tests
internal sealed class TestTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<string, Type> _map = new()
    {
        ["OrderPlaced"] = typeof(OrderPlaced),
    };

    public bool TryGetType(string eventType, out Type? type) => _map.TryGetValue(eventType, out type);
    public string GetTypeName(Type type) => type.Name;
}

public class EventStoreTests
{
    private static IEventStore BuildStore()
    {
        var adapter = new InMemoryEventStoreAdapter();
        return new EventStore(adapter, new JsonEventSerializer(), new TestTypeRegistry());
    }

    [Fact]
    public async Task Append_ThenRead_RoundTripsEvent()
    {
        var store = BuildStore();
        var id = new StreamId("orders-1");
        var @event = new OrderPlaced("ORD-001", 99.99m);

        var appendResult = await store.AppendAsync(id, new object[] { @event }.AsMemory(), StreamPosition.Start);
        appendResult.IsSuccess.Should().BeTrue();

        var events = new List<EventEnvelope>();
        await foreach (var e in store.ReadAsync(id))
            events.Add(e);

        events.Should().HaveCount(1);
        var read = events[0].Event.Should().BeOfType<OrderPlaced>().Subject;
        read.OrderId.Should().Be("ORD-001");
        read.Total.Should().Be(99.99m);
    }

    [Fact]
    public async Task Append_SetsEventMetadata_WithVersion7Guid()
    {
        var store = BuildStore();
        var id = new StreamId("orders-1");

        await store.AppendAsync(id, new object[] { new OrderPlaced("X", 1m) }.AsMemory(), StreamPosition.Start);

        var events = new List<EventEnvelope>();
        await foreach (var e in store.ReadAsync(id))
            events.Add(e);

        events[0].Metadata.EventId.Should().NotBe(Guid.Empty);
        events[0].Metadata.EventType.Should().Be("OrderPlaced");
        events[0].Metadata.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }
}
```

**Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --framework net9.0 --filter "EventStoreTests"
```
Expected: FAIL — `EventStore` not defined.

**Step 3: Implement `EventStore`**

```csharp
// src/ZeroAlloc.EventSourcing/EventStore.cs
using System.Runtime.CompilerServices;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

public sealed class EventStore : IEventStore
{
    private readonly IEventStoreAdapter _adapter;
    private readonly IEventSerializer _serializer;
    private readonly IEventTypeRegistry _registry;

    public EventStore(IEventStoreAdapter adapter, IEventSerializer serializer, IEventTypeRegistry registry)
    {
        _adapter = adapter;
        _serializer = serializer;
        _registry = registry;
    }

    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<object> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var raw = new RawEvent[events.Length];
        for (var i = 0; i < events.Length; i++)
        {
            var e = events.Span[i];
            var typeName = _registry.GetTypeName(e.GetType());
            raw[i] = new RawEvent(
                new StreamPosition(expectedVersion.Value + i),
                typeName,
                _serializer.Serialize(e),
                EventMetadata.New(typeName));
        }

        return await _adapter.AppendAsync(id, raw.AsMemory(), expectedVersion, ct);
    }

    public async IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var raw in _adapter.ReadAsync(id, from, ct))
        {
            if (!_registry.TryGetType(raw.EventType, out var type) || type is null)
                continue; // unknown event type — skip (forward-compatible)

            var deserialized = _serializer.Deserialize(raw.Payload, type);
            yield return new EventEnvelope(id, raw.Position, deserialized, raw.Metadata);
        }
    }

    public async ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        return await _adapter.SubscribeAsync(id, from, async (raw, token) =>
        {
            if (!_registry.TryGetType(raw.EventType, out var type) || type is null)
                return;

            var deserialized = _serializer.Deserialize(raw.Payload, type);
            var envelope = new EventEnvelope(id, raw.Position, deserialized, raw.Metadata);
            await handler(envelope, token);
        }, ct);
    }
}
```

**Step 4: Run all tests**

```bash
dotnet test ZeroAlloc.EventSourcing.sln --framework net9.0
```
Expected: All PASS.

**Step 5: Run on net10.0 as well**

```bash
dotnet test ZeroAlloc.EventSourcing.sln --framework net10.0
```
Expected: All PASS.

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/ tests/ZeroAlloc.EventSourcing.Tests/
git commit -m "feat(core): implement EventStore facade with serialization and type registry"
```

---

### Task 7: CI — GitHub Actions

**Files:**
- Create: `.github/workflows/ci.yml`

**Step 1: Create workflow**

```yaml
# .github/workflows/ci.yml
name: CI

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main]

jobs:
  build-and-test:
    name: Build & Test (.NET ${{ matrix.dotnet }})
    runs-on: ubuntu-latest
    strategy:
      matrix:
        dotnet: ['9.0.x', '10.0.x']

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET ${{ matrix.dotnet }}
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ matrix.dotnet }}

      - name: Restore
        run: dotnet restore ZeroAlloc.EventSourcing.sln

      - name: Build
        run: dotnet build ZeroAlloc.EventSourcing.sln --no-restore --configuration Release

      - name: Test
        run: dotnet test ZeroAlloc.EventSourcing.sln --no-build --configuration Release --logger "trx;LogFileName=results.trx"

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-${{ matrix.dotnet }}
          path: "**/*.trx"
```

> Note: `.NET 10.0.x` may require `allow-prereleases: true` in `setup-dotnet` until .NET 10 GA ships.

**Step 2: Commit**

```bash
git add .github/
git commit -m "ci: add GitHub Actions workflow for net9 and net10 build and test"
```

---

## Phase boundary

**Phase 1 is complete when:**
- `dotnet test ZeroAlloc.EventSourcing.sln` passes on both `net9.0` and `net10.0`
- CI is green on push to `main`
- All 6 src projects and 5 test projects build clean with `TreatWarningsAsErrors`

**Phase 2 — Aggregate Layer** covers:
`Aggregate<TId,TState>`, `IAggregateRepository`, source generator (`ZeroAlloc.EventSourcing.Generators`), snapshot support. See separate plan: `docs/plans/2026-04-01-phase2-aggregates.md` (to be written).

**Phase 3 — SQL Adapters** covers PostgreSQL + SQL Server adapters with migration scripts.

**Phase 4 — Subscriptions** covers catch-up + live subscription builder and `IEventHandler<T>`.

**Phase 5 — Projections & Snapshots** covers `Projection<TReadModel>` and `ISnapshotStore<TState>`.
