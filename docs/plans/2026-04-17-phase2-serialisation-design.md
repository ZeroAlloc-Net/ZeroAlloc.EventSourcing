# Phase 2 Serialisation Design

## Goal

Add a built-in `IEventSerializer` implementation backed by `ZeroAlloc.Serialisation` — zero-reflection, AOT-safe, source-generated dispatch — and make it the default when wiring up event sourcing via DI, with a clean opt-out path for users who bring their own serializer.

## Architecture

Two PRs in sequence:

1. **`ZeroAlloc.Serialisation` PR** — add runtime-type dispatch interface + extend generator
2. **`ZeroAlloc.EventSourcing` PR** — adapter + default DI wiring

Dependency direction: `ZeroAlloc.EventSourcing` → `ZeroAlloc.Serialisation` (correct, no circularity).

## PR 1: `ZeroAlloc.Serialisation` — Round-trip additions

### New interface: `ISerializerDispatcher`

Added to `src/ZeroAlloc.Serialisation/ISerializerDispatcher.cs`:

```csharp
public interface ISerializerDispatcher
{
    ReadOnlyMemory<byte> Serialize(object value, Type type);
    object? Deserialize(ReadOnlyMemory<byte> data, Type type);
}
```

This is the reusable runtime-type API. Any ZeroAlloc package that needs to serialize by `Type` at runtime uses this interface — not just event sourcing.

### Generator extension: `SerializerDispatcher`

The generator already collects all `[ZeroAllocSerializable]` types per assembly. We extend it to emit one additional file per assembly: a `SerializerDispatcher` partial class implementing `ISerializerDispatcher` with a compile-time switch over all known types — no reflection, AOT-safe.

```csharp
// SerializerDispatcher.g.cs — generated once per assembly
public sealed partial class SerializerDispatcher : ISerializerDispatcher
{
    public ReadOnlyMemory<byte> Serialize(object value, Type type)
    {
        var writer = new ArrayBufferWriter<byte>();
        switch (value)
        {
            case OrderCreated e: new OrderCreatedSerializer().Serialize(writer, e); break;
            case OrderShipped e: new OrderShippedSerializer().Serialize(writer, e); break;
            default: throw new NotSupportedException(
                $"No serializer registered for {type.FullName}. " +
                $"Ensure the type is annotated with [ZeroAllocSerializable].");
        }
        return writer.WrittenMemory;
    }

    public object? Deserialize(ReadOnlyMemory<byte> data, Type type)
    {
        if (type == typeof(OrderCreated)) return new OrderCreatedSerializer().Deserialize(data.Span);
        if (type == typeof(OrderShipped)) return new OrderShippedSerializer().Deserialize(data.Span);
        throw new NotSupportedException(
            $"No serializer registered for {type.FullName}. " +
            $"Ensure the type is annotated with [ZeroAllocSerializable].");
    }
}
```

The `partial` keyword allows users to extend the dispatcher with additional hand-written cases if needed.

### DI fix: `TryAddSingleton` everywhere

`DiEmitter.cs` switches from `AddSingleton` to `TryAddSingleton` for all registrations. This ensures user-provided registrations are never silently overwritten.

The generator also emits `AddSerializerDispatcher()` in the same `SerializerServiceCollectionExtensions` partial class:

```csharp
// Generated alongside existing per-type extensions
public static IServiceCollection AddSerializerDispatcher(
    this IServiceCollection services)
{
    services.TryAddSingleton<ISerializerDispatcher, SerializerDispatcher>();
    return services;
}
```

### Files changed in `ZeroAlloc.Serialisation`

| File | Change |
|---|---|
| `src/ZeroAlloc.Serialisation/ISerializerDispatcher.cs` | New — interface |
| `src/ZeroAlloc.Serialisation.Generator/DiEmitter.cs` | Fix `AddSingleton` → `TryAddSingleton`; emit `AddSerializerDispatcher()` |
| `src/ZeroAlloc.Serialisation.Generator/SerializerEmitter.cs` (or new `DispatcherEmitter.cs`) | New — emits `SerializerDispatcher.g.cs` per assembly |
| `tests/` | Tests for generated dispatcher output |

---

## PR 2: `ZeroAlloc.EventSourcing` — Adapter + default DI

### New file: `ZeroAllocEventSerializer`

Added to `src/ZeroAlloc.EventSourcing/ZeroAllocEventSerializer.cs`:

```csharp
/// <summary>
/// IEventSerializer implementation backed by ZeroAlloc.Serialisation's source-generated
/// ISerializerDispatcher. Zero-reflection, AOT-safe.
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

### DI default wiring

`AddEventSourcing()` (P2.1 — DI integration) registers both as `TryAddSingleton`:

```csharp
// Inside AddEventSourcing() internals:
services.TryAddSingleton<ISerializerDispatcher, SerializerDispatcher>();
services.TryAddSingleton<IEventSerializer, ZeroAllocEventSerializer>();
```

**Default usage — no explicit serializer call:**
```csharp
services.AddEventSourcing()
        .UsePostgreSql(connectionString);
```

**Opt-out — register your own before `AddEventSourcing()`:**
```csharp
services.AddSingleton<IEventSerializer, MyCustomSerializer>();
services.AddEventSourcing()
        .UsePostgreSql(connectionString);
```

`TryAdd` semantics mean the user's registration is never overwritten.

### Files changed in `ZeroAlloc.EventSourcing`

| File | Change |
|---|---|
| `src/ZeroAlloc.EventSourcing/ZeroAllocEventSerializer.cs` | New — adapter |
| `src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj` | Add `<PackageReference Include="ZeroAlloc.Serialisation" />` |
| `Directory.Packages.props` | Already has `ZeroAlloc.Serialisation` — no change needed |
| `tests/ZeroAlloc.EventSourcing.Tests/ZeroAllocEventSerializerTests.cs` | New — unit tests |

---

## Tech stack

- C# 13, .NET 8/9/10 multi-target
- `ZeroAlloc.Serialisation` 1.1.0+ (must ship PR 1 first)
- `Microsoft.Extensions.DependencyInjection.Abstractions` (for `TryAddSingleton`)
- `TreatWarningsAsErrors=true` — zero warnings

## Out of scope

- Upcasting / schema evolution (P2.5 — separate item)
- OpenTelemetry, metrics, health checks (P2.2–P2.4)
- The full `AddEventSourcing()` fluent builder (P2.1) — only the serializer wiring within it
