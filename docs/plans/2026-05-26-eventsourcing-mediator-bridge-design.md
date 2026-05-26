# ZeroAlloc.EventSourcing.Mediator v1.0.0 — Design

**Date:** 2026-05-26
**Scope:** New sub-package `ZeroAlloc.EventSourcing.Mediator` inside the existing `ZeroAlloc.EventSourcing` mono-repo. Bridges LIVE committed events from `IEventStore.SubscribeAsync` to `IMediator.Publish` so notification handlers / saga handlers / pipeline behaviors can react to ES events using the standard Mediator idiom.

## Background

The org-wide backlog (`docs/BACKLOG.md:393`) carries this item as a ~50 LOC bridge package surfaced during Saga brainstorming on 2026-04-28. Multiple consumers (read-model projections, integration handlers, `ZeroAlloc.Saga`) want to react to ES events through the Mediator notification handler pattern they already use elsewhere — without `ZeroAlloc.EventSourcing` taking a hard dependency on `ZeroAlloc.Mediator`.

The bridge package is opt-in; it depends on both `ZeroAlloc.EventSourcing` and `ZeroAlloc.Mediator` and is consumed only by hosts that want the integration. It does not modify ES core; it does not add Mediator as a transitive dependency for ES consumers who don't want it.

A core requirement from the backlog: **filter out replays.** Notification handlers must not fire on history replay — only on events appended AFTER the bridge starts. This is the entire reason the bridge exists rather than direct `IMediator.Publish` calls from event handlers — it sits at the LIVE/REPLAY boundary.

## Goal

Ship the smallest bridge that satisfies:

- One fluent extension `.PublishViaMediator(StreamId)` on `EventSourcingBuilder`.
- Subscribes to the named stream from `StreamPosition.End` (or equivalent) so replays never reach Mediator.
- Per LIVE event: casts `EventEnvelope.Event as INotification`; if non-null, calls `IMediator.Publish(notification, ct)`. Non-`INotification` events are silently skipped.
- Errors from `IMediator.Publish` (handler exceptions) are logged and swallowed — the bridge stays alive and continues processing.
- Lives as an `IHostedService` registered by the extension; auto-starts during host boot.
- Compiles under `PublishAot=true` with zero `IL2026`/`IL3050` warnings.

## Second revision (2026-05-26) — generator owns the entire extension surface

**First revision's partial-method approach was structurally broken.** C# `partial` declarations don't span compilation units. The plan declared `static partial class EventSourcingBuilderMediatorExtensions` in the runtime project (already compiled into the runtime DLL) with a `static partial void EnsureDispatcherRegistered(IServiceCollection)` extension point that the generator filled in from the consumer's compilation. This produces a `CS0759` (or silently no-ops in the runtime's baked-in body) — consumers would need to add a `PartialDeclarationShim.cs` file to their own project to make it work, defeating the bridge's purpose.

**Final architecture:** the runtime DLL ships ONLY the abstraction layer; the user-facing fluent extension lives entirely in generator-emitted code in the consumer's compilation.

- **Runtime DLL ships:**
  - `INotificationDispatcher` (public interface)
  - `EventStoreMediatorBridge` (**public** sealed class — was internal in the first revision; promoted so generator-emitted code can `new` it)
  - Bundled Roslyn generator (TFM netstandard2.0, IsRoslynComponent, IsPackable=false, packaged into `analyzers/dotnet/cs/` of the runtime nupkg)
- **Generator unconditionally emits into the consumer's compilation:**
  - `internal static class EventSourcingBuilderMediatorExtensions` — full `PublishViaMediator(EventSourcingBuilder, StreamId)` extension with body. `internal` so multiple consuming assemblies (e.g., a library + an executable both referencing the bridge) each generate their own copy without ambiguous-reference collisions.
  - `internal sealed class GeneratedNotificationDispatcher : INotificationDispatcher` in `ZeroAlloc.EventSourcing.Mediator.Generated` namespace — switch over discovered `INotification` types (empty switch with only the `_ => ValueTask.CompletedTask` default arm when no types are found).

The user calls `services.AddEventSourcing().PublishViaMediator(streamId)` — both the extension and the dispatcher come from the consumer's own compilation. No cross-assembly partial nonsense. No shim files. AOT-clean.

The empty-discovery case still emits a working `PublishViaMediator` extension — the dispatcher's switch has only the default arm, so dispatched events are silently skipped. `ZESM001` (Warning) fires to surface the misconfiguration.

