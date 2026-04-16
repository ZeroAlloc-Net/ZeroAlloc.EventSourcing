using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>Thread-safe in-memory dead-letter store. Not suitable for production use.</summary>
public sealed class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly ConcurrentQueue<DeadLetterEntry> _entries = new();

    /// <inheritdoc/>
    public ValueTask WriteAsync(string consumerId, EventEnvelope envelope, Exception exception, CancellationToken ct = default)
    {
        _entries.Enqueue(new DeadLetterEntry(
            envelope,
            consumerId,
            exception.GetType().Name,
            exception.Message,
            DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DeadLetterEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in _entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
