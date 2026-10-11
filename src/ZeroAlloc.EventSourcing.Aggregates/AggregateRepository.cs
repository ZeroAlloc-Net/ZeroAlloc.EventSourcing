using System.Buffers;
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
    /// <remarks>
    /// An aggregate deriving from <see cref="Aggregate{TId,TState}"/> gets <paramref name="id"/> as its
    /// <c>Id</c>, also when the stream is empty.
    /// </remarks>
    public async ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default)
    {
        var streamId = _streamIdFactory(id);
        var aggregate = _factory();
        AggregateIdentity.Assign(aggregate, id);

        await foreach (var envelope in _store.ReadAsync(streamId, StreamPosition.Start, ct).ConfigureAwait(false))
            aggregate.ApplyHistoric(envelope.Event, envelope.Position);

        return Result<TAggregate, StoreError>.Success(aggregate);
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var count = aggregate.UncommittedCount;
        if (count == 0)
            return Result<AppendResult, StoreError>.Success(new AppendResult(_streamIdFactory(id), aggregate.OriginalVersion));

        // The events go through a pooled buffer that lives for the duration of the append only;
        // IEventStore.AppendAsync does not keep the memory once its task has completed.
        var events = ArrayPool<object>.Shared.Rent(count);
        Result<AppendResult, StoreError> result;
        try
        {
            aggregate.DrainUncommitted(events.AsSpan(0, count));
            result = await _store.AppendAsync(_streamIdFactory(id), new ReadOnlyMemory<object>(events, 0, count), aggregate.OriginalVersion, ct).ConfigureAwait(false);
        }
        finally
        {
            // Drop the event references before the buffer goes back to the pool.
            events.AsSpan(0, count).Clear();
            ArrayPool<object>.Shared.Return(events);
        }

        if (result.IsSuccess)
            aggregate.AcceptVersion(result.Value.NextExpectedVersion);
        return result;
    }
}
