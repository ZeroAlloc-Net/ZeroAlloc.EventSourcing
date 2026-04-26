using System.Diagnostics;
using System.Diagnostics.Metrics;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Telemetry;

/// <summary>
/// Decorates <see cref="IAggregateRepository{TAggregate,TId}"/> with OpenTelemetry instrumentation.
/// Records Activity spans (<c>aggregate.load</c>, <c>aggregate.save</c>), success counters
/// (<c>aggregate.loads_total</c>, <c>aggregate.saves_total</c>) and duration histograms
/// (<c>aggregate.load_duration_ms</c>, <c>aggregate.save_duration_ms</c>).
/// </summary>
public sealed class InstrumentedAggregateRepository<TAggregate, TId> : IAggregateRepository<TAggregate, TId>
    where TId : struct
{
    // Static fields are intentional: ActivitySource and Meter register globally by name and
    // share listeners across all instances. Creating one per process avoids duplicate registrations.
    private static readonly ActivitySource _activitySource = new("ZeroAlloc.EventSourcing");
    private static readonly Meter _meter = new("ZeroAlloc.EventSourcing");
    private static readonly Counter<long> _loadsTotal = _meter.CreateCounter<long>("aggregate.loads_total");
    private static readonly Counter<long> _savesTotal = _meter.CreateCounter<long>("aggregate.saves_total");
    private static readonly Histogram<double> _loadDurationMs = _meter.CreateHistogram<double>("aggregate.load_duration_ms");
    private static readonly Histogram<double> _saveDurationMs = _meter.CreateHistogram<double>("aggregate.save_duration_ms");

    private readonly IAggregateRepository<TAggregate, TId> _inner;

    /// <summary>Initialises a new instance of <see cref="InstrumentedAggregateRepository{TAggregate, TId}"/> wrapping <paramref name="inner"/>.</summary>
    public InstrumentedAggregateRepository(IAggregateRepository<TAggregate, TId> inner) => _inner = inner;

    /// <inheritdoc />
    public async ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("aggregate.load");
        activity?.SetTag("aggregate.type", typeof(TAggregate).Name);
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var result = await _inner.LoadAsync(id, ct).ConfigureAwait(false);
            if (result.IsSuccess)
                _loadsTotal.Add(1);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            _loadDurationMs.Record(elapsedMs);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("aggregate.save");
        activity?.SetTag("aggregate.type", typeof(TAggregate).Name);
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var result = await _inner.SaveAsync(aggregate, id, ct).ConfigureAwait(false);
            if (result.IsSuccess)
                _savesTotal.Add(1);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            _saveDurationMs.Record(elapsedMs);
        }
    }
}
