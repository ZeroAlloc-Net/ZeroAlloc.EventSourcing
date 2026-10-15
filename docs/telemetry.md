---
id: telemetry
title: OpenTelemetry Instrumentation
sidebar_position: 10
---

# OpenTelemetry Instrumentation

`ZeroAlloc.EventSourcing.Telemetry` adds a hand-rolled decorator around `IAggregateRepository<TAggregate, TId>` that records `Activity` spans and metrics for every aggregate load/save — without taking a dependency on the OpenTelemetry SDK.

Instrumentation sits at the aggregate-repository layer, not the event-store layer: there are no `event_store.*` spans. Every published version of the package works this way; see [Event-store spans](#event-store-spans) for why some older material mentions them.

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
    .UseAggregateRepository<Order, OrderId>(() => new Order(), id => new StreamId($"order-{id.Value}"))
    .WithTelemetry();                 // wraps each IAggregateRepository<,> with InstrumentedAggregateRepository<,>
```

> Renamed from `UseEventSourcingTelemetry()` for consistency with the rest of the ecosystem. The old name remains as an `[Obsolete]` alias, diagnostic `ZAES001`, that delegates to `WithTelemetry()`; it is removed in the next major.

The extension replaces each existing `IAggregateRepository<TAggregate, TId>` registration with `InstrumentedAggregateRepository<TAggregate, TId>` wrapping the original. Any code that resolves `IAggregateRepository<,>` gets the decorated version automatically. That covers `UseAggregateRepository` and hand-written registrations alike, such as a `SnapshotCachingRepositoryDecorator` registered with `AddScoped`; keyed registrations stay keyed, and every registration keeps its lifetime.

### NativeAOT

`WithTelemetry()` is NativeAOT-compatible, also for a value-type `TId` such as `Guid`. It does not build `InstrumentedAggregateRepository<TAggregate, TId>` with `MakeGenericType`. It calls `IAggregateRepository.Accept`, the non-generic base interface's visitor method. The default implementation, on `IAggregateRepository<TAggregate, TId>`, hands the visitor the repository with its closed type arguments.

Every repository gets that default implementation without doing anything. A repository that re-implements `Accept` must call `visitor.Visit(this)` and return the result.

### One class serving several aggregates

A class that implements two or more closed `IAggregateRepository<,>` interfaces inherits a default `Accept` from each one, so it has no single most specific implementation.

- **Compile time.** Since 1.6.0 such a class no longer compiles, with error CS8705, until it implements `IAggregateRepository.Accept` itself.
- **Existing binaries.** A binary of such a class compiled against 1.5.0 or earlier still loads. It throws `AmbiguousImplementationException` when `WithTelemetry()` calls `Accept` on resolve.

Its own `Accept` has to pick one closed interface:

```csharp
TResult IAggregateRepository.Accept<TResult>(IAggregateRepositoryVisitor<TResult> visitor)
    => visitor.Visit<Order, OrderId>(this);
```

`WithTelemetry()` decorates it where it is registered as that interface. Where it is registered as another one, resolving throws an `InvalidOperationException` that names the registration. Register such a class only as the interface its `Accept` visits, or split it into one class per aggregate.

### Test doubles

A mocking-library proxy, such as an NSubstitute substitute or a Moq mock of `IAggregateRepository<,>`, intercepts `Accept` instead of running the default implementation. Resolving it after `WithTelemetry()` then throws an `InvalidOperationException` that says so. Either register a hand-written fake, or forward `Accept` on the substitute:

```csharp
var repository = Substitute.For<IAggregateRepository<Order, OrderId>>();
((IAggregateRepository)repository).Accept(Arg.Any<IAggregateRepositoryVisitor<object>>())
    .Returns(call => call.Arg<IAggregateRepositoryVisitor<object>>().Visit(repository));
```

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

The decorator publishes under the `ActivitySource` name and `Meter` name `"ZeroAlloc.EventSourcing"`. Wire those into your OTel SDK setup:

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

## Event-store spans

Before the first release, during development in April 2026, `WithTelemetry()` decorated `IEventStore` through a source-generated proxy and emitted `event_store.append`, `event_store.read` and `event_store.subscribe` spans. That design was replaced before `ZeroAlloc.EventSourcing.Telemetry` 1.0.0 shipped, so no NuGet release has emitted those spans and no released version needs migrating.

For stream consumers, wrap an `IStreamConsumer` in `InstrumentedStreamConsumer`, which records a `consumer.consume` span tagged with `consumer.id`. For anything else at the event-store level, instrument it yourself using the same `ZeroAlloc.EventSourcing` `ActivitySource` name.
