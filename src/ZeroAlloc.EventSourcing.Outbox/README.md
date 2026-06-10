# ZeroAlloc.EventSourcing.Outbox

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.EventSourcing.Outbox.svg)](https://www.nuget.org/packages/ZeroAlloc.EventSourcing.Outbox)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)

## What it is

At-least-once cross-aggregate event dispatch from the ZeroAlloc event store. `ZeroAlloc.EventSourcing.Outbox` runs as a hosted service that polls the global event stream and dispatches every event implementing `ZeroAlloc.Mediator.INotification` through an `INotificationDispatcher` (the in-process Mediator bridge). The key design choice: the event log **is** the outbox — there is no separate dispatch table and no dual-write to keep consistent. Position is tracked in `ICheckpointStore` so a restart resumes exactly where the previous process stopped. v0.1 is polling-based, in-process Mediator dispatch, and AOT-clean.

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
        var customer = await _customers.LoadAsync(e.CustomerId, ct);
        customer.CreditLoyaltyPoints(e.OrderId, (int)(e.Amount / 10m));
        await _customers.SaveAsync(customer, ct);
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
```

`AddOutbox(...)` registers the `OutboxDispatcher` as an `IHostedService`. You must also have an `INotificationDispatcher` in DI — this is what the `ZeroAlloc.EventSourcing.Mediator` source generator emits (zero reflection, zero `Activator.CreateInstance`).

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

See `tests/ZeroAlloc.EventSourcing.Outbox.Tests/IdempotencyDemoTests.cs` for a runnable demo. (v0.1 checks the conflict error code with the string literal `"CONFLICT"` — v0.2 will expose a public `StoreError.ConflictCode` constant.)

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

The package is validated against `PublishAot=true` in `samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/`. The dispatch path uses no reflection, no `Activator.CreateInstance`, no `MakeGenericMethod` — type registration and notification dispatch are emitted by the `ZeroAlloc.EventSourcing.Mediator` source generator, and the outbox itself only orchestrates `StreamConsumer` + `INotificationDispatcher.DispatchAsync(object, CancellationToken)`. `IL2026` / `IL3050` are treated as build errors in the smoke project.

## Roadmap

**v0.1 (this release) — explicitly out of scope:**

**v0.2 planned:**

- LIVE subscription path via `IEventStore.SubscribeAsync` (currently v0.1 is polling-only)
- Lifecycle hardening: restart-after-stop, double-`StopAsync` guards
- `InMemoryEventStoreAdapter` `*` pseudo-stream fix — the in-memory adapter currently conflates per-stream version with the global consumer cursor (see TODO at `src/ZeroAlloc.EventSourcing.InMemory/InMemoryEventStoreAdapter.cs`)
- `StoreError.ConflictCode` as a public constant so the aggregate-version idempotency recipe can stop matching the `"CONFLICT"` string literal

**v0.3+ planned:**

- Cross-process broker abstraction (Kafka, RabbitMQ, gRPC), shipped as separate sub-packages

**v1.0:**

- Public API freeze. Triggered when the outbox becomes the substrate for the planned `ZeroAlloc.Saga` package and the `za-cqrs-es` template in `ZeroAlloc.Templates` ships against it.

## License

MIT. See LICENSE.
