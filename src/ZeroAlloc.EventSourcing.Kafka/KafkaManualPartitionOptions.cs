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
#pragma warning disable MA0015 // parameter name does not match property name — ArgumentException convention requires property name as the paramName
    public override void Validate()
    {
        base.Validate();
        if (Partitions is null || Partitions.Length == 0)
            throw new ArgumentException("Partitions must contain at least one partition.", nameof(Partitions));
        if (Partitions.Any(p => p < 0))
            throw new ArgumentOutOfRangeException(nameof(Partitions), "All partition indices must be non-negative.");
    }
#pragma warning restore MA0015
}
