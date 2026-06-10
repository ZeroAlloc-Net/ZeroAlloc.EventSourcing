using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing.Mediator;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox;

/// <summary>
/// Hosted service that polls the global event stream and dispatches each
/// <see cref="INotification"/> event (subject to <see cref="OutboxOptions.ExcludedTypes"/>)
/// via the bundled <see cref="INotificationDispatcher"/>. At-least-once delivery;
/// handlers must be idempotent.
/// </summary>
public sealed class OutboxDispatcher : IHostedService, IAsyncDisposable
{
    private readonly IEventStore _store;
    private readonly ICheckpointStore _checkpoints;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IDeadLetterStore? _deadLetters;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    /// <summary>Initialises the dispatcher. Normally constructed by the DI container via <c>AddOutbox</c>.</summary>
    public OutboxDispatcher(
        IEventStore store,
        ICheckpointStore checkpoints,
        INotificationDispatcher dispatcher,
        IDeadLetterStore? deadLetters,
        OutboxOptions options,
        ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _checkpoints = checkpoints;
        _dispatcher = dispatcher;
        _deadLetters = deadLetters;
        _options = options;
        _logger = logger;
    }

    /// <summary>Starts the polling loop on a background <see cref="Task"/>. Returns immediately.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => RunAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Signals cancellation and awaits the background loop to exit gracefully.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopCts is not null)
            _loopCts.Cancel();

        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    /// <summary>Stops the loop and disposes the internal cancellation source.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(default).ConfigureAwait(false);
        _loopCts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var consumerOptions = new StreamConsumerOptions
        {
            BatchSize = _options.BatchSize,
            MaxRetries = _options.MaxRetries,
            RetryPolicy = _options.RetryPolicy,
            ErrorStrategy = _options.ErrorStrategy,
            CommitStrategy = _options.CommitStrategy,
        };
        var consumer = new StreamConsumer(
            _store,
            _checkpoints,
            _options.ConsumerId,
            consumerOptions,
            streamId: new StreamId("*"),
            deadLetterStore: _deadLetters);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await consumer.ConsumeAsync(DispatchAsync, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            // ConsumeAsync returns when the current batch is empty. Poll-sleep, then resume.
            try { await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DispatchAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Event is not INotification)
            return;
        if (_options.ExcludedTypes.Contains(envelope.Event.GetType()))
            return;

        await _dispatcher.DispatchAsync(envelope.Event, ct).ConfigureAwait(false);
    }
}
