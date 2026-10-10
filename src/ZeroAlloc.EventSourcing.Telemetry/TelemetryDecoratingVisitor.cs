using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Telemetry;

/// <summary>
/// Wraps a repository in <see cref="InstrumentedAggregateRepository{TAggregate, TId}"/>. The
/// closed decorator type comes from the visit's type arguments, so no generic is built at run time.
/// </summary>
internal sealed class TelemetryDecoratingVisitor : IAggregateRepositoryVisitor<object>
{
    public static readonly TelemetryDecoratingVisitor Instance = new();

    private TelemetryDecoratingVisitor() { }

    public object Visit<TAggregate, TId>(IAggregateRepository<TAggregate, TId> repository)
        where TId : struct
        => new InstrumentedAggregateRepository<TAggregate, TId>(repository);
}
