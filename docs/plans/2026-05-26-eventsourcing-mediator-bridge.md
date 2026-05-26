# ZeroAlloc.EventSourcing.Mediator v1.0.0 Implementation Plan (revised)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship `ZeroAlloc.EventSourcing.Mediator` v1.0.0 as one nupkg containing a runtime bridge + a bundled Roslyn generator. Bridge republishes LIVE committed `IEventStore` events through `IMediator.Publish` (typed, AOT-clean). History replays filtered positionally (subscribe from `StreamPosition.End`).

**Architecture:** Two assemblies in one nupkg. Runtime sub-package declares `INotificationDispatcher` (public interface) + `EventStoreMediatorBridge` (internal `IHostedService`) + `EventSourcingBuilderMediatorExtensions` (`static partial class` with a `partial void EnsureDispatcherRegistered(IServiceCollection)`). Bundled `ZeroAlloc.EventSourcing.Mediator.Generator` (TFM `netstandard2.0`, `IsRoslynComponent`, `IsPackable=false`) discovers `INotification` types in the consuming compilation and emits `GeneratedNotificationDispatcher : INotificationDispatcher` with a typed switch + the partial-method body that registers the dispatcher. Mirrors the bundle pattern used in `ZeroAlloc.Flux` and `ZeroAlloc.Mediator`.

