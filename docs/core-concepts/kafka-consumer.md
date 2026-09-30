# Kafka Consumer

The `ZeroAlloc.EventSourcing.Kafka` package consumes events from Kafka topics with the same
handler, retry, and error handling infrastructure as the built-in `StreamConsumer`. This allows you
to integrate externally-produced events (from other services, legacy systems, etc.) into your
event sourcing pipeline.

## Overview

While `StreamConsumer` reads events from an internal event log (`IEventStore`), the Kafka consumers
source events directly from a Kafka topic. They implement the same `IStreamConsumer` interface, so
you can switch between them without changing your handler code.

The package has two consumers, both deriving from `KafkaConsumerBase`:

- **`KafkaConsumerGroupConsumer`** joins a Kafka consumer group. Kafka assigns the topic's partitions
  to the running instances and rebalances them when instances come and go.
- **`KafkaManualPartitionConsumer`** reads the partitions you list, with no group rebalancing.

**Key differences from `StreamConsumer`:**
- Event source: Kafka topic vs. internal event store
- Positioning: Kafka offset per partition vs. event stream position
- Checkpoint tracking: Pluggable via `ICheckpointStore`, one checkpoint per partition

## Quick Start

### 1. Configure the Consumer

```csharp
var options = new KafkaConsumerGroupOptions
{
    BootstrapServers = "localhost:9092",
    Topic = "my-events",
    GroupId = "my-service",
    ConsumerId = "my-service-consumer",
    ConsumerOptions = new StreamConsumerOptions
    {
        BatchSize = 100,
        MaxRetries = 3,
        RetryPolicy = new ExponentialBackoffRetryPolicy(),
        ErrorStrategy = ErrorHandlingStrategy.FailFast,
        CommitStrategy = CommitStrategy.AfterBatch
    }
};
```

`BootstrapServers`, `Topic`, `ConsumerId` and, for a consumer group, `GroupId` are required.

### 2. Create the Consumer

```csharp
var checkpointStore = new InMemoryCheckpointStore();

using var consumer = new KafkaConsumerGroupConsumer(options, checkpointStore, serializer, registry);
```

`serializer` and `registry` are the `IEventSerializer` and `IEventTypeRegistry` your event store uses.

### 3. Consume Events

```csharp
// Processes the messages that are available and returns once a poll finds no new ones
await consumer.ConsumeAsync(async (envelope, ct) =>
{
    await handler.ProcessAsync(envelope, ct);
}, cancellationToken);
```

`ConsumeAsync` is a catch-up run: it processes the messages that are available and returns when a
poll finds none within `PollTimeout`. The consumer closes its Kafka connection when the run ends,
so start the next run with a new consumer instance; the checkpoint store carries the position over.

### Manual Partition Assignment

Use `KafkaManualPartitionConsumer` to read fixed partitions without a consumer group:

```csharp
var options = new KafkaManualPartitionOptions
{
    BootstrapServers = "localhost:9092",
    Topic = "my-events",
    ConsumerId = "billing-partitions-0-1",
    Partitions = [0, 1]
};

var consumer = new KafkaManualPartitionConsumer(options, checkpointStore, serializer, registry);
```

### Dependency Injection

```csharp
services
    .AddEventSourcing()
    .UseInMemoryCheckpointStore()   // or UsePostgreSqlCheckpointStore(cs) / UseSqlServerCheckpointStore(cs)
    .UseKafkaConsumerGroup(options);

// Resolve KafkaConsumerGroupConsumer from the container; it needs your IEventSerializer and
// IEventTypeRegistry registered too
```

`UseKafkaManualPartitions(options)` registers a `KafkaManualPartitionConsumer` the same way.

## Kafka Header Contract

Kafka messages must include metadata headers for proper event deserialization:

| Header | Type | Required | Purpose |
|--------|------|----------|---------|
| `event-type` | string | **Yes** | Event type name (e.g., `"OrderCreated"`) for registry lookup |
| `event-id` | UUID string | No | Event identifier; generated if absent |
| `occurred-at` | ISO-8601 | No | Event timestamp; falls back to `UtcNow` |
| `correlation-id` | UUID string | No | Tracing correlation ID |
| `causation-id` | UUID string | No | Parent event ID for causality tracking |

