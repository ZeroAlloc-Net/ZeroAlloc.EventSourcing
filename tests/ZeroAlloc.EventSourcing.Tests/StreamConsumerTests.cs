using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerTests
{
    private readonly IEventStore _eventStore;
    private readonly InMemoryCheckpointStore _checkpointStore = new InMemoryCheckpointStore();

    public StreamConsumerTests()
    {
        _eventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new StreamConsumerTestEventTypeRegistry());
    }

    [Fact]
    public async Task ConsumeAsync_ProcessesAllEvents_InOrder()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 2 } }.AsMemory(), new StreamPosition(1));
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 3 } }.AsMemory(), new StreamPosition(2));

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-1", new StreamConsumerOptions());
        var processedValues = new List<int>();

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processedValues.Add(te.Value);
            await Task.CompletedTask;
        }, default);

        processedValues.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ConsumeAsync_AdvancesPosition_AfterSuccessfulProcessing()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 2 } }.AsMemory(), new StreamPosition(1));

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-2", new StreamConsumerOptions());

        await consumer.ConsumeAsync(async (envelope, ct) => await Task.CompletedTask, default);

        var position = await consumer.GetPositionAsync(default);
        position.Should().NotBeNull();
        position.Value.Value.Should().Be(2);
    }

    [Fact]
    public async Task ConsumeAsync_ResumesFromPosition_AfterRestart()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 2 } }.AsMemory(), new StreamPosition(1));
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 3 } }.AsMemory(), new StreamPosition(2));

        var consumer1 = new StreamConsumer(_eventStore, _checkpointStore, "consumer-3", new StreamConsumerOptions { BatchSize = 2 });
        var processedFirst = new List<int>();

        await consumer1.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processedFirst.Add(te.Value);
            await Task.CompletedTask;
        }, default);

        var consumer2 = new StreamConsumer(_eventStore, _checkpointStore, "consumer-3", new StreamConsumerOptions());
        var processedSecond = new List<int>();

        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
                processedSecond.Add(te.Value);
            await Task.CompletedTask;
        }, default);

        processedFirst.Should().Equal(1, 2);
        processedSecond.Should().Equal(3);
    }

    [Fact]
    public async Task ConsumeAsync_WithError_RetriesAndSucceeds()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-4", new StreamConsumerOptions { MaxRetries = 2 });
        var attemptCount = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            attemptCount++;
            if (attemptCount < 2)
                throw new InvalidOperationException("Temporary error");
            await Task.CompletedTask;
        }, default);

        var position = await consumer.GetPositionAsync(default);
        position.Should().NotBeNull();
    }

    [Fact]
    public async Task ResetPositionAsync_AllowsReplay()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-5", new StreamConsumerOptions());

        var count1 = 0;
        await consumer.ConsumeAsync(async (envelope, ct) => { count1++; await Task.CompletedTask; }, default);
        count1.Should().Be(1);

        await consumer.ResetPositionAsync(StreamPosition.Start, default);

        var count2 = 0;
        await consumer.ConsumeAsync(async (envelope, ct) => { count2++; await Task.CompletedTask; }, default);
        count2.Should().Be(1);
    }

    [Fact]
    public async Task ConsumeAsync_WithError_FailFastStrategy_Throws()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);

        var options = new StreamConsumerOptions
        {
            MaxRetries = 1,
            ErrorStrategy = ErrorHandlingStrategy.FailFast
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-ff", options);

        Func<Task> action = async () =>
        {
            await consumer.ConsumeAsync(async (envelope, ct) =>
            {
                throw new InvalidOperationException("Processing failed");
            }, default);
        };

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConsumeAsync_WithError_SkipStrategy_ContinuesProcessing()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 2 } }.AsMemory(), new StreamPosition(1));

        var options = new StreamConsumerOptions
        {
            MaxRetries = 0,
            ErrorStrategy = ErrorHandlingStrategy.Skip
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-skip", options);

        var processedValues = new List<int>();
        var failedEvent = false;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is TestEvent te)
            {
                if (te.Value == 1)
                {
                    failedEvent = true;
                    throw new InvalidOperationException("Processing failed");
                }
                processedValues.Add(te.Value);
            }
            await Task.CompletedTask;
        }, default);

        failedEvent.Should().BeTrue();
        processedValues.Should().Contain(2); // Event 2 was processed despite event 1 failure
    }

    [Fact]
    public async Task ConsumeAsync_WithError_DeadLetterStrategy_Throws()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);

        var options = new StreamConsumerOptions
        {
            MaxRetries = 1,
            ErrorStrategy = ErrorHandlingStrategy.DeadLetter
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-dl", options);

        Func<Task> action = async () =>
        {
            await consumer.ConsumeAsync(async (envelope, ct) =>
            {
                throw new InvalidOperationException("Processing failed");
            }, default);
        };

        await action.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public async Task CommitAsync_WithManualStrategy_PersistsPosition()
    {
        var streamId = new StreamId("test-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);

        var options = new StreamConsumerOptions
        {
            CommitStrategy = CommitStrategy.Manual
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-manual", options);

        // Process event
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            await Task.CompletedTask;
        }, default);

        // No position committed yet (Manual strategy)
        var positionBeforeCommit = await _checkpointStore.ReadAsync("consumer-manual", default);

        // Manually commit
        await consumer.CommitAsync(default);

        // Position now in checkpoint
        var positionAfterCommit = await _checkpointStore.ReadAsync("consumer-manual", default);
        positionAfterCommit.Should().NotBeNull();
    }
}

public class TestEvent
{
    public int Value { get; set; }
}

internal sealed class StreamConsumerTestEventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<string, Type> _typeMap = new()
    {
        [nameof(TestEvent)] = typeof(TestEvent),
    };

    public bool TryGetType(string eventType, out Type? type) => _typeMap.TryGetValue(eventType, out type);

    public string GetTypeName(Type type) => type.Name;
}
