using Confluent.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Kafka stream consumer that reads from an explicitly configured list of partitions.
/// Partitions are assigned once at startup via <c>consumer.Assign()</c>; no rebalancing.
/// </summary>
public sealed class KafkaManualPartitionConsumer : KafkaConsumerBase
{
    private readonly KafkaManualPartitionOptions _options;

    /// <inheritdoc/>
    public override string ConsumerId => _options.ConsumerId;

    /// <inheritdoc/>
    protected override IReadOnlyList<int> GetAssignedPartitions() => _options.Partitions;

    /// <summary>Production constructor — builds Kafka consumer internally.</summary>
    public KafkaManualPartitionConsumer(
        KafkaManualPartitionOptions options,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        IDeadLetterStore? deadLetterStore = null)
        : base(
            Validated(options).BootstrapServers,
            options.GroupId,
            checkpointStore, serializer, registry,
            options.Topic, options.PollTimeout, options.ConsumerOptions, deadLetterStore)
    {
        _options = options;
    }

    /// <summary>Internal constructor for testing — accepts an injected IConsumer.</summary>
    internal KafkaManualPartitionConsumer(
        KafkaManualPartitionOptions options,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        IConsumer<string, byte[]> consumer,
        IDeadLetterStore? deadLetterStore = null)
        : base(consumer, checkpointStore, serializer, registry,
               Validated(options).Topic,
               options.PollTimeout, options.ConsumerOptions, deadLetterStore)
    {
        _options = options;
    }

    private static KafkaManualPartitionOptions Validated(KafkaManualPartitionOptions options)
    {
        (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        return options;
    }

    /// <inheritdoc/>
    protected override async Task InitializeAsync(CancellationToken ct)
    {
        var topicPartitions = new List<TopicPartitionOffset>(_options.Partitions.Length);
        foreach (var partition in _options.Partitions)
        {
            var pos    = await _checkpointStore.ReadAsync(CheckpointKey(ConsumerId, partition), ct).ConfigureAwait(false);
            var offset = pos.HasValue ? new Offset(pos.Value.Value) : Offset.Beginning;
            topicPartitions.Add(new TopicPartitionOffset(_options.Topic, new Partition(partition), offset));
        }
        _consumer.Assign(topicPartitions);
    }

    /// <inheritdoc/>
    protected override void OnRevoked(IReadOnlyList<TopicPartition> revoked)
    {
        // Manual assignment has no rebalancing; nothing to do on revoke.
    }
}
