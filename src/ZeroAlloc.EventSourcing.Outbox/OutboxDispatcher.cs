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

    // TODO(v0.2): lifecycle hardening — guard against double-start (overwriting an undisposed
    // _loopCts), guard against double-stop (cancelling a disposed CTS), and support
    // IHostedService-style StartAsync→StopAsync→StartAsync restart. v0.1 assumes single-shot
    // lifecycle managed by the generic host.

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

    /// <summary>
    /// Background polling loop. On any unhandled exception from the consumer or delay,
    /// the error is logged and re-thrown to fault the hosted-service task. This is a
    /// deliberate fail-fast choice for v0.1: silent retry on a corrupt checkpoint or a
    /// persistent store I/O failure would violate the at-least-once contract without
    /// any operator signal. Re-throwing lets the host's
    /// <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime"/> observe the
    /// failure. Continuing with backoff is also defensible but masks the failure mode;
    /// we lean fail-fast for v0.1 visibility.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "OutboxDispatcher starting (ConsumerId={ConsumerId}, BatchSize={BatchSize}, PollInterval={PollInterval}).",
            _options.ConsumerId, _options.BatchSize, _options.PollInterval);

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
                // ConsumeAsync returns when the current batch is empty. Poll-sleep, then resume.
                await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("OutboxDispatcher stopped cleanly.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxDispatcher background loop crashed; halting.");
                throw;
            }
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
