using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
    /// Registers <see cref="InMemorySnapshotStore{TState}"/> as <see cref="ISnapshotStore{TState}"/>
    /// for the specified aggregate state type.
    /// </summary>
    /// <remarks>
    /// Call this method once per aggregate state type. The registration is closed over
    /// <typeparamref name="TState"/>, so it resolves under NativeAOT; an open-generic
    /// registration cannot, because every snapshot state is a value type.
    /// Uses <c>TryAddSingleton</c> — an existing <see cref="ISnapshotStore{TState}"/>
    /// registration is not overwritten.
    /// <para>
    /// Snapshots are held in memory and lost on application restart.
    /// Use only for testing or single-process applications.
    /// </para>
    /// </remarks>
    /// <typeparam name="TState">The aggregate state type.</typeparam>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UseInMemorySnapshotStore<TState>(this EventSourcingBuilder builder)
        where TState : struct
    {
        builder.Services.TryAddSingleton<ISnapshotStore<TState>>(static _ => new InMemorySnapshotStore<TState>());
        return builder;
    }

    /// <summary>
    /// Registers <see cref="InMemorySnapshotStore{TState}"/> as the open-generic
    /// <see cref="ISnapshotStore{TState}"/>.
    /// </summary>
    /// <remarks>
    /// Snapshots are held in memory and lost on application restart.
    /// Use only for testing or single-process applications.
    /// <para>
    /// Obsolete: the open-generic registration throws under NativeAOT for every snapshot state,
    /// because <see cref="ISnapshotStore{TState}"/> requires a value type and the container
    /// cannot build a value-type instantiation of an open generic without dynamic code.
    /// </para>
    /// </remarks>
    [Obsolete("Use UseInMemorySnapshotStore<TState>() once per aggregate state type. The open-generic "
        + "registration cannot resolve a value-type state under NativeAOT. "
        + "This overload will be removed in the next major version.", DiagnosticId = "ZAES003")]
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

    /// <summary>
    /// Registers <see cref="InMemoryEventStoreHealthCheck"/> with the health check system.
    /// Always reports <see cref="HealthStatus.Healthy"/> — use mainly to keep health check
    /// registration consistent across environments.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">Health check registration name. Defaults to <c>inmemory-event-store</c>.</param>
    /// <param name="failureStatus">Status to report on failure.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    public static IHealthChecksBuilder AddInMemoryEventStore(
        this IHealthChecksBuilder builder,
        string name = "inmemory-event-store",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        => builder.Add(new HealthCheckRegistration(
            name,
            _ => new InMemoryEventStoreHealthCheck(),
            failureStatus,
            tags));
}
