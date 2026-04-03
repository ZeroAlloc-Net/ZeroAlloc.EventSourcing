using System.Collections.Concurrent;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// In-memory <see cref="ISnapshotStore{TState}"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// This is a test double — it is not a production snapshot store. Thread-safe and suitable for unit and integration tests.
/// </summary>
/// <typeparam name="TState">The aggregate state type. Must be a struct.</typeparam>
public sealed class InMemorySnapshotStore<TState> : ISnapshotStore<TState> where TState : struct
{
    private readonly ConcurrentDictionary<string, (StreamPosition Position, TState State)> _snapshots = new();

    /// <inheritdoc/>
    public ValueTask<(StreamPosition Position, TState State)?> ReadAsync(StreamId streamId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_snapshots.TryGetValue(streamId.Value, out var snapshot))
        {
            return ValueTask.FromResult<(StreamPosition, TState)?>(snapshot);
        }

        return ValueTask.FromResult<(StreamPosition, TState)?>(null);
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(StreamId streamId, StreamPosition position, TState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _snapshots[streamId.Value] = (position, state);
        return default;
    }
}
