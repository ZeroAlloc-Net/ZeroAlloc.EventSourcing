using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Mediator;

/// <summary>
/// IHostedService that subscribes to LIVE committed events on a single stream and republishes
/// those implementing <see cref="INotification"/> via the generator-emitted
/// <see cref="INotificationDispatcher"/>. History replays are filtered out positionally by
/// subscribing from <see cref="StreamPosition.End"/>.
/// </summary>
internal sealed class EventStoreMediatorBridge : IHostedService, IAsyncDisposable
{
    private readonly IEventStore _store;
    private readonly INotificationDispatcher _dispatch;
    private readonly ILogger<EventStoreMediatorBridge> _logger;
    private readonly StreamId _streamId;
    private IEventSubscription? _subscription;

    public EventStoreMediatorBridge(
        IEventStore store,
        INotificationDispatcher dispatch,
        ILogger<EventStoreMediatorBridge> logger,
        StreamId streamId)
    {
        _store = store;
        _dispatch = dispatch;
        _logger = logger;
        _streamId = streamId;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await _store
            .SubscribeAsync(_streamId, StreamPosition.End, OnEventAsync, cancellationToken)
            .ConfigureAwait(false);
        await _subscription.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
            _subscription = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
            _subscription = null;
        }
    }

    private async ValueTask OnEventAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Event is not INotification)
        {
            _logger.LogDebug(
                "Skipping non-notification event of type {EventType} on stream {StreamId}",
                envelope.Event?.GetType().FullName, _streamId);
            return;
        }

        try
        {
            await _dispatch.DispatchAsync(envelope.Event, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Mediator handler threw for event {EventType} on stream {StreamId}; swallowing to preserve subscription",
                envelope.Event.GetType().FullName, _streamId);
        }
    }
}