---

## First revision (superseded) — generator-based dispatch via partial-method extension point

**Initial assumption falsified during implementation:** the published `ZeroAlloc.Mediator` nupkg ships ONLY marker interfaces (`INotification`, `INotificationHandler`, etc.) — no `IMediator` type. `IMediator` and its `Publish` overloads are emitted by Mediator's source generator INTO the consuming compilation, with **per-concrete-type signatures only**. A bridge package compiled against `ZeroAlloc.Mediator` therefore cannot statically reference `IMediator.Publish(INotification, ct)` or even `IMediator` itself.

**Implication:** v1.0 cannot be a runtime-only ~50 LOC package. To preserve the family's AOT promise (`<IsAotCompatible>true</IsAotCompatible>` enforced + zero `IL2026`/`IL3050`), the bridge ships **two assemblies in one nupkg**, mirroring how `ZeroAlloc.Flux` and `ZeroAlloc.Mediator` bundle their generators:

- **Runtime** (`src/ZeroAlloc.EventSourcing.Mediator/`) — `EventStoreMediatorBridge` + `INotificationDispatcher` interface + `EventSourcingBuilderMediatorExtensions` (`partial` class with a `partial void EnsureDispatcherRegistered(...)` extension point).
- **Generator** (`src/ZeroAlloc.EventSourcing.Mediator.Generator/`, `netstandard2.0`, `IsRoslynComponent`) bundled into the runtime nupkg's `analyzers/dotnet/cs/` folder. Discovers every type implementing `INotification` in the consuming compilation; emits a `GeneratedNotificationDispatcher : INotificationDispatcher` with a typed switch dispatching to `_mediator.Publish<TConcrete>(concrete, ct)` per type; emits the partial method body that registers `GeneratedNotificationDispatcher` into the `IServiceCollection` when `.PublishViaMediator(...)` is called.

The bridge runtime ships a single nupkg. Users install one `<PackageReference>`; the generator runs automatically in their compilation; AOT publish produces 0 trim warnings because every `Publish<T>` call site is generated as a concrete-typed expression.

This pushes the package from "50 LOC runtime bridge" to "~50 LOC runtime + ~150 LOC generator with snapshot tests" — comparable in scale to `ZeroAlloc.Flux` v1.0.0 (which we shipped today), but smaller because the dispatch surface is one method, not five.

---

## Decisions

### D-0 (added): generator + runtime split in one nupkg

The package is **one nupkg containing two assemblies** — runtime + analyzer-bundled generator. The user installs one `PackageReference`; the generator runs in their compilation automatically. AOT-clean.

The generator's responsibility:
1. Discover every type in the consuming compilation that implements `ZeroAlloc.Mediator.INotification`.
2. Emit `internal sealed class GeneratedNotificationDispatcher : INotificationDispatcher` with a switch over the discovered set, each arm calling `_mediator.Publish<TConcrete>(concrete, ct)`.
3. Emit the `partial void EnsureDispatcherRegistered(IServiceCollection services)` body that registers `GeneratedNotificationDispatcher` as a singleton implementation of `INotificationDispatcher`.

The runtime's responsibility:
- Declare `public interface INotificationDispatcher { ValueTask DispatchAsync(object evt, CancellationToken ct); }`.
- Implement `EventStoreMediatorBridge : IHostedService` consuming `INotificationDispatcher` (NOT `IMediator` — the bridge doesn't see Mediator's generated types).
- Implement `EventSourcingBuilderMediatorExtensions` as a `static partial class` with `partial void EnsureDispatcherRegistered(IServiceCollection)` extension point that the generator fills in.

**If the generator doesn't run** (e.g., user's `INotification` types are in a separate referenced assembly and the generator's syntax-based discovery misses them), `EnsureDispatcherRegistered` is a no-op → `INotificationDispatcher` resolution at runtime throws a clear "missing dispatcher" exception → user knows to declare events in the same compilation as the bridge consumer. Documented as a known constraint.

**Considered and rejected:**

- **Reflection at runtime** (`AppDomain.CurrentDomain.GetAssemblies()` + `MethodInfo.Invoke`). Breaks AOT (`[RequiresUnreferencedCode]` required). Undercuts the family's identity. Rejected.
- **`EventCommittedNotification<TEvent>` wrapper.** Still requires `_mediator.Publish<EventCommittedNotification<TEvent>>(wrapper, ct)` — same `IMediator` reference problem. Rejected.
- **Require user to manually register `INotificationDispatcher`.** The user would have to write a switch by hand listing every event type. Boilerplate; defeats the purpose of the bridge. Rejected.

