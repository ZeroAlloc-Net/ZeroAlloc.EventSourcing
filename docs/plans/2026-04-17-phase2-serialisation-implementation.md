# Phase 2 Serialisation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `ISerializerDispatcher` + source-generated `SerializerDispatcher` to `ZeroAlloc.Serialisation`, then wire it as the default `IEventSerializer` in `ZeroAlloc.EventSourcing`.

**Architecture:** Two sequential PRs — PR 1 extends the `ZeroAlloc.Serialisation` repo with a runtime-type dispatch interface and a generator that emits one `SerializerDispatcher` class per user assembly (compile-time switch, no reflection). PR 2 adds a 15-line adapter (`ZeroAllocEventSerializer`) to `ZeroAlloc.EventSourcing` that delegates to that dispatcher, registered automatically via `AddEventSourcing()`.

**Tech Stack:** C# 13, .NET 8/9/10 multi-target, Roslyn incremental source generators, `Microsoft.Extensions.DependencyInjection.Abstractions`, xunit, `TreatWarningsAsErrors=true`.

---

## Repository Paths

| Repo | Path |
|---|---|
| ZeroAlloc.Serialisation | `c:\Projects\Prive\ZeroAlloc\ZeroAlloc.Serialisation\` |
| ZeroAlloc.EventSourcing | `c:\Projects\Prive\ZeroAlloc\ZeroAlloc.EventSourcing\` |

---

## Background: How the generator currently works

The `SerializerGenerator` (entry point: `src/ZeroAlloc.Serialisation.Generator/SerializerGenerator.cs`) uses `ForAttributeWithMetadataName` to find every class/struct annotated with `[ZeroAllocSerializable]`. For each type it calls:
- `SerializerEmitter.Emit(ctx, model)` → emits `{TypeName}Serializer.g.cs` (internal serializer class)
- `DiEmitter.Emit(ctx, model)` → emits `{TypeName}SerializerExtensions.g.cs` (DI extension using **`AddSingleton`**)

`SerializerModel` record: `Namespace`, `TypeName`, `FullTypeName` (= `typeSymbol.ToDisplayString()`), `FormatName`.

---

## PR 1: ZeroAlloc.Serialisation

Work in `c:\Projects\Prive\ZeroAlloc\ZeroAlloc.Serialisation\`

Test command: `dotnet test` (run from repo root)

---

### Task 1: Add `ISerializerDispatcher` interface

**Files:**
- Create: `src/ZeroAlloc.Serialisation/ISerializerDispatcher.cs`

**Step 1: Create the file**

```csharp
// src/ZeroAlloc.Serialisation/ISerializerDispatcher.cs
namespace ZeroAlloc.Serialisation;

/// <summary>
/// Runtime-type dispatch surface for source-generated serializers.
/// Implement via the generated <c>SerializerDispatcher</c> class (emitted per assembly
/// by the ZeroAlloc.Serialisation source generator).
/// </summary>
public interface ISerializerDispatcher
{
    /// <summary>Serializes <paramref name="value"/> using the serializer registered for <paramref name="type"/>.</summary>
    ReadOnlyMemory<byte> Serialize(object value, Type type);

