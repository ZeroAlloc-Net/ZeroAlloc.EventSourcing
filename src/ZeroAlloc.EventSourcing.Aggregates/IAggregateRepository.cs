using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Non-generic view of an <see cref="IAggregateRepository{TAggregate, TId}"/>. Lets code that
/// holds a repository as <see cref="object"/>, such as a DI decorator, recover its closed type
/// arguments without reflection.
/// </summary>
/// <remarks>
/// Every <see cref="IAggregateRepository{TAggregate, TId}"/> implements this interface through a
/// default implementation of <see cref="Accept{TResult}"/>, so implementers need not do anything.
/// <para>
/// The exception is a class that implements two or more closed
/// <see cref="IAggregateRepository{TAggregate, TId}"/> interfaces, such as one repository serving
/// two aggregate types. Each closed interface brings its own default <see cref="Accept{TResult}"/>,
/// so the class has no most specific implementation: it no longer compiles, error CS8705, until it
/// implements <see cref="Accept{TResult}"/> itself. A binary of such a class compiled against an
/// earlier version throws <see cref="System.Runtime.AmbiguousImplementationException"/> when
/// <see cref="Accept{TResult}"/> is called, which <c>WithTelemetry()</c> does on resolve.
/// </para>
/// </remarks>
public interface IAggregateRepository
{
    /// <summary>
    /// Calls <see cref="IAggregateRepositoryVisitor{TResult}.Visit{TAggregate, TId}"/> on
    /// <paramref name="visitor"/> with this repository and its closed type arguments, and
    /// returns the visitor's result.
    /// </summary>
    /// <remarks>
    /// The type arguments are known statically at the call, so this works under NativeAOT
    /// where building <c>SomeType&lt;TAggregate, TId&gt;</c> with <c>MakeGenericType</c> cannot
    /// be relied on. A repository that re-implements this method must keep the contract: call
    /// <c>visitor.Visit(this)</c> exactly once and return its result.
    /// <c>WithTelemetry()</c> relies on it to decorate every registered repository.
    /// <para>
    /// A class implementing two or more closed <see cref="IAggregateRepository{TAggregate, TId}"/>
    /// interfaces must implement this method itself, since the default implementations conflict:
    /// error CS8705 at compile time, <see cref="System.Runtime.AmbiguousImplementationException"/>
    /// at run time for a binary built against an earlier version. Its implementation has to pick
    /// one closed interface, for example <c>visitor.Visit&lt;Order, OrderId&gt;(this)</c>, and cannot
    /// tell which one it was resolved as. <c>WithTelemetry()</c> therefore decorates it only where it
    /// is registered as that interface, and throws on resolve where it is registered as another.
    /// </para>
    /// </remarks>
    /// <typeparam name="TResult">The visitor's result type.</typeparam>
    /// <param name="visitor">The visitor to call.</param>
    /// <returns>What <paramref name="visitor"/> returned.</returns>
    TResult Accept<TResult>(IAggregateRepositoryVisitor<TResult> visitor);
}

/// <summary>
/// Loads and saves aggregates via the event store.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type.</typeparam>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
public interface IAggregateRepository<TAggregate, TId> : IAggregateRepository
    where TId : struct
{
    /// <summary>Loads an aggregate by replaying its event stream. Returns a new empty aggregate if the stream does not exist.</summary>
    ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default);

    /// <summary>Appends uncommitted events from the aggregate to the event store.</summary>
    /// <param name="aggregate">The aggregate containing uncommitted events.</param>
    /// <param name="id">The aggregate's identifier, used to derive the stream ID.</param>
    /// <param name="ct">A token to cancel the asynchronous operation.</param>
    ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default);

    /// <inheritdoc/>
    TResult IAggregateRepository.Accept<TResult>(IAggregateRepositoryVisitor<TResult> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        return visitor.Visit(this);
    }
}
