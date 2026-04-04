namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Implements exponential backoff: delay doubles with each attempt, capped at maxDelayMs.
/// Formula: min(initialDelayMs * 2^(attempt-1), maxDelayMs).
/// </summary>
public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private readonly int _initialDelayMs;
    private readonly int _maxDelayMs;

    /// <summary>
    /// Create exponential backoff policy.
    /// </summary>
    /// <param name="initialDelayMs">Initial delay in milliseconds (default: 100ms).</param>
    /// <param name="maxDelayMs">Maximum delay cap in milliseconds (default: 30000ms).</param>
    /// <exception cref="ArgumentException">If initialDelayMs is not positive or maxDelayMs is less than initialDelayMs.</exception>
    public ExponentialBackoffRetryPolicy(int initialDelayMs = 100, int maxDelayMs = 30000)
    {
        if (initialDelayMs <= 0)
            throw new ArgumentException("Initial delay must be positive", nameof(initialDelayMs));
        if (maxDelayMs < initialDelayMs)
            throw new ArgumentException("Max delay must be >= initial delay", nameof(maxDelayMs));

        _initialDelayMs = initialDelayMs;
        _maxDelayMs = maxDelayMs;
    }

    /// <summary>
    /// Get delay for a given attempt number using exponential backoff.
    /// </summary>
    /// <param name="attemptNumber">Attempt number (1-based).</param>
    /// <returns>Delay duration (capped at maxDelayMs).</returns>
    /// <exception cref="ArgumentException">If attemptNumber is less than 1.</exception>
    public TimeSpan GetDelay(int attemptNumber)
    {
        if (attemptNumber < 1)
            throw new ArgumentException("Attempt number must be >= 1", nameof(attemptNumber));

        // Exponential backoff: 2^(n-1) multiplied by initial delay
        long delayMs = _initialDelayMs * (long)Math.Pow(2, attemptNumber - 1);

        // Cap at max delay
        delayMs = Math.Min(delayMs, _maxDelayMs);

        return TimeSpan.FromMilliseconds(delayMs);
    }
}
