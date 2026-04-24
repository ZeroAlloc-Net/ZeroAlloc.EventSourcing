using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.EventSourcing.Telemetry;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extensions for telemetry instrumentation.
/// </summary>
public static class EventSourcingBuilderExtensions
{
    /// <summary>
    /// Replaces the registered <see cref="IEventStore"/> with a source-generated <see cref="EventStoreInstrumented"/> decorator
    /// that records OpenTelemetry Activity spans and metrics for every store operation.
    /// </summary>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <returns>The same <paramref name="builder"/> instance for chaining.</returns>
    public static EventSourcingBuilder UseEventSourcingTelemetry(this EventSourcingBuilder builder)
    {
        var descriptor = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(IEventStore));
        if (descriptor is null)
            throw new InvalidOperationException(
                "No IEventStore registration found. Register an event store (e.g. UseInMemoryEventStore, UsePostgreSqlEventStore) before calling UseEventSourcingTelemetry.");

        builder.Services.Remove(descriptor);
        builder.Services.AddSingleton<IEventStore>(sp =>
        {
            IEventStore inner;
            if (descriptor.ImplementationInstance is IEventStore instance)
                inner = instance;
            else if (descriptor.ImplementationFactory is not null)
                inner = (IEventStore)descriptor.ImplementationFactory(sp);
            else
                inner = (IEventStore)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
            return new EventStoreInstrumented(inner);
        });

        return builder;
    }
}
