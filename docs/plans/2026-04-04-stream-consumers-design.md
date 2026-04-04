# Stream Consumers Implementation Design

**Date:** 2026-04-04  
**Status:** APPROVED  
**Scope:** Stateful stream consumer infrastructure for ZeroAlloc.EventSourcing

---

## Overview

ZeroAlloc.EventSourcing will implement a stateful consumer pattern that allows applications to reliably consume events from streams with automatic position tracking, retry logic, and error handling. This foundation enables downstream integration with message brokers (Kafka, RabbitMQ, Azure Service Bus) without reimplementing consumer semantics.

---

## Architecture

### Core Concepts

**Consumer Position (Offset)**
- Tracks where a consumer last successfully processed events
- Persisted to CheckpointStore after successful processing
- Allows resuming from exact point on restart
- One position per consumer per stream

**Event Batch**
- Multiple events consumed in a single read from event store
- Configurable size (1 to N events)
- All events in batch share same position upon completion

**Consumer Handler**
- User-provided function that processes events
- Called for each event or batch depending on configuration
- Must be idempotent (safe to re-execute on retry)

### Component Architecture

```
┌─────────────────────────────────────────────────────────┐
│ IStreamConsumer<TEvent>                                 │
│ - ConsumeAsync(handler, cancellation)                   │
│ - GetPositionAsync()                                    │
│ - ResetPositionAsync(position)                          │
└─────────────────────────────────────────────────────────┘
                           ↓
        ┌──────────────────┴──────────────────┐
        ↓                                      ↓
┌──────────────────────┐            ┌──────────────────────┐
│ ICheckpointStore     │            │ StreamConsumerOptions│
│ - ReadAsync(id)      │            │ - BatchSize          │
│ - WriteAsync(id, pos)│            │ - RetryPolicy        │
│ - DeleteAsync(id)    │            │ - ErrorHandling      │
└──────────────────────┘            │ - CommitStrategy     │
        ↑                            └──────────────────────┘
        │
   ┌────┴─────────────────────────┐
   ↓                              ↓
InMemoryCheckpointStore    SqlCheckpointStore
(testing)                   (production)
```

### Processing Flow

```
1. Read consumer position from CheckpointStore
2. Read batch of events from IEventStore starting at position
3. For each event in batch:
   a. Call handler with event
   b. On success: continue
   c. On error: apply retry policy
4. On batch completion (all events processed):
   a. Write new position to CheckpointStore
   b. Acknowledge consumption
5. On checkpoint error:
   a. Retry checkpoint write (events may be reprocessed)
   b. Preserve at-least-once semantics
```

---

## API Design

### IStreamConsumer Interface

```csharp
public interface IStreamConsumer<TEvent>
{
    /// <summary>
    /// Consume events from stream starting at last known position.
    /// </summary>
    Task ConsumeAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get current consumer position (last successfully processed event).
    /// </summary>
    Task<StreamPosition?> GetPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reset consumer to specific position (e.g., for replay).
    /// </summary>
    Task ResetPositionAsync(StreamPosition position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get consumer identifier (used for checkpoint storage).
    /// </summary>
    string ConsumerId { get; }
}
```

### ICheckpointStore Interface

```csharp
public interface ICheckpointStore
{
    /// <summary>
    /// Read last known consumer position.
    /// Returns null if consumer has never processed events.
    /// </summary>
    Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write consumer position after successful processing.
    /// </summary>
    Task WriteAsync(string consumerId, StreamPosition position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reset consumer position (for manual replay).
    /// </summary>
    Task DeleteAsync(string consumerId, CancellationToken cancellationToken = default);
}
```

### StreamConsumerOptions

