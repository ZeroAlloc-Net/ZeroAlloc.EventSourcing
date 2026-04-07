# Kafka Consumer

The `KafkaStreamConsumer` class enables consuming events from Kafka topics using the same handler, retry, and error handling infrastructure as the built-in `StreamConsumer`. This allows you to integrate externally-produced events (from other services, legacy systems, etc.) into your event sourcing pipeline.

## Overview

While `StreamConsumer` reads events from an internal event log (`IEventStore`), `KafkaStreamConsumer` sources events directly from a Kafka topic. Both implement the same `IStreamConsumer` interface, so you can switch between them without changing your handler code.

**Key differences:**
- Event source: Kafka topic vs. internal event store
- Positioning: Kafka offset vs. event stream index
- Checkpoint tracking: Pluggable via `ICheckpointStore`

## Quick Start

### 1. Configure the Consumer

```csharp
var options = new KafkaConsumerOptions
{
    BootstrapServers = "localhost:9092",
    Topic = "my-events",
    GroupId = "my-service-consumer",
    Partition = 0,  // Phase 6: single partition per instance
    ConsumerOptions = new()
    {
        BatchSize = 100,
        MaxRetries = 3,
        RetryPolicy = new ExponentialBackoffRetryPolicy(),
        ErrorStrategy = ErrorHandlingStrategy.FailFast,
        CommitStrategy = CommitStrategy.AfterBatch
    }
};
```

### 2. Create the Consumer

```csharp
var checkpointStore = new InMemoryCheckpointStore();
var serializer = new YourEventSerializer();
var registry = new YourEventTypeRegistry();

var consumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry);
```

### 3. Consume Events

```csharp
await consumer.ConsumeAsync(async (envelope, ct) =>
{
    // Handle the event
    await YourHandler.ProcessAsync(envelope, ct);
});
```

## Kafka Header Contract

Kafka messages must include metadata headers for proper event deserialization:

| Header | Type | Required | Purpose |
|--------|------|----------|---------|
| `event-type` | string | **Yes** | Event type name (e.g., `"OrderCreated"`) for registry lookup |
| `event-id` | UUID string | No | Event identifier; generated if absent |
| `occurred-at` | ISO-8601 | No | Event timestamp; falls back to `UtcNow` |
| `correlation-id` | UUID string | No | Tracing correlation ID |
| `causation-id` | UUID string | No | Parent event ID for causality tracking |

