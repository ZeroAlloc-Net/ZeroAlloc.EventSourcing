using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extensions for aggregate repositories.
/// </summary>
public static class EventSourcingBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="AggregateRepository{TAggregate,TId}"/> as
    /// <see cref="IAggregateRepository{TAggregate,TId}"/> for the specified aggregate type.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate root type. Must implement <see cref="IAggregate"/>.</typeparam>
    /// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
    /// <param name="builder">The event sourcing builder.</param>
    /// <param name="factory">Factory that creates a new empty aggregate instance.</param>
    /// <param name="streamIdFactory">Maps an aggregate ID to its event stream ID.</param>
    /// <remarks>
    /// Call once per aggregate type. Multiple aggregate types are supported — chain additional
    /// calls to this method.
    /// <para>
    /// <see cref="IEventStore"/> must be registered separately (e.g. via
    /// <c>UseInMemoryEventStore()</c> or <c>UsePostgreSqlEventStore()</c>).
    /// </para>
    /// </remarks>
    public static EventSourcingBuilder UseAggregateRepository<TAggregate, TId>(
        this EventSourcingBuilder builder,
        Func<TAggregate> factory,
        Func<TId, StreamId> streamIdFactory)
        where TAggregate : IAggregate
        where TId : struct
    {
        builder.Services.TryAddSingleton<IAggregateRepository<TAggregate, TId>>(
            sp => new AggregateRepository<TAggregate, TId>(
                sp.GetRequiredService<IEventStore>(),
                factory,
                streamIdFactory));
        return builder;
    }
}
