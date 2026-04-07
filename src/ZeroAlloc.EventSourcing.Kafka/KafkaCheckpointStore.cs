using Confluent.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Kafka-native checkpoint store using consumer group offset tracking.
/// Stores committed offsets to Kafka broker via consumer group offset commit.
/// </summary>
public sealed class KafkaCheckpointStore : ICheckpointStore
{
    private readonly IConsumer<string, byte[]> _consumer;
    private readonly string _topic;
    private readonly int _partition;

    /// <summary>
    /// Creates a checkpoint store backed by Kafka consumer group offset tracking.
    /// </summary>
    /// <param name="consumer">Kafka consumer instance to read/write offsets through.</param>
    /// <param name="topic">Topic name (used for reading committed offset).</param>
    /// <param name="partition">Partition number (used for reading committed offset).</param>
    /// <exception cref="ArgumentNullException">If consumer is null.</exception>
    public KafkaCheckpointStore(
        IConsumer<string, byte[]> consumer,
        string topic,
        int partition)
    {
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _topic = topic ?? throw new ArgumentNullException(nameof(topic));
        _partition = partition;
    }

    /// <summary>
    /// Reads the last committed offset for this consumer group.
    /// </summary>
    /// <param name="consumerId">Consumer identifier (ignored; broker lookup is scoped to consumer group).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The committed StreamPosition, or null if no offset is committed.</returns>
    public async Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken ct = default)
    {
        // Offset queries must happen on the consumer's assigned partition
        // If not assigned, return null
        try
        {
            var topicPartition = new TopicPartition(_topic, _partition);
            var committed = _consumer.Committed(new[] { topicPartition }, TimeSpan.FromSeconds(5));

            if (committed == null || committed.Count == 0)
                return null;

            var offset = committed[0].Offset;
            return new StreamPosition(offset.Value);
        }
        catch (InvalidOperationException)
        {
            // Partition not assigned yet
            return null;
        }
    }

    /// <summary>
    /// Stores the offset locally. Does not commit to broker.
    /// Broker commit happens via consumer group coordination (e.g., group rebalance).
    /// </summary>
    /// <param name="consumerId">Consumer identifier (ignored; broker lookup is scoped to consumer group).</param>
    /// <param name="position">Position to store.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteAsync(string consumerId, StreamPosition position, CancellationToken ct = default)
    {
        try
        {
            // Store offset locally; broker commit happens at rebalance or explicit Commit()
            _consumer.StoreOffset(new TopicPartitionOffset(
                _topic,
                _partition,
                new Offset(position.Value)));
        }
        catch (InvalidOperationException)
        {
            // Partition not assigned yet; ignore
        }
    }

    /// <summary>
    /// Deletes the checkpoint by storing Offset.Beginning.
    /// This effectively resets consumption to the start of the partition.
    /// </summary>
    /// <param name="consumerId">Consumer identifier (ignored; broker lookup is scoped to consumer group).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string consumerId, CancellationToken ct = default)
    {
        try
        {
            _consumer.StoreOffset(new TopicPartitionOffset(
                _topic,
                _partition,
                Offset.Beginning));
        }
        catch (InvalidOperationException)
        {
            // Partition not assigned yet; ignore
        }
    }
}
