# Core Concepts: Stream Consumers

Stream consumers are production-grade components for reliable, stateful event consumption with automatic position tracking, batch processing, retry logic, and configurable error handling strategies.

## Why Stream Consumers?

When you need to react to events in your system—updating projections, triggering workflows, syncing external systems—you need a consumer that:

- **Remembers where it stopped**: Automatically tracks consumed position so restarts don't reprocess or skip events
- **Handles failures gracefully**: Retry logic with exponential backoff, multiple error handling strategies
- **Performs well**: Batch processing for throughput, configurable concurrency
- **Respects your code**: Works with your existing event store, checkpoint persistence strategy, error handling preferences

Stream consumers bridge the gap between an immutable event log and stateful, external-facing business logic.

## Core Concepts

### Position Tracking

A **checkpoint** is a persistent record of "the last event position this consumer successfully processed."

```
Event Stream:                  Checkpoint Store:
Position 1: OrderPlaced  ——→  consumer-1: position 3
Position 2: Confirmed    ——→  consumer-2: position 1
Position 3: Shipped      ← ← ← (resume from here next time)
Position 4: Delivered
```

When your consumer restarts, it resumes from its checkpoint, not from the beginning. If it never ran before, it starts from position 1.

### Batch Processing

Events are consumed in configurable batches (1-10,000 events) for efficiency:

```csharp
// Process 100 events at a time for better throughput
var options = new StreamConsumerOptions { BatchSize = 100 };

// On first run:
// Batch 1: positions 1-100
// Batch 2: positions 101-200
// etc.
```

Batch size affects throughput vs. latency trade-off:
- **Small batches (1-10)**: Low latency, higher CPU overhead, slow throughput
- **Medium batches (50-500)**: Balanced, good for typical workloads
- **Large batches (1000+)**: High throughput, higher memory, slower reaction to new events

### Commit Strategies

**When** does the consumer save its checkpoint? Choose your trade-off:

#### AfterEvent (default)
```csharp
CommitStrategy = CommitStrategy.AfterEvent
```

Save checkpoint after **every event processed**. Slowest, safest:
- ✅ Exactly-once: Even system crash between events won't lose progress
- ❌ Write overhead: One database round-trip per event

#### AfterBatch
```csharp
CommitStrategy = CommitStrategy.AfterBatch
```

Save checkpoint after **each batch completes**. Balanced:
- ✅ Better throughput: One round-trip per batch
- ⚠️ At-least-once: System crash mid-batch re-processes a few events

#### Manual
```csharp
CommitStrategy = CommitStrategy.Manual
```

**You** control when to commit. Most flexible:
- ✅ Full control: Commit only when business logic succeeds
- ⚠️ Requires discipline: Must call `CommitAsync()` explicitly

### Error Handling Strategies

When an event handler throws, choose your response:

#### FailFast (default)
```csharp
ErrorStrategy = ErrorHandlingStrategy.FailFast
```

Stop immediately, throw to caller. Useful for finding bugs:
- ✅ No silent failures
- ❌ Entire consumer stops on any error

#### Skip
```csharp
ErrorStrategy = ErrorHandlingStrategy.Skip
```

Log error, move to next event. Keeps consumer running:
- ✅ Consumer doesn't stop
- ❌ Silently drops problematic events (data loss)

#### DeadLetter
```csharp
ErrorStrategy = ErrorHandlingStrategy.DeadLetter
```

Send failed event to a dead-letter queue for manual handling:
- ✅ Observability: You can investigate failures
- ⚠️ Requires: Separate dead-letter queue implementation

### Retry Logic

Transient failures (database timeout, network blip) are retried automatically:

```csharp
var options = new StreamConsumerOptions
{
    MaxRetries = 3,                                    // Retry up to 3 times
    RetryDelayMs = 100                                 // Wait 100ms first
};

// Retry backoff: 100ms → 200ms → 400ms (exponential)
```

**When does a retry help?**
- ✅ Transient errors (database briefly unavailable)
- ❌ Permanent errors (invalid event data) will keep failing

## IStreamConsumer Interface

