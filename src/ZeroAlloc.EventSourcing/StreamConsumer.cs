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
    private StreamPosition? _currentPosition;

    /// <inheritdoc/>
    public string ConsumerId { get; }

    /// <summary>Initializes a new instance of the StreamConsumer class.</summary>
    /// <param name="eventStore">The event store to read events from.</param>
    /// <param name="checkpointStore">The checkpoint store for position tracking.</param>
    /// <param name="consumerId">Unique identifier for this consumer.</param>
    /// <param name="options">Configuration options (uses defaults if null).</param>
    /// <param name="streamId">The stream to consume from (defaults to "*" for global stream).</param>
    public StreamConsumer(
        IEventStore eventStore,
        ICheckpointStore checkpointStore,
        string consumerId,
        StreamConsumerOptions? options = null,
        StreamId? streamId = null)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _options = options ?? new StreamConsumerOptions();
        _streamId = streamId ?? new StreamId("*");
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
        var position = await _checkpointStore.ReadAsync(ConsumerId, cancellationToken) ?? StreamPosition.Start;
        _currentPosition = position;

        // Consume events in batches
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = new List<EventEnvelope>();

            // Fetch batch of events
            await foreach (var envelope in _eventStore.ReadAsync(_streamId, position, cancellationToken))
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
                await ProcessEventWithRetryAsync(handler, envelope, cancellationToken);
                position = envelope.Position;
                _currentPosition = position;

                if (_options.CommitStrategy == CommitStrategy.AfterEvent)
                    await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken);
            }

            // Commit after batch if configured
            if (_options.CommitStrategy == CommitStrategy.AfterBatch)
                await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<StreamPosition?> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        return await _checkpointStore.ReadAsync(ConsumerId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ResetPositionAsync(StreamPosition position, CancellationToken cancellationToken = default)
    {
        await _checkpointStore.DeleteAsync(ConsumerId, cancellationToken);
        if (position.Value > 0)
            await _checkpointStore.WriteAsync(ConsumerId, position, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_currentPosition.HasValue)
            await _checkpointStore.WriteAsync(ConsumerId, _currentPosition.Value, cancellationToken);
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
                await handler(envelope, cancellationToken);
                return; // Success
            }
            catch (Exception) when (attemptCount < _options.MaxRetries)
            {
                attemptCount++;
                var delay = _options.RetryPolicy.GetDelay(attemptCount);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception) when (attemptCount >= _options.MaxRetries)
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
                        // TODO: Route to dead-letter store
                        throw new NotImplementedException("Dead-letter strategy not yet implemented");
                    default:
                        throw;
                }
            }
        }
    }
}
