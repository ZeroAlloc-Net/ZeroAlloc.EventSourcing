namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Lets a repository give a loaded aggregate the id it was loaded with.
/// <see cref="Aggregate{TId,TState}"/> implements it explicitly; the repositories in this assembly
/// call it through <see cref="AggregateIdentity.Assign{TAggregate,TId}"/>.
/// </summary>
internal interface IAggregateIdentity<in TId>
    where TId : struct
{
    void AssignId(TId id);
}
