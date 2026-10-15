# ZeroAlloc.EventSourcing

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.EventSourcing.svg)](https://www.nuget.org/packages/ZeroAlloc.EventSourcing)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/actions/workflows/ci.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/LICENSE)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

A high-performance event sourcing library for .NET with streaming capabilities and production-grade reliability features. The append path allocates nothing of its own; see [Allocations](#allocations) for what each path costs.

## Key Features

- **Allocation-free appends**: `EventStore.AppendAsync` and `AggregateRepository.SaveAsync` allocate nothing of their own, asserted in tests
- **Event Sourcing**: Full event sourcing support with append-only event store
- **Stream Consumers**: Production-grade consumers for reliable event consumption
- **Projections**: Multiple projection types for denormalized views
- **Snapshots**: Optimize aggregate loading with configurable snapshot strategies
- **Storage Adapters**: SQL Server, PostgreSQL and SQLite, tested against real databases; SQL Server and PostgreSQL through Testcontainers
- **Checkpoint Tracking**: Automatic position tracking with recovery capabilities
- **Comprehensive Testing**: Extensive test suite and integration testing patterns

## Quick Start

### Installation

```bash
dotnet add package ZeroAlloc.EventSourcing
```

Every package targets `net8.0`, `net9.0` and `net10.0`.

### Basic Event Sourcing

```csharp
// The adapter is the storage: InMemoryEventStoreAdapter for tests; SqlServerEventStoreAdapter,
// PostgreSqlEventStoreAdapter or SqliteEventStoreAdapter from their packages in production.
var adapter = new InMemoryEventStoreAdapter();

// serializer is your IEventSerializer; AddEventSourcing() registers the AOT-safe
// ZeroAllocEventSerializer. OrderEventTypeRegistry maps event names to types: the source
// generator emits it for an Order aggregate.
var eventStore = new EventStore(adapter, serializer, new OrderEventTypeRegistry());

// Append events
var streamId = new StreamId("order-123");
var appended = await eventStore.AppendAsync(
    streamId,
    new object[] { new OrderPlaced("alice"), new ItemAdded(100m) },
    StreamPosition.Start);

// Read events
await foreach (var envelope in eventStore.ReadAsync(streamId))
{
    Console.WriteLine($"Event {envelope.Position.Value}: {envelope.Event}");
}
```

Most applications work with aggregates instead of raw events: see
[Your First Aggregate](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/getting-started/first-aggregate.md).

## Packages

| Package | Description |
|---------|-------------|
| `ZeroAlloc.EventSourcing` | Core library with event store and serialization |
| `ZeroAlloc.EventSourcing.Aggregates` | Aggregate base class and repository |
| `ZeroAlloc.EventSourcing.Generators` | Source generator that emits aggregate `ApplyEvent` dispatch and the event type registry |
| `ZeroAlloc.EventSourcing.InMemory` | In-memory event store for testing |
| `ZeroAlloc.EventSourcing.PostgreSql` | PostgreSQL adapter with native streams |
| `ZeroAlloc.EventSourcing.SqlServer` | SQL Server adapter with native streams |
| `ZeroAlloc.EventSourcing.Sql` | Shared SQL checkpoint, snapshot, projection and dead-letter stores for SQL Server and PostgreSQL |
| `ZeroAlloc.EventSourcing.Sqlite` | SQLite adapter for embedded and single-node use ([README](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/src/ZeroAlloc.EventSourcing.Sqlite/README.md)) |
| `ZeroAlloc.EventSourcing.Kafka` | Kafka stream consumer for external event sources |
| `ZeroAlloc.EventSourcing.Outbox` | At-least-once cross-aggregate dispatch through ZeroAlloc.Mediator ([README](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/src/ZeroAlloc.EventSourcing.Outbox/README.md)) |
| `ZeroAlloc.EventSourcing.Mediator` | Republishes live committed events as ZeroAlloc.Mediator notifications |
| `ZeroAlloc.EventSourcing.Telemetry` | BCL `ActivitySource` + `Meter` decorator around `IAggregateRepository<,>` — OpenTelemetry spans and metrics with no OTel SDK dependency |

## Performance

Correctness-matched overhead vs a hand-rolled SQLite event store (same connection, both transactional, both check stream version inside the transaction). .NET 8.0.26, i9-12900HK, BenchmarkDotNet v0.15.8.

| Operation | Hand-rolled | ZA.EventSourcing | Overhead |
|---|---:|---:|---:|
| Append 1 event (transactional, OCC check) | 80.7 µs / 3.80 KB | **106.3 µs / 4.79 KB** | +33% time, +26% alloc |
| Read 100-event stream (ordered) | 66.0 µs / 11.95 KB | **140.9 µs / 25.23 KB** | +114% time, +111% alloc |

The delta is the cost of the `IEventStore` + `IEventStoreAdapter` + `IEventSerializer` + `IEventTypeRegistry` layer — what you get for it is typed events, pluggable serialization, optimistic concurrency through `StreamPosition`, and composability with `Aggregate<T>`, projections, snapshots, upcasters, and dead-letter handling. At real-database latency the abstraction tax becomes negligible vs the SQL round-trip.

Full methodology: [docs/performance.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/performance.md).

### Allocations

What the library itself allocates, measured with allocation-free test doubles for the adapter, serializer and type registry:

| Call | Library allocation | Test |
|---|---:|---|
| `EventStore.AppendAsync`, 1 to 64 events | 0 B | `EventStoreAllocationTests` |
| `AggregateRepository.SaveAsync` | 0 B | `AggregateRepositoryAllocationTests` |
| `EventStore.ReadAsync`, one full read of 1 or 100 events | 304 B per call, none per event | `EventStoreReadAllocationTests` |

The 304 B is the compiler-generated async iterator behind `ReadAsync` ([#504](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/504)). Storage adapters and serializers add their own allocations on top, as the SQLite numbers above show.

## Stream Consumers

ZeroAlloc.EventSourcing includes production-grade stream consumers for reliable event consumption with automatic position tracking, retry logic, and configurable error handling.

### Key Features

- **Position Tracking**: Resume consumption from exact point after restart
- **Batch Processing**: Configurable batch sizes (1-10,000 events) for optimal throughput
- **Retry Logic**: Exponential backoff with configurable max retries
- **Error Handling**: FailFast, Skip, or DeadLetter strategies
- **Commit Strategies**: AfterEvent, AfterBatch, or Manual control
- **Production Ready**: SQL checkpoint store with atomic upsert, tested with Testcontainers

### Quick Start

```csharp
var consumer = new StreamConsumer(eventStore, checkpointStore, "my-consumer");
await consumer.ConsumeAsync((envelope, ct) =>
{
    // Process event
    Console.WriteLine(envelope.Event);
    return Task.CompletedTask;
});
```

See [Stream Consumers Documentation](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/core-concepts/consumers.md) for complete guide.

## Kafka Integration

Consume events directly from Kafka topics with the same reliability features:

```csharp
var options = new KafkaConsumerGroupOptions
{
    BootstrapServers = "localhost:9092",
    Topic = "my-events",
    GroupId = "my-service",
    ConsumerId = "my-service-1"
};

using var consumer = new KafkaConsumerGroupConsumer(options, checkpointStore, serializer, registry);
await consumer.ConsumeAsync(async (envelope, ct) =>
{
    // Process event from Kafka
    await handler.ProcessAsync(envelope, ct);
});
```

See [Kafka Consumer Documentation](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/core-concepts/kafka-consumer.md) for complete guide.

## Projections

Build denormalized views of your event data by deriving from `Projection<TReadModel>`:

```csharp
public sealed record OrderTotals(int Orders, decimal Revenue);

public sealed class OrderTotalsProjection : Projection<OrderTotals>
{
    public OrderTotalsProjection() => Current = new OrderTotals(0, 0m);

    protected override OrderTotals Apply(OrderTotals current, EventEnvelope @event) => @event.Event switch
    {
        OrderPlaced => current with { Orders = current.Orders + 1 },
        ItemAdded e => current with { Revenue = current.Revenue + e.Price },
        _ => current
    };
}

// Feed it every stream's events, in append order
var projection = new OrderTotalsProjection();
await foreach (var envelope in eventStore.ReadAsync(StreamId.Global))
{
    await projection.HandleAsync(envelope);
}
```

`FilteredProjection<TReadModel>`, `BatchedProjection<TReadModel>` and
`ReplayableProjection<TReadModel>` add filtering, batching and rebuilds; see the
[Projections Usage Guide](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/usage-guides/projections-usage.md).

## Snapshots

Optimize aggregate loading with snapshots:

```csharp
// Loads start from the latest snapshot and replay only the newer events; saves write a new
// snapshot every 100 events
var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: new AggregateRepository<Order, OrderId>(
        eventStore,
        () => new Order(),
        id => new StreamId($"order-{id.Value}")),
    snapshotStore: new InMemorySnapshotStore<OrderState>(),
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    restoreState: (order, state, position) => order.RestoreState(state, position),
    eventStore: eventStore,
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    aggregateFactory: () => new Order(),
    snapshotPolicy: SnapshotPolicy.EveryNEvents(100),
    extractState: order => order.State);

var loaded = await repository.LoadAsync(orderId);
```

The SQL packages provide snapshot stores for PostgreSQL and SQL Server; see the
[Snapshots Usage Guide](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/usage-guides/snapshots-usage.md).

## OpenTelemetry Instrumentation

`ZeroAlloc.EventSourcing.Telemetry` adds a hand-rolled decorator around `IAggregateRepository<TAggregate, TId>` that records Activity spans and metrics for every aggregate `LoadAsync` and `SaveAsync` — without taking a dependency on the OTel SDK.

```bash
dotnet add package ZeroAlloc.EventSourcing.Telemetry
```

```csharp
services
    .AddEventSourcing()
    .UseInMemoryEventStore()
    .UseAggregateRepository<Order, OrderId>(() => new Order(), id => new StreamId($"order-{id.Value}"))
    .WithTelemetry();   // call after the aggregate repository registrations, before BuildServiceProvider()
```

The decorator emits, under the `ZeroAlloc.EventSourcing` activity-source/meter name:

- **Spans** — `aggregate.load` and `aggregate.save`, tagged with `aggregate.type` (= `typeof(TAggregate).Name`); status set to `Error` on exception
- **Counters** — `aggregate.loads_total` and `aggregate.saves_total`, incremented only when `Result.IsSuccess`
- **Histograms** — `aggregate.load_duration_ms` and `aggregate.save_duration_ms`, recorded for both success and failure paths

Any OpenTelemetry SDK wired to the process picks up all three instruments automatically.

Spans and metrics sit at the aggregate-repository layer: every published version of the package decorates `IAggregateRepository<,>`, not `IEventStore`. `UseEventSourcingTelemetry()` is an `[Obsolete]` alias that delegates to `WithTelemetry()`. See [docs/telemetry.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/telemetry.md) for the full guide.

## Documentation

Complete documentation available at [/docs](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/docs/):

- [Getting Started](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/docs/getting-started/)
- [Core Concepts](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/docs/core-concepts/)
- [Usage Guides](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/docs/usage-guides/)
- [Testing Strategies](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/docs/testing/)
- [Performance & Benchmarks](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/docs/performance/)
- [Advanced Topics](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/docs/advanced/)

## Development

### Build

```bash
dotnet build ZeroAlloc.EventSourcing.slnx --configuration Release
```

### Tests

```bash
dotnet test ZeroAlloc.EventSourcing.slnx --configuration Release
```

### Benchmarks

```bash
dotnet run --project src/ZeroAlloc.EventSourcing.Benchmarks -c Release
```

## License

MIT — see [LICENSE](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/LICENSE).

## Contributing

See [CONTRIBUTING.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/CONTRIBUTING.md) for guidelines.
