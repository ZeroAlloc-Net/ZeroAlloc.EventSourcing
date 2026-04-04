namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Defines the delay strategy for retrying failed operations.
/// </summary>
public interface IRetryPolicy
{
    /// <summary>
    /// Calculate the delay before the next retry attempt.
    /// </summary>
    /// <param name="attemptNumber">Attempt number (1-based: first failure = attempt 1)</param>
    /// <returns>Duration to wait before next retry</returns>
    TimeSpan GetDelay(int attemptNumber);
}
