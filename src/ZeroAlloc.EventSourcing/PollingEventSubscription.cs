namespace ZeroAlloc.EventSourcing;

/// <summary>
/// A catch-up + polling <see cref="IEventSubscription"/> for adapters that do not support
/// push-based delivery (e.g. SQL adapters). On <see cref="StartAsync"/>, replays all events
/// from <c>from</c> then polls at <see cref="DefaultPollInterval"/> until disposed.
/// </summary>
public sealed class PollingEventSubscription : IEventSubscription
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
    private int _started;   // 0 = not started, 1 = started — guards against double-start
    private int _disposed;  // 0 = live, 1 = disposed — guards against concurrent DisposeAsync

    /// <summary>Initialises the subscription but does not start polling. Call <see cref="StartAsync"/> to begin delivery.</summary>
    /// <param name="adapter">The event-store adapter used to read events.</param>
    /// <param name="id">The stream to subscribe to.</param>
    /// <param name="from">The position (inclusive) from which to begin catch-up.</param>
    /// <param name="handler">Callback invoked for each delivered event.</param>
    /// <param name="pollInterval">Interval between poll cycles once catch-up is complete.</param>
    public PollingEventSubscription(
        IEventStoreAdapter adapter,
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(handler);
        _adapter = adapter;
        _id = id;
        _nextPosition = from;
        _handler = handler;
        _pollInterval = pollInterval;
    }

    /// <inheritdoc/>
    public bool IsRunning => _running;

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown when called more than once.</exception>
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("StartAsync has already been called on this subscription.");
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
            _nextPosition = e.Position.Next();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
