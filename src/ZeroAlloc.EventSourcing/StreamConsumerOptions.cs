namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Configuration options for stream consumers.
/// </summary>
public class StreamConsumerOptions
{
    private int _batchSize = 100;
    private int _maxRetries = 3;

    /// <summary>
    /// Number of events to fetch and process in each batch.
    /// Default: 100. Range: 1-10000.
    /// </summary>
    public int BatchSize
    {
        get => _batchSize;
        set
        {
            if (value < 1 || value > 10000)
                throw new ArgumentOutOfRangeException(nameof(value), "BatchSize must be between 1 and 10000");
            _batchSize = value;
        }
    }

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// Default: 3. Valid range: 0+.
    /// </summary>
    public int MaxRetries
    {
        get => _maxRetries;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxRetries cannot be negative");
            _maxRetries = value;
        }
    }

    /// <summary>
    /// Delay strategy for retries (exponential backoff, fixed, etc).
    /// Default: ExponentialBackoff(initial: 100ms, max: 30s).
    /// </summary>
    public IRetryPolicy RetryPolicy { get; set; } = new ExponentialBackoffRetryPolicy();

    /// <summary>
    /// How to handle processing errors after retries exhausted.
    /// Default: FailFast (stops consumer and throws).
    /// </summary>
    public ErrorHandlingStrategy ErrorStrategy { get; set; } = ErrorHandlingStrategy.FailFast;

    /// <summary>
    /// When to commit consumer position to checkpoint store.
    /// Default: AfterBatch (commit after all events in batch processed).
    /// </summary>
    public CommitStrategy CommitStrategy { get; set; } = CommitStrategy.AfterBatch;
}

/// <summary>
/// Strategy for handling processing errors after retries exhausted.
/// </summary>
public enum ErrorHandlingStrategy
{
    /// <summary>Stop consumer immediately on error (fail-fast, throws exception)</summary>
    FailFast = 0,

    /// <summary>Skip failing event, continue processing with next event</summary>
    Skip = 1,

    /// <summary>Route to dead-letter store for later analysis (future feature)</summary>
    DeadLetter = 2,
}

/// <summary>
/// Strategy for when to commit consumer position to checkpoint store.
/// </summary>
public enum CommitStrategy
{
    /// <summary>Commit position after each event processed</summary>
    AfterEvent = 0,

    /// <summary>Commit position after entire batch processed (default, better performance)</summary>
    AfterBatch = 1,

    /// <summary>Manual commits (application calls CommitAsync explicitly)</summary>
    Manual = 2,
}
