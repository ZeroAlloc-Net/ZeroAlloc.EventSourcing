namespace ZeroAlloc.EventSourcing.Aggregates;

internal static class AggregateIdentity
{
    /// <summary>
    /// Sets <paramref name="aggregate"/>'s id to <paramref name="id"/> when it derives from
    /// <see cref="Aggregate{TId,TState}"/> with the same <typeparamref name="TId"/>. Other
    /// <see cref="IAggregate"/> implementations manage their own id and are left as they are.
    /// </summary>
    public static void Assign<TAggregate, TId>(TAggregate aggregate, TId id)
        where TAggregate : IAggregate
        where TId : struct
    {
        if (aggregate is IAggregateIdentity<TId> identity)
            identity.AssignId(id);
    }
}
