namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Configurable strategy for handling snapshots during aggregate load.
/// </summary>
public enum SnapshotLoadingStrategy
{
    /// <summary>
    /// Trust the snapshot completely. Replay events from snapshot position onward.
    /// Fastest, but assumes snapshot is always valid. Use when snapshots are immutable.
    /// </summary>
    TrustSnapshot = 0,

    /// <summary>
    /// Validate snapshot position exists in event store before using.
    /// If position not found, fall back to replaying from start.
    /// Safer, but adds validation overhead.
    /// </summary>
    ValidateAndReplay = 1,

    /// <summary>
    /// Ignore snapshots entirely. Always replay from start.
    /// Useful for disabling snapshot optimization without code changes.
    /// </summary>
    IgnoreSnapshot = 2
}