**Tech Stack:** .NET 8 / .NET 9 / .NET 10 multi-targeted (matching the EventSourcing repo's TFM convention); Roslyn `Microsoft.CodeAnalysis.CSharp` 4.14 incremental generator; xUnit + VerifyXunit (snapshot tests); `Microsoft.Extensions.Hosting.Abstractions` + `Microsoft.Extensions.Logging.Abstractions`; `Microsoft.CodeAnalysis.PublicApiAnalyzers`.

**Design doc:** `docs/plans/2026-05-26-eventsourcing-mediator-bridge-design.md` (revised at `4e72486`).

**Working branch:** `feat/eventsourcing-mediator-bridge`. After the prior reset to `7f1f910`, the branch state is: design + revised design committed + plan-v1 committed + scaffold csproj `3da3d5e` + slnx/release-please wiring `7f1f910`. This plan picks up from there.

**Key context:**

- **`IMediator` is generator-emitted into the consuming compilation, not in `ZeroAlloc.Mediator` nupkg.** This is the entire reason the bridge package needs its own generator.
- **`StreamPosition.End`** is a public static constant on `StreamPosition` (`src/ZeroAlloc.EventSourcing/StreamPosition.cs:10` — `public static readonly StreamPosition End = new(-1);`). Use it directly.
- **`Directory.Packages.props`** — repo uses central package management. The earlier subagent already added `ZeroAlloc.Mediator 4.1.4`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` entries during the prior Task-1-execution attempt. Those entries persisted across the `git reset --hard 7f1f910` since the reset rolled back the scaffold csproj edits — verify and re-add if necessary.
- **Bundle pattern reference:** look at how `ZeroAlloc.Flux` bundles `ZeroAlloc.Flux.Generator` into the core nupkg via `<ProjectReference OutputItemType="Analyzer" />` + a `<None Include="$(OutputPath)..\..\..\ZeroAlloc.Flux.Generator\bin\$(Configuration)\netstandard2.0\ZeroAlloc.Flux.Generator.dll" Pack="true" PackagePath="analyzers/dotnet/cs" />` block. Mirror it.

---

## Task 1: (already done — `3da3d5e`) Runtime sub-package csproj scaffold

This commit already landed (`3da3d5e`). It scaffolded `src/ZeroAlloc.EventSourcing.Mediator/ZeroAlloc.EventSourcing.Mediator.csproj` + empty PublicAPI files. **Skip — no work to do.**

The csproj as written includes a `<PackageReference Include="ZeroAlloc.Mediator" />`. Under the revised design the runtime doesn't reference `IMediator` directly (only the generator does, via reflection on the consuming compilation's symbols). The Mediator dependency can be DROPPED from the runtime csproj — but it doesn't hurt to keep it for now (events that the bridge filters via `is INotification` need `INotification` to be visible in the runtime's compilation, which is what the Mediator package reference provides). **Keep the Mediator reference.**

---

## Task 2: (already done — `7f1f910`) Add to slnx + release-please

This commit landed already. The runtime sub-package is registered in `ZeroAlloc.EventSourcing.slnx` + `release-please-config.json` + `.release-please-manifest.json`.

**To do in this pass:** add the new `src/ZeroAlloc.EventSourcing.Mediator.Generator` project path to `ZeroAlloc.EventSourcing.slnx` once Task 3 creates it. NOT to release-please-config — the generator is `IsPackable=false` and bundles into the runtime nupkg.

---

## Task 3: Scaffold the generator sub-package

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Mediator.Generator/ZeroAlloc.EventSourcing.Mediator.Generator.csproj`
- Modify: `ZeroAlloc.EventSourcing.slnx` (add the generator project path)

**Step 1: Author the generator csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <RootNamespace>ZeroAlloc.EventSourcing.Mediator.Generator</RootNamespace>
    <IsRoslynComponent>true</IsRoslynComponent>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <NoWarn>$(NoWarn);RS2008</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

Versions are inherited from `Directory.Packages.props` (central package management). If those entries don't exist, add them: `Microsoft.CodeAnalysis.CSharp 4.14.0` and `Microsoft.CodeAnalysis.Analyzers 3.11.0`.

**Step 2: Add InternalsVisibleTo for test project**

Append to the csproj:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ZeroAlloc.EventSourcing.Mediator.Generator.Tests" />
  </ItemGroup>
```

**Step 3: Wire bundling into the runtime nupkg**

Open `src/ZeroAlloc.EventSourcing.Mediator/ZeroAlloc.EventSourcing.Mediator.csproj`. Append:

```xml
  <ItemGroup>
    <ProjectReference Include="..\ZeroAlloc.EventSourcing.Mediator.Generator\ZeroAlloc.EventSourcing.Mediator.Generator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\ZeroAlloc.EventSourcing.Mediator.Generator\bin\$(Configuration)\netstandard2.0\ZeroAlloc.EventSourcing.Mediator.Generator.dll"
          Pack="true"
          PackagePath="analyzers/dotnet/cs"
          Visible="false" />
  </ItemGroup>
```

Verify this pattern against `ZeroAlloc.Flux`'s actual bundling — if Flux uses different MSBuild variables (e.g., `$(OutputPath)`), match Flux exactly.

**Step 4: Add to slnx**

`ZeroAlloc.EventSourcing.slnx` — add inside the `/src/` folder:

```xml
<Project Path="src/ZeroAlloc.EventSourcing.Mediator.Generator/ZeroAlloc.EventSourcing.Mediator.Generator.csproj" />
```

**Step 5: Verify the build**

```bash
cd c:/Projects/Prive/ZeroAlloc/ZeroAlloc.EventSourcing
dotnet build src/ZeroAlloc.EventSourcing.Mediator.Generator/ -c Release
dotnet build src/ZeroAlloc.EventSourcing.Mediator/ -c Release
```

Expected: both SUCCEEDED. Generator builds to an empty analyzer DLL (no source yet); runtime builds with the analyzer reference resolving to that empty DLL.

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Mediator.Generator/ \
        src/ZeroAlloc.EventSourcing.Mediator/ZeroAlloc.EventSourcing.Mediator.csproj \
        ZeroAlloc.EventSourcing.slnx
git commit -m "chore: scaffold ZeroAlloc.EventSourcing.Mediator.Generator csproj + bundle into runtime nupkg

Generator targets netstandard2.0 with IsRoslynComponent. Runtime csproj
references it as OutputItemType=Analyzer and packs the DLL into
analyzers/dotnet/cs of the runtime nupkg. Bundling pattern matches
ZeroAlloc.Flux. Source files land in subsequent tasks."
```

---

## Task 4: `INotificationDispatcher` interface + PublicAPI

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Mediator/INotificationDispatcher.cs`
- Modify: `src/ZeroAlloc.EventSourcing.Mediator/PublicAPI.Unshipped.txt`

**Step 1: Write the interface**

```csharp
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Mediator;

/// <summary>
/// Dispatches an ES event payload to Mediator's typed <c>Publish&lt;T&gt;</c>. The bridge
/// package declares this interface; the bundled source generator emits the concrete
/// implementation that switches over every <see cref="INotification"/> type discovered in
/// the consuming compilation. Users never implement this interface themselves.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Dispatches <paramref name="event"/> via <c>IMediator.Publish&lt;TConcrete&gt;</c>.
    /// Returns <see cref="ValueTask.CompletedTask"/> for events that aren't a discovered
    /// <see cref="INotification"/> type (silent skip).
    /// </summary>
    ValueTask DispatchAsync(object @event, CancellationToken ct);
}
```

**Step 2: PublicAPI append**

`src/ZeroAlloc.EventSourcing.Mediator/PublicAPI.Unshipped.txt`:

```
ZeroAlloc.EventSourcing.Mediator.INotificationDispatcher
ZeroAlloc.EventSourcing.Mediator.INotificationDispatcher.DispatchAsync(object! event, System.Threading.CancellationToken ct) -> System.Threading.Tasks.ValueTask
```

If RS0016/RS0017 fires, match the analyzer's suggested text (likely needs the parameter name `@event` escaped or different `!`/`?` annotation).

**Step 3: Verify build**

```bash
dotnet build src/ZeroAlloc.EventSourcing.Mediator/ -c Release
```

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Mediator/INotificationDispatcher.cs \
        src/ZeroAlloc.EventSourcing.Mediator/PublicAPI.Unshipped.txt
git commit -m "feat(esmediator): add public INotificationDispatcher interface

Declared in the runtime; implemented by the bundled generator with a
type switch over discovered INotification types. Users never write
their own implementation."
```

