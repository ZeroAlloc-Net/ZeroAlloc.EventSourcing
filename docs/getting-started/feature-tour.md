# Feature Tour

A short tour of the main features, one example each. Every snippet on this page is compiled and
run by `FeatureTourSnippetTests` in `tests/ZeroAlloc.EventSourcing.Aggregates.ConsumerTests`, except the
Kafka one, which needs a broker and is only compiled.

Every package targets `net8.0`, `net9.0` and `net10.0`.

## The event store without aggregates

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
[Your First Aggregate](first-aggregate.md).

## Stream Consumers

ZeroAlloc.EventSourcing includes production-grade stream consumers for reliable event consumption with automatic position tracking, retry logic, and configurable error handling.

### Features

- **Position Tracking**: Resume consumption from exact point after restart
- **Batch Processing**: Configurable batch sizes (1-10,000 events) for optimal throughput
- **Retry Logic**: Exponential backoff with configurable max retries
- **Error Handling**: FailFast, Skip, or DeadLetter strategies
- **Commit Strategies**: AfterEvent, AfterBatch, or Manual control
- **Production Ready**: SQL checkpoint store with atomic upsert, tested with Testcontainers

### Example

```csharp
var consumer = new StreamConsumer(eventStore, checkpointStore, "my-consumer");
await consumer.ConsumeAsync((envelope, ct) =>
{
    // Process event
    Console.WriteLine(envelope.Event);
    return Task.CompletedTask;
});
```

See [Stream Consumers Documentation](../core-concepts/consumers.md) for complete guide.

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

See [Kafka Consumer Documentation](../core-concepts/kafka-consumer.md) for complete guide.

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
[Projections Usage Guide](../usage-guides/projections-usage.md).

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
[Snapshots Usage Guide](../usage-guides/snapshots-usage.md).

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

Spans and metrics sit at the aggregate-repository layer: every published version of the package decorates `IAggregateRepository<,>`, not `IEventStore`. `UseEventSourcingTelemetry()` is an `[Obsolete]` alias that delegates to `WithTelemetry()`. See [Telemetry](../telemetry.md) for the full guide.
