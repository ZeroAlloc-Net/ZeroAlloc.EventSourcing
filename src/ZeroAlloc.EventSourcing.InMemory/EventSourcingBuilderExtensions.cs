using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extensions for in-memory adapters.
/// </summary>
public static class EventSourcingBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryEventStoreAdapter"/> as <see cref="IEventStoreAdapter"/>
    /// and <see cref="EventStore"/> as <see cref="IEventStore"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IEventTypeRegistry"/> must be registered separately — it is domain-specific
    /// and cannot be provided by the library.
    /// <para>
    /// Stored events are held in memory and lost on application restart.
    /// Use only for testing or single-process applications.
    /// </para>
    /// </remarks>
    public static EventSourcingBuilder UseInMemoryEventStore(this EventSourcingBuilder builder)
    {
        builder.Services.TryAddSingleton<IEventStoreAdapter, InMemoryEventStoreAdapter>();
        builder.Services.TryAddSingleton<IEventStore, EventStore>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="InMemorySnapshotStore{TState}"/> as the open-generic
    /// <see cref="ISnapshotStore{TState}"/>.
    /// </summary>
    /// <remarks>
    /// Snapshots are held in memory and lost on application restart.
    /// Use only for testing or single-process applications.
    /// </remarks>
    public static EventSourcingBuilder UseInMemorySnapshotStore(this EventSourcingBuilder builder)
    {
        builder.Services.TryAdd(
            ServiceDescriptor.Singleton(
                typeof(ISnapshotStore<>),
                typeof(InMemorySnapshotStore<>)));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="InMemoryDeadLetterStore"/> as <see cref="IDeadLetterStore"/>.
    /// </summary>
    /// <remarks>
    /// Dead letters are held in memory and lost on application restart.
    /// Use only for testing or single-process applications.
    /// </remarks>
    public static EventSourcingBuilder UseInMemoryDeadLetterStore(this EventSourcingBuilder builder)
    {
        builder.Services.TryAddSingleton<IDeadLetterStore, InMemoryDeadLetterStore>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="InMemoryProjectionStore"/> as <see cref="IProjectionStore"/>.
    /// </summary>
    /// <remarks>
    /// Projections are held in memory and lost on application restart.
    /// Use only for testing or single-process applications.
    /// </remarks>
    public static EventSourcingBuilder UseInMemoryProjectionStore(this EventSourcingBuilder builder)
    {
        builder.Services.TryAddSingleton<IProjectionStore, InMemoryProjectionStore>();
        return builder;
    }
}
