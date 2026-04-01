using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Loads and saves aggregates via the event store.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type.</typeparam>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
public interface IAggregateRepository<TAggregate, TId>
    where TId : struct
{
    /// <summary>Loads an aggregate by replaying its event stream. Returns a new empty aggregate if the stream does not exist.</summary>
    ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default);

    /// <summary>Appends uncommitted events from the aggregate to the event store.</summary>
    /// <param name="aggregate">The aggregate containing uncommitted events.</param>
    /// <param name="id">The aggregate's identifier, used to derive the stream ID.</param>
    /// <param name="ct">A token to cancel the asynchronous operation.</param>
    ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default);
}
