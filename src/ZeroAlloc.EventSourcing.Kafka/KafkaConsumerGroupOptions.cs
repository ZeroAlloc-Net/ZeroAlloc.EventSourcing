namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Configuration for the consumer-group Kafka consumer.
/// Partitions are assigned dynamically by Kafka via consumer-group rebalancing.
/// </summary>
public sealed class KafkaConsumerGroupOptions : KafkaBaseOptions
{
    /// <summary>Kafka consumer group ID. Required.</summary>
    public required string GroupId { get; set; }

    /// <inheritdoc/>
#pragma warning disable MA0015
    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(GroupId))
            throw new ArgumentException("GroupId cannot be null or whitespace.", nameof(GroupId));
    }
#pragma warning restore MA0015
}