### D-1: bridging shape — events implement `INotification`

ES events that the user wants bridged through Mediator implement `ZeroAlloc.Mediator.INotification`. The bridge casts `envelope.Event as INotification`; non-null → publish; null → skip with a structured log line.

**Rationale:**

- AOT-clean. No reflection. No `MakeGenericMethod` calls.
- Single registration call — `.PublishViaMediator(streamId)` covers every event type appended to that stream that opts into the marker.
- Saga + read-model use cases assume events already flow through Mediator, so implementing `INotification` on event payloads is the natural consumer pattern anyway.

**Considered and rejected:**

- **Wrap events in `EventCommittedNotification<TEvent> : INotification`.** Decouples ES event types from `INotification` but adds one allocation per dispatch (the wrapper) and requires reflection or per-type registration to call `mediator.Publish<TEvent>` correctly. The user can build this as a v1.1 extension if they need it; v1 doesn't lock them out.
- **Per-event-type fluent registration:** `.PublishViaMediator<UserCreated>("users").PublishViaMediator<OrderPlaced>("orders")`. Verbose at the registration site; doesn't materially improve over Option D-1 since events still need `INotification` either way.

### D-2: single-stream-per-call registration

Each `.PublishViaMediator(StreamId)` call registers ONE subscription as ONE `IHostedService`. Multiple streams → multiple calls:

```csharp
services.AddEventSourcing()
    .AddInMemoryPersistence()
    .PublishViaMediator(new StreamId("users"))
    .PublishViaMediator(new StreamId("orders"));
```

**Rationale:** simplest unit; the user knows exactly which streams emit to Mediator. A `params StreamId[]` convenience overload may be added in v1.0 as a thin wrapper that calls the single-stream form N times — non-load-bearing.

**Considered and rejected:**

- **All-streams subscription.** `IEventStore` has no "list streams" or "subscribe to all streams" API. Implementing one would be a cross-cutting change to ES core, out of scope for a 50-LOC bridge.

### D-3: live-only subscription via `StreamPosition.End`

The bridge calls `IEventStore.SubscribeAsync(streamId, StreamPosition.End, OnEventAsync, ct)` (or the closest equivalent the ES API exposes for "end of stream"). The catch-up phase is never entered — the handler only fires for events appended AFTER `StartAsync` completes.

If `StreamPosition.End` doesn't exist as a constant: the bridge reads the current end position at subscription time (`var endPos = await endProbe(streamId, ct);`) and passes that to `SubscribeAsync`. Either way the semantic is positional: no historical events reach the bridge.

**Trade-off:** events appended between the user's `.PublishViaMediator(streamId)` call and the host's `StartAsync` completion are missed. That gap is microseconds during host boot — acceptable per the backlog ("replays go via direct subscription, never Mediator"). Implementation-level documentation flags this.

**Considered and rejected:**

