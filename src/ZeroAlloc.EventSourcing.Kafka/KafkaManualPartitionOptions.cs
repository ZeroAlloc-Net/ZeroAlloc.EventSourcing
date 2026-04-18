namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Configuration for the manual-partition Kafka consumer.
/// Partitions are explicitly specified; no consumer-group rebalancing.
/// </summary>
public sealed class KafkaManualPartitionOptions : KafkaBaseOptions
{
    /// <summary>Partitions to consume. At least one required. Default: [0].</summary>
    public int[] Partitions { get; set; } = [0];

    /// <summary>Optional Kafka consumer group ID for broker-side offset storage.</summary>
    public string? GroupId { get; set; }

    /// <inheritdoc/>
    public override void Validate()
    {
        base.Validate();
        if (Partitions is null || Partitions.Length == 0)
            throw new InvalidOperationException("Partitions must contain at least one partition.");
    }
}
