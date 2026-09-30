using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

/// <summary>Implemented by storage backends (InMemory, SQL Server, PostgreSQL). Operates on raw serialized bytes.</summary>
public interface IEventStoreAdapter
{
    /// <summary>Appends raw events to a stream with optimistic concurrency check.</summary>
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

    /// <summary>Subscribes to raw events appended to a stream.</summary>
    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
