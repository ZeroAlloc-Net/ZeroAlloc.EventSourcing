using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Telemetry;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extensions for telemetry instrumentation.
/// </summary>
public static class EventSourcingBuilderExtensions
{
    /// <summary>
    /// Decorates every registered <see cref="IAggregateRepository{TAggregate, TId}"/> with
    /// <see cref="InstrumentedAggregateRepository{TAggregate, TId}"/>, which records OpenTelemetry
    /// Activity spans (<c>aggregate.load</c>, <c>aggregate.save</c>), success counters
    /// (<c>aggregate.loads_total</c>, <c>aggregate.saves_total</c>) and duration histograms
    /// (<c>aggregate.load_duration_ms</c>, <c>aggregate.save_duration_ms</c>) on the
    /// <c>ZeroAlloc.EventSourcing</c> ActivitySource and Meter.
    /// </summary>
    /// <remarks>
    /// Idempotent — calling more than once on the same builder has no additional effect.
    /// Aggregate repositories registered after this call will not be instrumented.
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <returns>The same <paramref name="builder"/> instance for chaining.</returns>
    public static EventSourcingBuilder WithTelemetry(this EventSourcingBuilder builder)
    {
        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(EventSourcingTelemetryMarker)))
            return builder;
        services.AddSingleton<EventSourcingTelemetryMarker>();

        for (int i = 0; i < services.Count; i++)
        {
            var d = services[i];
            if (!d.ServiceType.IsGenericType)
                continue;
            if (d.ServiceType.GetGenericTypeDefinition() != typeof(IAggregateRepository<,>))
                continue;

            var typeArgs = d.ServiceType.GetGenericArguments();
            var proxyType = typeof(InstrumentedAggregateRepository<,>).MakeGenericType(typeArgs);

            var captured = d;
            services[i] = ServiceDescriptor.Describe(
                d.ServiceType,
                sp =>
                {
                    var inner = CreateFromDescriptor(captured, sp);
                    return Activator.CreateInstance(proxyType, inner)!;
                },
                d.Lifetime);
        }

        return builder;
    }

    /// <summary>
    /// Legacy alias for <see cref="WithTelemetry"/>. Use <see cref="WithTelemetry"/> instead.
    /// </summary>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <returns>The same <paramref name="builder"/> instance for chaining.</returns>
    [Obsolete("Use WithTelemetry() instead. Will be removed in the next major.", DiagnosticId = "ZAES001")]
    public static EventSourcingBuilder UseEventSourcingTelemetry(this EventSourcingBuilder builder)
        => builder.WithTelemetry();

    private static object CreateFromDescriptor(ServiceDescriptor d, IServiceProvider sp)
    {
        if (d.ImplementationInstance is not null)
            return d.ImplementationInstance;
        if (d.ImplementationFactory is not null)
            return d.ImplementationFactory(sp);
        return ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }

    private sealed class EventSourcingTelemetryMarker { }
}
