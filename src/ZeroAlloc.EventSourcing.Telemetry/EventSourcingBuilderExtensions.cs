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
    /// Every <see cref="IAggregateRepository{TAggregate, TId}"/> registration made before this call
    /// is decorated, whether it came from <c>UseAggregateRepository</c> or from a hand-written
    /// descriptor, keyed or not, and it keeps its lifetime. The decorator is built through
    /// <see cref="IAggregateRepository.Accept{TResult}"/>, so no generic type is constructed at run
    /// time and the call is NativeAOT-safe.
    /// <para>
    /// Idempotent — calling more than once on the same builder has no additional effect.
    /// Aggregate repositories registered after this call will not be instrumented.
    /// </para>
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
            if (!d.ServiceType.IsGenericType
                || d.ServiceType.GetGenericTypeDefinition() != typeof(IAggregateRepository<,>))
            {
                continue;
            }

            services[i] = d.IsKeyedService
                ? ServiceDescriptor.DescribeKeyed(
                    d.ServiceType,
                    d.ServiceKey,
                    (sp, key) => Decorate(CreateFromKeyedDescriptor(d, sp, key)),
                    d.Lifetime)
                : ServiceDescriptor.Describe(
                    d.ServiceType,
                    sp => Decorate(CreateFromDescriptor(d, sp)),
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

    // The service type is IAggregateRepository<,>, so the instance always implements the
    // non-generic IAggregateRepository and its Accept hands back the closed type arguments.
    private static object Decorate(object inner)
        => ((IAggregateRepository)inner).Accept(TelemetryDecoratingVisitor.Instance)
            ?? throw new InvalidOperationException(
                $"{inner.GetType().FullName} returned null from IAggregateRepository.Accept, so WithTelemetry() "
                + "cannot decorate it. Accept must call visitor.Visit(this) and return its result: remove an "
                + "override that does not, and configure a mocking-library substitute to forward Accept to the visitor.");

    private static object CreateFromDescriptor(ServiceDescriptor d, IServiceProvider sp)
    {
        if (d.ImplementationInstance is not null)
            return d.ImplementationInstance;
        if (d.ImplementationFactory is not null)
            return d.ImplementationFactory(sp);
        return ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }

    private static object CreateFromKeyedDescriptor(ServiceDescriptor d, IServiceProvider sp, object? key)
    {
        if (d.KeyedImplementationInstance is not null)
            return d.KeyedImplementationInstance;
        if (d.KeyedImplementationFactory is not null)
            return d.KeyedImplementationFactory(sp, key);
        return ActivatorUtilities.CreateInstance(sp, d.KeyedImplementationType!);
    }

    private sealed class EventSourcingTelemetryMarker { }
}
