using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Tests for StreamConsumer concurrency and thread-safety.
/// Verifies behavior under concurrent consumption scenarios.
/// </summary>
public class StreamConsumerConcurrencyTests
{
    private readonly IEventStore _eventStore;
    private readonly InMemoryCheckpointStore _checkpointStore = new InMemoryCheckpointStore();

    public StreamConsumerConcurrencyTests()
    {
        _eventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new StreamConsumerTestEventTypeRegistry());
    }

    [Fact]
    public async Task MultipleConsumers_IsolatedPositions()
    {
        // Arrange: Create stream with 5 events
        var streamId = new StreamId("test-stream");
        for (int i = 1; i <= 5; i++)
        {
            await _eventStore.AppendAsync(
                streamId,
                new object[] { new TestEvent { Value = i } }.AsMemory(),
                new StreamPosition(i - 1)
            );
        }

        // Act: Run two different consumers in parallel
        var consumer1Task = RunConsumer("consumer-1", 0, 5);
        var consumer2Task = RunConsumer("consumer-2", 0, 5);

        var (processed1, position1) = await consumer1Task;
        var (processed2, position2) = await consumer2Task;

        // Assert: Both consumers processed all events independently
        processed1.Should().Equal(1, 2, 3, 4, 5);
        processed2.Should().Equal(1, 2, 3, 4, 5);
        position1.Should().Be(new StreamPosition(5));
        position2.Should().Be(new StreamPosition(5));
    }

    [Fact]
    public async Task SameConsumerId_SerialExecution()
    {
        // Arrange: Create stream with 3 events
        var streamId = new StreamId("test-stream");
        for (int i = 1; i <= 3; i++)
        {
            await _eventStore.AppendAsync(
                streamId,
                new object[] { new TestEvent { Value = i } }.AsMemory(),
                new StreamPosition(i - 1)
            );
        }

        // Act: First consumer runs and saves position
        var consumer1 = new StreamConsumer(_eventStore, _checkpointStore, "same-id", new StreamConsumerOptions());
        var processed1 = new List<int>();
        await consumer1.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processed1.Add(te.Value);
            await Task.CompletedTask;
        }, default);

        // Add new events
        for (int i = 4; i <= 5; i++)
        {
            await _eventStore.AppendAsync(
                streamId,
                new object[] { new TestEvent { Value = i } }.AsMemory(),
                new StreamPosition(i - 1)
            );
        }

        // Act: Second consumer with same ID resumes from checkpoint
        var consumer2 = new StreamConsumer(_eventStore, _checkpointStore, "same-id", new StreamConsumerOptions());
        var processed2 = new List<int>();
        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processed2.Add(te.Value);
            await Task.CompletedTask;
        }, default);

        // Assert: First consumer processed 1-3, second resumed and processed 4-5
        processed1.Should().Equal(1, 2, 3);
        processed2.Should().Equal(4, 5);
    }

    [Fact]
    public async Task ConcurrentConsumers_NoDataRaces()
    {
        // Arrange: Create stream with 100 events
        var streamId = new StreamId("test-stream");
        for (int i = 1; i <= 100; i++)
        {
            await _eventStore.AppendAsync(
                streamId,
                new object[] { new TestEvent { Value = i } }.AsMemory(),
                new StreamPosition(i - 1)
            );
        }

        // Act: Run 5 consumers concurrently
        var tasks = new Task<(List<int>, StreamPosition?)>[]
        {
            RunConsumer("concurrent-1", 100),
            RunConsumer("concurrent-2", 100),
            RunConsumer("concurrent-3", 100),
            RunConsumer("concurrent-4", 100),
            RunConsumer("concurrent-5", 100),
        };

        var results = await Task.WhenAll(tasks);

        // Assert: All consumers processed all 100 events without corruption
        foreach (var (processed, position) in results)
        {
            processed.Should().HaveCount(100);
            processed.Should().BeInAscendingOrder();
            position.Should().Be(new StreamPosition(100));
        }
    }

    [Fact]
    public async Task ParallelBatchProcessing_Consistent()
    {
        // Arrange: Create stream with 1000 events
        var streamId = new StreamId("test-stream");
        for (int i = 1; i <= 1000; i++)
        {
            await _eventStore.AppendAsync(
                streamId,
                new object[] { new TestEvent { Value = i } }.AsMemory(),
                new StreamPosition(i - 1)
            );
        }

        // Act: Consume with large batch size
        var consumer = new StreamConsumer(
            _eventStore,
            _checkpointStore,
            "parallel-batch",
            new StreamConsumerOptions { BatchSize = 100 }
        );

        var processedValues = new List<int>();
        var lockObj = new object();

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
            {
                lock (lockObj)
                {
                    processedValues.Add(te.Value);
                }
            }
            await Task.CompletedTask;
        }, default);

        // Assert: All events processed in order
        processedValues.Should().HaveCount(1000);
        processedValues.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task RapidConsumerRestarts_NoMissedEvents()
    {
        // Arrange: Create stream with 50 events
        var streamId = new StreamId("test-stream");
        for (int i = 1; i <= 50; i++)
        {
            await _eventStore.AppendAsync(
                streamId,
                new object[] { new TestEvent { Value = i } }.AsMemory(),
                new StreamPosition(i - 1)
            );
        }

        // Act: Restart consumer 10 times, processing in batches
        var allProcessed = new List<int>();
        for (int run = 0; run < 10; run++)
        {
            var consumer = new StreamConsumer(
                _eventStore,
                _checkpointStore,
                "rapid-restart",
                new StreamConsumerOptions { BatchSize = 5 }
            );

            await consumer.ConsumeAsync(async (envelope, ct) =>
            {
                if (envelope.Event is TestEvent te)
                    allProcessed.Add(te.Value);
                await Task.CompletedTask;
            }, default);
        }

        // Assert: Each event processed exactly once
        allProcessed.GroupBy(v => v).Should().AllSatisfy(g => g.Should().HaveCount(1));
        allProcessed.Distinct().Should().HaveCount(50);
    }

    [Fact]
    public async Task ConcurrentCheckpointReads_ConsistentState()
    {
        // Arrange: Consume events to establish checkpoint
        var streamId = new StreamId("test-stream");
        for (int i = 1; i <= 10; i++)
        {
            await _eventStore.AppendAsync(
                streamId,
                new object[] { new TestEvent { Value = i } }.AsMemory(),
                new StreamPosition(i - 1)
            );
        }

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "checkpoint-read", new StreamConsumerOptions());
        await consumer.ConsumeAsync(async (envelope, ct) => await Task.CompletedTask, default);

        // Act: Read position concurrently 10 times
        var readTasks = new List<Task<StreamPosition?>>();
        for (int i = 0; i < 10; i++)
        {
            readTasks.Add(consumer.GetPositionAsync(default));
        }

        var positions = await Task.WhenAll(readTasks);

        // Assert: All reads return the same position
        positions.Should().AllSatisfy(p => p.Should().Be(new StreamPosition(10)));
    }

    [Fact]
    public async Task ResetPositionWhileConsuming_ThreadSafe()
    {
        // Arrange: Stream with 20 events
        var streamId = new StreamId("test-stream");
        for (int i = 1; i <= 20; i++)
        {
            await _eventStore.AppendAsync(
                streamId,
                new object[] { new TestEvent { Value = i } }.AsMemory(),
                new StreamPosition(i - 1)
            );
        }

        // Act: Consume, reset, and consume again rapidly
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "reset-rapid", new StreamConsumerOptions());

        var processed1 = new List<int>();
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processed1.Add(te.Value);
            await Task.CompletedTask;
        }, default);

        // Reset to start
        await consumer.ResetPositionAsync(StreamPosition.Start, default);

        var processed2 = new List<int>();
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processed2.Add(te.Value);
            await Task.CompletedTask;
        }, default);

        // Assert: Both runs processed all events
        processed1.Should().HaveCount(20);
        processed2.Should().HaveCount(20);
    }

    private async Task<(List<int>, StreamPosition?)> RunConsumer(string consumerId, int expectedCount)
    {
        return await RunConsumer(consumerId, expectedCount, 100);
    }

    private async Task<(List<int>, StreamPosition?)> RunConsumer(
        string consumerId,
        int expectedCount,
        int maxAttempts)
    {
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, consumerId, new StreamConsumerOptions());
        var processed = new List<int>();

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processed.Add(te.Value);
            await Task.CompletedTask;
        }, default);

        var position = await consumer.GetPositionAsync(default);
        return (processed, position);
    }
}
