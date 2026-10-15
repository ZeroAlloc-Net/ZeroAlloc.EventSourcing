# ZeroAlloc.EventSourcing.Outbox

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.EventSourcing.Outbox.svg)](https://www.nuget.org/packages/ZeroAlloc.EventSourcing.Outbox)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/actions/workflows/ci.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/LICENSE)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

## What it is

At-least-once cross-aggregate event dispatch from the ZeroAlloc event store. `ZeroAlloc.EventSourcing.Outbox` runs as a hosted service that polls the global event stream and dispatches every event implementing `ZeroAlloc.Mediator.INotification` through an `INotificationDispatcher` (the in-process Mediator bridge). The key design choice: the event log **is** the outbox — there is no separate dispatch table and no dual-write to keep consistent. Position is tracked in `ICheckpointStore` so a restart resumes exactly where the previous process stopped. Dispatch is polling-based and in-process through Mediator, and the package is AOT-clean.

## Install

```sh
dotnet add package ZeroAlloc.EventSourcing.Outbox
```

## Quick start

A canonical cross-aggregate flow: when an `Order` aggregate emits `OrderShipped`, credit loyalty points on the `Customer` aggregate.

```csharp
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Mediator;
using ZeroAlloc.EventSourcing.Outbox;
using ZeroAlloc.Mediator;

public sealed record OrderShipped(Guid OrderId, Guid CustomerId, decimal Amount) : INotification;

public sealed class LoyaltyPointsCreditHandler : INotificationHandler<OrderShipped>
{
    private readonly IAggregateRepository<Customer, Guid> _customers;

    public LoyaltyPointsCreditHandler(IAggregateRepository<Customer, Guid> customers)
        => _customers = customers;

    public async ValueTask Handle(OrderShipped e, CancellationToken ct)
    {
        var loaded = await _customers.LoadAsync(e.CustomerId, ct);
        using var customer = loaded.Value;
        customer.CreditLoyaltyPoints(e.OrderId, (int)(e.Amount / 10m));

        var saved = await _customers.SaveAsync(customer, e.CustomerId, ct);
        if (!saved.IsSuccess)
            throw new InvalidOperationException(saved.Error.Message);
    }
}
```

Wire it up:

```csharp
services.AddEventSourcing()
        .UseInMemoryCheckpointStore()
        .UseInMemoryEventStore()
        .AddOutbox(opts =>
        {
            opts.ConsumerId = "myapp-outbox";
        });

// The dispatcher the ZeroAlloc.EventSourcing.Mediator source generator emits into your assembly.
services.AddSingleton<INotificationDispatcher,
    ZeroAlloc.EventSourcing.Mediator.Generated.GeneratedNotificationDispatcher>();
```

`AddOutbox(...)` registers the `OutboxDispatcher` as an `IHostedService`. It resolves `IEventStore`, `ICheckpointStore` and `INotificationDispatcher` from DI. The generated `GeneratedNotificationDispatcher` forwards each event to `IMediator.Publish` through a type switch, with no reflection and no `Activator.CreateInstance`, so `IMediator` must be registered as well. The handler above also needs an `IEventTypeRegistry` and an `IAggregateRepository<Customer, Guid>` registration.

## Configuration

| Property | Type | Default | Description |
|---|---|---|---|
| `ConsumerId` | `string` | `"outbox"` | Checkpoint-store key. Must be non-whitespace. |
| `BatchSize` | `int` | `100` | Events per poll. Must be >= 1. |
| `PollInterval` | `TimeSpan` | `1s` | Delay between empty-batch polls. Must be non-negative. |
| `ErrorStrategy` | `ErrorHandlingStrategy` | `DeadLetter` | What to do after retries are exhausted. |
| `CommitStrategy` | `CommitStrategy` | `AfterEvent` | When to advance the checkpoint. |
| `MaxRetries` | `int` | `3` | Per-event retry budget. `0` disables retry. |
| `RetryPolicy` | `IRetryPolicy` | exponential backoff 100ms -> 30s | Delay schedule between retries. |

## At-least-once contract

`OutboxDispatcher` guarantees **at-least-once** delivery, not exactly-once. A process crash, a network blip on the checkpoint write, or `host.StopAsync()` mid-dispatch will all cause the next run to re-deliver events from the last successfully committed checkpoint. **Handlers MUST be idempotent.** This is a load-bearing invariant — if a handler is not safe to re-run, the outbox will eventually corrupt your data.

## Idempotency recipes

### Aggregate-version-based (canonical)

When the handler mutates an aggregate, lean on the event store's optimistic concurrency. The handler reloads the target aggregate, applies the operation, and calls `AppendAsync(...)` at the version it loaded. On a redelivery the aggregate version has already advanced past that expected version, so `AppendAsync` returns a `StoreError.Conflict`. The handler swallows the conflict and treats the work as already done.

See [`IdempotencyDemoTests.cs`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/tests/ZeroAlloc.EventSourcing.Outbox.Tests/IdempotencyDemoTests.cs) for a runnable demo. A conflict is recognised by `StoreError.Code == "CONFLICT"`; there is no public constant for that code yet.

### Dedup-table-based

For handlers without an aggregate (email senders, webhook publishers, projection writers), persist the event id + a processed flag in the handler's own table inside the same transaction as the side effect. If the row already exists, skip. ZA.ORM's `[Command]` covers this in roughly five lines:

```csharp
[Command("""
INSERT INTO outbox_dedup (event_id, processed_at)
VALUES (@EventId, @Now)
ON CONFLICT (event_id) DO NOTHING
RETURNING event_id;
""")]
public partial Task<Guid?> TryClaimAsync(Guid eventId, DateTime now);
```

If `TryClaimAsync` returns null the event has already been processed — return without doing the work.

## Excluding event types

Some `INotification` events are emitted for aggregate-internal reasons (snapshot markers, debug audit records) and should never reach external handlers. Opt them out at the type level:

```csharp
services.AddEventSourcing()
        .UseInMemoryCheckpointStore()
        .UseInMemoryEventStore()
        .AddOutbox(opts =>
        {
            opts.Exclude<InternalSnapshotMarker>();
            opts.Exclude<DebugAuditEvent>();
        });
```

`Exclude<TEvent>()` is chainable and idempotent. Excluded events still flow through the consumer (their checkpoint advances), they just bypass `INotificationDispatcher.DispatchAsync`.

## Error handling strategies

When per-event retries are exhausted (`MaxRetries` reached), `ErrorStrategy` decides what happens next:

- **`Skip`** — log the failure and continue with the next event. The failing event is lost for this handler. Unsafe for anything you care about.
- **`DeadLetter`** (default) — write the envelope + the exception to `IDeadLetterStore` and continue. The event is preserved for manual inspection and replay. Requires an `IDeadLetterStore` registration.
- **`FailFast`** — rethrow and halt the dispatcher. Loud failure. Pick this when silent data loss is worse than downtime.

## AOT-clean

The package is validated against `PublishAot=true` in [`samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke), which CI publishes and runs. The dispatch path uses no reflection, no `Activator.CreateInstance`, no `MakeGenericMethod` — type registration and notification dispatch are emitted by the `ZeroAlloc.EventSourcing.Mediator` source generator, and the outbox itself only orchestrates `StreamConsumer` + `INotificationDispatcher.DispatchAsync(object, CancellationToken)`. The repository builds with warnings as errors, so any trim or AOT warning such as `IL2026` or `IL3050` fails the smoke publish.

## Limitations

- **Polling only.** The dispatcher polls the global stream every `PollInterval`; it does not use `IEventStore.SubscribeAsync` for live delivery.
- **In-process only.** Events go to handlers in the same process through `INotificationDispatcher`; there is no cross-process broker transport.
- **No conflict-code constant.** The aggregate-version recipe above matches the `"CONFLICT"` string literal, because `StoreError` has no public constant for it.

## License

MIT — see [LICENSE](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/LICENSE).
