# `ZeroAlloc.EventSourcing.Outbox` v0.1 — Design

**Status:** approved 2026-06-10
**Scope:** New `ZeroAlloc.EventSourcing.Outbox` sub-package — at-least-once cross-aggregate event dispatch from the event store, in-process via ZA.Mediator.
**Target version:** v0.1.0 (initial release, `feat:` commit).
**Estimated scope:** 1-2 days of focused work.
**Branch:** `feat/eventsourcing-outbox` off `main`.

## Background

The `za-cqrs-es` template (in `ZeroAlloc.Templates`) needs an outbox-pattern primitive for cross-aggregate flows — the canonical example being `OrderShipped → LoyaltyPointsCredited`, where shipping an Order triggers a Customer aggregate mutation that the original write transaction must not encompass. Without an outbox, this fan-out either (a) couples the two aggregates in a single transaction (loses the aggregate boundary), (b) uses a per-stream subscription that breaks on per-aggregate stream topologies, or (c) requires bespoke wiring per cross-aggregate edge.

The pattern is well-known. The novel observation for ZA is that **the event log itself IS the outbox** — there is no separate `outbox_events` table, no dual-write, no transactional outbox. A durable consumer reads the global event stream, dispatches each event to in-process notification handlers, and tracks its own checkpoint. Process restarts pick up from the last checkpoint; handler failures retry per policy with a dead-letter fallback.

This package is a **thin orchestration layer** over existing ZA.EventSourcing primitives:

- `StreamConsumer` already provides polling, batching, checkpointing, retry, and DLQ for a stream
- `INotificationDispatcher` (from `ZA.Mediator`) already routes events to `INotificationHandler<TEvent>` registrations
- `ICheckpointStore`, `IDeadLetterStore`, `IRetryPolicy`, `ErrorHandlingStrategy`, `CommitStrategy` are already first-class abstractions

The Outbox package wires these together with a hosted-service lifecycle, an opt-out filter for event types that should NOT fan out, and an `EventSourcingBuilder` registration extension.

## Decision

Ship a separate `ZeroAlloc.EventSourcing.Outbox` package depending on `ZeroAlloc.EventSourcing` + `ZeroAlloc.Mediator`. **Not** a hard dep on `ZeroAlloc.EventSourcing.Mediator` — the 5-line dispatch shape `if (envelope.Event is INotification && !excluded) dispatcher.DispatchAsync(...)` is copy-paste-clean from the bridge; extracting a shared helper for 5 lines is premature abstraction.

**Rejected alternatives:**

- **Land inside `ZA.EventSourcing.Mediator`** — bundles two distinct dispatch models (per-stream live bridge + global durable outbox) under one package name. Users may not realize both exist. Splitting cleanly signals "different topology, different package."
- **Implicit-everywhere dispatch (no opt-out)** — leaves no escape hatch for users whose events implement `INotification` for in-process aggregate reasons (snapshot replay, etc.) but should NOT fan out cross-aggregate. The opt-out is a small surface area win for a real footgun.
- **`[OutboxEvent]` attribute opt-in** — adds a second marker on top of `INotification`. Two markers for one concept. The opt-out via `OutboxOptions.Exclude<TEvent>()` is the safety belt; the default path stays "register a handler = subscribe."
- **Cross-process broker dispatch** (Kafka, RabbitMQ, gRPC) — deferred to a future package. v0.1 is in-process Mediator dispatch only. Cross-process is the natural extension; not blocking.
- **`outbox_events` table** — rejected. The event log IS the outbox; a separate dispatch table re-implements what `StreamConsumer + ICheckpointStore` already provide.
- **LIVE subscription** via `IEventStore.SubscribeAsync` — deferred. Polling via `StreamConsumer.ReadAsync` works with every adapter (in-memory, SQLite-once-it-ships, Postgres) and gives the same at-least-once contract. LIVE is a latency optimization, not v0.1 scope.
- **Exactly-once delivery** — out of scope. Requires two-phase commit or transactional outbox; impossible across aggregates by definition. At-least-once + idempotent handlers is the textbook contract.

