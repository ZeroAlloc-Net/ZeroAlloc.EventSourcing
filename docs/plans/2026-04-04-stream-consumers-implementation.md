# Stream Consumers Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Implement Phase 1 stateful stream consumer infrastructure with position tracking, retry logic, and error handling.

**Architecture:** IStreamConsumer provides consumer contract; ICheckpointStore persists positions; StreamConsumerOptions configures behavior; StreamConsumer implementation orchestrates event consumption with batch processing and retry logic.

**Tech Stack:** .NET 8+, xUnit, Npgsql/SqlClient, existing IEventStore interfaces

---

## Task 1: Create ICheckpointStore Interface

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/ICheckpointStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/CheckpointStoreTests.cs`

**Step 1: Write the test file with interface contract tests**

```csharp
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Contract tests for ICheckpointStore implementations.
/// All implementations must pass these tests.
/// </summary>
public abstract class CheckpointStoreContractTests
{
    protected abstract ICheckpointStore CreateStore();

    [Fact]
    public async Task ReadAsync_NonExistentConsumer_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.ReadAsync("nonexistent-consumer");
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAsync_NewPosition_SuccessfullyStored()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-1";
        var position = new StreamPosition(42);

        await store.WriteAsync(consumerId, position);
        var result = await store.ReadAsync(consumerId);

