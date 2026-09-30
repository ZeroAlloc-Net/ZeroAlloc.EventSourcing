using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

/// <summary>The public-facing event store API. Wraps an <see cref="IEventStoreAdapter"/> with serialization.</summary>
public interface IEventStore
{
    /// <summary>Appends events to a stream. Returns <see cref="StoreError.Conflict"/> if <paramref name="expectedVersion"/> mismatches.</summary>
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

    /// <summary>Subscribes to events appended to a stream from <paramref name="from"/> onward.</summary>
    ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default);
}
