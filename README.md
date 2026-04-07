# ZeroAlloc.EventSourcing

A high-performance, zero-allocation event sourcing library for .NET with streaming capabilities and production-grade reliability features.

## Key Features

- **Zero-Allocation Design**: Optimized for performance-critical applications with minimal garbage collection
- **Event Sourcing**: Full event sourcing support with append-only event store
- **Stream Consumers**: Production-grade consumers for reliable event consumption
- **Projections**: Multiple projection types for denormalized views
- **Snapshots**: Optimize aggregate loading with configurable snapshot strategies
- **SQL Adapters**: SQL Server and PostgreSQL support with Testcontainers testing
- **Checkpoint Tracking**: Automatic position tracking with recovery capabilities
- **Comprehensive Testing**: Extensive test suite and integration testing patterns

## Quick Start

### Installation

```bash
dotnet add package ZeroAlloc.EventSourcing
```

### Basic Event Sourcing

```csharp
var adapter = new SqlEventStoreAdapter(connectionString);
var serializer = new JsonEventSerializer();
var typeRegistry = new EventTypeRegistry();

var eventStore = new EventStore(adapter, serializer, typeRegistry);

// Append events
var streamId = new StreamId("order-123");
await eventStore.AppendAsync(
    streamId,
    new[] { (object)new OrderPlacedEvent { Id = "123", Amount = 100m } },
    StreamPosition.Start
);

// Read events
var envelope = await eventStore.ReadAsync(streamId);
foreach (var evt in envelope.Events)
{
    Console.WriteLine($"Event: {evt.Event}");
}
```

## Packages

| Package | Description |
|---------|-------------|
| `ZeroAlloc.EventSourcing` | Core library with event store and serialization |
| `ZeroAlloc.EventSourcing.Aggregates` | Aggregate patterns and source generation |
| `ZeroAlloc.EventSourcing.InMemory` | In-memory event store for testing |
| `ZeroAlloc.EventSourcing.PostgreSql` | PostgreSQL adapter with native streams |
| `ZeroAlloc.EventSourcing.SqlServer` | SQL Server adapter with native streams |
| `ZeroAlloc.EventSourcing.Kafka` | Kafka stream consumer for external event sources |

All packages follow zero-allocation principles and are optimized for high-throughput scenarios.

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
await consumer.ConsumeAsync(async (envelope, ct) => {
    // Process event
    Console.WriteLine(envelope.Event);
});
```

See [Stream Consumers Documentation](docs/core-concepts/consumers.md) for complete guide.

## Kafka Integration

Consume events directly from Kafka topics with the same reliability features:

```csharp
var options = new KafkaConsumerOptions
{
    BootstrapServers = "localhost:9092",
    Topic = "my-events",
    GroupId = "my-service"
};

var consumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry);
await consumer.ConsumeAsync(async (envelope, ct) => {
    // Process event from Kafka
    await handler.ProcessAsync(envelope, ct);
});
```

See [Kafka Consumer Documentation](docs/core-concepts/kafka-consumer.md) for complete guide.

## Projections

Build denormalized views of your event data with multiple projection types:

```csharp
var projection = new Projection(eventStore, "order-summaries");
await projection.ProjectAsync(async (envelope, state, ct) => {
    if (envelope.Event is OrderPlacedEvent ope)
    {
        state["total"] = (decimal)(state["total"] ?? 0m) + ope.Amount;
    }
    return state;
});
```

## Snapshots

Optimize aggregate loading with snapshots:

```csharp
var options = new SnapshotOptions
{
    Strategy = SnapshotStrategy.EveryNEvents(100)
};

var snapshot = await eventStore.GetSnapshotAsync(streamId, options);
```

## Documentation

Complete documentation available at [/docs](docs/):

- [Getting Started](docs/getting-started/)
- [Core Concepts](docs/core-concepts/)
- [Usage Guides](docs/usage-guides/)
- [Testing Strategies](docs/testing/)
- [Performance & Benchmarks](docs/performance/)
- [Advanced Topics](docs/advanced/)

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
dotnet run --project benchmarks/ZeroAlloc.EventSourcing.Benchmarks -c Release
```

## License

See LICENSE file for details.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.
