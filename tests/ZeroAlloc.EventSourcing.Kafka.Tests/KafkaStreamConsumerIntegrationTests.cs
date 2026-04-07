#pragma warning disable CS8601 // Possible null reference assignment.
using Confluent.Kafka;
using FluentAssertions;
using Testcontainers.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public class KafkaStreamConsumerIntegrationTests : IAsyncLifetime
{
    private KafkaContainer _kafkaContainer = null!;
    private string _bootstrapServers = null!;
    private const int TestPartition = 0;

    public async Task InitializeAsync()
    {
        _kafkaContainer = new KafkaBuilder()
            .WithImage("confluentinc/cp-kafka:7.5.0")
            .Build();

        await _kafkaContainer.StartAsync();
        _bootstrapServers = _kafkaContainer.GetBootstrapAddress();
    }

    public async Task DisposeAsync()
    {
        if (_kafkaContainer != null)
            await _kafkaContainer.StopAsync();
    }

    [Fact]
    public async Task ConsumeAsync_ReadsMessagesFromTopic()
    {
        // Arrange
        const string topic = "test-reads-messages";
        ProduceTestMessages(topic, 3);

        var options = new KafkaConsumerOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = topic,
            GroupId = "test-group-1",
            Partition = TestPartition,
            ConsumerOptions = new()
            {
                BatchSize = 100,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };

        var checkpointStore = new InMemoryCheckpointStore();
        var serializer = new JsonEventSerializer();
        var registry = new TestEventTypeRegistry();

        var consumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry);
        var processedCount = 0;
        var processedOffsets = new List<long>();

        // Act
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref processedCount);
            processedOffsets.Add(envelope.Position.Value);
            await Task.CompletedTask;
        });

        consumer.Dispose();

        // Assert
        processedCount.Should().Be(3);
        processedOffsets.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ConsumeAsync_ResumesFromCheckpoint()
    {
        // Arrange
        const string topic = "test-resumes-checkpoint";
        ProduceTestMessages(topic, 5);

        var options = new KafkaConsumerOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = topic,
            GroupId = "test-group-2",
            Partition = TestPartition,
            ConsumerOptions = new()
            {
                BatchSize = 100,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };

        var checkpointStore = new InMemoryCheckpointStore();
        var serializer = new JsonEventSerializer();
        var registry = new TestEventTypeRegistry();

        // Consume first 3 messages
        var consumer1 = new KafkaStreamConsumer(options, checkpointStore, serializer, registry);
        var count1 = 0;
        try
        {
            await consumer1.ConsumeAsync(async (envelope, ct) =>
            {
                Interlocked.Increment(ref count1);
                if (count1 == 3)
                    throw new OperationCanceledException();
                await Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            // Expected - we're stopping after 3 messages
        }
        consumer1.Dispose();

        count1.Should().Be(3);

        // Consume remaining messages with new consumer
        var consumer2 = new KafkaStreamConsumer(options, checkpointStore, serializer, registry);
        var count2 = 0;
        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref count2);
            await Task.CompletedTask;
        });
        consumer2.Dispose();

        // Assert - should only get remaining 2 messages
        count2.Should().Be(2);
    }

    [Fact]
    public async Task ResetPositionAsync_AllowsReplay()
    {
        // Arrange
        const string topic = "test-reset-replay";
        ProduceTestMessages(topic, 3);

        var options = new KafkaConsumerOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = topic,
            GroupId = "test-group-3",
            Partition = TestPartition,
            ConsumerOptions = new()
            {
                BatchSize = 100,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };

        var checkpointStore = new InMemoryCheckpointStore();
        var serializer = new JsonEventSerializer();
        var registry = new TestEventTypeRegistry();

        // Consume all messages
        var consumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry);
        var count = 0;
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref count);
            await Task.CompletedTask;
        });

        count.Should().Be(3);

        // Reset to beginning
        await consumer.ResetPositionAsync(StreamPosition.Start);

        // Consume again
        count = 0;
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref count);
            await Task.CompletedTask;
        });

        consumer.Dispose();

        // Assert - should get all 3 again
        count.Should().Be(3);
    }

    [Fact]
    public async Task GetPositionAsync_ReturnsCurrentPosition()
    {
        // Arrange
        const string topic = "test-getposition";
        ProduceTestMessages(topic, 1);

        var options = new KafkaConsumerOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = topic,
            GroupId = "test-group-4",
            Partition = TestPartition,
            ConsumerOptions = new()
            {
                BatchSize = 100,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };

        var checkpointStore = new InMemoryCheckpointStore();
        var serializer = new JsonEventSerializer();
        var registry = new TestEventTypeRegistry();

        var consumer = new KafkaStreamConsumer(options, checkpointStore, serializer, registry);
        var lastPosition = new StreamPosition(0);

        // Act
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            lastPosition = envelope.Position;
            await Task.CompletedTask;
        });

        var position = await consumer.GetPositionAsync();
        consumer.Dispose();

        // Assert
        position.Should().NotBeNull();
    }

    [Fact]
    public async Task MultipleConsumers_IndependentPositions()
    {
        // Arrange
        const string topic = "test-multiple-consumers";
        ProduceTestMessages(topic, 3);

        var options1 = new KafkaConsumerOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = topic,
            GroupId = "test-group-5a",
            Partition = TestPartition,
            ConsumerOptions = new()
            {
                BatchSize = 100,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };

        var options2 = new KafkaConsumerOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = topic,
            GroupId = "test-group-5b",
            Partition = TestPartition,
            ConsumerOptions = new()
            {
                BatchSize = 100,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };

        var store1 = new InMemoryCheckpointStore();
        var store2 = new InMemoryCheckpointStore();
        var serializer = new JsonEventSerializer();
        var registry = new TestEventTypeRegistry();

        var consumer1 = new KafkaStreamConsumer(options1, store1, serializer, registry);
        var consumer2 = new KafkaStreamConsumer(options2, store2, serializer, registry);

        var count1 = 0;
        var count2 = 0;

        // Act - Both consumers read all messages independently
        await consumer1.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref count1);
            await Task.CompletedTask;
        });

        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref count2);
            await Task.CompletedTask;
        });

        consumer1.Dispose();
        consumer2.Dispose();

        // Assert - Both consumers see all messages independently
        count1.Should().Be(3);
        count2.Should().Be(3);
    }

    private void ProduceTestMessages(string topic, int count)
    {
        var config = new ProducerConfig { BootstrapServers = _bootstrapServers };
        using var producer = new ProducerBuilder<string, byte[]>(config).Build();

        for (int i = 0; i < count; i++)
        {
            var headers = new Headers();
            headers.Add("event-type", System.Text.Encoding.UTF8.GetBytes("TestEvent"));
            headers.Add("event-id", System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));

            var message = new Message<string, byte[]>
            {
                Key = $"stream-{i}",
                Value = System.Text.Encoding.UTF8.GetBytes($"{{\"id\": {i}}}"),
                Headers = headers
            };

            producer.ProduceAsync(topic, message).GetAwaiter().GetResult();
        }

        producer.Flush();
    }

    private class TestEventTypeRegistry : IEventTypeRegistry
    {
        public bool TryGetType(string eventTypeName, out Type? type)
        {
            type = eventTypeName == "TestEvent" ? typeof(TestEvent) : null;
            return type != null;
        }

        public string GetTypeName(Type type)
        {
            return type == typeof(TestEvent) ? "TestEvent" : type.Name;
        }
    }

    private class JsonEventSerializer : IEventSerializer
    {
        public object Deserialize(ReadOnlyMemory<byte> data, Type type)
        {
            return Activator.CreateInstance(type) ?? throw new InvalidOperationException();
        }

        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    private class TestEvent
    {
        public int Id { get; set; }
    }
}
