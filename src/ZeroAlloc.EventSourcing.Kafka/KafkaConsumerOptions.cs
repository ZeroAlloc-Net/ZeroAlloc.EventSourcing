namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Configuration options for Kafka stream consumer.
/// Combines Kafka-specific settings with the shared stream consumer options.
/// </summary>
public sealed class KafkaConsumerOptions
{
    /// <summary>
    /// Kafka broker bootstrap servers (e.g., "localhost:9092").
    /// Required.
    /// </summary>
    public required string BootstrapServers { get; set; }

    /// <summary>
    /// Kafka topic to consume from.
    /// Required.
    /// </summary>
    public required string Topic { get; set; }

    /// <summary>
    /// Kafka consumer group ID.
    /// Used for offset tracking and consumer group coordination.
    /// Required.
    /// </summary>
    public required string GroupId { get; set; }

    /// <summary>
    /// Kafka partition to consume from.
    /// Phase 6: Single partition support only.
    /// Default: 0.
    /// </summary>
    public int Partition { get; set; } = 0;

    /// <summary>
    /// Consumer identifier used in checkpoint store.
    /// Defaults to GroupId if not explicitly set.
    /// Optional.
    /// </summary>
    public string? ConsumerId { get; set; }

    /// <summary>
    /// Timeout for polling new messages from Kafka.
    /// Default: 1 second.
    /// </summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Shared consumer behavior: batch size, retry policy, error handling, and commit strategy.
    /// Default: new StreamConsumerOptions() with defaults (batch=100, maxRetries=3, etc).
    /// </summary>
    public StreamConsumerOptions ConsumerOptions { get; set; } = new();

    /// <summary>
    /// Validates the configuration.
    /// </summary>
    /// <exception cref="ArgumentException">If required fields are null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If PollTimeout is not positive.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BootstrapServers))
            throw new ArgumentException("BootstrapServers cannot be null or whitespace", nameof(BootstrapServers));

        if (string.IsNullOrWhiteSpace(Topic))
            throw new ArgumentException("Topic cannot be null or whitespace", nameof(Topic));

        if (string.IsNullOrWhiteSpace(GroupId))
            throw new ArgumentException("GroupId cannot be null or whitespace", nameof(GroupId));

        if (PollTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollTimeout), "PollTimeout must be positive");

        // Validate nested StreamConsumerOptions through its property setters (which validate)
        // No additional validation needed — the setters handle BatchSize, MaxRetries range checks
    }
}
