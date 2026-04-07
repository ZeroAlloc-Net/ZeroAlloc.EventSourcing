namespace ZeroAlloc.EventSourcing;

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
