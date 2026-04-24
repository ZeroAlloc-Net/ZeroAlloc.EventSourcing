using System.Diagnostics.Metrics;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Telemetry;

/// <summary>
/// Decorates <see cref="IEventStore"/> with OpenTelemetry instrumentation.
/// This type is a thin public wrapper around the source-generated <c>EventStoreInstrumented</c> proxy
/// and is preserved for API compatibility. Prefer using <see cref="EventSourcingBuilderExtensions.UseEventSourcingTelemetry"/>
/// to wire up instrumentation via the DI builder.
/// </summary>
[Obsolete("Use AddEventSourcingTelemetry() or EventStoreInstrumented directly. InstrumentedEventStore adds a redundant wrapper.")]
public sealed class InstrumentedEventStore : IEventStore
{
    // Static field is intentional: Meter registers globally by name and shares listeners across all instances.
    // The counter is placed here (not on IEventStore via [Count]) because [Count] cannot inspect whether a
    // Result<T,E> return value is Success or Failure — it always increments on non-exception return.
    //
    // Known limitation: this creates a second Meter("ZeroAlloc.EventSourcing") alongside the one held by
    // the source-generated EventStoreInstrumented._meter. The generated class is emitted as
    // `internal sealed class` (not partial), so the two Meters cannot be unified without modifying the
    // ZeroAlloc.Telemetry.Generator. Having two Meter instances with the same name is a .NET diagnostics
    // anti-pattern, but it is acceptable here because InstrumentedEventStore is [Obsolete] and will be
    // removed in the next minor version. MeterListener subscriptions match on instrument.Meter.Name, so
    // all measurements remain visible to any listener subscribing to "ZeroAlloc.EventSourcing".
    private static readonly Counter<long> _appendsTotal =
        new Meter("ZeroAlloc.EventSourcing").CreateCounter<long>("event_store.appends_total");

    private readonly IEventStore _proxy;

    /// <summary>Initialises a new instance of <see cref="InstrumentedEventStore"/> wrapping <paramref name="inner"/>.</summary>
    public InstrumentedEventStore(IEventStore inner) => _proxy = new EventStoreInstrumented(inner);

    /// <inheritdoc />
    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<object> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var result = await _proxy.AppendAsync(id, events, expectedVersion, ct).ConfigureAwait(false);
        if (result.IsSuccess)
            _appendsTotal.Add(1);
        return result;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        CancellationToken ct = default) =>
        _proxy.ReadAsync(id, from, ct);

    /// <inheritdoc />
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default) =>
        _proxy.SubscribeAsync(id, from, handler, ct);
}
