# Stream Consumers

Stream consumers reliably consume events from streams with automatic position tracking and retry logic.

## Key Concepts

### Position Tracking
- Consumers track their position (last successfully processed event) in a checkpoint store
- Supports resuming from exact point after restart
- Enables at-least-once processing semantics

### Batch Processing
- Events are fetched and processed in configurable batches (1-10000 events)
- Improves performance vs. single-event processing
- Reduces checkpoint writes with AfterBatch strategy

### Error Handling
- **FailFast**: Stops consumer on error (default)
- **Skip**: Continues with next event after error
- **DeadLetter**: Routes failed events (future feature)

### Commit Strategies
- **AfterEvent**: Commit after each event (strong consistency)
- **AfterBatch**: Commit after batch (better performance, default)
- **Manual**: Application controls when to commit

### Retry Logic
- Configurable maximum retries (default: 3)
- Exponential backoff delay between retries
- Customizable via IRetryPolicy interface

## Implementations

### InMemoryCheckpointStore
For testing and single-process applications. Positions lost on restart.

### PostgreSqlCheckpointStore
Production-grade SQL persistence with atomic upsert semantics.

## Example Usage

See StreamConsumerExample.cs for complete working example.
