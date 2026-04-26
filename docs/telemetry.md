---
id: telemetry
title: OpenTelemetry Instrumentation
sidebar_position: 10
---

# OpenTelemetry Instrumentation

`ZeroAlloc.EventSourcing.Telemetry` adds a hand-rolled decorator around `IAggregateRepository<TAggregate, TId>` that records `Activity` spans and metrics for every aggregate load/save — without taking a dependency on the OpenTelemetry SDK.

> **Breaking change in v2.0.** Instrumentation now sits at the aggregate-repository layer, not the event-store layer. Existing dashboards subscribed to `event_store.append` / `event_store.read` / `event_store.subscribe` will see nothing after upgrading. See [Migrating from v1.x](#migrating-from-v1x).

## Install

```bash
dotnet add package ZeroAlloc.EventSourcing.Telemetry
```

## Registration

Call `.WithTelemetry()` **after** registering the aggregate repository and **before** building the service provider. `WithTelemetry()` is registered per-aggregate, so it decorates each `IAggregateRepository<TAggregate, TId>` that has been registered up to that point:

```csharp
services
    .AddEventSourcing()
    .UseInMemoryEventStore()          // or UsePostgreSqlEventStore(), etc.
    .AddAggregate<OrderAggregate, Guid>()
    .WithTelemetry();                 // wraps each IAggregateRepository<,> with InstrumentedAggregateRepository<,>
```

> Renamed from `UseEventSourcingTelemetry()` for consistency with the rest of the ecosystem; the old name remains as `[Obsolete]` for one minor version and delegates to `WithTelemetry()`.

The extension replaces each existing `IAggregateRepository<TAggregate, TId>` registration with `InstrumentedAggregateRepository<TAggregate, TId>` wrapping the original. Any code that resolves `IAggregateRepository<,>` gets the decorated version automatically.

## What Gets Instrumented

The decorator targets `IAggregateRepository<TAggregate, TId>` and emits the following:

| Operation | Activity name | Counter (success only) | Histogram |
|-----------|--------------|------------------------|-----------|
| `LoadAsync` | `aggregate.load` | `aggregate.loads_total` | `aggregate.load_duration_ms` |
| `SaveAsync` | `aggregate.save` | `aggregate.saves_total` | `aggregate.save_duration_ms` |

Every span is tagged with `aggregate.type`, set to `typeof(TAggregate).Name`. On exception the span status is set to `ActivityStatusCode.Error` and the exception message is recorded; the exception is then rethrown.

The success counters (`aggregate.loads_total`, `aggregate.saves_total`) increment **only when** `Result.IsSuccess` is `true`. Failure results (i.e. `StoreError`) do not increment the counters but are still recorded in the duration histograms.

The duration histograms (`aggregate.load_duration_ms`, `aggregate.save_duration_ms`) are recorded in a `finally` block, so they cover both success and failure paths.

## Connecting to OpenTelemetry

The decorator publishes under the `ActivitySource` name and `Meter` name `"ZeroAlloc.EventSourcing"` (unchanged from v1.x). Wire those into your OTel SDK setup:

```csharp
services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource("ZeroAlloc.EventSourcing"))
        .WithMetrics(m => m.AddMeter("ZeroAlloc.EventSourcing"));
```

For exporters:

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("ZeroAlloc.EventSourcing")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("ZeroAlloc.EventSourcing")
        .AddOtlpExporter());
```

## Migrating from v1.x

In v1.x, `WithTelemetry()` (and the previous `UseEventSourcingTelemetry()` shim) decorated `IEventStore` directly via a source-generated proxy and emitted spans named after store operations. In v2.0 the decorator was moved up the abstraction stack to `IAggregateRepository<TAggregate, TId>` so spans correspond to domain-meaningful operations (loading an aggregate, saving an aggregate) instead of low-level append/read/subscribe calls.

Map your existing dashboards and alerts using the table below:

| Before (event-store-level) | After (aggregate-level) |
|---|---|
| Span `event_store.append` | Span `aggregate.save` |
| Span `event_store.read` | Span `aggregate.load` |
| Span `event_store.subscribe` | _removed; subscribe was an event-store concern_ |
| Counter `event_store.appends_total` | Counter `aggregate.saves_total` |
| Histogram `event_store.read_duration_ms` | Histogram `aggregate.load_duration_ms` |
| Tag `stream.id` | Tag `aggregate.type` (set to `typeof(TAggregate).Name`) |
| ActivitySource `ZeroAlloc.EventSourcing` | ActivitySource `ZeroAlloc.EventSourcing` (unchanged) |
| Meter `ZeroAlloc.EventSourcing` | Meter `ZeroAlloc.EventSourcing` (unchanged) |

Notes for upgraders:

- **`event_store.subscribe` has no replacement.** The decorator no longer instruments subscriptions; if you need that signal, instrument your subscription pipeline directly using the same `ActivitySource` name.
- **Per-stream tags are gone.** The aggregate-level decorator tags spans with `aggregate.type` rather than `stream.id`. If you need per-aggregate-id breakdowns, add them in your own decorator below `WithTelemetry()`.
- **`InstrumentedEventStore` has been deleted.** The class no longer exists; the `[Instrument]` attribute that drove the source generator has also been removed from `IEventStore`.
- **Registration shape is unchanged.** `.WithTelemetry()` is still the only call you need. Provided you have at least one `IAggregateRepository<,>` registered before the call, decoration happens automatically.
