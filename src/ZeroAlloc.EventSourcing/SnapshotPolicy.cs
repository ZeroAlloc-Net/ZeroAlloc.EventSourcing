namespace ZeroAlloc.EventSourcing;

/// <summary>Factory for built-in <see cref="ISnapshotPolicy"/> implementations.</summary>
public static class SnapshotPolicy
{
    /// <summary>Write a snapshot every time an aggregate is saved.</summary>
    public static ISnapshotPolicy Always { get; } = new AlwaysPolicy();

    /// <summary>Never write automatic snapshots.</summary>
    public static ISnapshotPolicy Never { get; } = new NeverPolicy();

    /// <summary>
    /// Write a snapshot when at least <paramref name="n"/> events have been appended since the
    /// last snapshot (or since stream start if none exists).
    /// </summary>
    /// <param name="n">Minimum event interval. Must be ≥ 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="n"/> is less than 1.</exception>
    public static ISnapshotPolicy EveryNEvents(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "Snapshot interval must be at least 1");
        return new EveryNEventsPolicy(n);
    }

    private sealed class AlwaysPolicy : ISnapshotPolicy
    {
        public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition) => true;
    }

    private sealed class NeverPolicy : ISnapshotPolicy
    {
        public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition) => false;
    }

    private sealed class EveryNEventsPolicy : ISnapshotPolicy
    {
        private readonly int _n;
        public EveryNEventsPolicy(int n) => _n = n;

        public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition? lastSnapshotPosition)
        {
            var baseline = lastSnapshotPosition?.Value ?? 0L;
            return (currentPosition.Value - baseline) >= _n;
        }
    }
}