**Producer Example (C#):**

```csharp
var headers = new Headers();
headers.Add("event-type", Encoding.UTF8.GetBytes("OrderCreated"));
headers.Add("event-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
headers.Add("correlation-id", Encoding.UTF8.GetBytes(correlationId.ToString()));

var message = new Message<string, byte[]>
{
    Key = orderId.ToString(),  // Maps to StreamId
    Value = SerializeEvent(evt),
    Headers = headers
};

await producer.ProduceAsync("my-events", message);
```

## Checkpoint Stores

Choose a checkpoint store to persist consumption progress (which offset was last processed):

### InMemoryCheckpointStore
- In-memory only, lost on restart
- Useful for testing or non-critical scenarios

```csharp
var checkpointStore = new InMemoryCheckpointStore();
```

### KafkaCheckpointStore
- Uses Kafka broker offset tracking (consumer group coordination)
- Automatic rebalancing support
- Requires no external database

```csharp
var kafkaConsumer = new ConsumerBuilder<string, byte[]>(config).Build();
var checkpointStore = new KafkaCheckpointStore(kafkaConsumer, "my-events", 0);
```

### SQL Checkpoint Stores
- PostgreSql: `PostgreSqlCheckpointStore`
- SQL Server: `SqlServerCheckpointStore`
- Persistent, queryable, external state management

```csharp
var checkpointStore = new PostgreSqlCheckpointStore(connectionString);
```

## Commit Strategies

Control when consumption progress is persisted:

### `CommitStrategy.AfterEvent`
Commit after processing each message (safest, slowest).

```csharp
ConsumerOptions = new()
{
    CommitStrategy = CommitStrategy.AfterEvent  // Commit offset for every message
}
```

### `CommitStrategy.AfterBatch`
Commit after processing a batch of messages (balanced).

```csharp
ConsumerOptions = new()
{
    CommitStrategy = CommitStrategy.AfterBatch,  // Commit after BatchSize messages
    BatchSize = 100
}
```

### `CommitStrategy.Manual`
Commit on-demand via `CommitAsync()` (explicit control).

```csharp
ConsumerOptions = new()
{
    CommitStrategy = CommitStrategy.Manual
}
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
Throw on failure after retries (halts consumption).

```csharp
ConsumerOptions = new()
{
    MaxRetries = 3,
    ErrorStrategy = ErrorHandlingStrategy.FailFast
}
```

On permanent failure, the exception propagates and consumption stops. Useful for detecting data quality issues early.

### `ErrorHandlingStrategy.Skip`
Skip problematic messages and continue.

```csharp
ConsumerOptions = new()
{
    MaxRetries = 3,
    ErrorStrategy = ErrorHandlingStrategy.Skip
}
```

Skipped messages are still committed, so you don't retry them. Useful for handling malformed messages in high-volume streams.

### `ErrorHandlingStrategy.DeadLetter`
Send failed messages to a dead-letter queue (not yet implemented in Phase 6).

## Retry Policy

Configure exponential backoff for transient failures:

```csharp
ConsumerOptions = new()
{
    MaxRetries = 3,
    RetryPolicy = new ExponentialBackoffRetryPolicy()
}
```

Retries are paused between attempts. The policy implements `IRetryPolicy` and can be customized.

## Position Management

### Get Current Position

```csharp
var position = await consumer.GetPositionAsync();
Console.WriteLine($"Last processed offset: {position?.Value}");
```

### Reset to Start

```csharp
await consumer.ResetPositionAsync(StreamPosition.Start);
// Next call to ConsumeAsync will start from offset 0
```

### Reset to Specific Position

```csharp
await consumer.ResetPositionAsync(new StreamPosition(100));
// Next call to ConsumeAsync will resume from offset 100
```

## Scaling: Multi-Partition Consumption

**Phase 6 limitation:** A single `KafkaStreamConsumer` instance handles one partition only.

To consume multiple partitions in parallel:

1. **Manual partitioning:** Run one consumer instance per partition:
   ```csharp
   var tasks = new Task[partitionCount];
   for (int p = 0; p < partitionCount; p++)
   {
       var options = new KafkaConsumerOptions { Partition = p, GroupId = "shared-group" };
       tasks[p] = ConsumePartitionAsync(options);
   }
   await Task.WhenAll(tasks);
   ```

2. **Separate services:** Deploy multiple instances, each consuming a different partition with the same `GroupId`. Kafka coordinates offset tracking automatically.

3. **Future enhancement:** Phase 7 or later may add multi-partition support via consumer groups.

## Integration Testing with Testcontainers

Use `Testcontainers.Kafka` to test locally:

```csharp
public class KafkaTests : IAsyncLifetime
{
    private KafkaContainer _kafka = null!;

    public async Task InitializeAsync()
    {
        _kafka = new KafkaBuilder().Build();
        await _kafka.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _kafka.StopAsync();
    }

    [Fact]
    public async Task ConsumerReadsMessages()
    {
        var bootstrapServers = _kafka.GetBootstrapAddress();
        
        // Produce test messages
        var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = bootstrapServers }
        ).Build();
        
        // Consume and verify
        var options = new KafkaConsumerOptions
        {
            BootstrapServers = bootstrapServers,
            Topic = "test",
            GroupId = "test-group"
        };
        
        var consumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry);
        // ... assertions
    }
}
```

## Performance Tuning

- **BatchSize**: Larger batches = fewer commits, higher memory usage. Default 100.
- **PollTimeout**: How long `Consume()` waits for messages. Default 1 second.
- **MaxRetries**: Higher = more resilience, slower on failures. Default 3.
- **CommitStrategy**: `AfterBatch` is faster than `AfterEvent` but risks data loss on crash.

### Benchmark Example

Typical throughput with 100-message batches on a local broker:

```
ConsumeAllMessages_DefaultBatchSize: ~50,000 messages/sec
ConsumeAllMessages_SmallBatch (10): ~20,000 messages/sec
ConsumeAllMessages_AfterEventCommit: ~10,000 messages/sec
```

Run benchmarks locally:

```bash
dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks/
```

## Troubleshooting

### Consumer Doesn't Start

- Check `BootstrapServers` points to running Kafka cluster
- Verify `Topic` exists and `Partition` is valid
- Ensure `GroupId` is set

### Messages Not Being Read

- Check Kafka has messages: `kafka-console-consumer --bootstrap-server localhost:9092 --topic my-events --from-beginning`
- Verify headers include `event-type`
- Check `IEventTypeRegistry` recognizes the event type

### Offset Resets

- If checkpoint store returns `null`, consumption starts at the beginning
- Verify checkpoint store is persisted (not in-memory for production)
- Check consumer group offsets: `kafka-consumer-groups --bootstrap-server localhost:9092 --group my-group --describe`

## See Also

- `IStreamConsumer` — Shared consumer interface
- `StreamConsumerOptions` — Retry and error handling config
- `IEventTypeRegistry` — Type resolution contract
- `ICheckpointStore` — Position persistence interface
