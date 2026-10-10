namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Receives an <see cref="IAggregateRepository{TAggregate, TId}"/> with its closed type
/// arguments, from <see cref="IAggregateRepository.Accept{TResult}"/>.
/// </summary>
/// <remarks>
/// Use it to build something typed over a repository known only as <see cref="IAggregateRepository"/>,
/// such as a decorator, without <c>MakeGenericType</c> or <c>Activator</c>. The generic method is
/// instantiated from statically known types, so the result is NativeAOT-safe.
/// </remarks>
/// <typeparam name="TResult">The type the visitor returns.</typeparam>
public interface IAggregateRepositoryVisitor<out TResult>
{
    /// <summary>Called with the repository and its closed type arguments.</summary>
    /// <typeparam name="TAggregate">The repository's aggregate type.</typeparam>
    /// <typeparam name="TId">The repository's identifier type.</typeparam>
    /// <param name="repository">The repository that accepted this visitor.</param>
    /// <returns>The visitor's result.</returns>
    TResult Visit<TAggregate, TId>(IAggregateRepository<TAggregate, TId> repository)
        where TId : struct;
}