---

## Task 5: `EventStoreMediatorBridge` (uses `INotificationDispatcher`)

**File:** Create `src/ZeroAlloc.EventSourcing.Mediator/EventStoreMediatorBridge.cs` per the revised design doc verbatim.

Key shape:
- Constructor takes `IEventStore`, `INotificationDispatcher`, `ILogger<EventStoreMediatorBridge>`, `StreamId`.
- `StartAsync` calls `_store.SubscribeAsync(_streamId, StreamPosition.End, OnEventAsync, ct)`.
- `OnEventAsync`: tests `envelope.Event is INotification`; if yes, `await _dispatch.DispatchAsync(envelope.Event, ct)` inside try/catch (log + swallow on exception); if no, debug-log + return.

**Verify build:**
```bash
dotnet build src/ZeroAlloc.EventSourcing.Mediator/ -c Release
```

**Commit:**
```bash
git add src/ZeroAlloc.EventSourcing.Mediator/EventStoreMediatorBridge.cs
git commit -m "feat(esmediator): add EventStoreMediatorBridge IHostedService

Per-stream bridge subscribing from StreamPosition.End so history
replays are filtered positionally. Events implementing INotification
are dispatched through INotificationDispatcher (generator-emitted);
non-notification events skipped with debug log; handler exceptions
logged + swallowed."
```

---

