using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Factory for creating <see cref="ISnapshotStore{TState}"/> instances.
/// Abstracts database selection and connection management.
/// </summary>
public interface ISnapshotStoreProvider
{
    /// <summary>
    /// Creates a snapshot store instance for the specified aggregate state type.
    /// The store is configured to use the database backend specified during initialization.
    /// </summary>
    /// <typeparam name="TState">The aggregate state type. Must be a struct implementing <see cref="IAggregateState{TState}"/>.</typeparam>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A configured <see cref="ISnapshotStore{TState}"/> instance.</returns>
    ValueTask<ISnapshotStore<TState>> CreateAsync<TState>(CancellationToken ct = default)
        where TState : struct, IAggregateState<TState>;
}