- **High-watermark tracking** (subscribe from `Start`, track stream's end at subscription time, skip events with `Position <= watermark`). Adds state, complexity, and a memory cost per bridge. Positional subscribe-from-End achieves the same outcome with zero state.

### D-4: log + continue on `IMediator.Publish` exceptions

A handler exception is caught at the per-event boundary inside `OnEventAsync`. The bridge logs:

```
[Error] EventStoreMediatorBridge: Mediator.Publish threw for event {EventId} of type {EventType} on stream {StreamId}: {ExceptionType}: {Message}
```

via `ILogger<EventStoreMediatorBridge>` and proceeds to the next event. The bridge stays alive.

**Rationale:** a misbehaving notification handler shouldn't kill the bridge. ES itself uses the same posture for projection errors (logs + continues). Consistent with the family.

**Considered and rejected:**

- **Configurable `StopOnError` flag.** Deferred to v1.1; v1 keeps the surface small. If a real consumer needs hard-stop semantics, the v1.1 design can take that signal.
- **Rethrow / let `IHostedService` die.** Would crash the host on any handler bug. Wrong default.

### D-5: `IHostedService` lifecycle

Each `.PublishViaMediator(StreamId)` call registers an `EventStoreMediatorBridge` as a hosted service:

```csharp
services.AddHostedService<EventStoreMediatorBridge>(sp => new EventStoreMediatorBridge(
    sp.GetRequiredService<IEventStore>(),
    sp.GetRequiredService<IMediator>(),
    sp.GetRequiredService<ILogger<EventStoreMediatorBridge>>(),
    streamId));
```

The .NET Generic Host starts every hosted service during `Host.RunAsync()`. Each bridge:
- `StartAsync(ct)` — calls `IEventStore.SubscribeAsync(streamId, StreamPosition.End, OnEventAsync, ct)`, stores the returned `IEventSubscription`.
- `StopAsync(ct)` — disposes the subscription.

**Rationale:** `IHostedService` is the .NET-idiomatic shape for background subscriptions. Integrates with `Host.RunAsync` naturally. Users don't write any lifecycle code.

**Considered and rejected:**

- **Explicit `IBridge.StartAsync()` call.** Forces every user to remember to start the bridge after building the host. Bridge that silently doesn't subscribe is a worse default than a bridge that auto-starts.
- **Per-service-provider singleton with lazy subscribe-on-first-resolve.** Doesn't compose with .NET Host; cannot be stopped cleanly.

## Design

### User-facing API

```csharp
// Program.cs
services.AddEventSourcing()
    .AddInMemoryPersistence()
    .PublishViaMediator(new StreamId("users"));

services.AddMediator();

// Domain event implements INotification.
public readonly record struct UserCreated(Guid UserId, string Email) : INotification;

// Notification handler — standard Mediator pattern.
public sealed class UserCreatedHandler : INotificationHandler<UserCreated>
{
    public async ValueTask Handle(UserCreated notification, CancellationToken ct)
    {
        // Reactive logic — read-model projection, integration callout, saga trigger, etc.
    }
}

// Anywhere an event is appended:
await eventStore.AppendAsync(
    new StreamId("users"),
    new object[] { new UserCreated(userId, email) },
    StreamPosition.Any);

// Notification handler fires automatically via the bridge.
```

### Internal implementation

`src/ZeroAlloc.EventSourcing.Mediator/EventSourcingBuilderMediatorExtensions.cs`:

```csharp
namespace ZeroAlloc.EventSourcing.Mediator;

public static class EventSourcingBuilderMediatorExtensions
{
    /// <summary>
    /// Registers a live-event bridge from <paramref name="streamId"/> to <see cref="IMediator"/>.
    /// LIVE committed events implementing <see cref="INotification"/> are published via
    /// <see cref="IMediator.Publish"/>. History replays are NEVER bridged.
    /// </summary>
    public static EventSourcingBuilder PublishViaMediator(this EventSourcingBuilder builder, StreamId streamId)
    {
        builder.Services.AddHostedService<EventStoreMediatorBridge>(sp =>
            new EventStoreMediatorBridge(
                sp.GetRequiredService<IEventStore>(),
                sp.GetRequiredService<IMediator>(),
                sp.GetRequiredService<ILogger<EventStoreMediatorBridge>>(),
                streamId));
        return builder;
    }
}
```

`src/ZeroAlloc.EventSourcing.Mediator/INotificationDispatcher.cs`:

```csharp
namespace ZeroAlloc.EventSourcing.Mediator;

/// <summary>
/// Dispatches an ES event payload to Mediator's typed <c>Publish&lt;T&gt;</c>. The bridge package
/// declares this interface; the bundled source generator emits the concrete implementation
/// that switches over every <see cref="INotification"/> type discovered in the consuming
/// compilation. Users never implement this interface themselves.
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

`src/ZeroAlloc.EventSourcing.Mediator/EventStoreMediatorBridge.cs`:

```csharp
internal sealed class EventStoreMediatorBridge : IHostedService
{
    private readonly IEventStore _store;
    private readonly INotificationDispatcher _dispatch;
    private readonly ILogger<EventStoreMediatorBridge> _log;
    private readonly StreamId _streamId;
    private IEventSubscription? _subscription;

    public EventStoreMediatorBridge(
        IEventStore store,
        INotificationDispatcher dispatch,
        ILogger<EventStoreMediatorBridge> log,
        StreamId streamId)
    {
        _store = store;
        _dispatch = dispatch;
        _log = log;
        _streamId = streamId;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _subscription = await _store
            .SubscribeAsync(_streamId, StreamPosition.End, OnEventAsync, ct)
            .ConfigureAwait(false);
        await _subscription.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask OnEventAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Event is not INotification)
        {
            _log.LogDebug(
                "Bridge skipping non-INotification event {EventType} on {StreamId}",
                envelope.Metadata.EventType,
                _streamId);
            return;
        }

        try
        {
            await _dispatch.DispatchAsync(envelope.Event, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Mediator.Publish threw for event {EventId} of type {EventType} on stream {StreamId}",
                envelope.Metadata.EventId,
                envelope.Metadata.EventType,
                _streamId);
        }
    }
}
```

`src/ZeroAlloc.EventSourcing.Mediator/EventSourcingBuilderMediatorExtensions.cs`:

```csharp
public static partial class EventSourcingBuilderMediatorExtensions
{
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

