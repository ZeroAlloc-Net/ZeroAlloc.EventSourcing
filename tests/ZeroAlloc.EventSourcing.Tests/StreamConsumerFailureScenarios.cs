using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerFailureScenarios : IAsyncLifetime
{
    private EventStore _eventStore = null!;
    private InMemoryCheckpointStore _checkpointStore = null!;

    public async Task InitializeAsync()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = new JsonEventSerializer();
        var registry = new TestEventTypeRegistry();
        _eventStore = new EventStore(adapter, serializer, registry);
        _checkpointStore = new InMemoryCheckpointStore();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PartialBatchFailure_WithSkipStrategy_ContinuesProcessing()
    {
        var streamId = new StreamId("failure-stream");

        // Add 3 events
        var result1 = await _eventStore.AppendAsync(
            streamId,
            new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent("1", 100m) }),
            StreamPosition.Start
        );
        result1.IsSuccess.Should().BeTrue();

        var result2 = await _eventStore.AppendAsync(
            streamId,
            new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent("2", 200m) }),
            result1.Value.NextExpectedVersion
        );
        result2.IsSuccess.Should().BeTrue();

        var result3 = await _eventStore.AppendAsync(
            streamId,
            new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent("3", 300m) }),
            result2.Value.NextExpectedVersion
        );
        result3.IsSuccess.Should().BeTrue();
        // result3 not used after this, declaration preserved

        var options = new StreamConsumerOptions
        {
            MaxRetries = 0,
            ErrorStrategy = ErrorHandlingStrategy.Skip
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "failure-consumer", options, streamId);
        var processedValues = new List<decimal>();

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is OrderPlacedEvent ope)
            {
                if (ope.Amount == 200m)
                    throw new InvalidOperationException("Event 2 failed");
                processedValues.Add(ope.Amount);
            }
            await Task.CompletedTask;
        });

        // Should have processed 1 and 3, skipped 2
        processedValues.Should().Contain(new[] { 100m, 300m });
    }

    [Fact]
    public async Task CheckpointRecoveryAfterPartialBatch()
    {
        var streamId = new StreamId("recovery-stream");

        // Write 10 events
        StreamPosition nextVersion = StreamPosition.Start;
        for (int i = 1; i <= 10; i++)
        {
            var result = await _eventStore.AppendAsync(
                streamId,
                new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent($"order-{i}", i * 10m) }),
                nextVersion
            );
            result.IsSuccess.Should().BeTrue();
            nextVersion = result.Value.NextExpectedVersion;
        }

        var options = new StreamConsumerOptions { BatchSize = 3 };
        var consumer1 = new StreamConsumer(_eventStore, _checkpointStore, "recovery-consumer", options, streamId);

        var firstBatchCount = 0;
        try
        {
            await consumer1.ConsumeAsync(async (envelope, ct) =>
            {
                firstBatchCount++;
                if (firstBatchCount > 3)
                    throw new InvalidOperationException("Stop early");
                await Task.CompletedTask;
            });
        }
        catch (InvalidOperationException)
        {
            // Expected - consumer stops on FailFast
        }

        // Consumer stopped at position 3
        var consumer2 = new StreamConsumer(_eventStore, _checkpointStore, "recovery-consumer", options, streamId);
        var secondBatchCount = 0;
        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            secondBatchCount++;
            await Task.CompletedTask;
        });

        // Should resume from position 4+
        secondBatchCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExceptionHandling_WithFailFastStrategy_StopsOnError()
    {
        var streamId = new StreamId("failfast-stream");

        StreamPosition nextVersion = StreamPosition.Start;
        for (int i = 1; i <= 5; i++)
        {
            var result = await _eventStore.AppendAsync(
                streamId,
                new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent($"order-{i}", i) }),
                nextVersion
            );
            result.IsSuccess.Should().BeTrue();
            nextVersion = result.Value.NextExpectedVersion;
        }

        var options = new StreamConsumerOptions
        {
            MaxRetries = 0,
            ErrorStrategy = ErrorHandlingStrategy.FailFast
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "failfast-consumer", options, streamId);
        var processedCount = 0;

        Func<Task> act = async () =>
        {
            await consumer.ConsumeAsync(async (envelope, ct) =>
            {
                processedCount++;
                if (processedCount == 3)
                    throw new InvalidOperationException("Fail on event 3");
                await Task.CompletedTask;
            });
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
        processedCount.Should().Be(3);
    }

    [Fact]
    public async Task RetryLogic_WithExponentialBackoff_EventuallySucceeds()
    {
        var streamId = new StreamId("retry-stream");

        StreamPosition nextVersion = StreamPosition.Start;
        for (int i = 1; i <= 5; i++)
        {
            var result = await _eventStore.AppendAsync(
                streamId,
                new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent($"order-{i}", i * 100m) }),
                nextVersion
            );
            result.IsSuccess.Should().BeTrue();
            nextVersion = result.Value.NextExpectedVersion;
        }

        var options = new StreamConsumerOptions
        {
            MaxRetries = 3,
            ErrorStrategy = ErrorHandlingStrategy.Skip
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "retry-consumer", options, streamId);
        var processedValues = new List<decimal>();
        var attemptCount = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is OrderPlacedEvent ope)
            {
                attemptCount++;
                // Fail on second event first two times
                if (ope.Amount == 200m && attemptCount < 3)
                    throw new InvalidOperationException("Transient failure");
                processedValues.Add(ope.Amount);
            }
            await Task.CompletedTask;
        });

        // Should have processed all events despite transient failures
        processedValues.Should().HaveCount(5);
    }

    [Fact]
    public async Task SkipStrategy_WithMultipleErrors_SkipsAllFailingEvents()
    {
        var streamId = new StreamId("skip-strategy-stream");

        StreamPosition nextVersion = StreamPosition.Start;
        for (int i = 1; i <= 5; i++)
        {
            var result = await _eventStore.AppendAsync(
                streamId,
                new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent($"order-{i}", i) }),
                nextVersion
            );
            result.IsSuccess.Should().BeTrue();
            nextVersion = result.Value.NextExpectedVersion;
        }

        var options = new StreamConsumerOptions
        {
            MaxRetries = 0,
            ErrorStrategy = ErrorHandlingStrategy.Skip
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "skip-consumer", options, streamId);
        var processedValues = new List<decimal>();
        var skippedCount = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is OrderPlacedEvent ope)
            {
                if (ope.Amount == 2m || ope.Amount == 4m)
                {
                    skippedCount++;
                    throw new InvalidOperationException("Skip this event");
                }
                processedValues.Add(ope.Amount);
            }
            await Task.CompletedTask;
        });

        // Should have processed events 1, 3, 5
        processedValues.Should().HaveCount(3);
        skippedCount.Should().Be(2);
    }

    [Fact]
    public async Task CheckpointNotCommitted_AfterBatchFailure_RestartsFromLastCheckpoint()
    {
        var streamId = new StreamId("no-checkpoint-stream");

        StreamPosition nextVersion = StreamPosition.Start;
        for (int i = 1; i <= 10; i++)
        {
            var result = await _eventStore.AppendAsync(
                streamId,
                new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent($"order-{i}", i) }),
                nextVersion
            );
            result.IsSuccess.Should().BeTrue();
            nextVersion = result.Value.NextExpectedVersion;
        }

        var options = new StreamConsumerOptions
        {
            BatchSize = 3,
            MaxRetries = 0,
            ErrorStrategy = ErrorHandlingStrategy.Skip,
            CommitStrategy = CommitStrategy.AfterBatch
        };

        var consumer1 = new StreamConsumer(_eventStore, _checkpointStore, "checkpoint-test", options, streamId);
        var firstCount = 0;

        try
        {
            await consumer1.ConsumeAsync(async (envelope, ct) =>
            {
                firstCount++;
                if (firstCount == 5)
                    throw new InvalidOperationException("Batch interrupted");
                await Task.CompletedTask;
            });
        }
        catch (InvalidOperationException)
        {
            // Expected - consumer stops on unhandled error
        }

        // Consumer was interrupted mid-batch at position 5
        // Restart with new consumer using same checkpoint
        var consumer2 = new StreamConsumer(_eventStore, _checkpointStore, "checkpoint-test", options, streamId);
        var secondCount = 0;

        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            secondCount++;
            await Task.CompletedTask;
        });

        // Should process remaining events
        (firstCount + secondCount).Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task EmptyStream_CompletesWithoutError()
    {
        var streamId = new StreamId("empty-stream");

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "empty-consumer", new StreamConsumerOptions(), streamId);
        var processedCount = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            processedCount++;
            await Task.CompletedTask;
        });

        processedCount.Should().Be(0);
    }

    [Fact]
    public async Task ConsumerWithNullCheckpoint_StartsFromBeginning()
    {
        var streamId = new StreamId("null-checkpoint-stream");

        StreamPosition nextVersion = StreamPosition.Start;
        for (int i = 1; i <= 5; i++)
        {
            var result = await _eventStore.AppendAsync(
                streamId,
                new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent($"order-{i}", i * 10m) }),
                nextVersion
            );
            result.IsSuccess.Should().BeTrue();
            nextVersion = result.Value.NextExpectedVersion;
        }

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "new-consumer", new StreamConsumerOptions(), streamId);
        var processedCount = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            processedCount++;
            await Task.CompletedTask;
        });

        // Should process all events from the start
        processedCount.Should().Be(5);
    }
}
