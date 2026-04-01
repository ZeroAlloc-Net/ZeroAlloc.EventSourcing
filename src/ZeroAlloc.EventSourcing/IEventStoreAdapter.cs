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

    /// <summary>Reads raw events from a stream starting at <paramref name="from"/>.</summary>
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