    static partial void EnsureDispatcherRegistered(IServiceCollection services);
}
```

### Generator emit shape

In the consuming compilation the generator emits (under `// <auto-generated/>`):

```csharp
namespace ZeroAlloc.EventSourcing.Mediator.Generated
{
    internal sealed class GeneratedNotificationDispatcher : INotificationDispatcher
    {
        private readonly IMediator _mediator;
        public GeneratedNotificationDispatcher(IMediator mediator) => _mediator = mediator;

        public ValueTask DispatchAsync(object @event, CancellationToken ct) => @event switch
        {
            // One arm per INotification-implementing type discovered in the compilation:
            global::MyApp.UserCreated uc  => _mediator.Publish(uc, ct),
            global::MyApp.OrderPlaced op  => _mediator.Publish(op, ct),
            // ... etc ...
            _ => ValueTask.CompletedTask
        };
    }
}

namespace ZeroAlloc.EventSourcing.Mediator
{
    public static partial class EventSourcingBuilderMediatorExtensions
    {
        static partial void EnsureDispatcherRegistered(IServiceCollection services)
        {
            services.TryAddSingleton<INotificationDispatcher, Generated.GeneratedNotificationDispatcher>();
        }
    }
}
```

The `switch` over `@event` is JIT-optimized into a type-check chain; for each match the typed `_mediator.Publish<TConcrete>(concrete, ct)` is the same overload Mediator's own generator emits for the consumer. No reflection, no boxing beyond what the `object` parameter already requires (the JIT will pattern-match without allocating).

### Generator discovery

The generator uses `IIncrementalGenerator` + `SyntaxProvider.CreateSyntaxProvider`:

```csharp
context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (n, _) => n is TypeDeclarationSyntax,
    transform: static (ctx, ct) => {
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol;
        if (symbol is null) return null;
        if (symbol.AllInterfaces.Any(i => i.ToDisplayString() == "ZeroAlloc.Mediator.INotification"))
            return symbol;
        return null;
    })
    .Where(static s => s is not null)
    .Collect();
```

Types implementing `INotification` transitively (e.g., a base class implements it) are included via `AllInterfaces`. No attribute decoration is required on event types — implementing `INotification` is the discovery signal.

### Diagnostics (`ZESM###` prefix)

The generator declares one diagnostic descriptor:

- **`ZESM001` (Warning)** — Bridge generator found zero `INotification`-implementing types in the consuming compilation. Either no events are bridged (likely a misconfiguration), or the user hasn't yet declared event types. The generated dispatcher emits an empty switch (default-case-only); the bridge will throw a clear "no events registered" exception at runtime when the user appends an event to a bridged stream.

No `Error`-severity diagnostics. A misconfigured bridge is a runtime-discoverable problem, not a build-blocker — users may legitimately scaffold the bridge before declaring events.

### Tests

`tests/ZeroAlloc.EventSourcing.Mediator.Tests/`:

1. **`Bridge_PublishesLiveEvent_ToMediator`** — append AFTER bridge starts; assert handler fired with the right event.
2. **`Bridge_DoesNotPublishHistory_FromBeforeSubscription`** — append BEFORE bridge starts; start bridge; assert handler did NOT fire (the entire replay-filter promise).
3. **`Bridge_SkipsNonNotificationEvents`** — append an event that doesn't implement `INotification`; assert handler not called and no exception thrown.
4. **`Bridge_ContinuesAfterHandlerThrows`** — handler throws on event #1; append event #2; assert event #2 still reaches the handler.
5. **`Bridge_StopAsync_DisposesSubscription`** — start, stop, append; assert handler does NOT fire after stop.

All run against `AddInMemoryPersistence()` — fast, no IO.

