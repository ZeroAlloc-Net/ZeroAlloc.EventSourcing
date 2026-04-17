using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Telemetry;

/// <summary>
/// Decorates <see cref="IEventStore"/> with OpenTelemetry instrumentation.
/// Records Activity spans and metrics for append, read, and subscribe operations.
/// No OpenTelemetry SDK dependency — uses BCL <see cref="ActivitySource"/> and <see cref="Meter"/> directly.
/// </summary>
public sealed class InstrumentedEventStore : IEventStore
{
    private static readonly ActivitySource _activitySource = new("ZeroAlloc.EventSourcing");
    private static readonly Meter _meter = new("ZeroAlloc.EventSourcing");
    private static readonly Counter<long> _appendsTotal = _meter.CreateCounter<long>("event_store.appends_total");
    private static readonly Histogram<double> _readDurationMs = _meter.CreateHistogram<double>("event_store.read_duration_ms");

    private readonly IEventStore _inner;

    /// <summary>Initialises a new instance of <see cref="InstrumentedEventStore"/> wrapping <paramref name="inner"/>.</summary>
    public InstrumentedEventStore(IEventStore inner) => _inner = inner;

    /// <inheritdoc />
    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<object> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("event_store.append");
        activity?.SetTag("stream.id", id.Value);
        try
        {
            var result = await _inner.AppendAsync(id, events, expectedVersion, ct).ConfigureAwait(false);
            _appendsTotal.Add(1);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        CancellationToken ct = default) =>
        ReadInstrumentedAsync(id, from, ct);

    private async IAsyncEnumerable<EventEnvelope> ReadInstrumentedAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var activity = _activitySource.StartActivity("event_store.read");
        activity?.SetTag("stream.id", id.Value);
        var sw = Stopwatch.GetTimestamp();
        Exception? caught = null;

        await foreach (var envelope in ReadWithCatch(_inner.ReadAsync(id, from, ct), e => caught = e).ConfigureAwait(false))
        {
            yield return envelope;
        }

        var elapsed = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
        _readDurationMs.Record(elapsed);

        if (caught is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, caught.Message);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }

    private static async IAsyncEnumerable<EventEnvelope> ReadWithCatch(
        IAsyncEnumerable<EventEnvelope> source,
        Action<Exception> onError,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    onError(ex);
                    yield break;
                }

                if (!hasNext)
                    yield break;

                yield return enumerator.Current;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("event_store.subscribe");
        activity?.SetTag("stream.id", id.Value);
        try
        {
            return await _inner.SubscribeAsync(id, from, handler, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
