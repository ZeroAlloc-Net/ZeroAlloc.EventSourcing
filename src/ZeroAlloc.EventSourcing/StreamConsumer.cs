namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Stateful stream consumer with position tracking, retry logic, and batch processing.
/// </summary>
public sealed class StreamConsumer : IStreamConsumer
{
    private readonly IEventStore _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly StreamConsumerOptions _options;
    private readonly StreamId _streamId;
    private readonly IDeadLetterStore? _deadLetterStore;
    private StreamPosition? _currentPosition;

    /// <inheritdoc/>
    public string ConsumerId { get; }

    /// <summary>Initializes a new instance of the StreamConsumer class.</summary>
    /// <param name="eventStore">The event store to read events from.</param>
    /// <param name="checkpointStore">The checkpoint store for position tracking.</param>
    /// <param name="consumerId">Unique identifier for this consumer.</param>
    /// <param name="options">Configuration options (uses defaults if null).</param>
    /// <param name="streamId">The stream to consume from (defaults to "*" for global stream).</param>
    /// <param name="deadLetterStore">Optional dead-letter store used when <see cref="StreamConsumerOptions.ErrorStrategy"/> is <see cref="ErrorHandlingStrategy.DeadLetter"/>.</param>
    public StreamConsumer(
        IEventStore eventStore,
        ICheckpointStore checkpointStore,
        string consumerId,
        StreamConsumerOptions? options = null,
        StreamId? streamId = null,
        IDeadLetterStore? deadLetterStore = null)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _options = options ?? new StreamConsumerOptions();
        _streamId = streamId ?? new StreamId("*");
        _deadLetterStore = deadLetterStore;
        ConsumerId = consumerId;
    }

    /// <inheritdoc/>
    public async Task ConsumeAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        // Read starting position from checkpoint
        var position = await _checkpointStore.ReadAsync(ConsumerId, cancellationToken).ConfigureAwait(false) ?? StreamPosition.Start;
        _currentPosition = position;

        // Consume events in batches
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = new List<EventEnvelope>();

            // Fetch batch of events
            await foreach (var envelope in _eventStore.ReadAsync(_streamId, position, cancellationToken).ConfigureAwait(false))
            {
                batch.Add(envelope);
                if (batch.Count >= _options.BatchSize)
                    break;
            }

            if (batch.Count == 0)
                break; // No more events

            // Process batch
            foreach (var envelope in batch)
            {
                await ProcessEventWithRetryAsync(handler, envelope, cancellationToken).ConfigureAwait(false);
                position = envelope.Position;
                _currentPosition = position;

                if (_options.CommitStrategy == CommitStrategy.AfterEvent)
                    await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken).ConfigureAwait(false);
            }

            // Commit after batch if configured
            if (_options.CommitStrategy == CommitStrategy.AfterBatch)
                await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<StreamPosition?> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        return await _checkpointStore.ReadAsync(ConsumerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ResetPositionAsync(StreamPosition position, CancellationToken cancellationToken = default)
    {
        await _checkpointStore.DeleteAsync(ConsumerId, cancellationToken).ConfigureAwait(false);
        if (position.Value > 0)
            await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_currentPosition.HasValue)
            await _checkpointStore.WriteAsync(ConsumerId, _currentPosition.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessEventWithRetryAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        int attemptCount = 0;

        while (true)
        {
            try
            {
                await handler(envelope, cancellationToken).ConfigureAwait(false);
                return; // Success
            }
            catch (Exception) when (attemptCount < _options.MaxRetries)
            {
                attemptCount++;
                var delay = _options.RetryPolicy.GetDelay(attemptCount);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attemptCount >= _options.MaxRetries)
            {
                // Retries exhausted
                switch (_options.ErrorStrategy)
                {
                    case ErrorHandlingStrategy.FailFast:
                        throw;
                    case ErrorHandlingStrategy.Skip:
                        // Log and continue silently
                        return;
                    case ErrorHandlingStrategy.DeadLetter:
                        if (_deadLetterStore == null)
                            throw new InvalidOperationException(
                                "ErrorHandlingStrategy.DeadLetter requires a dead-letter store. " +
                                "Pass an IDeadLetterStore to the StreamConsumer constructor.");
                        await _deadLetterStore.WriteAsync(ConsumerId, envelope, ex, cancellationToken).ConfigureAwait(false);
                        return; // skip this event and continue consuming
                    default:
                        throw;
                }
            }
        }
    }
}
