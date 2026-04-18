namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Temporary compatibility shim kept while <see cref="KafkaStreamConsumer"/> is being replaced.
/// Will be removed in a follow-up task.
/// </summary>
public sealed class KafkaConsumerOptions
{
    /// <summary>Kafka broker bootstrap servers (e.g., "localhost:9092"). Required.</summary>
    public required string BootstrapServers { get; set; }

    /// <summary>Kafka topic to consume from. Required.</summary>
    public required string Topic { get; set; }

    /// <summary>Kafka consumer group ID. Required.</summary>
    public required string GroupId { get; set; }

    /// <summary>Kafka partitions to consume from. Defaults to partition 0 only.</summary>
    public int[] Partitions { get; set; } = [0];

    /// <summary>Consumer identifier used in checkpoint store. Defaults to GroupId if not set.</summary>
    public string? ConsumerId { get; set; }

    /// <summary>Timeout for polling new messages from Kafka. Default: 1 second.</summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Shared consumer behavior: batch size, retry policy, error handling, and commit strategy.</summary>
    public StreamConsumerOptions ConsumerOptions { get; set; } = new();

    /// <summary>Validates the configuration.</summary>
#pragma warning disable MA0015
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BootstrapServers))
            throw new ArgumentException("BootstrapServers cannot be null or whitespace", nameof(BootstrapServers));
        if (string.IsNullOrWhiteSpace(Topic))
            throw new ArgumentException("Topic cannot be null or whitespace", nameof(Topic));
        if (string.IsNullOrWhiteSpace(GroupId))
            throw new ArgumentException("GroupId cannot be null or whitespace", nameof(GroupId));
        if (Partitions == null || Partitions.Length == 0)
            throw new InvalidOperationException("Partitions must contain at least one partition.");
        if (PollTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollTimeout), "PollTimeout must be positive");
    }
#pragma warning restore MA0015
}
