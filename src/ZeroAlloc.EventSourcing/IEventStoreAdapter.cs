using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

/// <summary>Implemented by storage backends (InMemory, SQL Server, PostgreSQL). Operates on raw serialized bytes.</summary>
public interface IEventStoreAdapter
{
    /// <summary>Appends raw events to a stream with optimistic concurrency check.</summary>
    /// <remarks>
    /// <paramref name="events"/> is valid only until the returned task completes:
    /// <see cref="EventStore"/> passes a pooled buffer and reuses it afterwards. An adapter that
    /// needs the events later, for example to notify subscribers in the background, copies them
    /// before it returns.
    /// </remarks>
    ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default);

    /// <summary>Reads the raw events of a stream after <paramref name="from"/>. The bound is exclusive: the events with a position greater than <paramref name="from"/>, so <see cref="StreamPosition.Start"/> reads the whole stream.</summary>
    IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        CancellationToken ct = default);

    /// <summary>
    /// Reads at most <paramref name="maxCount"/> raw events of a stream after <paramref name="from"/>,
    /// in position order. The bound on <paramref name="from"/> is exclusive, as for
    /// <see cref="ReadAsync"/>. Fewer than
    /// <paramref name="maxCount"/> events means the end of the stream was reached.
    /// </summary>
    /// <remarks>
    /// The default implementation stops the unbounded read after <paramref name="maxCount"/>
    /// events. A storage backend should override it to bound the query itself, for example with
    /// <c>LIMIT</c> or <c>TOP</c>, so a read of a long stream does not cost the whole tail of it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is zero or negative.</exception>
    IAsyncEnumerable<RawEvent> ReadBatchAsync(
        StreamId id,
        StreamPosition from,
        int maxCount,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        return Internal.BoundedRead.Take(ReadAsync(id, from, ct), maxCount, ct);
    }

    /// <summary>Subscribes to raw events appended to a stream.</summary>
    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
