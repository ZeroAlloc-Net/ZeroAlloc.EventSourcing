using System.Collections.Concurrent;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// In-memory <see cref="IProjectionStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// This is a test double — it is not a production projection store. Thread-safe and suitable for unit and integration tests.
/// </summary>
public sealed class InMemoryProjectionStore : IProjectionStore
{
    private readonly ConcurrentDictionary<string, byte[]> _projections = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    /// <remarks>Copies <paramref name="state"/>, so the caller may reuse its buffer.</remarks>
    public ValueTask SaveAsync(string key, ReadOnlyMemory<byte> state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _projections[key] = state.ToArray();
        return default;
    }

    /// <inheritdoc/>
    /// <remarks>Returns the stored copy without copying it again, so a load allocates nothing.</remarks>
    public ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _projections.TryGetValue(key, out var state)
            ? ValueTask.FromResult<ReadOnlyMemory<byte>?>(state)
            : ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _projections.TryRemove(key, out _);
        return default;
    }
}
