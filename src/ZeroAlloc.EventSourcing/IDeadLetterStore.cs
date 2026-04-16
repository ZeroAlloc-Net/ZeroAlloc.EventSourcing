namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Stores events that permanently failed processing after all retries are exhausted.
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>Persist a failed event with its exception details.</summary>
    ValueTask WriteAsync(string consumerId, EventEnvelope envelope, Exception exception, CancellationToken ct = default);

    /// <summary>Read all dead-letter entries. For monitoring and manual replay.</summary>
    IAsyncEnumerable<DeadLetterEntry> ReadAllAsync(CancellationToken ct = default);
}
