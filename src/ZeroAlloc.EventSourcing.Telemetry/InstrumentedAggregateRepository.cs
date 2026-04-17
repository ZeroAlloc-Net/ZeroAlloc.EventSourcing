using System.Diagnostics;
using System.Diagnostics.Metrics;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Telemetry;

/// <summary>
/// Decorates <see cref="IAggregateRepository{TAggregate,TId}"/> with OpenTelemetry instrumentation.
/// Records Activity spans and metrics for load and save operations.
/// </summary>
public sealed class InstrumentedAggregateRepository<TAggregate, TId> : IAggregateRepository<TAggregate, TId>
    where TId : struct
{
    private static readonly ActivitySource _activitySource = new("ZeroAlloc.EventSourcing");
    private static readonly Meter _meter = new("ZeroAlloc.EventSourcing");
    private static readonly Counter<long> _savesTotal = _meter.CreateCounter<long>("aggregate.saves_total");

    private readonly IAggregateRepository<TAggregate, TId> _inner;

    /// <summary>Initialises a new instance of <see cref="InstrumentedAggregateRepository{TAggregate, TId}"/> wrapping <paramref name="inner"/>.</summary>
    public InstrumentedAggregateRepository(IAggregateRepository<TAggregate, TId> inner) => _inner = inner;

    /// <inheritdoc />
    public async ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("aggregate.load");
        activity?.SetTag("aggregate.type", typeof(TAggregate).Name);
        try
        {
            return await _inner.LoadAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("aggregate.save");
        activity?.SetTag("aggregate.type", typeof(TAggregate).Name);
        try
        {
            var result = await _inner.SaveAsync(aggregate, id, ct).ConfigureAwait(false);
            _savesTotal.Add(1);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
