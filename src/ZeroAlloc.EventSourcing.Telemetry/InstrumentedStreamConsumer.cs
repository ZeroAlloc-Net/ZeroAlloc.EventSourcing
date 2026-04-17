using System.Diagnostics;

namespace ZeroAlloc.EventSourcing.Telemetry;

/// <summary>
/// Decorates <see cref="IStreamConsumer"/> with OpenTelemetry instrumentation.
/// Records Activity spans for the consume operation.
/// No OpenTelemetry SDK dependency — uses BCL <see cref="ActivitySource"/> directly.
/// </summary>
public sealed class InstrumentedStreamConsumer : IStreamConsumer
{
    private static readonly ActivitySource _activitySource = new("ZeroAlloc.EventSourcing");

    private readonly IStreamConsumer _inner;

    /// <summary>Initialises a new instance of <see cref="InstrumentedStreamConsumer"/> wrapping <paramref name="inner"/>.</summary>
    public InstrumentedStreamConsumer(IStreamConsumer inner) => _inner = inner;

    /// <inheritdoc />
    public string ConsumerId => _inner.ConsumerId;

    /// <inheritdoc />
    public async Task ConsumeAsync(Func<EventEnvelope, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("consumer.consume");
        activity?.SetTag("consumer.id", _inner.ConsumerId);
        try
        {
            await _inner.ConsumeAsync(handler, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<StreamPosition?> GetPositionAsync(CancellationToken ct = default) =>
        _inner.GetPositionAsync(ct);

    /// <inheritdoc />
    public Task ResetPositionAsync(StreamPosition position, CancellationToken ct = default) =>
        _inner.ResetPositionAsync(position, ct);

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken ct = default) =>
        _inner.CommitAsync(ct);
}
