using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Default repository implementation. Loads aggregates by replaying their event stream
/// and saves uncommitted events with optimistic concurrency via <see cref="IEventStore"/>.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type. Must implement <see cref="IAggregate"/>.</typeparam>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
public sealed class AggregateRepository<TAggregate, TId> : IAggregateRepository<TAggregate, TId>
    where TAggregate : IAggregate
    where TId : struct
{
    private readonly IEventStore _store;
    private readonly Func<TAggregate> _factory;
    private readonly Func<TId, StreamId> _streamIdFactory;

    /// <summary>
    /// Initialises the repository.
    /// </summary>
    /// <param name="store">The event store to read from and write to.</param>
    /// <param name="factory">Factory that creates a new, empty aggregate instance.</param>
    /// <param name="streamIdFactory">Maps an aggregate ID to a stream ID. Convention: <c>id => new StreamId($"order-{id.Value}")</c>.</param>
    public AggregateRepository(IEventStore store, Func<TAggregate> factory, Func<TId, StreamId> streamIdFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(streamIdFactory);
        _store = store;
        _factory = factory;
        _streamIdFactory = streamIdFactory;
    }

    /// <inheritdoc/>
    public async ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default)
    {
        var streamId = _streamIdFactory(id);
        var aggregate = _factory();

        await foreach (var envelope in _store.ReadAsync(streamId, StreamPosition.Start, ct).ConfigureAwait(false))
            aggregate.ApplyHistoric(envelope.Event, envelope.Position);

        return Result<TAggregate, StoreError>.Success(aggregate);
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var uncommitted = aggregate.DequeueUncommitted();
        if (uncommitted.Length == 0)
            return Result<AppendResult, StoreError>.Success(new AppendResult(_streamIdFactory(id), aggregate.OriginalVersion));

        // Copy events to array for IEventStore API (one allocation per save, acceptable)
        var events = new object[uncommitted.Length];
        for (var i = 0; i < uncommitted.Length; i++)
            events[i] = uncommitted[i];

        var result = await _store.AppendAsync(_streamIdFactory(id), events.AsMemory(), aggregate.OriginalVersion, ct).ConfigureAwait(false);
        if (result.IsSuccess)
            aggregate.AcceptVersion(result.Value.NextExpectedVersion);
        return result;
    }
}
