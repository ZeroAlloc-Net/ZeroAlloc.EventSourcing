---
id: telemetry
title: OpenTelemetry Instrumentation
sidebar_position: 10
---

# OpenTelemetry Instrumentation

`ZeroAlloc.EventSourcing.Telemetry` adds a source-generated decorator around `IEventStore` that records `Activity` spans and metrics for every store operation — without taking a dependency on the OpenTelemetry SDK.

## Install

```bash
dotnet add package ZeroAlloc.EventSourcing.Telemetry
```

## Registration

Call `.UseEventSourcingTelemetry()` **after** registering a store backend and **before** building the service provider:

```csharp
services
    .AddEventSourcing()
    .UseInMemoryEventStore()          // or UsePostgreSqlEventStore(), etc.
    .UseEventSourcingTelemetry();     // wraps the store with the instrumented decorator
```

The extension replaces the existing `IEventStore` registration with a source-generated `EventStoreInstrumented` wrapper. Any code that resolves `IEventStore` gets the decorated version automatically.

## What Gets Instrumented

| Operation | Activity name | Metrics |
|-----------|--------------|---------|
| `AppendAsync` | `event_store.append` | `event_store.appends_total` (counter, labels: `stream_id`, `success`) |
| `ReadAsync` | `event_store.read` | — |
| `SubscribeAsync` | `event_store.subscribe` | — |

All activities are tagged with `stream.id`. On failure, `store.error` is set to the `StoreError` enum value.

## Connecting to OpenTelemetry

The decorator publishes under the source name and meter name `"ZeroAlloc.EventSourcing"`. Wire those into your OTel SDK setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("ZeroAlloc.EventSourcing")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("ZeroAlloc.EventSourcing")
        .AddOtlpExporter());
```

## Aggregate Repository Instrumentation

`ZeroAlloc.EventSourcing.Telemetry` also instruments `IAggregateRepository` when `ZeroAlloc.EventSourcing.Aggregates` is installed:

```csharp
services
    .AddEventSourcing()
    .AddAggregates()
    .UseInMemoryEventStore()
    .UseEventSourcingTelemetry();
```

The `InstrumentedAggregateRepository` decorator adds spans for `LoadAsync` and `SaveAsync`.

## Migration from `InstrumentedEventStore`

`InstrumentedEventStore` (the manual wrapper from earlier versions) is marked `[Obsolete]` and will be removed in the next minor version. Migrate by removing the manual wrapping:

```csharp
// Before (obsolete)
services.AddSingleton<IEventStore>(sp =>
    new InstrumentedEventStore(sp.GetRequiredService<MyEventStore>()));

// After
services
    .AddEventSourcing()
    .UseMyEventStore()
    .UseEventSourcingTelemetry();
```

The source-generated `EventStoreInstrumented` used internally by `UseEventSourcingTelemetry()` is equivalent but avoids the double-Meter anti-pattern that `InstrumentedEventStore` exhibited.
