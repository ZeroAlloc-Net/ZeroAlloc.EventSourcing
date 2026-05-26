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

## Decisions

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

`src/ZeroAlloc.EventSourcing.Mediator/EventStoreMediatorBridge.cs`:

```csharp
internal sealed class EventStoreMediatorBridge : IHostedService
{
    private readonly IEventStore _store;
    private readonly IMediator _mediator;
    private readonly ILogger _log;
    private readonly StreamId _streamId;
    private IEventSubscription? _subscription;

    public EventStoreMediatorBridge(
        IEventStore store,
        IMediator mediator,
        ILogger<EventStoreMediatorBridge> log,
        StreamId streamId)
    {
        _store = store;
        _mediator = mediator;
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
        if (envelope.Event is not INotification notification)
        {
            _log.LogDebug(
                "Bridge skipping non-INotification event {EventType} on {StreamId}",
                envelope.Metadata.EventType,
                _streamId);
            return;
        }

        try
        {
            await _mediator.Publish(notification, ct).ConfigureAwait(false);
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

### Note on `IMediator.Publish`

Mediator's `Publish` is generic — `mediator.Publish<TNotification>(TNotification, CancellationToken)`. The bridge calls `mediator.Publish(notification, ct)` where `notification` is typed as `INotification`. Mediator's source generator emits `Publish<TConcrete>` overloads for every discovered notification type in the consuming compilation; runtime dispatch via the interface is supported by Mediator's runtime layer. (Verify the exact API shape during Task 1 of the implementation plan; if Mediator requires `Publish<T>` and rejects `Publish(INotification)`, the bridge will need to do a runtime dispatch via `MakeGenericMethod` once — a known AOT compromise that we'd flag and revisit.)

If Mediator requires `Publish<TConcrete>(TConcrete)` only, the most AOT-safe fallback is to dispatch via `mediator.Publish<INotification>(notification, ct)` — the generated `Publish<INotification>` overload covers the interface dispatch. This should be confirmed against the actual Mediator runtime in Task 1.

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
src/ZeroAlloc.EventSourcing.Mediator/
├── ZeroAlloc.EventSourcing.Mediator.csproj
├── PublicAPI.Shipped.txt                            # empty
├── PublicAPI.Unshipped.txt                          # PublishViaMediator + EventStoreMediatorBridge surface
├── EventSourcingBuilderMediatorExtensions.cs        # public fluent extension
└── EventStoreMediatorBridge.cs                      # internal IHostedService

tests/ZeroAlloc.EventSourcing.Mediator.Tests/
├── ZeroAlloc.EventSourcing.Mediator.Tests.csproj
├── BridgeTests.cs                                   # 5 tests above
└── TestFixtures.cs                                  # notification types + handler stubs

samples/ZeroAlloc.EventSourcing.Mediator.AotSmoke/
├── ZeroAlloc.EventSourcing.Mediator.AotSmoke.csproj
└── Program.cs
```

Plus updates to:
- `ZeroAlloc.EventSourcing.slnx` — add 3 new project paths
- `release-please-config.json` — add `src/ZeroAlloc.EventSourcing.Mediator` package mapping
- `.release-please-manifest.json` — add the sub-package at `0.0.0`
- `.github/workflows/release-please.yml` (and/or `ci.yml`) — add the new csproj to the pack step
- `docs/BACKLOG.md` (org-wide) — mark this item shipped post-release

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
