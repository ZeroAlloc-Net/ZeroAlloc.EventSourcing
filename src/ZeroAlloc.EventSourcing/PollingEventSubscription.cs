namespace ZeroAlloc.EventSourcing;

/// <summary>
/// A catch-up + polling <see cref="IEventSubscription"/> for adapters that do not support
/// push-based delivery (e.g. SQL adapters). On <see cref="StartAsync"/>, replays all events
/// from <c>from</c> then polls at <see cref="DefaultPollInterval"/> until disposed.
/// </summary>
internal sealed class PollingEventSubscription : IEventSubscription
{
    /// <summary>Default interval between poll cycles.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IEventStoreAdapter _adapter;
    private readonly StreamId _id;
    private readonly Func<RawEvent, CancellationToken, ValueTask> _handler;
    private readonly TimeSpan _pollInterval;
    private StreamPosition _nextPosition;
    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;
    private volatile bool _running;

    internal PollingEventSubscription(
        IEventStoreAdapter adapter,
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        TimeSpan pollInterval)
    {
        _adapter = adapter;
        _id = id;
        _nextPosition = from;
        _handler = handler;
        _pollInterval = pollInterval;
    }

    /// <inheritdoc/>
    public bool IsRunning => _running;

    /// <inheritdoc/>
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        _running = true;
        _backgroundTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Catch-up: deliver all events from _nextPosition onward.
            await DeliverNewEventsAsync(ct).ConfigureAwait(false);

            // Live: poll on interval until cancelled.
            using var timer = new PeriodicTimer(_pollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await DeliverNewEventsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — swallow.
        }
    }

    private async Task DeliverNewEventsAsync(CancellationToken ct)
    {
        await foreach (var e in _adapter.ReadAsync(_id, _nextPosition, ct).ConfigureAwait(false))
        {
            await _handler(e, ct).ConfigureAwait(false);
            _nextPosition = new StreamPosition(e.Position.Value + 1);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_running) return;
        _running = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_backgroundTask is not null)
        {
            try { await _backgroundTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }
        _cts.Dispose();
    }
}
