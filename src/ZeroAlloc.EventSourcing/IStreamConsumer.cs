namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Consumes and processes events from a stream with position tracking, retry logic, and batch processing.
/// </summary>
public interface IStreamConsumer
{
    /// <summary>
    /// Unique identifier for this consumer (used in checkpoint store).
    /// </summary>
    string ConsumerId { get; }

    /// <summary>
    /// Processes events from the stream starting at the last checkpoint position.
    /// Reads events in batches, applies handler to each event, and manages retry logic.
    /// </summary>
    /// <param name="handler">Async handler that processes each event.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ConsumeAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken ct = default);

    /// <summary>
    /// Get the current consumer position from checkpoint store.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current position, or null if no checkpoint exists.</returns>
    Task<StreamPosition?> GetPositionAsync(CancellationToken ct = default);

    /// <summary>
    /// Reset/replay the consumer position. Useful for manual replay of events.
    /// </summary>
    /// <param name="position">Position to reset to (typically StreamPosition.Start for full replay).</param>
    /// <param name="ct">Cancellation token.</param>
    Task ResetPositionAsync(StreamPosition position, CancellationToken ct = default);

    /// <summary>
    /// Manually commit the current position to checkpoint (used with CommitStrategy.Manual).
    /// For other strategies (AfterEvent, AfterBatch), positions are committed automatically.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task CommitAsync(CancellationToken ct = default);
}
