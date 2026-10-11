using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

/// <summary>The public-facing event store API. Wraps an <see cref="IEventStoreAdapter"/> with serialization.</summary>
public interface IEventStore
{
    /// <summary>Appends events to a stream. Returns <see cref="StoreError.Conflict"/> if <paramref name="expectedVersion"/> mismatches.</summary>
    /// <remarks>
    /// <paramref name="events"/> is valid only until the returned task completes: a caller such as
    /// the aggregate repository may pass a pooled buffer and reuse it afterwards. An implementation
    /// that needs the events later copies them before it returns.
    /// </remarks>
    ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<object> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default);

    /// <summary>Reads the events of a stream after <paramref name="from"/>. The bound is exclusive: the events with a position greater than <paramref name="from"/>, so <see cref="StreamPosition.Start"/> reads the whole stream.</summary>
    IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        CancellationToken ct = default);

    /// <summary>
    /// Reads at most <paramref name="maxCount"/> events of a stream after <paramref name="from"/>,
    /// in position order. The bound on <paramref name="from"/> is exclusive, as for
    /// <see cref="ReadAsync"/>. Fewer than
    /// <paramref name="maxCount"/> events means the end of the stream was reached.
    /// </summary>
    /// <remarks>
    /// The default implementation stops the unbounded read after <paramref name="maxCount"/>
    /// events. <see cref="EventStore"/> overrides it to bound the query the adapter runs.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is zero or negative.</exception>
    IAsyncEnumerable<EventEnvelope> ReadBatchAsync(
        StreamId id,
        StreamPosition from,
        int maxCount,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        return Internal.BoundedRead.Take(ReadAsync(id, from, ct), maxCount, ct);
    }

    /// <summary>Subscribes to events appended to a stream from <paramref name="from"/> onward.</summary>
    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