```csharp
public interface IStreamConsumer
{
    /// <summary>
    /// Consume events from the stream, starting from checkpoint (or position 1 if new).
    /// This is a long-running operation that processes all available events then returns.
    /// </summary>
    ValueTask ConsumeAsync(
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);

    /// <summary>
    /// Get the current consumer checkpoint (last processed position).
    /// Returns null if consumer has never run.
    /// </summary>
    ValueTask<StreamPosition?> GetPositionAsync(CancellationToken ct = default);

    /// <summary>
    /// Reset consumer position for replay (e.g., restart from beginning).
    /// </summary>
    ValueTask ResetPositionAsync(StreamPosition position, CancellationToken ct = default);

    /// <summary>
    /// Manually commit current position to checkpoint store.
    /// Only used with CommitStrategy.Manual.
    /// </summary>
    ValueTask CommitAsync(CancellationToken ct = default);
}
```

## Basic Usage

### Setup

```csharp
// 1. Event store (existing)
var eventStore = new EventStore(
    new SqlEventStoreAdapter(connectionString),
    new JsonEventSerializer(),
    typeRegistry
);

// 2. Checkpoint store (where consumer tracks its position)
ICheckpointStore checkpointStore = new SqlCheckpointStore(connectionString);

// 3. Consumer options
var options = new StreamConsumerOptions
{
    BatchSize = 100,                                // Process 100 events at a time
    CommitStrategy = CommitStrategy.AfterBatch,    // Save checkpoint after each batch
    MaxRetries = 3,                                 // Retry transient failures
    ErrorStrategy = ErrorHandlingStrategy.FailFast // Stop on errors (fail loudly)
};

// 4. Create consumer
var consumer = new StreamConsumer(
    eventStore,
    checkpointStore,
    "my-projection",  // Consumer ID (used as checkpoint key)
    options
);
```

### Consuming Events

```csharp
await consumer.ConsumeAsync(async (envelope, ct) =>
{
    // envelope.Event is your domain event (OrderPlaced, OrderShipped, etc.)
    // envelope.Position is the stream position (1, 2, 3...)

    if (envelope.Event is OrderPlaced op)
    {
        await _projectionDb.InsertOrderAsync(op.OrderId, op.Total, ct);
    }
    else if (envelope.Event is OrderShipped os)
    {
        await _projectionDb.UpdateOrderStatusAsync(os.OrderId, "shipped", ct);
    }
}, cancellationToken);

// After ConsumeAsync returns, all available events have been processed
// The checkpoint has been saved (or batched, depending on commit strategy)
```

## Common Patterns

### Projections

Build a denormalized view by consuming events:

```csharp
var consumer = new StreamConsumer(eventStore, checkpointStore, "orders-projection");

await consumer.ConsumeAsync(async (envelope, ct) =>
{
    var order = await projectionDb.GetOrderAsync(order.OrderId) 
        ?? new OrderProjection();
    
    order = order.Apply(envelope.Event);
    
    await projectionDb.SaveOrderAsync(order, ct);
}, ct);
```

### External System Sync

Sync to external services (email, webhook, payment provider):

```csharp
var consumer = new StreamConsumer(
    eventStore, 
    checkpointStore, 
    "webhook-dispatcher",
    new StreamConsumerOptions 
    { 
        MaxRetries = 5,  // Retry transient network errors
        CommitStrategy = CommitStrategy.AfterBatch  // Don't commit until webhook succeeds
    }
);

await consumer.ConsumeAsync(async (envelope, ct) =>
{
    if (envelope.Event is OrderPlaced op)
    {
        await _webhookService.SendAsync(
            "order.placed",
            new { orderId = op.OrderId, total = op.Total },
            ct
        );
    }
}, ct);
```

### Manual Commit Control

For workflows requiring precise control:

```csharp
var options = new StreamConsumerOptions 
{ 
    CommitStrategy = CommitStrategy.Manual,
    BatchSize = 10
};
var consumer = new StreamConsumer(eventStore, checkpointStore, "workflow", options);

await consumer.ConsumeAsync(async (envelope, ct) =>
{
    // Process event
    var result = await _workflow.HandleAsync(envelope.Event, ct);
    
    if (!result.Success)
    {
        // Don't commit on failure—next run will retry this event
        throw new InvalidOperationException(result.Error);
    }
    
    // Manually commit when business logic succeeds
    await consumer.CommitAsync(ct);
}, ct);
```

### Replaying Events

Reset consumer to re-process events from a specific position:

```csharp
// Re-process all events (rebuild projection from scratch)
await consumer.ResetPositionAsync(StreamPosition.Start, ct);
await consumer.ConsumeAsync(handler, ct);

// Or reset to a specific point in time
var replayFrom = new StreamPosition(1000);  // Position 1000
await consumer.ResetPositionAsync(replayFrom, ct);
await consumer.ConsumeAsync(handler, ct);
```

## Best Practices

### 1. Choose Appropriate Batch Size

```csharp
// Throughput-focused (high-volume systems)
BatchSize = 500;

// Latency-focused (low-latency reactions)
BatchSize = 10;

// Balanced (typical)
BatchSize = 100;
```

### 2. Use Appropriate Error Strategy

```csharp
// Critical path (payment processing)
ErrorStrategy = ErrorHandlingStrategy.FailFast;

// Non-critical updates (cache warming, analytics)
ErrorStrategy = ErrorHandlingStrategy.Skip;  // with logging

// Mission-critical (money, compliance)
ErrorStrategy = ErrorHandlingStrategy.DeadLetter;  // with manual review
```

### 3. Match CommitStrategy to Failure Tolerance

```csharp
// Zero tolerance for duplication (financial transactions)
CommitStrategy = CommitStrategy.AfterEvent;

// Some duplication acceptable (cache updates)
CommitStrategy = CommitStrategy.AfterBatch;

// Need precise control (complex workflows)
CommitStrategy = CommitStrategy.Manual;
```

### 4. Log Checkpoint Progress

```csharp
var startPosition = await consumer.GetPositionAsync(ct);
Console.WriteLine($"Resuming from position {startPosition?.Value ?? 1}");

await consumer.ConsumeAsync(async (envelope, ct) =>
{
    // Your handler
    if (envelope.Position.Value % 100 == 0)
    {
        _logger.LogInformation("Processed position {Position}", envelope.Position.Value);
    }
}, ct);
```

### 5. Handle Cancellation Gracefully

```csharp
using var cts = new CancellationTokenSource();

// Shutdown signal (SIGTERM, shutdown event)
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

try
{
    await consumer.ConsumeAsync(handler, cts.Token);
}
catch (OperationCanceledException)
{
    _logger.LogInformation("Consumer shutdown gracefully");
}
```

## Checkpoint Stores

You can implement `ICheckpointStore` to persist positions wherever you want:

- **SQL**: `SqlCheckpointStore` (included) - uses atomic upsert
- **In-Memory**: `InMemoryCheckpointStore` - for testing
- **Redis**: Custom implementation for distributed systems
- **File System**: Custom implementation for serverless

See [Testing with Testcontainers](../testing/integration-testing.md) for SQL checkpoint store examples.

## Performance Characteristics

| Operation | Complexity | Notes |
|-----------|-----------|-------|
| `ConsumeAsync` | O(n) | n = events to process |
| `GetPositionAsync` | O(1) | Single checkpoint lookup |
| `ResetPositionAsync` | O(1) | Single write |
| `CommitAsync` | O(1) | Single write (manual strategy only) |

Batch size is the primary performance tuning lever. Larger batches = fewer database round-trips = higher throughput.

## Troubleshooting

### Consumer Stops Unexpectedly

**Cause**: `ErrorStrategy = ErrorHandlingStrategy.FailFast` and an event handler threw.

**Fix**: Either:
1. Fix the event handler to not throw
2. Change to `ErrorStrategy = ErrorHandlingStrategy.Skip` (with logging)
3. Use `ErrorStrategy = ErrorHandlingStrategy.DeadLetter` to queue problem events

### Events Being Reprocessed

**Cause**: `CommitStrategy = CommitStrategy.AfterBatch` and system crashed mid-batch.

**Fix**: If duplication is unacceptable, use `CommitStrategy = CommitStrategy.AfterEvent` or implement idempotent handlers.

### Slow Consumer

**Cause**: Batch size too small, database round-trips dominate.

**Fix**: Increase `BatchSize` from 1 to 100+ (balance with memory usage).

### Consumer Never Ran

**Cause**: `GetPositionAsync` returns null.

**Fix**: This is normal for new consumers. First run starts from position 1.

## Related Concepts

- [Event Store](event-store.md) - Where events come from
- [Events](events.md) - The data being consumed
- [Projections](projections.md) - Building read models with consumers
- [Testing Strategies](../testing/) - How to test consumers