## Task 6: `EventSourcingBuilderMediatorExtensions` (static partial class)

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Mediator/EventSourcingBuilderMediatorExtensions.cs`
- Modify: `src/ZeroAlloc.EventSourcing.Mediator/PublicAPI.Unshipped.txt`

**Step 1: Write the extension**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Mediator;

/// <summary>
/// Fluent extensions on <see cref="EventSourcingBuilder"/> for bridging LIVE committed events
/// to <see cref="ZeroAlloc.Mediator.IMediator.Publish"/>. The bundled source generator emits
/// the <see cref="INotificationDispatcher"/> implementation + registration via the
/// <see cref="EnsureDispatcherRegistered"/> partial method below.
/// </summary>
public static partial class EventSourcingBuilderMediatorExtensions
{
    /// <summary>
    /// Registers an <see cref="IHostedService"/> bridging <paramref name="streamId"/> to
    /// Mediator. LIVE events implementing <c>INotification</c> are published; history
    /// replays are filtered out positionally. Multiple streams = multiple calls.
    /// </summary>
    public static EventSourcingBuilder PublishViaMediator(this EventSourcingBuilder builder, StreamId streamId)
    {
        builder.Services.AddSingleton<IHostedService>(sp =>
            new EventStoreMediatorBridge(
                sp.GetRequiredService<IEventStore>(),
                sp.GetRequiredService<INotificationDispatcher>(),
                sp.GetRequiredService<ILogger<EventStoreMediatorBridge>>(),
                streamId));
        EnsureDispatcherRegistered(builder.Services);
        return builder;
    }

    /// <summary>
    /// Partial method implemented by the bundled source generator. Registers
    /// <see cref="INotificationDispatcher"/> as a singleton. If the generator did NOT run
    /// (e.g., no <c>INotification</c> types in the consuming compilation), this method is a
    /// no-op and <see cref="INotificationDispatcher"/> resolution at runtime will throw — a
    /// signal that the bridge has nothing to dispatch.
    /// </summary>
    static partial void EnsureDispatcherRegistered(IServiceCollection services);
}
```

**Step 2: PublicAPI append**

```
ZeroAlloc.EventSourcing.Mediator.EventSourcingBuilderMediatorExtensions
static ZeroAlloc.EventSourcing.Mediator.EventSourcingBuilderMediatorExtensions.PublishViaMediator(this ZeroAlloc.EventSourcing.EventSourcingBuilder! builder, ZeroAlloc.EventSourcing.StreamId streamId) -> ZeroAlloc.EventSourcing.EventSourcingBuilder!
```

**Step 3: Verify build**