    /// <summary>Deserializes <paramref name="data"/> to an instance of <paramref name="type"/>.</summary>
    object? Deserialize(ReadOnlyMemory<byte> data, Type type);
}
```

**Step 2: Build to verify it compiles**

```bash
dotnet build src/ZeroAlloc.Serialisation/ZeroAlloc.Serialisation.csproj
```

Expected: Build succeeded, 0 warnings.

**Step 3: Commit**

```bash
git add src/ZeroAlloc.Serialisation/ISerializerDispatcher.cs
git commit -m "feat: add ISerializerDispatcher runtime-type dispatch interface"
```

---

### Task 2: Fix `DiEmitter` — `AddSingleton` → `TryAddSingleton`

**Files:**
- Modify: `src/ZeroAlloc.Serialisation.Generator/DiEmitter.cs`

The current emitted DI extension calls `services.AddSingleton(...)`, which overwrites any user-provided registration. Fix it to use `TryAddSingleton` so user registrations are never silently clobbered.

**Step 1: Write the failing test**

Add this test to `tests/ZeroAlloc.Serialisation.Generator.Tests/SerializerGeneratorTests.cs`:

```csharp
[Fact]
public void Generator_EmittedDiExtension_UsesTryAddSingleton()
{
    var source = """
        using ZeroAlloc.Serialisation;

        namespace MyApp;

        [ZeroAllocSerializable(SerializationFormat.MemoryPack)]
        public class OrderEvent { }
        """;

    var compilation = CreateCompilation(source);
    var driver = CSharpGeneratorDriver.Create(new SerializerGenerator())
        .RunGenerators(compilation);

    var result = driver.GetRunResult();
    var diFile = result.GeneratedTrees
        .FirstOrDefault(t => t.FilePath.Contains("OrderEventSerializerExtensions.g.cs"));

    Assert.NotNull(diFile);
    var text = diFile!.GetText().ToString();
    Assert.Contains("TryAddSingleton", text);
    Assert.DoesNotContain("services.AddSingleton", text);
}
```

**Step 2: Run the test to verify it fails**

```bash
dotnet test tests/ZeroAlloc.Serialisation.Generator.Tests/ --filter "Generator_EmittedDiExtension_UsesTryAddSingleton" -v minimal
```

Expected: FAIL — `DoesNotContain` assertion fails because current code uses `AddSingleton`.

**Step 3: Fix `DiEmitter.cs`**

Replace the entire file content:

```csharp
using Microsoft.CodeAnalysis;
using ZeroAlloc.Serialisation.Generator.Models;

namespace ZeroAlloc.Serialisation.Generator;

internal static class DiEmitter
{
    public static void Emit(SourceProductionContext ctx, SerializerModel model)
    {
        var ns = string.IsNullOrEmpty(model.Namespace)
            ? ""
            : $"namespace {model.Namespace};\n\n";

        var source = $$"""
            // <auto-generated/>
            #nullable enable
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection.Extensions;
            using ZeroAlloc.Serialisation;

            {{ns}}public static partial class SerializerServiceCollectionExtensions
            {
                public static IServiceCollection Add{{model.TypeName}}Serializer(
                    this IServiceCollection services)
                {
                    services.TryAddSingleton<ISerializer<{{model.FullTypeName}}>, {{model.TypeName}}Serializer>();
                    return services;
                }
            }
            """;

        ctx.AddSource($"{model.TypeName}SerializerExtensions.g.cs", source);
    }
}
```

Note: Return type changed from expression-body to block body so `TryAddSingleton` (void) can be called before returning `services`.

**Step 4: Run the test to verify it passes**

```bash
dotnet test tests/ZeroAlloc.Serialisation.Generator.Tests/ --filter "Generator_EmittedDiExtension_UsesTryAddSingleton" -v minimal
```

Expected: PASS.

**Step 5: Run all generator tests to check for regressions**

```bash
dotnet test tests/ZeroAlloc.Serialisation.Generator.Tests/ -v minimal
```

Expected: All 6 tests pass (5 existing + 1 new).

**Step 6: Update existing test expectation that checked for `AddSingleton`**

The existing test `Generator_EmittedDiExtension_ContainsAddMethod` asserts `AddInvoiceEventSerializer` is present — that still holds. Check it still passes (Step 5 covers this). No change needed.

**Step 7: Commit**

```bash
git add src/ZeroAlloc.Serialisation.Generator/DiEmitter.cs \
        tests/ZeroAlloc.Serialisation.Generator.Tests/SerializerGeneratorTests.cs
git commit -m "fix: use TryAddSingleton in generated DI extensions to avoid overwriting user registrations"
```

---

### Task 3: Add `DispatcherEmitter.cs`

**Files:**
- Create: `src/ZeroAlloc.Serialisation.Generator/DispatcherEmitter.cs`

This emitter receives ALL annotated types in the assembly (via `Collect()`) and emits two files:
1. `SerializerDispatcher.g.cs` — a `public sealed partial class SerializerDispatcher : ISerializerDispatcher` with compile-time type switch
2. `SerializerDispatcherExtensions.g.cs` — `AddSerializerDispatcher()` DI extension using `TryAddSingleton`

**Step 1: Create `DispatcherEmitter.cs`**

```csharp
// src/ZeroAlloc.Serialisation.Generator/DispatcherEmitter.cs
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using ZeroAlloc.Serialisation.Generator.Models;