```csharp
public class StreamConsumerOptions
{
    /// <summary>
    /// Number of events to fetch and process in each batch.
    /// Default: 100. Range: 1-10000.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// Default: 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Delay strategy for retries (exponential backoff, fixed, etc).
    /// Default: ExponentialBackoff(initial: 100ms, max: 30s).
    /// </summary>
    public IRetryPolicy RetryPolicy { get; set; } = new ExponentialBackoffRetryPolicy();

    /// <summary>
    /// How to handle processing errors after retries exhausted.
    /// Default: FailFast (stops consumer and throws).
    /// </summary>
    public ErrorHandlingStrategy ErrorStrategy { get; set; } = ErrorHandlingStrategy.FailFast;

    /// <summary>
    /// When to commit consumer position to checkpoint store.
    /// Default: AfterBatch (commit after all events in batch processed).
    /// </summary>
    public CommitStrategy CommitStrategy { get; set; } = CommitStrategy.AfterBatch;
}

public enum ErrorHandlingStrategy
{
    /// <summary>Stop consumer immediately on error (fail-fast)</summary>
    FailFast,
    
    /// <summary>Skip failing event, continue with next</summary>
    Skip,
    
    /// <summary>Route to dead-letter store for later analysis</summary>
    DeadLetter,
}

public enum CommitStrategy
{
    /// <summary>Commit position after each event processed</summary>
    AfterEvent,
    
    /// <summary>Commit position after entire batch processed</summary>
    AfterBatch,
    
    /// <summary>Manual commits (application calls CommitAsync explicitly)</summary>
    Manual,
}
```

---

## Built-in Implementations

### InMemoryCheckpointStore
- Stores positions in-memory using `Dictionary<string, StreamPosition>`
- **Use case:** Testing, development, single-process applications
- **Trade-off:** Positions lost on restart, not suitable for production

### SqlCheckpointStore
- Persists positions to SQL database (PostgreSQL, SQL Server)
- **Use case:** Production systems requiring durable position tracking
- **Schema:** Simple table with ConsumerId (PK), Position, UpdatedAt
- **Consistency:** Atomic writes, can handle concurrent consumer instances

---

## Error Handling & Guarantees

### Processing Guarantees

**At-Least-Once Semantics**
- Events will be processed at least once
- Handler may be called multiple times for same event (on checkpoint failures)
- Application must ensure idempotent handlers

**No Event Loss**
- Position is only advanced after successful processing
- If consumer crashes mid-batch, will reprocess entire batch on restart

**Duplicate Prevention**
- Depends on handler idempotency
- Framework provides position tracking; deduplication is application responsibility

### Failure Scenarios

| Scenario | Behavior |
|----------|----------|
| Handler throws (transient) | Retry with backoff up to MaxRetries |
| Handler throws (permanent) | Apply ErrorStrategy (FailFast/Skip/DeadLetter) |
| Checkpoint write fails | Retry checkpoint write; events may be reprocessed |
| Event store unavailable | Retry with backoff; consumer pauses |
| Consumer process crashes | Resume from last checkpoint on restart |

---

## Integration Points

### For Message Broker Adapters (Phase 2+)

**Kafka Integration**
- Consumer group = ConsumerId
- Topic partition offset = StreamPosition
- Consumer lag tracking via position difference

**RabbitMQ Integration**
- Acknowledgment model = commit strategy
- Nack with requeue = retry policy
- Dead-letter exchange = error strategy

**Azure Service Bus/EventHub Integration**
- Consumer group = ConsumerId
- Checkpoint = position in stream
- Lease/lock mechanism = concurrent consumer safety

---

## Success Criteria

✓ Consumer can reliably process events with automatic position tracking  
✓ Handles restarts without data loss  
✓ Configurable retry and error handling policies  
✓ Both in-memory (testing) and SQL (production) checkpoint stores  
✓ Foundation ready for broker adapters without major refactoring  
✓ Exactly-once handler execution semantics (with idempotent handler requirement)  
✓ Comprehensive documentation and examples  

---

## Related Work

**Phase 1 (this repo):**
- Stream consumer infrastructure
- Checkpoint stores (in-memory, SQL)
- Retry and error handling

**Phase 1 (separate repo):**
- ZeroAlloc.Outbox — generic outbox pattern

**Phase 2+ (backlog in this repo):**
- Kafka adapter
- RabbitMQ adapter
- Azure Service Bus/EventHub adapter

---

## Notes

- **Performance:** Batch processing reduces I/O; configurable based on latency needs
- **Scalability:** Multiple consumer instances can run concurrently (each with unique ConsumerId)
- **Monitoring:** Position tracking enables consumer lag metrics
- **Testing:** InMemoryCheckpointStore makes unit testing straightforward
