namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Stores and retrieves consumer positions (offsets) for stream consumers.
/// Implementations must provide durable storage for at-least-once semantics.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>
    /// Read the last known position for a consumer.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Last known position, or null if consumer has never processed events</returns>
    Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a new position for a consumer after successful event processing.
    /// Implementation must be atomic and handle concurrent writes (last-write-wins).
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer</param>
    /// <param name="position">Position to persist (must be equal to or greater than previous position)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WriteAsync(string consumerId, StreamPosition position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete/reset a consumer's position. Used for manual replays or consumer cleanup.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAsync(string consumerId, CancellationToken cancellationToken = default);
}
