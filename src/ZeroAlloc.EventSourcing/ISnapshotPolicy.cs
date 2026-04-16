namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Controls when a snapshot should be written for an aggregate stream.
/// </summary>
public interface ISnapshotPolicy
{
    /// <summary>
    /// Returns true if a snapshot should be written at <paramref name="currentPosition"/>.
    /// </summary>
    /// <param name="currentPosition">The aggregate's current stream position after saving.</param>
    /// <param name="lastSnapshotPosition">The position of the last written snapshot, or null if none.</param>
    bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition);
}
