#pragma warning disable CS1591 // Missing XML comment
using System.Text;
using BenchmarkDotNet.Attributes;
using Confluent.Kafka;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Consumes <see cref="MessageCount"/> messages through <see cref="KafkaManualPartitionConsumer"/>.
/// Every call replays the same pre-built messages from a hand-written fake consumer, so each call
/// consumes all of them and the measurement holds only what the library does. The fakes allocate
/// nothing per call, unlike a mocking library, which records every call it receives.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, launchCount: 1, iterationCount: 5)]
public class KafkaStreamConsumerBenchmarks
{
    private FakeKafkaConsumer _fakeConsumer = null!;
    private KafkaManualPartitionOptions _defaultBatch = null!;
    private KafkaManualPartitionOptions _smallBatch = null!;
    private KafkaManualPartitionOptions _afterEventCommit = null!;
    private readonly NullCheckpointStore _checkpointStore = new();
    private readonly KafkaBenchSerializer _serializer = new();
    private readonly KafkaBenchTypeRegistry _registry = new();
    private Func<EventEnvelope, CancellationToken, Task> _handler = null!;
    private int _consumed;

    [Params(100, 1000, 10000)]
    public int MessageCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _fakeConsumer = new FakeKafkaConsumer(KafkaBenchMessages.Create(MessageCount));
        _defaultBatch = Options(100, CommitStrategy.AfterBatch);
        _smallBatch = Options(10, CommitStrategy.AfterBatch);
        _afterEventCommit = Options(100, CommitStrategy.AfterEvent);
        _handler = Handle;
    }

    [Benchmark]
    public Task ConsumeAllMessages_DefaultBatchSize() => ConsumeAll(_defaultBatch);

    [Benchmark]
    public Task ConsumeAllMessages_SmallBatchSize() => ConsumeAll(_smallBatch);

    [Benchmark]
    public Task ConsumeAllMessages_AfterEventCommit() => ConsumeAll(_afterEventCommit);

    private async Task ConsumeAll(KafkaManualPartitionOptions options)
    {
        _fakeConsumer.Rewind();
        _consumed = 0;

        using var consumer = new KafkaManualPartitionConsumer(options, _checkpointStore, _serializer, _registry, _fakeConsumer);
        await consumer.ConsumeAsync(_handler).ConfigureAwait(false);

        // Guards the measurement itself: a fake that ran dry once made every call consume nothing.
        if (_consumed != MessageCount)
            throw new InvalidOperationException($"Consumed {_consumed} of {MessageCount} messages.");
    }

    private Task Handle(EventEnvelope envelope, CancellationToken ct)
    {
        _consumed++;
        return Task.CompletedTask;
    }

    private static KafkaManualPartitionOptions Options(int batchSize, CommitStrategy commitStrategy) => new()
    {
        BootstrapServers = "localhost:9092",
        Topic = "bench-topic",
        ConsumerId = "bench-consumer",
        Partitions = [0],
        ConsumerOptions = new()
        {
            BatchSize = batchSize,
            MaxRetries = 1,
            ErrorStrategy = ErrorHandlingStrategy.FailFast,
            CommitStrategy = commitStrategy,
        },
    };
}

/// <summary>
/// Maps one Kafka message to an <see cref="EventEnvelope"/>. The message is built once in setup.
/// </summary>
[MemoryDiagnoser]
public class KafkaMessageMapperBenchmarks
{
    private ConsumeResult<string, byte[]> _message = null!;
    private readonly KafkaBenchSerializer _serializer = new();
    private readonly KafkaBenchTypeRegistry _registry = new();

    [GlobalSetup]
    public void Setup() => _message = KafkaBenchMessages.Create(1)[0];

    [Benchmark]
    public EventEnvelope MapKafkaMessage_ToEnvelope()
        => KafkaMessageMapper.ToEnvelope(_message, _serializer, _registry);
}