## What ships in this package

### Folder structure

```
src/ZeroAlloc.EventSourcing.Outbox/
├── ZeroAlloc.EventSourcing.Outbox.csproj
├── OutboxOptions.cs                              (~50 LOC)
├── OutboxDispatcher.cs                           (~200 LOC — IHostedService + IAsyncDisposable)
├── EventSourcingBuilderOutboxExtensions.cs       (~80 LOC — AddOutbox(...) chain)
├── README.md                                     (~150 LOC — concepts + recipes)
└── CHANGELOG.md                                  (release-please managed)

tests/ZeroAlloc.EventSourcing.Outbox.Tests/
├── ZeroAlloc.EventSourcing.Outbox.Tests.csproj
└── OutboxDispatcherTests.cs                      (~15 facts, see Testing)

samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/
├── ZeroAlloc.EventSourcing.Outbox.AotSmoke.csproj  (PublishAot=true)
└── Program.cs                                      (~80 LOC console app)

src/ZeroAlloc.EventSourcing.Outbox.Benchmarks/
├── ZeroAlloc.EventSourcing.Outbox.Benchmarks.csproj
└── OutboxDispatchBenchmark.cs                      (~3 benchmarks)
```

### Public API surface

```csharp
public sealed class OutboxOptions
{
    public string ConsumerId { get; set; } = "outbox";
    public int BatchSize { get; set; } = 100;
    public IRetryPolicy RetryPolicy { get; set; } = new ExponentialBackoffRetryPolicy(...);
    public ErrorHandlingStrategy ErrorStrategy { get; set; } = ErrorHandlingStrategy.DeadLetter;
    public CommitStrategy CommitStrategy { get; set; } = CommitStrategy.AfterEvent;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public OutboxOptions Exclude<TEvent>() where TEvent : class;
    internal IReadOnlyCollection<Type> ExcludedTypes { get; }
}

public sealed class OutboxDispatcher : IHostedService, IAsyncDisposable
{
    public OutboxDispatcher(
        IEventStore store,
        ICheckpointStore checkpoints,
        INotificationDispatcher dispatcher,
        IDeadLetterStore? deadLetters,
        OutboxOptions options,
        ILogger<OutboxDispatcher> logger);

    public Task StartAsync(CancellationToken ct);
    public Task StopAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
}

public static class EventSourcingBuilderOutboxExtensions
{
    public static EventSourcingBuilder AddOutbox(
        this EventSourcingBuilder builder,
        Action<OutboxOptions>? configure = null);
}
```

User-facing wiring:

```csharp
services.AddZeroAllocEventSourcing(es =>
{
    es.UseSqliteAdapter(connectionString);   // (waits for SQLite event-store work)
    es.AddOutbox(opts =>
    {
        opts.ConsumerId = "myapp-outbox";
        opts.Exclude<InternalSnapshotMarker>();
    });
});
```

### Architecture

```
┌─ OutboxDispatcher : IHostedService ──────────────────────────────┐
│                                                                  │
│   StreamConsumer("outbox-{ConsumerId}", "*", checkpointStore)    │
│                          │                                       │
│                  for each EventEnvelope                          │
│                          ▼                                       │
│   if (envelope.Event is INotification &&                         │
│       !Options.Excluded.Contains(event.GetType()))               │
│       INotificationDispatcher.DispatchAsync(event)               │
│   else skip                                                      │
│                                                                  │
│   On dispatcher exception: IRetryPolicy → IDeadLetterStore       │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

Three observations:
- **Reuses `StreamConsumer`** for polling, batch processing, checkpointing, retry, DLQ — zero re-implementation
- **Reuses `INotificationDispatcher`** from `ZA.Mediator` — same dispatch hook the bridge uses
- **Polling, not LIVE** — works with any adapter, at-least-once contract intact

### Data flow

**Write path** (unchanged — outbox is invisible to writers):

```
PlaceOrderHandler / ShipOrderHandler / etc.
  → aggregate.Raise(event)
  → repo.SaveAsync(aggregate)
    → IEventStore.AppendAsync(streamId, [event], expectedVersion)
      → IEventStoreAdapter.AppendAsync(...)   ← SQL transaction commits event row
  → returns to handler                         ← write is durable