namespace ZeroAlloc.Serialisation.Generator;

internal static class DispatcherEmitter
{
    public static void Emit(SourceProductionContext ctx, ImmutableArray<SerializerModel> models)
    {
        if (models.IsDefaultOrEmpty) return;

        EmitDispatcher(ctx, models);
        EmitDiExtension(ctx);
    }

    private static void EmitDispatcher(SourceProductionContext ctx, ImmutableArray<SerializerModel> models)
    {
        var serializeCases = string.Join(
            "\n            ",
            models.Select(m =>
            {
                var typeRef = $"global::{m.FullTypeName}";
                var serializerRef = string.IsNullOrEmpty(m.Namespace)
                    ? $"global::{m.TypeName}Serializer"
                    : $"global::{m.Namespace}.{m.TypeName}Serializer";
                return $"case {typeRef} __e: new {serializerRef}().Serialize(__writer, __e); break;";
            }));

        var deserializeCases = string.Join(
            "\n        ",
            models.Select(m =>
            {
                var typeRef = $"global::{m.FullTypeName}";
                var serializerRef = string.IsNullOrEmpty(m.Namespace)
                    ? $"global::{m.TypeName}Serializer"
                    : $"global::{m.Namespace}.{m.TypeName}Serializer";
                return $"if (type == typeof({typeRef})) return new {serializerRef}().Deserialize(data.Span);";
            }));

        var source = $$"""
            // <auto-generated/>
            #nullable enable
            using System;
            using System.Buffers;
            using global::ZeroAlloc.Serialisation;

            public sealed partial class SerializerDispatcher : ISerializerDispatcher
            {
                /// <inheritdoc/>
                public ReadOnlyMemory<byte> Serialize(object value, Type type)
                {
                    var __writer = new ArrayBufferWriter<byte>();
                    switch (value)
                    {
                        {{serializeCases}}
                        default:
                            throw new NotSupportedException(
                                $"No serializer registered for {type.FullName}. " +
                                "Ensure the type is annotated with [ZeroAllocSerializable].");
                    }
                    return __writer.WrittenMemory;
                }

                /// <inheritdoc/>
                public object? Deserialize(ReadOnlyMemory<byte> data, Type type)
                {
                    {{deserializeCases}}
                    throw new NotSupportedException(
                        $"No serializer registered for {type.FullName}. " +
                        "Ensure the type is annotated with [ZeroAllocSerializable].");
                }
            }
            """;

        ctx.AddSource("SerializerDispatcher.g.cs", source);
    }

    private static void EmitDiExtension(SourceProductionContext ctx)
    {
        var source = """
            // <auto-generated/>
            #nullable enable
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection.Extensions;
            using global::ZeroAlloc.Serialisation;

            public static partial class SerializerServiceCollectionExtensions
            {
                public static IServiceCollection AddSerializerDispatcher(
                    this IServiceCollection services)
                {
                    services.TryAddSingleton<ISerializerDispatcher, SerializerDispatcher>();
                    return services;
                }
            }
            """;

        ctx.AddSource("SerializerDispatcherExtensions.g.cs", source);
    }
}
```

**Step 2: Build to check for compile errors (no tests yet)**

```bash
dotnet build src/ZeroAlloc.Serialisation.Generator/ZeroAlloc.Serialisation.Generator.csproj
```

Expected: Build succeeded, 0 warnings.

**Step 3: Commit (the emitter alone — wiring comes next)**

```bash
git add src/ZeroAlloc.Serialisation.Generator/DispatcherEmitter.cs
git commit -m "feat: add DispatcherEmitter — generates SerializerDispatcher.g.cs per assembly"
```

---

### Task 4: Wire `DispatcherEmitter` into `SerializerGenerator`

**Files:**
- Modify: `src/ZeroAlloc.Serialisation.Generator/SerializerGenerator.cs`

The generator must call `types.Collect()` to gather all models, then pass them to `DispatcherEmitter.Emit` in a single `RegisterSourceOutput` call.

**Step 1: Update `SerializerGenerator.cs`**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZeroAlloc.Serialisation.Generator;

[Generator]
public sealed class SerializerGenerator : IIncrementalGenerator
{
    private const string AttributeFullName =
        "ZeroAlloc.Serialisation.ZeroAllocSerializableAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFullName,
                predicate: static (node, _) =>
                    node is ClassDeclarationSyntax or StructDeclarationSyntax,
                transform: static (ctx, ct) => ModelExtractor.Extract(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // Emit one serializer + DI extension per annotated type
        context.RegisterSourceOutput(types, static (ctx, model) =>
        {
            SerializerEmitter.Emit(ctx, model);
            DiEmitter.Emit(ctx, model);
        });

        // Emit one dispatcher for ALL types in the assembly
        var allTypes = types.Collect();
        context.RegisterSourceOutput(allTypes, static (ctx, models) =>
        {
            DispatcherEmitter.Emit(ctx, models);
        });
    }
}
```