Expected: SUCCEEDED. The `partial void EnsureDispatcherRegistered` has no implementing partial yet (Task 8's generator emits it) — that's allowed for `partial void` declarations; missing implementations become no-ops.

**Step 4: Commit (Release-As: 1.0.0)**

```bash
git add src/ZeroAlloc.EventSourcing.Mediator/EventSourcingBuilderMediatorExtensions.cs \
        src/ZeroAlloc.EventSourcing.Mediator/PublicAPI.Unshipped.txt
git commit -m "feat(esmediator): expose PublishViaMediator fluent extension

Static partial class with a partial-method extension point
(EnsureDispatcherRegistered) that the bundled source generator fills
in. The user-facing call is a single .PublishViaMediator(StreamId) on
EventSourcingBuilder. Multi-stream: call multiple times.

Release-As: 1.0.0"
```

---

## Task 7: Generator — Diagnostics + Discovery

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Mediator.Generator/Diagnostics.cs`
- Create: `src/ZeroAlloc.EventSourcing.Mediator.Generator/NotificationDiscovery.cs`

**Step 1: Diagnostics**

```csharp
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.EventSourcing.Mediator.Generator;

internal static class Diagnostics
{
    private const string Category = "ZeroAlloc.EventSourcing.Mediator";

    public static readonly DiagnosticDescriptor ZESM001_NoNotificationTypesFound = new(
        id: "ZESM001",
        title: "Bridge generator found zero INotification-implementing types",
        messageFormat: "No INotification-implementing types found in the consuming compilation — the bridge will throw at runtime if appended events don't match a discovered type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
```

**Step 2: NotificationDiscovery**

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZeroAlloc.EventSourcing.Mediator.Generator;

/// <summary>
/// Discovers every type in the consuming compilation that implements
/// <c>ZeroAlloc.Mediator.INotification</c>. Returns null for non-matching nodes; the pipeline
/// filters nulls via Where.
/// </summary>
internal static class NotificationDiscovery
{
    public const string NotificationInterfaceFullName = "ZeroAlloc.Mediator.INotification";

    public static NotificationTypeInfo? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not TypeDeclarationSyntax decl) return null;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(decl, ct) as INamedTypeSymbol;
        if (symbol is null) return null;

        var hasInterface = symbol.AllInterfaces
            .Any(i => i.ToDisplayString() == NotificationInterfaceFullName);
        if (!hasInterface) return null;

        return new NotificationTypeInfo(
            FullyQualifiedName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }
}

internal sealed record NotificationTypeInfo(string FullyQualifiedName);
```

`NotificationTypeInfo` is cache-friendly (string only, equatable) — important for Roslyn incremental cache hygiene.

**Step 3: Verify build**

```bash
dotnet build src/ZeroAlloc.EventSourcing.Mediator.Generator/ -c Release
```

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Mediator.Generator/Diagnostics.cs \
        src/ZeroAlloc.EventSourcing.Mediator.Generator/NotificationDiscovery.cs
git commit -m "feat(esmediator.gen): declare ZESM001 + INotification discovery walker

ZESM001 (Warning) fires when zero INotification types are found in
the consuming compilation. NotificationDiscovery.Transform uses
SyntaxProvider + SemanticModel.GetDeclaredSymbol to detect types
whose AllInterfaces includes ZeroAlloc.Mediator.INotification. Output
is a cache-friendly NotificationTypeInfo record (string FQN only)."
```

---

## Task 8: Generator — Emitter + IIncrementalGenerator entry point

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Mediator.Generator/DispatcherEmitter.cs`
- Create: `src/ZeroAlloc.EventSourcing.Mediator.Generator/EventSourcingMediatorGenerator.cs`

**Step 1: DispatcherEmitter**

```csharp
using System.Collections.Immutable;
using System.Text;

namespace ZeroAlloc.EventSourcing.Mediator.Generator;

internal static class DispatcherEmitter
{
    public static string Emit(ImmutableArray<NotificationTypeInfo> notifications)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine("using ZeroAlloc.Mediator;");
        sb.AppendLine();
        sb.AppendLine("namespace ZeroAlloc.EventSourcing.Mediator.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    internal sealed class GeneratedNotificationDispatcher : ZeroAlloc.EventSourcing.Mediator.INotificationDispatcher");
        sb.AppendLine("    {");
        sb.AppendLine("        private readonly IMediator _mediator;");
        sb.AppendLine();
        sb.AppendLine("        public GeneratedNotificationDispatcher(IMediator mediator)");
        sb.AppendLine("        {");
        sb.AppendLine("            _mediator = mediator;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public ValueTask DispatchAsync(object @event, CancellationToken ct) => @event switch");
        sb.AppendLine("        {");
        foreach (var n in notifications)
        {
            // Each arm: `case T t: return _mediator.Publish(t, ct);`
            sb.Append("            ").Append(n.FullyQualifiedName).AppendLine(" __evt => _mediator.Publish(__evt, ct),");
        }
        sb.AppendLine("            _ => ValueTask.CompletedTask");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("namespace ZeroAlloc.EventSourcing.Mediator");
        sb.AppendLine("{");
        sb.AppendLine("    public static partial class EventSourcingBuilderMediatorExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        static partial void EnsureDispatcherRegistered(IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            services.TryAddSingleton<INotificationDispatcher, Generated.GeneratedNotificationDispatcher>();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
```

Note: the switch arm uses pattern syntax `Type __evt => expression` (C# 8+ switch expression). `__evt` is a unique local per arm to keep the compiler happy with multiple arms returning the same expression.

**Step 2: IIncrementalGenerator entry point**

```csharp
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ZeroAlloc.EventSourcing.Mediator.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class EventSourcingMediatorGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var notifications = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (n, _) => n is TypeDeclarationSyntax,
                transform: NotificationDiscovery.Transform)
            .Where(static n => n is not null)
            .Select(static (n, _) => n!)
            .Collect();

        context.RegisterSourceOutput(notifications, (spc, notifs) =>
        {
            if (notifs.IsDefaultOrEmpty)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ZESM001_NoNotificationTypesFound,
                    location: null));
                // Still emit the partial-method body so EnsureDispatcherRegistered is a no-op
                // rather than a compile error from a non-existent partial implementation.
                spc.AddSource("EventSourcingMediatorGenerated.g.cs",
                    SourceText.From(DispatcherEmitter.Emit(ImmutableArray<NotificationTypeInfo>.Empty), Encoding.UTF8));
                return;
            }

            // Dedupe by FQN — distinct discovered types only.
            var deduped = notifs.Distinct().ToImmutableArray();
            spc.AddSource("EventSourcingMediatorGenerated.g.cs",
                SourceText.From(DispatcherEmitter.Emit(deduped), Encoding.UTF8));
        });
    }
}
```

**Step 3: Verify build**

```bash
dotnet build src/ZeroAlloc.EventSourcing.Mediator.Generator/ -c Release
```

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Mediator.Generator/DispatcherEmitter.cs \
        src/ZeroAlloc.EventSourcing.Mediator.Generator/EventSourcingMediatorGenerator.cs
git commit -m "feat(esmediator.gen): emit GeneratedNotificationDispatcher + DI registration

DispatcherEmitter produces a type-switch dispatcher with one arm per
discovered INotification type, each calling _mediator.Publish<T>.
EventSourcingMediatorGenerator wires the pipeline: SyntaxProvider →
NotificationDiscovery → Distinct → RegisterSourceOutput. Empty
discovery still emits the partial-method no-op body (so the runtime
csproj compiles) and fires ZESM001."
```

---

## Task 9: Generator snapshot tests

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.Mediator.Generator.Tests/ZeroAlloc.EventSourcing.Mediator.Generator.Tests.csproj`
- Create: `tests/ZeroAlloc.EventSourcing.Mediator.Generator.Tests/TestHarness.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Mediator.Generator.Tests/DispatcherEmitterTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Mediator.Generator.Tests/Snapshots/*.verified.txt` (after first-run review)
- Modify: `ZeroAlloc.EventSourcing.slnx` (add the test project)

**Step 1: csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.Mediator.Generator\ZeroAlloc.EventSourcing.Mediator.Generator.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" />
    <PackageReference Include="Verify.Xunit" />
    <PackageReference Include="Verify.SourceGenerators" />
  </ItemGroup>
</Project>
```

Versions from central package management. If `Verify.Xunit` / `Verify.SourceGenerators` aren't in `Directory.Packages.props`, add `Verify.Xunit 28.0.0` and `Verify.SourceGenerators 2.5.0`.

**Step 2: TestHarness**

Port the pattern from `ZeroAlloc.Flux.Generator.Tests/TestHarness.cs` — but simpler since this generator has just one output file. Expose:

```csharp
public static class TestHarness
{
    public static Task Verify(string source);
    public static ImmutableArray<Diagnostic> RunDiagnostics(string source);
    public static (CSharpCompilation comp, ImmutableArray<Diagnostic> diags) Run(string source);
}
```

Reference `ZeroAlloc.Mediator` so the compilation includes `INotification`. Use a `MetadataReference` to the runtime assembly + Mediator assembly.

**Step 3: Snapshot tests**

```csharp
public sealed class DispatcherEmitterTests
{
    [Fact]
    public Task SingleNotificationType_SnapshotMatches() => TestHarness.Verify("""
        using ZeroAlloc.Mediator;
        namespace MyApp;
        public readonly record struct UserCreated(System.Guid Id) : INotification;
        """);

    [Fact]
    public Task MultipleNotificationTypes_SnapshotMatches() => TestHarness.Verify("""
        using ZeroAlloc.Mediator;
        namespace MyApp;
        public readonly record struct UserCreated(System.Guid Id) : INotification;
        public readonly record struct OrderPlaced(decimal Total) : INotification;
        """);

    [Fact]
    public void EmptyCompilation_FiresZESM001()
    {
        var diags = TestHarness.RunDiagnostics("namespace MyApp;");
        Assert.Contains(diags, d => d.Id == "ZESM001");
    }
}
```

**Step 4: First-run review**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Mediator.Generator.Tests/ -c Release
```

First run creates `.received.txt` files. Inspect each, verify the emitted code looks correct (correct namespace, balanced braces, correct switch arms), rename to `.verified.txt`, re-run.

**Step 5: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.Mediator.Generator.Tests/ ZeroAlloc.EventSourcing.slnx
git commit -m "test(esmediator.gen): snapshot tests for emit + ZESM001"
```

---

## Task 10: Runtime tests (using the bundled generator)

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.Mediator.Tests/ZeroAlloc.EventSourcing.Mediator.Tests.csproj`
- Create: `tests/ZeroAlloc.EventSourcing.Mediator.Tests/TestFixtures.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Mediator.Tests/BridgeTests.cs`
- Modify: `ZeroAlloc.EventSourcing.slnx`

Per the prior plan version. The 5 tests still apply unchanged — the generator runs in the test project's compilation, discovers `UserCreated` etc., emits the dispatcher, the bridge's `INotificationDispatcher` dependency resolves correctly.

**Critical:** the test project MUST have the runtime project AND the generator project both as references — the generator project as `OutputItemType="Analyzer"` so it runs against the test fixtures.

```xml
<ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.Mediator\ZeroAlloc.EventSourcing.Mediator.csproj" />
<ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.Mediator.Generator\ZeroAlloc.EventSourcing.Mediator.Generator.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
<ProjectReference Include="..\..\src\ZeroAlloc.EventSourcing.InMemory\ZeroAlloc.EventSourcing.InMemory.csproj" />
```

Plus the test fixtures need `using ZeroAlloc.Mediator;` and `: INotification` on the event records. Mediator's package reference comes transitively from the runtime project.

For Mediator handler registration: each handler needs to be wired so Mediator's source generator (in the test compilation) emits `IMediator.Publish<UserCreated>` etc. This may require explicit `INotificationHandler<UserCreated>` registration in DI per the same pattern Mediator's own tests use.

Run + commit per plan-v1 Task 5.

---

## Task 11: AOT smoke

Per plan-v1 Task 6. The sample now works because the generator emits typed dispatch — no reflection. Verify 0 `IL2026`/`IL3050` warnings on `dotnet publish -p:PublishAot=true`.

---

## Task 12: Push + PR + admin-merge + NuGet verify

Per plan-v1 Task 7.

---

## Task 13: Backlog hygiene

Per plan-v1 Task 8 — mark workspace `docs/BACKLOG.md` entry shipped.

---

## Verification checklist

- [ ] Task 3: generator csproj scaffolded + bundled into runtime nupkg via analyzer reference.
- [ ] Task 4: `INotificationDispatcher` public interface declared.
- [ ] Task 5: `EventStoreMediatorBridge` consumes `INotificationDispatcher`, NOT `IMediator`.
- [ ] Task 6: `EventSourcingBuilderMediatorExtensions` is `static partial class`; `EnsureDispatcherRegistered` declared as `static partial void`.
- [ ] Task 7: ZESM001 + NotificationDiscovery wired.
- [ ] Task 8: DispatcherEmitter emits typed switch + partial-method body; IIncrementalGenerator entry point wires the pipeline.
- [ ] Task 9: snapshot tests committed with approved `.verified.txt`.
- [ ] Task 10: 5 runtime tests pass; history-filter is the load-bearing one.
- [ ] Task 11: AOT smoke publishes with 0 trim warnings.
- [ ] Task 12: PR green, admin-merged, release-please cuts mediator-v1.0.0, NuGet has it.
- [ ] Task 13: org BACKLOG.md entry marked shipped.

## Out of scope (v1.1+)

Same as design doc:
- Configurable error policy.
- `EventCommittedNotification<TEvent>` wrapper.
- Position checkpoint persistence.
- Wildcard / multi-stream subscription.
- Dedup of duplicate bridge registrations.
- Telemetry tracing.
- AsyncEvents alternative.
- Generator detection of `INotification` types declared in EXTERNAL assemblies (today: same-compilation discovery only; cross-assembly events require the user to mark them in a way the discovery walker can see, e.g., re-export via a partial or a marker `[BridgeNotification]` attribute — design TBD).