        Assert.NotNull(result);
        Assert.Equal(position.Value, result.Value.Value);
    }

    [Fact]
    public async Task WriteAsync_UpdateExistingPosition_LastWriteWins()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-2";

        await store.WriteAsync(consumerId, new StreamPosition(10));
        await store.WriteAsync(consumerId, new StreamPosition(20));
        var result = await store.ReadAsync(consumerId);

        Assert.Equal(20, result.Value.Value);
    }

    [Fact]
    public async Task DeleteAsync_ExistingConsumer_ResetToNull()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-3";

        await store.WriteAsync(consumerId, new StreamPosition(15));
        await store.DeleteAsync(consumerId);
        var result = await store.ReadAsync(consumerId);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentConsumer_DoesNotThrow()
    {
        var store = CreateStore();
        var exception = await Record.ExceptionAsync(() => store.DeleteAsync("nonexistent"));
        Assert.Null(exception);
    }

    [Fact]
    public async Task MultipleConsumers_IndependentPositions()
    {
        var store = CreateStore();

        await store.WriteAsync("consumer-a", new StreamPosition(10));
        await store.WriteAsync("consumer-b", new StreamPosition(20));

        var posA = await store.ReadAsync("consumer-a");
        var posB = await store.ReadAsync("consumer-b");

        Assert.Equal(10, posA.Value.Value);
        Assert.Equal(20, posB.Value.Value);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
cd c:\Projects\Prive\ZeroAlloc.EventSourcing
dotnet test tests/ZeroAlloc.EventSourcing.Tests/CheckpointStoreTests.cs -v
```

Expected: FAIL - interface not found

**Step 3: Create ICheckpointStore interface**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Stores and retrieves consumer positions (offsets) for stream consumers.
/// Implementations must provide durable storage for at-least-once semantics.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>
    /// Read the last known position for a consumer.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Last known position, or null if consumer has never processed events</returns>
    Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a new position for a consumer after successful event processing.
    /// Implementation must be atomic and handle concurrent writes (last-write-wins).
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer</param>
    /// <param name="position">Position to persist (must be equal to or greater than previous position)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WriteAsync(string consumerId, StreamPosition position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete/reset a consumer's position. Used for manual replays or consumer cleanup.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAsync(string consumerId, CancellationToken cancellationToken = default);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/CheckpointStoreTests.cs -v
```

Expected: PASS (all tests pass with interface defined)

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/ICheckpointStore.cs tests/ZeroAlloc.EventSourcing.Tests/CheckpointStoreTests.cs
git commit -m "feat: add ICheckpointStore interface for consumer position management"
```

---

## Task 2: Create InMemoryCheckpointStore Implementation

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/InMemoryCheckpointStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/InMemoryCheckpointStoreTests.cs`

**Step 1: Create test class inheriting from contract tests**

```csharp
namespace ZeroAlloc.EventSourcing.Tests;

public class InMemoryCheckpointStoreTests : CheckpointStoreContractTests
{
    protected override ICheckpointStore CreateStore() => new InMemoryCheckpointStore();

    [Fact]
    public async Task IsThreadSafe_ConcurrentWrites()
    {
        var store = new InMemoryCheckpointStore();
        var tasks = Enumerable.Range(0, 10)
            .Select(async i =>
            {
                await store.WriteAsync($"consumer-{i}", new StreamPosition(i * 10));
            });

        await Task.WhenAll(tasks);

        for (int i = 0; i < 10; i++)
        {
            var pos = await store.ReadAsync($"consumer-{i}");
            Assert.NotNull(pos);
        }
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/InMemoryCheckpointStoreTests.cs -v
```

Expected: FAIL - InMemoryCheckpointStore not found

**Step 3: Create InMemoryCheckpointStore implementation**

```csharp
using System.Collections.Concurrent;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// In-memory checkpoint store for testing and single-process applications.
/// Positions are lost on application restart.
/// NOT suitable for production distributed systems.
/// </summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, StreamPosition> _positions = new();

    public Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        var result = _positions.TryGetValue(consumerId, out var position) ? position : (StreamPosition?)null;
        return Task.FromResult(result);
    }

    public Task WriteAsync(string consumerId, StreamPosition position, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        _positions[consumerId] = position;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string consumerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        _positions.TryRemove(consumerId, out _);
        return Task.CompletedTask;
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/InMemoryCheckpointStoreTests.cs -v
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/InMemoryCheckpointStore.cs tests/ZeroAlloc.EventSourcing.Tests/InMemoryCheckpointStoreTests.cs
git commit -m "feat: implement InMemoryCheckpointStore for testing"
```

---

## Task 3: Create Retry Policy Interfaces and Implementations

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/IRetryPolicy.cs`
- Create: `src/ZeroAlloc.EventSourcing/ExponentialBackoffRetryPolicy.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/RetryPolicyTests.cs`

**Step 1: Write tests**

```csharp
using System.Diagnostics;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class ExponentialBackoffRetryPolicyTests
{
    [Fact]
    public void GetDelay_FirstAttempt_ReturnsInitialDelay()
    {
        var policy = new ExponentialBackoffRetryPolicy(initialDelayMs: 100, maxDelayMs: 5000);
        var delay = policy.GetDelay(attemptNumber: 1);

        Assert.Equal(100, delay.TotalMilliseconds);
    }

    [Fact]
    public void GetDelay_SecondAttempt_DoublesPreviousDelay()
    {
        var policy = new ExponentialBackoffRetryPolicy(initialDelayMs: 100, maxDelayMs: 5000);
        var delay1 = policy.GetDelay(attemptNumber: 1);
        var delay2 = policy.GetDelay(attemptNumber: 2);

        Assert.Equal(200, delay2.TotalMilliseconds);
    }

    [Fact]
    public void GetDelay_ExceedsMaxDelay_CapsAtMax()
    {
        var policy = new ExponentialBackoffRetryPolicy(initialDelayMs: 100, maxDelayMs: 500);
        var delay10 = policy.GetDelay(attemptNumber: 10);

        Assert.Equal(500, delay10.TotalMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidValues_ThrowsArgumentException(int value)
    {
        var ex = Assert.Throws<ArgumentException>(() => 
            new ExponentialBackoffRetryPolicy(initialDelayMs: value, maxDelayMs: 5000));
        Assert.NotNull(ex);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/RetryPolicyTests.cs -v
```

Expected: FAIL

**Step 3: Create IRetryPolicy interface**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Defines the delay strategy for retrying failed operations.
/// </summary>
public interface IRetryPolicy
{
    /// <summary>
    /// Calculate the delay before the next retry attempt.
    /// </summary>
    /// <param name="attemptNumber">Attempt number (1-based: first failure = attempt 1)</param>
    /// <returns>Duration to wait before next retry</returns>
    TimeSpan GetDelay(int attemptNumber);
}
```

**Step 4: Create ExponentialBackoffRetryPolicy**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Implements exponential backoff: delay doubles with each attempt, capped at maxDelayMs.
/// Formula: min(initialDelayMs * 2^(attempt-1), maxDelayMs)
/// </summary>
public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private readonly int _initialDelayMs;
    private readonly int _maxDelayMs;

    public ExponentialBackoffRetryPolicy(int initialDelayMs = 100, int maxDelayMs = 30000)
    {
        if (initialDelayMs <= 0)
            throw new ArgumentException("Initial delay must be positive", nameof(initialDelayMs));
        if (maxDelayMs < initialDelayMs)
            throw new ArgumentException("Max delay must be >= initial delay", nameof(maxDelayMs));

        _initialDelayMs = initialDelayMs;
        _maxDelayMs = maxDelayMs;
    }

    public TimeSpan GetDelay(int attemptNumber)
    {
        if (attemptNumber < 1)
            throw new ArgumentException("Attempt number must be >= 1", nameof(attemptNumber));

        // Exponential backoff: 2^(n-1) multiplied by initial delay
        long delayMs = _initialDelayMs * (long)Math.Pow(2, attemptNumber - 1);

        // Cap at max delay
        delayMs = Math.Min(delayMs, _maxDelayMs);

        return TimeSpan.FromMilliseconds(delayMs);
    }
}
```

**Step 5: Run tests to verify they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/RetryPolicyTests.cs -v
```

Expected: PASS

**Step 6: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/IRetryPolicy.cs src/ZeroAlloc.EventSourcing/ExponentialBackoffRetryPolicy.cs tests/ZeroAlloc.EventSourcing.Tests/RetryPolicyTests.cs
git commit -m "feat: add retry policy interfaces and exponential backoff implementation"
```

---

## Task 4: Create StreamConsumerOptions and Enums

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/StreamConsumerOptions.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerOptionsTests.cs`

**Step 1: Write validation tests**

```csharp
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerOptionsTests
{
    [Fact]
    public void DefaultValues_AreReasonable()
    {
        var options = new StreamConsumerOptions();

        Assert.Equal(100, options.BatchSize);
        Assert.Equal(3, options.MaxRetries);
        Assert.IsType<ExponentialBackoffRetryPolicy>(options.RetryPolicy);
        Assert.Equal(ErrorHandlingStrategy.FailFast, options.ErrorStrategy);
        Assert.Equal(CommitStrategy.AfterBatch, options.CommitStrategy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void BatchSize_OutOfRange_ThrowsArgumentException(int value)
    {
        var options = new StreamConsumerOptions();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.BatchSize = value);
        Assert.NotNull(ex);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    public void BatchSize_ValidRange_Accepted(int value)
    {
        var options = new StreamConsumerOptions { BatchSize = value };
        Assert.Equal(value, options.BatchSize);
    }

    [Theory]
    [InlineData(-1)]
    public void MaxRetries_Negative_ThrowsArgumentException(int value)
    {
        var options = new StreamConsumerOptions();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxRetries = value);
        Assert.NotNull(ex);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerOptionsTests.cs -v
```

Expected: FAIL

**Step 3: Create StreamConsumerOptions class**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Configuration options for stream consumers.
/// </summary>
public class StreamConsumerOptions
{
    private int _batchSize = 100;
    private int _maxRetries = 3;

    /// <summary>
    /// Number of events to fetch and process in each batch.
    /// Default: 100. Range: 1-10000.
    /// </summary>
    public int BatchSize
    {
        get => _batchSize;
        set
        {
            if (value < 1 || value > 10000)
                throw new ArgumentOutOfRangeException(nameof(value), "BatchSize must be between 1 and 10000");
            _batchSize = value;
        }
    }

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// Default: 3. Valid range: 0+.
    /// </summary>
    public int MaxRetries
    {
        get => _maxRetries;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxRetries cannot be negative");
            _maxRetries = value;
        }
    }

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

/// <summary>
/// Strategy for handling processing errors after retries exhausted.
/// </summary>
public enum ErrorHandlingStrategy
{
    /// <summary>Stop consumer immediately on error (fail-fast, throws exception)</summary>
    FailFast = 0,

    /// <summary>Skip failing event, continue processing with next event</summary>
    Skip = 1,

    /// <summary>Route to dead-letter store for later analysis (future feature)</summary>
    DeadLetter = 2,
}

/// <summary>
/// Strategy for when to commit consumer position to checkpoint store.
/// </summary>
public enum CommitStrategy
{
    /// <summary>Commit position after each event processed</summary>
    AfterEvent = 0,

    /// <summary>Commit position after entire batch processed (default, better performance)</summary>
    AfterBatch = 1,

    /// <summary>Manual commits (application calls CommitAsync explicitly)</summary>
    Manual = 2,
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerOptionsTests.cs -v
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/StreamConsumerOptions.cs tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerOptionsTests.cs
git commit -m "feat: add StreamConsumerOptions with configuration validation"
```

---

## Task 5: Create IStreamConsumer Interface

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/IStreamConsumer.cs`

**Step 1: Create IStreamConsumer interface**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Consumes events from a stream reliably with position tracking and retry logic.
/// Implementations should support at-least-once processing semantics.
/// </summary>
public interface IStreamConsumer
{
    /// <summary>
    /// Unique identifier for this consumer (used for checkpoint storage).
    /// </summary>
    string ConsumerId { get; }

    /// <summary>
    /// Start consuming events from stream starting at last known position.
    /// This method runs continuously, fetching batches of events and invoking the handler.
    /// </summary>
    /// <param name="handler">Async function called for each event (must be idempotent)</param>
    /// <param name="cancellationToken">Token to stop consumption</param>
    Task ConsumeAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get current consumer position (last successfully processed event).
    /// Returns null if consumer has never processed events.
    /// </summary>
    Task<StreamPosition?> GetPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reset consumer to specific position (for manual replay or recovery).
    /// </summary>
    Task ResetPositionAsync(StreamPosition position, CancellationToken cancellationToken = default);
}
```

**Step 2: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/IStreamConsumer.cs
git commit -m "feat: add IStreamConsumer interface"
```

---

## Task 6: Create StreamConsumer Implementation

**Files:**
- Create: `src/ZeroAlloc.EventSourcing/StreamConsumer.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerTests.cs`

**Step 1: Write comprehensive tests**

```csharp
using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerTests
{
    private readonly InMemoryEventStore _eventStore = new InMemoryEventStore(
        new InMemoryEventStoreAdapter(),
        new JsonEventSerializer(),
        new TestEventTypeRegistry());

    private readonly InMemoryCheckpointStore _checkpointStore = new InMemoryCheckpointStore();

    [Fact]
    public async Task ConsumeAsync_ProcessesAllEvents_InOrder()
    {
        var streamId = new StreamId("test-stream");
        var events = new object[] { new TestEvent { Value = 1 }, new TestEvent { Value = 2 }, new TestEvent { Value = 3 } };

        foreach (var e in events)
            await _eventStore.AppendAsync(streamId, e);

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-1", new StreamConsumerOptions());
        var processedValues = new List<int>();

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processedValues.Add(te.Value);
            await Task.CompletedTask;
        });

        processedValues.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ConsumeAsync_AdvancesPosition_AfterSuccessfulProcessing()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 1 });
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 2 });

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-2", new StreamConsumerOptions());

        await consumer.ConsumeAsync(async (envelope, ct) => await Task.CompletedTask);

        var position = await consumer.GetPositionAsync();
        position.Should().NotBeNull();
        position.Value.Value.Should().Be(2); // Last event position
    }

    [Fact]
    public async Task ConsumeAsync_ResumesFromPosition_AfterRestart()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 1 });
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 2 });
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 3 });

        // First consumer processes two events
        var consumer1 = new StreamConsumer(_eventStore, _checkpointStore, "consumer-3", new StreamConsumerOptions { BatchSize = 2 });
        var processedFirst = new List<int>();

        await consumer1.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processedFirst.Add(te.Value);
            await Task.CompletedTask;
        });

        // Second consumer instance with same ID resumes from checkpoint
        var consumer2 = new StreamConsumer(_eventStore, _checkpointStore, "consumer-3", new StreamConsumerOptions());
        var processedSecond = new List<int>();

        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processedSecond.Add(te.Value);
            await Task.CompletedTask;
        });

        processedFirst.Should().Equal(1, 2);
        processedSecond.Should().Equal(3);
    }

    [Fact]
    public async Task ConsumeAsync_WithError_RetriesAndSucceeds()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 1 });

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-4", new StreamConsumerOptions { MaxRetries = 2 });
        var attemptCount = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            attemptCount++;
            if (attemptCount < 2)
                throw new InvalidOperationException("Temporary error");
            await Task.CompletedTask;
        });

        // Event should have been processed after retry
        var position = await consumer.GetPositionAsync();
        position.Should().NotBeNull();
    }

    [Fact]
    public async Task ResetPositionAsync_AllowsReplay()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 1 });

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-5", new StreamConsumerOptions());

        // Process one event
        var count1 = 0;
        await consumer.ConsumeAsync(async (envelope, ct) => { count1++; await Task.CompletedTask; });
        count1.Should().Be(1);

        // Reset and replay
        await consumer.ResetPositionAsync(StreamPosition.Start);

        var count2 = 0;
        await consumer.ConsumeAsync(async (envelope, ct) => { count2++; await Task.CompletedTask; });
        count2.Should().Be(1);
    }
}

public class TestEvent
{
    public int Value { get; set; }
}

public class TestEventTypeRegistry : IEventTypeRegistry
{
    public string GetEventType(object @event) => @event.GetType().Name;
    public object GetInstance(string eventType, object data) => data;
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerTests.cs -v
```

Expected: FAIL

**Step 3: Create StreamConsumer implementation**

```csharp
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Stateful stream consumer with position tracking, retry logic, and batch processing.
/// </summary>
public sealed class StreamConsumer : IStreamConsumer
{
    private readonly IEventStore _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly StreamConsumerOptions _options;

    public string ConsumerId { get; }

    public StreamConsumer(
        IEventStore eventStore,
        ICheckpointStore checkpointStore,
        string consumerId,
        StreamConsumerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _options = options ?? new StreamConsumerOptions();
        ConsumerId = consumerId;
    }

    public async Task ConsumeAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        // Read starting position from checkpoint
        var position = await _checkpointStore.ReadAsync(ConsumerId, cancellationToken) ?? StreamPosition.Start;

        // Consume events in batches
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = new List<EventEnvelope>();

            // Fetch batch of events
            await foreach (var envelope in _eventStore.ReadAsync(new StreamId("*"), position, cancellationToken))
            {
                batch.Add(envelope);
                if (batch.Count >= _options.BatchSize)
                    break;
            }

            if (batch.Count == 0)
                break; // No more events

            // Process batch
            foreach (var envelope in batch)
            {
                await ProcessEventWithRetryAsync(handler, envelope, cancellationToken);
                position = envelope.Position;

                if (_options.CommitStrategy == CommitStrategy.AfterEvent)
                    await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken);
            }

            // Commit after batch if configured
            if (_options.CommitStrategy == CommitStrategy.AfterBatch)
                await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken);
        }
    }

    public async Task<StreamPosition?> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        return await _checkpointStore.ReadAsync(ConsumerId, cancellationToken);
    }

    public async Task ResetPositionAsync(StreamPosition position, CancellationToken cancellationToken = default)
    {
        await _checkpointStore.DeleteAsync(ConsumerId, cancellationToken);
        if (position.Value > 0)
            await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken);
    }

    private async Task ProcessEventWithRetryAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        int attemptCount = 0;

        while (true)
        {
            try
            {
                await handler(envelope, cancellationToken);
                return; // Success
            }
            catch (Exception ex) when (attemptCount < _options.MaxRetries)
            {
                attemptCount++;
                var delay = _options.RetryPolicy.GetDelay(attemptCount);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (attemptCount >= _options.MaxRetries)
            {
                // Retries exhausted
                switch (_options.ErrorStrategy)
                {
                    case ErrorHandlingStrategy.FailFast:
                        throw;
                    case ErrorHandlingStrategy.Skip:
                        // Log and continue silently
                        return;
                    case ErrorHandlingStrategy.DeadLetter:
                        // TODO: Route to dead-letter store
                        throw new NotImplementedException("Dead-letter strategy not yet implemented");
                    default:
                        throw;
                }
            }
        }
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerTests.cs -v
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing/StreamConsumer.cs tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerTests.cs
git commit -m "feat: implement StreamConsumer with batch processing and retry logic"
```

---

## Task 7: Create SqlCheckpointStore Implementation

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sql/SqlCheckpointStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlCheckpointStoreTests.cs`

**Step 1: Create contract test for SQL store**

```csharp
using Testcontainers.PostgreSql;
using Xunit;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public class PostgreSqlCheckpointStoreTests : CheckpointStoreContractTests, IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlCheckpointStore _store = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder().Build();
        await _container.StartAsync();

        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        _store = new PostgreSqlCheckpointStore(_dataSource);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.StopAsync();
    }

    protected override ICheckpointStore CreateStore() => _store;
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlCheckpointStoreTests.cs -v
```

Expected: FAIL

**Step 3: Create PostgreSqlCheckpointStore implementation**

```csharp
using Npgsql;
using System.Data;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL checkpoint store for persisting consumer positions.
/// Uses atomic upsert for last-write-wins semantics.
/// </summary>
public sealed class PostgreSqlCheckpointStore : ICheckpointStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private const string TableName = "consumer_checkpoints";

    public PostgreSqlCheckpointStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task EnsureSchemaAsync()
    {
        using var connection = await _dataSource.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {TableName} (
                consumer_id VARCHAR(256) PRIMARY KEY,
                position BIGINT NOT NULL,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
        ";
        await command.ExecuteNonQueryAsync();
    }

    public async Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken cancellationToken = default)
    {
        ValidateConsumerId(consumerId);

        using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT position FROM {TableName} WHERE consumer_id = @consumer_id";
        command.Parameters.AddWithValue("@consumer_id", consumerId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value 
            ? new StreamPosition((long)result) 
            : null;
    }

    public async Task WriteAsync(string consumerId, StreamPosition position, CancellationToken cancellationToken = default)
    {
        ValidateConsumerId(consumerId);

        using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            INSERT INTO {TableName} (consumer_id, position, updated_at)
            VALUES (@consumer_id, @position, CURRENT_TIMESTAMP)
            ON CONFLICT (consumer_id) DO UPDATE SET
                position = EXCLUDED.position,
                updated_at = CURRENT_TIMESTAMP;
        ";
        command.Parameters.AddWithValue("@consumer_id", consumerId);
        command.Parameters.AddWithValue("@position", position.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string consumerId, CancellationToken cancellationToken = default)
    {
        ValidateConsumerId(consumerId);

        using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE consumer_id = @consumer_id";
        command.Parameters.AddWithValue("@consumer_id", consumerId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateConsumerId(string consumerId)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));
        if (consumerId.Length > 256)
            throw new ArgumentException("Consumer ID cannot exceed 256 characters", nameof(consumerId));
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlCheckpointStoreTests.cs -v
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Sql/SqlCheckpointStore.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlCheckpointStoreTests.cs
git commit -m "feat: implement PostgreSqlCheckpointStore with atomic upsert"
```

---

## Task 8: Create Integration Tests

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerIntegrationTests.cs`

**Step 1: Write integration tests demonstrating real scenarios**

```csharp
using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerIntegrationTests : IAsyncLifetime
{
    private InMemoryEventStore _eventStore = null!;
    private InMemoryCheckpointStore _checkpointStore = null!;

    public async Task InitializeAsync()
    {
        // Initialize test infrastructure
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = new JsonEventSerializer();
        var registry = new TestEventTypeRegistry();
        _eventStore = new InMemoryEventStore(adapter, serializer, registry);
        _checkpointStore = new InMemoryCheckpointStore();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CompleteWorkflow_ProducesConsumedEvents()
    {
        var streamId = new StreamId("orders");
        
        // Produce events
        await _eventStore.AppendAsync(streamId, new OrderCreatedEvent { OrderId = "1" });
        await _eventStore.AppendAsync(streamId, new OrderConfirmedEvent { OrderId = "1" });
        await _eventStore.AppendAsync(streamId, new OrderShippedEvent { OrderId = "1" });

        // Consume events
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "order-processor", new StreamConsumerOptions());
        var events = new List<object>();

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            events.Add(envelope.Event);
            await Task.CompletedTask;
        });

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<OrderCreatedEvent>();
        events[1].Should().BeOfType<OrderConfirmedEvent>();
        events[2].Should().BeOfType<OrderShippedEvent>();
    }

    [Fact]
    public async Task MultipleConsumers_ProcessIndependently()
    {
        var streamId = new StreamId("events");
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 1 });
        await _eventStore.AppendAsync(streamId, new TestEvent { Value = 2 });

        var consumer1Events = new List<int>();
        var consumer2Events = new List<int>();

        var consumer1 = new StreamConsumer(_eventStore, _checkpointStore, "consumer-a", new StreamConsumerOptions());
        var consumer2 = new StreamConsumer(_eventStore, _checkpointStore, "consumer-b", new StreamConsumerOptions());

        await consumer1.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te) consumer1Events.Add(te.Value);
            await Task.CompletedTask;
        });

        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te) consumer2Events.Add(te.Value);
            await Task.CompletedTask;
        });

        consumer1Events.Should().Equal(1, 2);
        consumer2Events.Should().Equal(1, 2);
    }
}

public class OrderCreatedEvent { public string OrderId { get; set; } }
public class OrderConfirmedEvent { public string OrderId { get; set; } }
public class OrderShippedEvent { public string OrderId { get; set; } }
```

**Step 2: Run tests**

```bash
dotnet test tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerIntegrationTests.cs -v
```

Expected: PASS

**Step 3: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.Tests/StreamConsumerIntegrationTests.cs
git commit -m "test: add integration tests for stream consumer workflow"
```

---

## Task 9: Create Documentation and Examples

**Files:**
- Create: `docs/examples/StreamConsumerExample.cs`
- Create: `docs/core-concepts/consumers.md` (documentation)

**Step 1: Create runnable example**

Create `docs/examples/StreamConsumerExample.cs` with complete working example

**Step 2: Create concept documentation**

Create `docs/core-concepts/consumers.md` explaining consumers, position tracking, error handling

**Step 3: Update sidebars.js**

Add consumers documentation to sidebar navigation

**Step 4: Commit**

```bash
git add docs/examples/StreamConsumerExample.cs docs/core-concepts/consumers.md
git commit -m "docs: add stream consumer documentation and examples"
```

---

## Task 10: Update Project Files and Clean Up

**Files:**
- Modify: `src/ZeroAlloc.EventSourcing/ZeroAlloc.EventSourcing.csproj` (ensure exports)

**Step 1: Verify all exports**

Ensure IStreamConsumer, ICheckpointStore are exported from main project

**Step 2: Run full test suite**

```bash
dotnet test ZeroAlloc.EventSourcing.slnx --configuration Release
```

Expected: All tests pass

**Step 3: Commit**

```bash
git commit -m "chore: verify exports and full test suite passes"
```

---

## Summary

**Completion Criteria:**
- ✅ ICheckpointStore interface with contract tests
- ✅ InMemoryCheckpointStore implementation
- ✅ SqlCheckpointStore implementation (PostgreSQL)
- ✅ Retry policy interfaces and exponential backoff
- ✅ StreamConsumerOptions with validation
- ✅ IStreamConsumer interface
- ✅ StreamConsumer implementation with batch processing
- ✅ Comprehensive unit tests (all green)
- ✅ Integration tests demonstrating workflows
- ✅ Documentation and examples
- ✅ All code follows ZeroAlloc patterns (value types, zero-allocation where possible)

**Commits:** ~10 commits, one per logical task

---

Plan complete and saved to `docs/plans/2026-04-04-stream-consumers-implementation.md`.

## Execution Options

You have two paths forward:

**1. Subagent-Driven (this session)** — I dispatch fresh subagent per task, review between tasks, fast iteration with immediate feedback

**2. Parallel Session (separate)** — Open new session with executing-plans skill, batch execution with checkpoints, better for independent work

**Which approach would you prefer?**