**Step 2: Build the generator**

```bash
dotnet build src/ZeroAlloc.Serialisation.Generator/ZeroAlloc.Serialisation.Generator.csproj
```

Expected: Build succeeded, 0 warnings.

**Step 3: Commit**

```bash
git add src/ZeroAlloc.Serialisation.Generator/SerializerGenerator.cs
git commit -m "feat: wire DispatcherEmitter into SerializerGenerator via Collect()"
```

---

### Task 5: Add generator tests for the dispatcher

**Files:**
- Modify: `tests/ZeroAlloc.Serialisation.Generator.Tests/SerializerGeneratorTests.cs`

**Step 1: Add four new test methods** (append to the existing test class, before the `CreateCompilation` helper)

```csharp
[Fact]
public void Generator_MultipleTypes_EmitsDispatcherFiles()
{
    var source = """
        using ZeroAlloc.Serialisation;

        namespace MyApp;

        [ZeroAllocSerializable(SerializationFormat.MemoryPack)]
        public class OrderCreated { }

        [ZeroAllocSerializable(SerializationFormat.MessagePack)]
        public class OrderShipped { }
        """;

    var compilation = CreateCompilation(source);
    var driver = CSharpGeneratorDriver.Create(new SerializerGenerator())
        .RunGenerators(compilation);

    var result = driver.GetRunResult();
    Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    Assert.Contains(result.GeneratedTrees, t => t.FilePath.Contains("SerializerDispatcher.g.cs"));
    Assert.Contains(result.GeneratedTrees, t => t.FilePath.Contains("SerializerDispatcherExtensions.g.cs"));
}

[Fact]
public void Generator_Dispatcher_ContainsAllRegisteredTypes()
{
    var source = """
        using ZeroAlloc.Serialisation;

        namespace MyApp;

        [ZeroAllocSerializable(SerializationFormat.MemoryPack)]
        public class OrderCreated { }

        [ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
        public class OrderShipped { }
        """;

    var compilation = CreateCompilation(source);
    var driver = CSharpGeneratorDriver.Create(new SerializerGenerator())
        .RunGenerators(compilation);

    var result = driver.GetRunResult();
    var dispatcherFile = result.GeneratedTrees
        .FirstOrDefault(t => t.FilePath.Contains("SerializerDispatcher.g.cs"));

    Assert.NotNull(dispatcherFile);
    var text = dispatcherFile!.GetText().ToString();
    Assert.Contains("OrderCreated", text);
    Assert.Contains("OrderShipped", text);
    Assert.Contains("ISerializerDispatcher", text);
    Assert.Contains("SerializerDispatcher", text);
}

[Fact]
public void Generator_DispatcherDi_ContainsTryAddSingleton()
{
    var source = """
        using ZeroAlloc.Serialisation;

        namespace MyApp;

        [ZeroAllocSerializable(SerializationFormat.MemoryPack)]
        public class Event1 { }
        """;

    var compilation = CreateCompilation(source);
    var driver = CSharpGeneratorDriver.Create(new SerializerGenerator())
        .RunGenerators(compilation);

    var result = driver.GetRunResult();
    var diFile = result.GeneratedTrees
        .FirstOrDefault(t => t.FilePath.Contains("SerializerDispatcherExtensions.g.cs"));

    Assert.NotNull(diFile);
    var text = diFile!.GetText().ToString();
    Assert.Contains("AddSerializerDispatcher", text);
    Assert.Contains("TryAddSingleton", text);
}

[Fact]
public void Generator_NoAnnotatedTypes_DoesNotEmitDispatcher()
{
    var source = "namespace MyApp; public class PlainClass { }";

    var compilation = CreateCompilation(source);
    var driver = CSharpGeneratorDriver.Create(new SerializerGenerator())
        .RunGenerators(compilation);

    var result = driver.GetRunResult();
    Assert.DoesNotContain(result.GeneratedTrees,
        t => t.FilePath.Contains("SerializerDispatcher.g.cs"));
    Assert.DoesNotContain(result.GeneratedTrees,
        t => t.FilePath.Contains("SerializerDispatcherExtensions.g.cs"));
}
```

