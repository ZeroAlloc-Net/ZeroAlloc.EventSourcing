#pragma warning disable CS8601 // Possible null reference assignment.
using Confluent.Kafka;
using FluentAssertions;
using NSubstitute;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public class KafkaStreamConsumerTests
{
    private const string TestTopic = "test-topic";
    private const int TestPartition = 0;
    private const string TestGroupId = "test-group";

    private KafkaConsumerOptions CreateOptions(string? consumerId = null, int batchSize = 100)
    {
        return new()
        {
            BootstrapServers = "localhost:9092",
            Topic = TestTopic,
            GroupId = TestGroupId,
            Partitions = [TestPartition],
            ConsumerId = consumerId,
            ConsumerOptions = new()
            {
                BatchSize = batchSize,
                MaxRetries = 3,
                RetryPolicy = new ExponentialBackoffRetryPolicy(),
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };
    }

    private ConsumeResult<string, byte[]> CreateMessage(long offset, string key = "stream-1")
    {
        var headers = new Headers();
        headers.Add("event-type", System.Text.Encoding.UTF8.GetBytes("TestEvent"));
        headers.Add("event-id", System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));

        return new()
        {
            Message = new()
            {
                Key = key,
                Value = [1, 2, 3],
                Headers = headers
            },
            Topic = TestTopic,
            Partition = TestPartition,
            Offset = offset,
            IsPartitionEOF = false,
            TopicPartitionOffset = new TopicPartitionOffset(TestTopic, TestPartition, offset)
        };
    }

    [Fact]
    public async Task ConsumeAsync_ProcessesAllMessages()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var messages = new Queue<ConsumeResult<string, byte[]>?>(new[] { CreateMessage(0), CreateMessage(1), CreateMessage(2), (ConsumeResult<string, byte[]>?)null });
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);
        checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<StreamPosition?>(null));
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>()).Returns(x => { x[1] = typeof(TestEvent); return true; });

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);
        var processedCount = 0;

        // Act
        await streamConsumer.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref processedCount);
            await Task.CompletedTask;
        });

        // Assert
        processedCount.Should().Be(3);
    }

    [Fact]
    public async Task ConsumeAsync_WithAfterBatchStrategy_CommitsAfterBatch()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions(batchSize: 2);
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var messages = new Queue<ConsumeResult<string, byte[]>?>(new[] { CreateMessage(0), CreateMessage(1), CreateMessage(2), (ConsumeResult<string, byte[]>?)null });
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);
        checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<StreamPosition?>(null));
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>()).Returns(x => { x[1] = typeof(TestEvent); return true; });

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);

        // Act
        await streamConsumer.ConsumeAsync(async (envelope, ct) => await Task.CompletedTask);

        // Assert
        await checkpointStore.Received(2).WriteAsync(Arg.Any<string>(), Arg.Any<StreamPosition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_WithAfterEventStrategy_CommitsAfterEach()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        options.ConsumerOptions.CommitStrategy = CommitStrategy.AfterEvent;
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var messages = new Queue<ConsumeResult<string, byte[]>?>(new[] { CreateMessage(0), CreateMessage(1), CreateMessage(2), (ConsumeResult<string, byte[]>?)null });
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);
        checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<StreamPosition?>(null));
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>()).Returns(x => { x[1] = typeof(TestEvent); return true; });

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);

        // Act
        await streamConsumer.ConsumeAsync(async (envelope, ct) => await Task.CompletedTask);

        // Assert
        await checkpointStore.Received(3).WriteAsync(Arg.Any<string>(), Arg.Any<StreamPosition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_WithRetryLogic_RetriesOnFailure()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var messages = new Queue<ConsumeResult<string, byte[]>?>(new[] { CreateMessage(0), (ConsumeResult<string, byte[]>?)null });
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);
        checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<StreamPosition?>(null));
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>()).Returns(x => { x[1] = typeof(TestEvent); return true; });

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);
        var attemptCount = 0;

        // Act
        await streamConsumer.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref attemptCount);
            if (attemptCount < 2)
                throw new InvalidOperationException("Transient");
            await Task.CompletedTask;
        });

        // Assert
        attemptCount.Should().Be(2);
    }

    [Fact]
    public async Task ConsumeAsync_WithSkipStrategy_ContinuesOnFailure()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        options.ConsumerOptions.ErrorStrategy = ErrorHandlingStrategy.Skip;
        options.ConsumerOptions.MaxRetries = 1;
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var messages = new Queue<ConsumeResult<string, byte[]>?>(new[] { CreateMessage(0), CreateMessage(1), (ConsumeResult<string, byte[]>?)null });
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);
        checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<StreamPosition?>(null));
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>()).Returns(x => { x[1] = typeof(TestEvent); return true; });

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);
        var processedCount = 0;

        // Act
        await streamConsumer.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Position.Value == 0)
                throw new InvalidOperationException("Failed");
            Interlocked.Increment(ref processedCount);
            await Task.CompletedTask;
        });

        // Assert
        processedCount.Should().Be(1);
    }

    [Fact]
    public async Task GetPositionAsync_ReturnsCheckpointPosition()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var expectedPosition = new StreamPosition(42);
        checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StreamPosition?>(expectedPosition));

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);

        // Act
        var position = await streamConsumer.GetPositionAsync();

        // Assert
        position.Should().Be(expectedPosition);
    }

    [Fact]
    public async Task ResetPositionAsync_DeletesAndWritesNewPosition()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);

        // Act
        await streamConsumer.ResetPositionAsync(new StreamPosition(10));

        // Assert
        await checkpointStore.Received(1).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await checkpointStore.Received(1).WriteAsync(Arg.Any<string>(), new StreamPosition(10), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CommitAsync_WritesCurrentPosition()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        options.ConsumerOptions.CommitStrategy = CommitStrategy.Manual;
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var messages = new Queue<ConsumeResult<string, byte[]>?>(new[] { CreateMessage(0), (ConsumeResult<string, byte[]>?)null });
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);
        checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<StreamPosition?>(null));
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>()).Returns(x => { x[1] = typeof(TestEvent); return true; });

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);

        // Act
        await streamConsumer.ConsumeAsync(async (envelope, ct) => await Task.CompletedTask);
        await streamConsumer.CommitAsync();

        // Assert
        await checkpointStore.Received(1).WriteAsync(Arg.Any<string>(), new StreamPosition(0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ConsumerId_WithExplicitValue_ReturnsIt()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions(consumerId: "custom-id");
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);

        // Act
        var id = streamConsumer.ConsumerId;

        // Assert
        id.Should().Be("custom-id");
    }

    [Fact]
    public void ConsumerId_DefaultsToGroupId()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);

        // Act
        var id = streamConsumer.ConsumerId;

        // Assert
        id.Should().Be(TestGroupId);
    }

    [Fact]
    public async Task ConsumeAsync_WithFailFastStrategy_ThrowsOnBadMessage()
    {
        // Arrange
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var options = CreateOptions();
        options.ConsumerOptions.ErrorStrategy = ErrorHandlingStrategy.FailFast;
        options.ConsumerOptions.MaxRetries = 1;
        var checkpointStore = Substitute.For<ICheckpointStore>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        var messages = new Queue<ConsumeResult<string, byte[]>?>(new[] { CreateMessage(0), (ConsumeResult<string, byte[]>?)null });
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);
        checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<StreamPosition?>(null));
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>()).Returns(x => { x[1] = typeof(TestEvent); return true; });

        var streamConsumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry, consumer);

        // Act
        Func<Task> act = () => streamConsumer.ConsumeAsync(async (envelope, ct) =>
        {
            throw new InvalidOperationException("Message processing failed");
        });

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private class TestEvent
    {
    }
}
