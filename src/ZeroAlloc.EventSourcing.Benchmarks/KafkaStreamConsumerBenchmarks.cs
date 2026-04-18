#pragma warning disable CS8601 // Possible null reference assignment.
#pragma warning disable CS1591 // Missing XML comment
using BenchmarkDotNet.Attributes;
using Confluent.Kafka;
using NSubstitute;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, launchCount: 1, iterationCount: 5)]
public class KafkaStreamConsumerBenchmarks
{
    private IConsumer<string, byte[]> _fakeConsumer = null!;
    private KafkaManualPartitionOptions _options = null!;
    private ICheckpointStore _checkpointStore = null!;
    private IEventSerializer _serializer = null!;
    private IEventTypeRegistry _registry = null!;

    [Params(100, 1000, 10000)]
    public int MessageCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _fakeConsumer = CreateFakeConsumer(MessageCount);
        _options = new KafkaManualPartitionOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "bench-topic",
            ConsumerId = "bench-consumer",
            Partitions = [0],
            ConsumerOptions = new()
            {
                BatchSize = 100,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };

        _checkpointStore = Substitute.For<ICheckpointStore>();
        _checkpointStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StreamPosition?>(null));

        _serializer = Substitute.For<IEventSerializer>();
        _serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>())
            .Returns(new TestEvent());

        _registry = Substitute.For<IEventTypeRegistry>();
        _registry.TryGetType("TestEvent", out Arg.Any<Type>())
            .Returns(x => { x[1] = typeof(TestEvent); return true; });
    }

    [Benchmark]
    public async Task ConsumeAllMessages_DefaultBatchSize()
    {
        var consumer = new KafkaManualPartitionConsumer(_options, _checkpointStore, _serializer, _registry, _fakeConsumer);
        var count = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref count);
            await Task.CompletedTask;
        });

        consumer.Dispose();
    }

    [Benchmark]
    public async Task ConsumeAllMessages_SmallBatchSize()
    {
        var options = new KafkaManualPartitionOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "bench-topic",
            ConsumerId = "bench-consumer",
            Partitions = [0],
            ConsumerOptions = new()
            {
                BatchSize = 10,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };

        var consumer = new KafkaManualPartitionConsumer(options, _checkpointStore, _serializer, _registry, _fakeConsumer);
        var count = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref count);
            await Task.CompletedTask;
        });

        consumer.Dispose();
    }

    [Benchmark]
    public async Task ConsumeAllMessages_AfterEventCommit()
    {
        var options = new KafkaManualPartitionOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "bench-topic",
            ConsumerId = "bench-consumer",
            Partitions = [0],
            ConsumerOptions = new()
            {
                BatchSize = 100,
                MaxRetries = 1,
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterEvent
            }
        };

        var consumer = new KafkaManualPartitionConsumer(options, _checkpointStore, _serializer, _registry, _fakeConsumer);
        var count = 0;

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            Interlocked.Increment(ref count);
            await Task.CompletedTask;
        });

        consumer.Dispose();
    }

    [Benchmark]
    public void MapKafkaMessage_ToEnvelope()
    {
        var headers = new Headers();
        headers.Add("event-type", System.Text.Encoding.UTF8.GetBytes("TestEvent"));
        headers.Add("event-id", System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));

        var message = new ConsumeResult<string, byte[]>
        {
            Message = new()
            {
                Key = "stream-1",
                Value = [1, 2, 3, 4],
                Headers = headers
            },
            Topic = "bench-topic",
            Partition = 0,
            Offset = 0,
            IsPartitionEOF = false,
            TopicPartitionOffset = new TopicPartitionOffset("bench-topic", 0, 0)
        };

        for (int i = 0; i < 1000; i++)
        {
            KafkaMessageMapper.ToEnvelope(message, _serializer, _registry);
        }
    }

    private IConsumer<string, byte[]> CreateFakeConsumer(int messageCount)
    {
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var messages = new Queue<ConsumeResult<string, byte[]>?>();

        for (int i = 0; i < messageCount; i++)
        {
            var headers = new Headers();
            headers.Add("event-type", System.Text.Encoding.UTF8.GetBytes("TestEvent"));
            headers.Add("event-id", System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));

            var message = new ConsumeResult<string, byte[]>
            {
                Message = new()
                {
                    Key = $"stream-{i}",
                    Value = [1, 2, 3],
                    Headers = headers
                },
                Topic = "bench-topic",
                Partition = 0,
                Offset = i,
                IsPartitionEOF = false,
                TopicPartitionOffset = new TopicPartitionOffset("bench-topic", 0, i)
            };

            messages.Enqueue(message);
        }

        messages.Enqueue(null);

        consumer.Consume(Arg.Any<TimeSpan>())
            .Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        return consumer;
    }

    private class TestEvent { }
}