internal static class KafkaBenchMessages
{
    public static ConsumeResult<string, byte[]>[] Create(int count)
    {
        var messages = new ConsumeResult<string, byte[]>[count];
        for (int i = 0; i < count; i++)
        {
            var headers = new Headers
            {
                { "event-type", Encoding.UTF8.GetBytes(nameof(KafkaBenchEvent)) },
                { "event-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            };

            messages[i] = new ConsumeResult<string, byte[]>
            {
                Message = new Message<string, byte[]> { Key = $"stream-{i}", Value = [1, 2, 3], Headers = headers },
                Topic = "bench-topic",
                Partition = 0,
                Offset = i,
                IsPartitionEOF = false,
                TopicPartitionOffset = new TopicPartitionOffset("bench-topic", 0, i),
            };
        }

        return messages;
    }
}

internal sealed class KafkaBenchEvent;

internal sealed class KafkaBenchSerializer : IEventSerializer
{
    private static readonly KafkaBenchEvent Cached = new();

    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => throw new NotSupportedException();

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType) => Cached;
}

internal sealed class KafkaBenchTypeRegistry : IEventTypeRegistry
{
    public bool TryGetType(string eventType, out Type? type)
    {
        type = string.Equals(eventType, nameof(KafkaBenchEvent), StringComparison.Ordinal) ? typeof(KafkaBenchEvent) : null;
        return type is not null;
    }

    public string GetTypeName(Type type) => type.Name;
}

/// <summary>A checkpoint store that never has a checkpoint and discards writes.</summary>
internal sealed class NullCheckpointStore : ICheckpointStore
{
    private static readonly Task<StreamPosition?> NoCheckpoint = Task.FromResult<StreamPosition?>(null);

    public Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken ct = default) => NoCheckpoint;

    public Task WriteAsync(string consumerId, StreamPosition position, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAsync(string consumerId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Replays a fixed array of messages, then returns <see langword="null"/>, which ends a consume
/// run. <see cref="Rewind"/> starts the replay again. Only the members the library calls are
/// implemented; the rest throw.
/// </summary>
internal sealed class FakeKafkaConsumer(ConsumeResult<string, byte[]>[] messages) : IConsumer<string, byte[]>
{
    private int _next;

    public void Rewind() => _next = 0;

    public ConsumeResult<string, byte[]> Consume(TimeSpan timeout)
        => _next < messages.Length ? messages[_next++] : null!;

    public void Assign(TopicPartition partition) { }
    public void Assign(TopicPartitionOffset partition) { }
    public void Assign(IEnumerable<TopicPartitionOffset> partitions) { }
    public void Assign(IEnumerable<TopicPartition> partitions) { }
    public void Subscribe(IEnumerable<string> topics) { }
    public void Subscribe(string topic) { }
    public void Seek(TopicPartitionOffset tpo) { }
    public void StoreOffset(ConsumeResult<string, byte[]> result) { }
    public void StoreOffset(TopicPartitionOffset offset) { }
    public List<TopicPartitionOffset> Commit() => [];
    public void Commit(IEnumerable<TopicPartitionOffset> offsets) { }
    public void Commit(ConsumeResult<string, byte[]> result) { }
    public void Close() { }
    public void Dispose() { }

    public ConsumeResult<string, byte[]> Consume(int millisecondsTimeout) => throw new NotSupportedException();
    public ConsumeResult<string, byte[]> Consume(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Unsubscribe() => throw new NotSupportedException();
    public void IncrementalAssign(IEnumerable<TopicPartitionOffset> partitions) => throw new NotSupportedException();
    public void IncrementalAssign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();
    public void IncrementalUnassign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();
    public void Unassign() => throw new NotSupportedException();
    public void Pause(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();
    public void Resume(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();
    public List<TopicPartitionOffset> Committed(TimeSpan timeout) => throw new NotSupportedException();
    public List<TopicPartitionOffset> Committed(IEnumerable<TopicPartition> partitions, TimeSpan timeout) => throw new NotSupportedException();
    public Offset Position(TopicPartition partition) => throw new NotSupportedException();
    public List<TopicPartitionOffset> OffsetsForTimes(IEnumerable<TopicPartitionTimestamp> timestampsToSearch, TimeSpan timeout) => throw new NotSupportedException();
    public WatermarkOffsets GetWatermarkOffsets(TopicPartition topicPartition) => throw new NotSupportedException();
    public WatermarkOffsets QueryWatermarkOffsets(TopicPartition topicPartition, TimeSpan timeout) => throw new NotSupportedException();
    public int AddBrokers(string brokers) => throw new NotSupportedException();
    public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();

    public string MemberId => throw new NotSupportedException();
    public List<TopicPartition> Assignment => throw new NotSupportedException();
    public List<string> Subscription => throw new NotSupportedException();
    public IConsumerGroupMetadata ConsumerGroupMetadata => throw new NotSupportedException();
    public Handle Handle => throw new NotSupportedException();
    public string Name => "fake";
}