**Step 2: Run just the new tests first to confirm they fail before the wiring is done**

Wait — Task 4 already wired the emitter. Run the tests to confirm they pass:

```bash
dotnet test tests/ZeroAlloc.Serialisation.Generator.Tests/ -v minimal
```

Expected: All 10 tests pass (6 from Tasks 1-2 + 4 new).

**Step 3: Run the full test suite**

```bash
dotnet test
```

Expected: All tests pass, 0 failures.

**Step 4: Commit**

```bash
git add tests/ZeroAlloc.Serialisation.Generator.Tests/SerializerGeneratorTests.cs
git commit -m "test: add generator tests for SerializerDispatcher and DispatcherExtensions emission"
```

---

### Task 6: PR 1 final verification

**Step 1: Full clean build**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings (TreatWarningsAsErrors=true).

**Step 2: Full test run**

```bash
dotnet test
```

Expected: All tests pass.

**Step 3: Push and open PR**

```bash
git push -u origin <branch-name>
# Open PR in GitHub targeting main
# PR title: "feat: add ISerializerDispatcher, source-generated SerializerDispatcher, fix TryAddSingleton"
```

> **Important:** After PR 1 is merged, wait for the release-please automation to create and merge the version bump PR. This publishes `ZeroAlloc.Serialisation 1.1.0` to NuGet. PR 2 cannot proceed until 1.1.0 is on NuGet.

---

## PR 2: ZeroAlloc.EventSourcing

Work in `c:\Projects\Prive\ZeroAlloc\ZeroAlloc.EventSourcing\`

The `Directory.Packages.props` already references `ZeroAlloc.Serialisation Version="1.1.0"`. Once 1.1.0 is published, this resolves automatically.

Test command: `dotnet test tests/ZeroAlloc.EventSourcing.Tests/` (skip SQL/Kafka tests that require containers)

---

### Task 7: Add `ZeroAllocEventSerializer`

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/ZeroAllocEventSerializer.cs`
- Modify: `src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj`

**Step 1: Add the package reference**

In `src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj`, add inside the existing `<ItemGroup>`:

```xml
<PackageReference Include="ZeroAlloc.Serialisation" />
```

Final csproj content:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <Description>Core abstractions and primitives for ZeroAlloc.EventSourcing.</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ZeroAlloc.Results" />
    <PackageReference Include="ZeroAlloc.Collections" />
    <PackageReference Include="ZeroAlloc.AsyncEvents" />
    <PackageReference Include="ZeroAlloc.ValueObjects" />
    <PackageReference Include="ZeroAlloc.Serialisation" />
  </ItemGroup>

  <ItemGroup Condition="'$(IsRoslynComponent)' != 'true'">
    <PackageReference Include="Meziantou.Analyzer" PrivateAssets="all" />
    <PackageReference Include="Roslynator.Analyzers" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

**Step 2: Write the failing test**

Create `tests/ZeroAlloc.EventSourcing.Tests/ZeroAllocEventSerializerTests.cs`:

