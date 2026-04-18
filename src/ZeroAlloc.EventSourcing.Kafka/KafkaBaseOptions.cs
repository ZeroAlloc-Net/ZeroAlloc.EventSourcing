namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>Base configuration shared by all Kafka consumer modes.</summary>
public abstract class KafkaBaseOptions
{
    /// <summary>Kafka broker bootstrap servers (e.g. "localhost:9092"). Required.</summary>
    public required string BootstrapServers { get; set; }

    /// <summary>Kafka topic to consume from. Required.</summary>
    public required string Topic { get; set; }

    /// <summary>Consumer identifier used in checkpoint store keys. Required.</summary>
    public required string ConsumerId { get; set; }

    /// <summary>Timeout for polling new messages from Kafka. Default: 1 second.</summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Batch size, retry policy, error handling, and commit strategy.</summary>
    public StreamConsumerOptions ConsumerOptions { get; set; } = new();

#pragma warning disable MA0015
    /// <summary>Validates required fields. Called internally before consumption starts.</summary>
    public virtual void Validate()
    {
        if (string.IsNullOrWhiteSpace(BootstrapServers))
            throw new ArgumentException("BootstrapServers cannot be null or whitespace.", nameof(BootstrapServers));
        if (string.IsNullOrWhiteSpace(Topic))
            throw new ArgumentException("Topic cannot be null or whitespace.", nameof(Topic));
        if (string.IsNullOrWhiteSpace(ConsumerId))
            throw new ArgumentException("ConsumerId cannot be null or whitespace.", nameof(ConsumerId));
        if (PollTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollTimeout), "PollTimeout must be positive.");
    }
#pragma warning restore MA0015
}
