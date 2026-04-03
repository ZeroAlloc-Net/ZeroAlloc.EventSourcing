namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Storage-agnostic snapshot persistence. Implementations are responsible for reading
/// and writing snapshot payloads of aggregate state at specific versions.
/// </summary>
/// <typeparam name="TState">The aggregate state type. Must match the type used in snapshots.</typeparam>
public interface ISnapshotStore<TState> where TState : struct
{
    /// <summary>
    /// Reads the most recent snapshot for a given stream, if one exists.
    /// Returns null if no snapshot is found.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A tuple of (position, state) or null if no snapshot exists.</returns>
    ValueTask<(StreamPosition Position, TState State)?> ReadAsync(StreamId streamId, CancellationToken ct = default);

    /// <summary>
    /// Saves a snapshot of the aggregate state at the given position.
    /// Should replace any existing snapshot for this stream (last-write-wins).
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="position">The stream position (version) at which this snapshot was taken.</param>
    /// <param name="state">The aggregate state to persist.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask WriteAsync(StreamId streamId, StreamPosition position, TState state, CancellationToken ct = default);
}