```csharp
using System;
using System.Buffers;
using NSubstitute;
using Xunit;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.Tests;

public class ZeroAllocEventSerializerTests
{
    private readonly ISerializerDispatcher _dispatcher = Substitute.For<ISerializerDispatcher>();
    private readonly ZeroAllocEventSerializer _sut;

    public ZeroAllocEventSerializerTests()
        => _sut = new ZeroAllocEventSerializer(_dispatcher);

    [Fact]
    public void Constructor_NullDispatcher_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ZeroAllocEventSerializer(null!));
    }

    [Fact]
    public void Serialize_DelegatesToDispatcher()
    {
        var @event = new TestEvent("hello");
        var expected = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 });
        _dispatcher.Serialize(@event, typeof(TestEvent)).Returns(expected);

        var result = _sut.Serialize(@event);

        Assert.Equal(expected, result);
        _dispatcher.Received(1).Serialize(@event, typeof(TestEvent));
    }

    [Fact]
    public void Deserialize_DelegatesToDispatcher()
    {
        var payload = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 });
        var expected = new TestEvent("world");
        _dispatcher.Deserialize(payload, typeof(TestEvent)).Returns(expected);

        var result = _sut.Deserialize(payload, typeof(TestEvent));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Deserialize_DispatcherReturnsNull_Throws()
    {
        var payload = new ReadOnlyMemory<byte>(new byte[] { 1 });
        _dispatcher.Deserialize(payload, typeof(TestEvent)).Returns((object?)null);

        Assert.Throws<InvalidOperationException>(() =>
            _sut.Deserialize(payload, typeof(TestEvent)));
    }

    private sealed record TestEvent(string Value);
}
```

**Step 3: Run the test to verify it fails**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "ZeroAllocEventSerializerTests" -v minimal
```

Expected: FAIL — `ZeroAllocEventSerializer` does not exist yet.

**Step 4: Create `ZeroAllocEventSerializer.cs`**

```csharp
// src/ZeroAlloc.EventSourcing/ZeroAllocEventSerializer.cs
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// <see cref="IEventSerializer"/> implementation backed by ZeroAlloc.Serialisation's
/// source-generated <see cref="ISerializerDispatcher"/>. Zero-reflection, AOT-safe.
/// </summary>
public sealed class ZeroAllocEventSerializer : IEventSerializer
{
    private readonly ISerializerDispatcher _dispatcher;

    public ZeroAllocEventSerializer(ISerializerDispatcher dispatcher)
        => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => _dispatcher.Serialize(@event, typeof(TEvent));

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => _dispatcher.Deserialize(payload, eventType)
           ?? throw new InvalidOperationException(
               $"Deserialization of {eventType.FullName} returned null.");
}
```

**Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "ZeroAllocEventSerializerTests" -v minimal
```

Expected: 4 tests PASS.

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/ZeroAllocEventSerializer.cs \
        src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj \
        tests/ZeroAlloc.EventSourcing.Tests/ZeroAllocEventSerializerTests.cs
git commit -m "feat: add ZeroAllocEventSerializer — IEventSerializer backed by ISerializerDispatcher"
```

---

### Task 8: Add `AddEventSourcing()` DI extension

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/ServiceCollectionExtensions.cs`

**Background:** There is currently no DI extension in the EventSourcing package. `AddEventSourcing()` starts here. It registers `IEventSerializer → ZeroAllocEventSerializer` as a `TryAddSingleton`. Callers that want the default serializer must also call `services.AddSerializerDispatcher()` (from the code generated by ZeroAlloc.Serialisation in their assembly) to supply `ISerializerDispatcher`. Callers that bring their own `IEventSerializer` register it before calling `AddEventSourcing()`.

**Step 1: Write the failing test**

Create `tests/ZeroAlloc.EventSourcing.Tests/ServiceCollectionExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddEventSourcing_RegistersZeroAllocEventSerializer()
    {
        var services = new ServiceCollection();
        var dispatcher = Substitute.For<ISerializerDispatcher>();
        services.AddSingleton(dispatcher);

        services.AddEventSourcing();

        var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IEventSerializer>();
        Assert.IsType<ZeroAllocEventSerializer>(serializer);
    }

    [Fact]
    public void AddEventSourcing_DoesNotOverwriteUserSerializer()
    {
        var services = new ServiceCollection();
        var custom = Substitute.For<IEventSerializer>();
        services.AddSingleton(custom);  // user registers first

        services.AddEventSourcing();    // TryAddSingleton must not overwrite

        var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IEventSerializer>();
        Assert.Same(custom, serializer);
    }

    [Fact]
    public void AddEventSourcing_ReturnsServices_ForChaining()
    {
        var services = new ServiceCollection();
        var result = services.AddEventSourcing();
        Assert.Same(services, result);
    }
}
```

