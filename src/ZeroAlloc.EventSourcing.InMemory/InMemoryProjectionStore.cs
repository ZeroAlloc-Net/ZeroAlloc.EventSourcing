using System.Collections.Concurrent;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// In-memory <see cref="IProjectionStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// This is a test double — it is not a production projection store. Thread-safe and suitable for unit and integration tests.
/// </summary>
public sealed class InMemoryProjectionStore : IProjectionStore
{
    private readonly ConcurrentDictionary<string, string> _projections = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ValueTask SaveAsync(string key, string state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _projections[key] = state;
        return default;
    }

    /// <inheritdoc/>
    public ValueTask<string?> LoadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_projections.TryGetValue(key, out var state))
        {
            return ValueTask.FromResult<string?>(state);
        }

        return ValueTask.FromResult<string?>(null);
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _projections.TryRemove(key, out _);
        return default;
    }
}
