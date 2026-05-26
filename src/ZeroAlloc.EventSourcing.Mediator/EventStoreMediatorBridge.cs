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
/// <remarks>
/// Public so the generator-emitted <c>EventSourcingBuilderMediatorExtensions.PublishViaMediator</c>
/// can construct it. End users normally call <c>.PublishViaMediator(streamId)</c> — direct
/// construction is allowed but uncommon.
/// </remarks>
public sealed class EventStoreMediatorBridge : IHostedService, IAsyncDisposable
{
    private readonly IEventStore _store;
    private readonly INotificationDispatcher _dispatch;
    private readonly ILogger<EventStoreMediatorBridge> _logger;
    private readonly StreamId _streamId;
    private IEventSubscription? _subscription;

    /// <summary>
    /// Initializes a new bridge for a single stream. Normally constructed by the
    /// generator-emitted <c>PublishViaMediator</c> extension; direct construction is allowed.
    /// </summary>
    /// <param name="store">The underlying event store providing LIVE subscriptions.</param>
    /// <param name="dispatch">The generator-emitted dispatcher routing events to Mediator.</param>
    /// <param name="logger">Logger for skip and handler-failure diagnostics.</param>
    /// <param name="streamId">The single stream this bridge subscribes to.</param>
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

    /// <summary>
    /// Subscribes to LIVE events on the configured stream starting at
    /// <see cref="StreamPosition.End"/>, so prior history is never replayed through Mediator.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token from the host lifecycle.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await _store
            .SubscribeAsync(_streamId, StreamPosition.End, OnEventAsync, cancellationToken)
            .ConfigureAwait(false);
        await _subscription.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the bridge by disposing the active subscription. Safe to call repeatedly.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token from the host lifecycle.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
            _subscription = null;
        }
    }

    /// <summary>
    /// Disposes the underlying subscription. Equivalent to <see cref="StopAsync"/>.
    /// </summary>
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