```

The writer never sees the Outbox. No dual-write. No extra table.

**Dispatch path** (the Outbox loop, running as IHostedService):

```
OutboxDispatcher.StartAsync()
  → spawns background Task running:
    StreamConsumer("outbox-{ConsumerId}", "*").ConsumeAsync(handler):
      loop:
        position ← checkpointStore.ReadAsync("outbox-{ConsumerId}")
        batch ← eventStore.ReadAsync("*", position, take: BatchSize)
        if batch is empty:
          await Task.Delay(PollInterval)
          continue
        for each envelope in batch:
          if envelope.Event is INotification && !Options.Excluded.Contains(event.GetType()):
            try:
              notificationDispatcher.DispatchAsync(envelope.Event, ct)
            catch ex:
              retry per Options.RetryPolicy
              on exhaustion → Options.ErrorStrategy:
                Skip       → log + continue
                DeadLetter → deadLetterStore.WriteAsync(envelope, ex) + continue
                Stop       → throw → dispatcher halts
          else:
            skip (no-op)
          checkpointStore.WriteAsync("outbox-{ConsumerId}", envelope.Position)
```

**Cross-aggregate flow end-to-end** (the za-cqrs-es Task 5 case):

```
1. POST /orders/{id}/ship
   → ShipOrderHandler → Order.Ship(tracking) → Raise(OrderShipped)
   → repo.SaveAsync → event row committed

2. Outbox loop polls "*" → sees OrderShipped envelope
   → INotification? yes. Excluded? no.
   → dispatcher.DispatchAsync(orderShipped):
       LoyaltyPointsCreditHandler.Handle(orderShipped):
         customer ← customerRepo.LoadAsync(orderShipped.CustomerId)
         customer.CreditLoyaltyPoints((int)Math.Floor(orderShipped.Total))
         customerRepo.SaveAsync(customer)   ← LoyaltyPointsCredited committed

3. CustomerProfilesProjection (a SEPARATE INotificationHandler<LoyaltyPointsCredited>)
   eventually picks up LoyaltyPointsCredited via the same outbox loop
   → materializes customer_profiles.loyalty_points row