The message key becomes the envelope's `StreamId`; a message without a key gets the topic name.

**Producer Example (C#):**

```csharp
var headers = new Headers();
headers.Add("event-type", Encoding.UTF8.GetBytes("OrderCreated"));
headers.Add("event-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
headers.Add("occurred-at", Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
headers.Add("correlation-id", Encoding.UTF8.GetBytes(correlationId.ToString()));

var message = new Message<string, byte[]>
{
    Key = orderId,   // becomes the envelope's StreamId; the topic name is used when it is empty
    Value = payload, // serialized with the same format as your IEventSerializer reads
    Headers = headers
};

await producer.ProduceAsync("my-events", message);
```

## Checkpoint Stores

Choose a checkpoint store to persist consumption progress, the offset last processed in each
partition. The consumer keeps one checkpoint per partition, under the key
`{ConsumerId}:p{partition}`.

```csharp
// In memory: lost on restart
var inMemory = new InMemoryCheckpointStore();

// PostgreSQL and SQL Server, from the ZeroAlloc.EventSourcing.Sql package
var postgreSql = new PostgreSqlCheckpointStore(NpgsqlDataSource.Create(postgreSqlConnectionString));
var sqlServer = new SqlServerCheckpointStore(sqlServerConnectionString);
```

The in-memory store is useful for tests and non-critical scenarios. The SQL stores are persistent
and queryable; with dependency injection, register them with `UsePostgreSqlCheckpointStore(cs)` or
`UseSqlServerCheckpointStore(cs)`.

## Commit Strategies

Control when consumption progress is persisted through `options.ConsumerOptions`:

### `CommitStrategy.AfterEvent`
Commit after processing each message (safest, slowest).

```csharp
options.ConsumerOptions.CommitStrategy = CommitStrategy.AfterEvent;  // checkpoint every message
```

### `CommitStrategy.AfterBatch`
Commit after processing a batch of messages (balanced). This is the default.

```csharp
options.ConsumerOptions.CommitStrategy = CommitStrategy.AfterBatch;  // checkpoint after each batch
options.ConsumerOptions.BatchSize = 100;
```

### `CommitStrategy.Manual`
Commit on-demand via `CommitAsync()` (explicit control).

```csharp
options.ConsumerOptions.CommitStrategy = CommitStrategy.Manual;
```

Later:

```csharp
// Process messages
await consumer.ConsumeAsync(handler);

// Explicitly commit when ready
await consumer.CommitAsync();
```

## Error Handling Strategies

### `ErrorHandlingStrategy.FailFast`
Throw on failure after retries (halts consumption). This is the default.

```csharp
options.ConsumerOptions.MaxRetries = 3;
options.ConsumerOptions.ErrorStrategy = ErrorHandlingStrategy.FailFast;
```

On permanent failure, the exception propagates and consumption stops. Useful for detecting data
quality issues early.

### `ErrorHandlingStrategy.Skip`
Skip problematic messages and continue.

```csharp
options.ConsumerOptions.MaxRetries = 3;
options.ConsumerOptions.ErrorStrategy = ErrorHandlingStrategy.Skip;
```

Skipped messages are still committed, so you don't retry them. Useful for handling malformed
messages in high-volume streams.

### `ErrorHandlingStrategy.DeadLetter`
Write failed messages to an `IDeadLetterStore` and continue.

```csharp
options.ConsumerOptions.ErrorStrategy = ErrorHandlingStrategy.DeadLetter;

// The consumer writes a message that still fails after the retries to this store
var consumer = new KafkaConsumerGroupConsumer(options, checkpointStore, serializer, registry, deadLetterStore);
```

Without a dead-letter store, this strategy throws `InvalidOperationException` when a message fails.

### Shutdown is not a failure

A handler that fails after the consumer's token was cancelled is not treated as a failing message,
whatever it throws: the consumer stops with an `OperationCanceledException`, no retry or error
strategy runs, and neither the offset nor the checkpoint moves past that message, so it is
consumed again on the next start.
Cancellation during a retry backoff stops the consumer the same way.

## Retry Policy

Configure exponential backoff for transient failures:

```csharp
options.ConsumerOptions.MaxRetries = 3;
options.ConsumerOptions.RetryPolicy = new ExponentialBackoffRetryPolicy(initialDelayMs: 100, maxDelayMs: 30_000);
```

Retries are paused between attempts. The policy implements `IRetryPolicy` and can be customized.

## Position Management

### Get Current Position

```csharp
var position = await consumer.GetPositionAsync();
Console.WriteLine($"Lowest checkpointed offset across the assigned partitions: {position?.Value}");
```

### Reset to Start

```csharp
await consumer.ResetPositionAsync(StreamPosition.Start);
// Deletes the checkpoints: the next run starts from the beginning of each partition
```

### Reset to Specific Position

```csharp
await consumer.ResetPositionAsync(new StreamPosition(100));
// The next run starts at offset 100 of each assigned partition
```

A checkpoint holds the offset of the last message processed, and a run seeks to that offset. So after
a restart the last processed message of each partition is delivered once more: make handlers
idempotent.

## Scaling: Multi-Partition Consumption

Both consumers read several partitions:

1. **Consumer group:** run several instances with the same `GroupId`, each with its own
   `ConsumerId`. Kafka assigns every partition to one of them and rebalances when an instance
   starts or stops. The consumer seeks each newly assigned partition to its checkpoint.
2. **Manual partitions:** give each instance a fixed set with `KafkaManualPartitionOptions.Partitions`,
   for example one instance for `[0, 1]` and another for `[2, 3]`.

## Integration Testing with Testcontainers

Use `Testcontainers.Kafka` to test against a real broker. The repository's
`tests/ZeroAlloc.EventSourcing.Kafka.Tests/KafkaConsumerIntegrationTests.cs` has complete examples:
it starts a container, creates a topic, produces messages with the header contract above, and
consumes them with both consumers.

```csharp
// Sketch: the container gives you the bootstrap address for the options
var kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.0").Build();
await kafka.StartAsync();

var options = new KafkaManualPartitionOptions
{
    BootstrapServers = kafka.GetBootstrapAddress(),
    Topic = "test",
    ConsumerId = "test-consumer",
    Partitions = [0]
};
```

## Performance Tuning

- **BatchSize**: Larger batches = fewer commits, higher memory usage. Default 100, range 1-10,000.
- **PollTimeout**: How long each poll waits for a message. Default 1 second. A run ends when a poll
  times out.
- **MaxRetries**: Higher = more resilience, slower on failures. Default 3.
- **CommitStrategy**: `AfterBatch` is faster than `AfterEvent`, but a crash mid-batch redelivers the
  whole batch.

### Benchmarks

`KafkaStreamConsumerBenchmarks` measures the consumer's own overhead against an in-process fake
Kafka consumer, so no broker is involved: `ConsumeAllMessages_DefaultBatchSize`,
`ConsumeAllMessages_SmallBatchSize`, `ConsumeAllMessages_AfterEventCommit` and
`MapKafkaMessage_ToEnvelope`, for 100, 1,000 and 10,000 messages.

Run them locally:

```bash
dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/ -- --filter "*KafkaStreamConsumer*"
```

## Troubleshooting

### Consumer Doesn't Start

- Check `BootstrapServers` points to running Kafka cluster
- Verify `Topic` exists and, for manual assignment, that the `Partitions` exist
- Ensure `ConsumerId` is set, and `GroupId` for a consumer group

### Messages Not Being Read

- Check Kafka has messages: `kafka-console-consumer --bootstrap-server localhost:9092 --topic my-events --from-beginning`
- Verify headers include `event-type`
- Check `IEventTypeRegistry` recognizes the event type

### Offset Resets

- If the checkpoint store has no checkpoint for a partition, consumption starts at its beginning
- Verify checkpoint store is persisted (not in-memory for production)
- Check consumer group offsets: `kafka-consumer-groups --bootstrap-server localhost:9092 --group my-group --describe`

## See Also

- `IStreamConsumer` — Shared consumer interface
- `StreamConsumerOptions` — Retry and error handling config
- `IEventTypeRegistry` — Type resolution contract
- `ICheckpointStore` — Position persistence interface