**Step 2: Run the test to verify it fails**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "ServiceCollectionExtensionsTests" -v minimal
```

Expected: FAIL — `AddEventSourcing()` does not exist.

**Step 3: Create `ServiceCollectionExtensions.cs`**

```csharp
// src/ZeroAlloc.EventSourcing/ServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.EventSourcing;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers ZeroAlloc.EventSourcing services.
    /// By default wires <see cref="ZeroAllocEventSerializer"/> as <see cref="IEventSerializer"/>.
    /// Register your own <see cref="IEventSerializer"/> before calling this to opt out.
    /// </summary>
    public static IServiceCollection AddEventSourcing(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventSerializer, ZeroAllocEventSerializer>();
        return services;
    }
}
```

Note: `Microsoft.Extensions.DependencyInjection.Extensions` (for `TryAddSingleton`) is available transitively via `ZeroAlloc.Serialisation` which already references `Microsoft.Extensions.DependencyInjection.Abstractions`. If the build complains, add explicitly to the csproj.

**Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ --filter "ServiceCollectionExtensionsTests" -v minimal
```

Expected: 3 tests PASS.

**Step 5: Check if `Microsoft.Extensions.DependencyInjection` package is needed**

`TryAddSingleton` is in the `Microsoft.Extensions.DependencyInjection.Extensions` namespace, inside `Microsoft.Extensions.DependencyInjection.Abstractions`. If Step 4 fails with a namespace error, add to `ZeroAlloc.EventSourcing.csproj`:

```xml
<PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
```

And add to `Directory.Packages.props`:

```xml
<PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
```

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/ServiceCollectionExtensions.cs \
        tests/ZeroAlloc.EventSourcing.Tests/ServiceCollectionExtensionsTests.cs
git commit -m "feat: add AddEventSourcing() — registers ZeroAllocEventSerializer as default IEventSerializer"
```

---

### Task 9: PR 2 final verification

**Step 1: Full clean build**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings.

**Step 2: Full test run (non-infrastructure tests)**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/ tests/ZeroAlloc.EventSourcing.Aggregates.Tests/
```

Expected: All tests pass.

**Step 3: Verify `ZeroAlloc.EventSourcing.Tests.csproj` references NSubstitute**

The new tests use `NSubstitute`. Check `tests/ZeroAlloc.EventSourcing.Tests/ZeroAlloc.EventSourcing.Tests.csproj`:

```bash
grep -i "NSubstitute" tests/ZeroAlloc.EventSourcing.Tests/ZeroAlloc.EventSourcing.Tests.csproj
```

If missing, add `<PackageReference Include="NSubstitute" />` to that csproj (it's already in `Directory.Packages.props`).

Also check `Microsoft.Extensions.DependencyInjection` (for `ServiceCollection` in tests):

```bash
grep -i "DependencyInjection" tests/ZeroAlloc.EventSourcing.Tests/ZeroAlloc.EventSourcing.Tests.csproj
```

If missing, add:
```xml
<PackageReference Include="Microsoft.Extensions.DependencyInjection" />
```

And in `Directory.Packages.props`:
```xml
<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
```

**Step 4: Push and open PR**

```bash
git push -u origin <branch-name>
# PR title: "feat(p2.6): ZeroAllocEventSerializer + AddEventSourcing() default DI wiring"
```

---

## Checklist Summary

### PR 1 (ZeroAlloc.Serialisation)
- [ ] `ISerializerDispatcher` interface added
- [ ] `DiEmitter` uses `TryAddSingleton` (not `AddSingleton`)
- [ ] `DispatcherEmitter` emits `SerializerDispatcher.g.cs` + `SerializerDispatcherExtensions.g.cs`
- [ ] `SerializerGenerator` wires `Collect()` → `DispatcherEmitter`
- [ ] 4 new generator tests covering dispatcher emission
- [ ] `dotnet build` — 0 warnings
- [ ] `dotnet test` — all pass

### PR 2 (ZeroAlloc.EventSourcing)
- [ ] `ZeroAllocEventSerializer.cs` added
- [ ] `AddEventSourcing()` in `ServiceCollectionExtensions.cs` added
- [ ] `ZeroAlloc.EventSourcing.csproj` references `ZeroAlloc.Serialisation`
- [ ] 4 `ZeroAllocEventSerializerTests` pass
- [ ] 3 `ServiceCollectionExtensionsTests` pass
- [ ] `dotnet build` — 0 warnings
- [ ] `dotnet test` — all pass