```

Step 2's `SaveAsync` writes a NEW event that flows through the same outbox loop on the next poll. Projections see it. Self-consistent.

**Restart semantics:** dispatcher dies mid-batch → on restart, checkpoint hasn't advanced past the failed event → outbox re-delivers from last checkpoint. **At-least-once.** Handlers MUST be idempotent.

### Error handling & idempotency

| Failure | Detection | Response |
|---|---|---|
| Handler throws (transient) | `try/catch` around `DispatchAsync` | Retry per `IRetryPolicy` (default exponential, 3 attempts). On exhaustion → `ErrorStrategy`. |
| Handler throws (poison) | Same exception path | Same retry exhaustion → `ErrorStrategy.DeadLetter` (default) writes DLQ; loop continues. |
| Dispatcher crashes | Process restart | Hosted service restarts → `StreamConsumer` resumes from `ICheckpointStore` → re-delivers in-flight batch. |

**At-least-once contract is load-bearing — all handlers MUST be idempotent.**

Two recipes documented in the README:

1. **Aggregate-version-based.** Handlers mutating aggregates use `expectedVersion`. A redelivered event re-loads, re-applies, gets `StoreError.Conflict` from `AppendAsync` (version advanced). Treat conflict as "already done" → swallow. `LoyaltyPointsCreditHandler` from the template falls into this naturally.
2. **Dedup-table-based.** Handlers without an aggregate (email senders, webhook publishers) write event id + processed flag in their own transaction, skip if seen. ZA.ORM `[Command]` partial covers in ~5 lines.

### Testing

Three projects mirror the existing ZA.EventSourcing convention.

**`ZeroAlloc.EventSourcing.Outbox.Tests`** (xUnit + ZA.TestHelpers) against `InMemoryEventStoreAdapter` + `InMemoryCheckpointStore`. Target ~15 facts:

- Happy path: append → dispatcher → handler runs once → checkpoint advances
- Filter: `INotification` event, type excluded → skip, checkpoint advances
- Filter: non-`INotification` event → skip, checkpoint advances
- Retry: handler throws transient → retry → eventually succeeds
- DeadLetter: handler throws past retry exhaustion → DLQ row written → loop continues
- Skip strategy: same setup, `ErrorStrategy.Skip` → no DLQ row
- Stop strategy: same setup, `ErrorStrategy.Stop` → background task surfaces exception
- Restart: seed events past checkpoint → dispatcher picks up from checkpoint, not Start
- Cross-aggregate: integration test mirroring za-cqrs-es Task 5 — handler triggers second `SaveAsync` whose new event the outbox picks up on next poll
- Idempotency demo: handler runs twice → second is `StoreError.Conflict` swallow → end state correct
- Batch boundary: seed `BatchSize + 5` events → all delivered across two poll cycles
- Cancellation: `StopAsync` mid-batch → in-flight handler completes, loop exits cleanly

**`ZeroAlloc.EventSourcing.Outbox.AotSmoke`** — `PublishAot=true` console app wires dispatcher against `InMemoryEventStoreAdapter`, raises events, asserts handler ran. CI runs `dotnet publish -r linux-x64 -c Release` and asserts exit-code-0. Validates no reflection in the runtime path.

**`ZeroAlloc.EventSourcing.Outbox.Benchmarks`** (BenchmarkDotNet) — three benchmarks:
1. Single-event dispatch throughput end-to-end
2. Batch throughput at `BatchSize = 100`
3. Exclusion-set check overhead on a no-handler event

Target: dispatcher overhead ≤ 5µs per event on top of `StreamConsumer` baseline. Land the suite, don't gate v0.1 on the target.

## Out of scope (deferrals)

- Cross-process broker dispatch (Kafka, RabbitMQ, gRPC) — future `ZA.EventSourcing.Outbox.<Broker>` packages
- LIVE subscription path — `IEventStore.SubscribeAsync` integration, future perf work
- DLQ replay endpoint / admin tools — pattern documented, not shipped
- Saga support — that's the future `ZA.Saga` template's job; the Outbox is the substrate it will use
- Exactly-once delivery — impossible across aggregates by definition

## Risk

- **`StreamConsumer("*", ...)` correctness.** The global-stream read path is exercised by the existing tests, but the outbox use case (every event, durable consumer, in-process Mediator dispatch) is new. Cross-aggregate integration test mitigates.
- **Polling latency.** Default 1s poll interval means cross-aggregate flows have ~1s p99 latency. Documented; acceptable for v0.1. Cross-process LIVE adapters are the path to <100ms.
- **Handler-mutation-cascade growth.** If handler A triggers handler B triggers handler C..., the chain length is bounded only by user discipline. Standard outbox-pattern risk; not unique to this package. Documented in README under "Designing cross-aggregate flows."
- **DLQ growth.** No built-in retention. Operators must monitor + drain. Documented.

## Versioning

Initial release `v0.1.0`. Subsequent feat: minor, fix:/perf:/refactor:/docs: patch, chore: no bump. Standard release-please mapping that other ZA.EventSourcing.* packages use.

## Roadmap (post v0.1)

- v0.2: LIVE subscription path via `IEventStore.SubscribeAsync` when the adapter supports it; polling fallback otherwise
- v0.3: cross-process broker abstraction (`IOutboxPublisher`) — separate package per broker
- v1.0: API freeze once the Outbox is the substrate for `ZA.Saga` and the za-cqrs-es template ships