### AOT smoke

`samples/ZeroAlloc.EventSourcing.Mediator.AotSmoke/` — minimal host wiring `AddEventSourcing().AddInMemoryPersistence().PublishViaMediator("test")` + `AddMediator()` + a handler. Boots the host, appends one event, asserts the handler fires, exits. Published under `PublishAot=true` in CI.

### Files

```
src/ZeroAlloc.EventSourcing.Mediator/                # runtime (TFM net8/9/10)
├── ZeroAlloc.EventSourcing.Mediator.csproj          # bundles generator DLL into analyzers/dotnet/cs/
├── PublicAPI.Shipped.txt                            # empty
├── PublicAPI.Unshipped.txt                          # PublishViaMediator + INotificationDispatcher surface
├── INotificationDispatcher.cs                       # public interface — generator implements it
├── EventSourcingBuilderMediatorExtensions.cs        # static partial class
└── EventStoreMediatorBridge.cs                      # internal IHostedService

src/ZeroAlloc.EventSourcing.Mediator.Generator/      # generator (TFM netstandard2.0)
├── ZeroAlloc.EventSourcing.Mediator.Generator.csproj  # IsRoslynComponent + IsPackable=false
├── Diagnostics.cs                                   # ZESM001 descriptor
├── NotificationDiscovery.cs                         # SyntaxProvider walker
├── DispatcherEmitter.cs                             # emits GeneratedNotificationDispatcher
└── EventSourcingMediatorGenerator.cs                # IIncrementalGenerator entry point

tests/ZeroAlloc.EventSourcing.Mediator.Tests/
├── ZeroAlloc.EventSourcing.Mediator.Tests.csproj
├── BridgeTests.cs                                   # 5 runtime tests
└── TestFixtures.cs                                  # notification types + handler stubs

tests/ZeroAlloc.EventSourcing.Mediator.Generator.Tests/
├── ZeroAlloc.EventSourcing.Mediator.Generator.Tests.csproj
├── TestHarness.cs                                   # VerifyXunit + CSharpCompilation helpers
├── DispatcherEmitterTests.cs                        # snapshot tests for emit
└── Snapshots/*.verified.txt                         # committed approved snapshots

samples/ZeroAlloc.EventSourcing.Mediator.AotSmoke/
├── ZeroAlloc.EventSourcing.Mediator.AotSmoke.csproj
└── Program.cs
```

Plus updates to:
- `ZeroAlloc.EventSourcing.slnx` — add 5 new project paths (runtime + generator + 2 test projects + sample).
- `release-please-config.json` — `src/ZeroAlloc.EventSourcing.Mediator` package mapping. The generator csproj is `IsPackable=false`, so no separate release-please entry; its DLL ships inside the runtime nupkg via analyzer bundling.
- `.release-please-manifest.json` — runtime sub-package at `0.0.0`.
- `.github/workflows/ci.yml` — runtime csproj added to the pack-list (generator csproj NOT added — it's `IsPackable=false`).
- `docs/BACKLOG.md` (org-wide, post-release) — mark this item shipped.

## Out of scope (v1.1+)

- **`StopOnError` / configurable error policy.** Today: log+continue, hardcoded.
- **`EventCommittedNotification<TEvent>` wrapper** for events that can't implement `INotification`.
- **Position checkpoint persistence.** Today: live-only, subscriptions restart from End on each host boot. No replay-recovery on host restart.
- **Wildcard / multi-stream subscription in one call beyond `params StreamId[]`.**
- **Idempotency / dedup** of duplicate `.PublishViaMediator(sameStream)` calls. Today: same stream registered twice → two bridges → two publishes per event (user's responsibility).
- **`ZeroAlloc.Telemetry` integration.** Tracing the bridge dispatch path is interesting but deferred until usage signals it.
- **`AsyncEvents` alternative bridge.** ES has `ZeroAlloc.AsyncEvents` integration; if a user wants events to flow there instead, they use AsyncEvents directly — not this bridge.

## Backward compatibility

New sub-package, additive only. The existing `EventSourcingBuilder` gets one new extension method (`PublishViaMediator`) — no changes to existing public APIs.

SemVer: the new sub-package starts at `1.0.0`. release-please cuts the tag as `mediator-v1.0.0` (matching the multi-package config's `include-component-in-tag: true` for sub-packages). Core `ZeroAlloc.EventSourcing` is unaffected — its version line continues independently.
