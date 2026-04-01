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

    /// <summary>Reads events from a stream starting at <paramref name="from"/>.</summary>
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
