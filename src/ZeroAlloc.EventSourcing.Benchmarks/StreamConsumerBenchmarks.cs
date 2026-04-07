using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Benchmarks for StreamConsumer throughput and performance.
/// Measures event consumption rate under various configurations.
/// </summary>
[SimpleJob(warmupCount: 3, invocationCount: 5)]
[MemoryDiagnoser]
public class StreamConsumerBenchmarks
{
    private const int EventCount = 10_000;

    private IEventStore _eventStore = null!;
    private InMemoryCheckpointStore _checkpointStore = null!;
    private StreamId _streamId;

    /// <summary>
    /// Global setup for benchmarks.
    /// Populates event store with 10,000 events across multiple streams.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var registry = new BenchmarkTypeRegistry();
        var serializer = new BenchmarkSerializer();
        _eventStore = new EventStore(adapter, serializer, registry);
        _checkpointStore = new InMemoryCheckpointStore();

        _streamId = new StreamId("benchmark-stream");

        // Pre-populate stream with events
        for (int i = 0; i < EventCount; i++)
        {
            var @event = new BenchmarkEvent($"event-{i}", new byte[] { 1, 2, 3, 4, 5 });
            var result = _eventStore.AppendAsync(
                _streamId,
                new object[] { @event }.AsMemory(),
                new StreamPosition(i),
                CancellationToken.None
            ).GetAwaiter().GetResult();

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Failed to append event {i} to benchmark stream during setup: {result.Error}");
            }
        }
    }

    /// <summary>
    /// Benchmark: Consume all events with default batch size.
    /// Measures baseline consumption rate.
    /// </summary>
    [Benchmark]
    public async Task ConsumeAllEvents_DefaultBatchSize()
    {
        var consumer = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            $"consumer-default-{Guid.NewGuid()}",
            new StreamConsumerOptions()
        );

        int processedCount = 0;
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            processedCount++;
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Benchmark: Consume with small batch size (10).
    /// Measures impact of frequent checkpoint commits.
    /// </summary>
    [Benchmark]
    public async Task ConsumeAllEvents_SmallBatch()
    {
        var consumer = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            $"consumer-small-{Guid.NewGuid()}",
            new StreamConsumerOptions { BatchSize = 10 }
        );

        int processedCount = 0;
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            processedCount++;
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Benchmark: Consume with large batch size (1000).
    /// Measures impact of batching on throughput.
    /// </summary>
    [Benchmark]
    public async Task ConsumeAllEvents_LargeBatch()
    {
        var consumer = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            $"consumer-large-{Guid.NewGuid()}",
            new StreamConsumerOptions { BatchSize = 1000 }
        );

        int processedCount = 0;
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            processedCount++;
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Benchmark: Consume with AfterEvent commit strategy.
    /// Measures overhead of per-event checkpoint commits.
    /// </summary>
    [Benchmark]
    public async Task ConsumeAllEvents_AfterEventCommit()
    {
        var consumer = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            $"consumer-after-event-{Guid.NewGuid()}",
            new StreamConsumerOptions
            {
                CommitStrategy = CommitStrategy.AfterEvent,
                BatchSize = 100
            }
        );

        int processedCount = 0;
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            processedCount++;
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Benchmark: Consume with AfterBatch commit strategy.
    /// Measures baseline commit overhead per batch.
    /// </summary>
    [Benchmark]
    public async Task ConsumeAllEvents_AfterBatchCommit()
    {
        var consumer = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            $"consumer-after-batch-{Guid.NewGuid()}",
            new StreamConsumerOptions
            {
                CommitStrategy = CommitStrategy.AfterBatch,
                BatchSize = 100
            }
        );

        int processedCount = 0;
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            processedCount++;
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Benchmark: Resume consumption from checkpoint.
    /// Measures cost of resuming from saved position.
    /// </summary>
    [Benchmark]
    public async Task ResumeFromCheckpoint()
    {
        var consumerId = $"consumer-resume-{Guid.NewGuid()}";

        // First run: consume first 5000 events
        var consumer1 = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            consumerId,
            new StreamConsumerOptions { BatchSize = 100 }
        );

        int count = 0;
        await consumer1.ConsumeAsync(async (envelope, ct) =>
        {
            count++;
            if (count >= 5000) return;  // Stop after 5000
            await Task.CompletedTask;
        }, CancellationToken.None);

        // Second run: resume from checkpoint
        var consumer2 = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            consumerId,
            new StreamConsumerOptions { BatchSize = 100 }
        );

        int resumeCount = 0;
        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            resumeCount++;
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Benchmark: Consume with expensive handler (simulates real-world processing).
    /// Measures total throughput with realistic workload.
    /// </summary>
    [Benchmark]
    public async Task ConsumeWithExpensiveHandler()
    {
        var consumer = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            $"consumer-expensive-{Guid.NewGuid()}",
            new StreamConsumerOptions { BatchSize = 100 }
        );

        int processedCount = 0;
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            // Simulate expensive processing (e.g., database update)
            await Task.Delay(1, ct);
            processedCount++;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Benchmark: GetPosition after consumption.
    /// Measures cost of reading checkpoint.
    /// </summary>
    [Benchmark]
    public async Task GetPositionAfterConsumption()
    {
        var consumerId = $"consumer-getpos-{Guid.NewGuid()}";
        var consumer = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            consumerId,
            new StreamConsumerOptions()
        );

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            await Task.CompletedTask;
        }, CancellationToken.None);

        // Benchmark the GetPositionAsync call
        var position = await consumer.GetPositionAsync(CancellationToken.None);
    }
}
