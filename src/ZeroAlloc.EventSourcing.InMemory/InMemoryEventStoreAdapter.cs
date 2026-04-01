using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>In-memory <see cref="IEventStoreAdapter"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>. Intended for testing.</summary>
public sealed class InMemoryEventStoreAdapter : IEventStoreAdapter
{
    private readonly ConcurrentDictionary<string, InMemoryStream> _streams = new();

    /// <inheritdoc/>
    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var stream = _streams.GetOrAdd(id.Value, _ => new InMemoryStream());

        if (!stream.TryAppend(events, expectedVersion.Value, out var newVersion))
        {
            var error = StoreError.Conflict(id, expectedVersion, new StreamPosition(newVersion));
            return ValueTask.FromResult(Result<AppendResult, StoreError>.Failure(error));
        }

        var result = new AppendResult(id, new StreamPosition(newVersion));
        return ValueTask.FromResult(Result<AppendResult, StoreError>.Success(result));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_streams.TryGetValue(id.Value, out var stream))
            yield break;

        foreach (var e in stream.ReadFrom(from.Value))
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    /// <inheritdoc/>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => throw new NotImplementedException("Subscriptions are implemented in Task 5.");
}
