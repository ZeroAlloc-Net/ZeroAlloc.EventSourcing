namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Stateful stream consumer with position tracking, retry logic, and batch processing.
/// </summary>
public sealed class StreamConsumer : IStreamConsumer
{
    private readonly IEventStore _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly StreamConsumerOptions _options;
    private StreamPosition? _currentPosition = null;

    /// <inheritdoc/>
    public string ConsumerId { get; }

    /// <summary>
    /// Creates a new stream consumer with the specified configuration.
    /// </summary>
    /// <param name="eventStore">The event store to read events from.</param>
    /// <param name="checkpointStore">The checkpoint store for position tracking.</param>
    /// <param name="consumerId">Unique identifier for this consumer.</param>
    /// <param name="options">Configuration options (defaults to new StreamConsumerOptions() if null).</param>
    /// <exception cref="ArgumentNullException">If eventStore or checkpointStore is null.</exception>
    /// <exception cref="ArgumentException">If consumerId is null or whitespace.</exception>
    public StreamConsumer(
        IEventStore eventStore,
        ICheckpointStore checkpointStore,
        string consumerId,
        StreamConsumerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _options = options ?? new StreamConsumerOptions();
        ConsumerId = consumerId;
    }

    /// <inheritdoc/>
    public async Task ConsumeAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        // Read the last checkpoint position, default to start
        var position = await _checkpointStore.ReadAsync(ConsumerId, ct) ?? StreamPosition.Start;
        _currentPosition = position;

        // Continuously process batches until no more events or cancellation requested
        while (!ct.IsCancellationRequested)
        {
            var batch = new List<EventEnvelope>();

            // Read a batch of events from the event store starting at current position
            await foreach (var envelope in _eventStore.ReadAsync(new StreamId("*"), position, ct))
            {
                batch.Add(envelope);
                if (batch.Count >= _options.BatchSize)
                    break;
            }

            // If no events were read, exit the batch processing loop
            if (batch.Count == 0)
                break;

            // Process each event in the batch
            foreach (var envelope in batch)
            {
                // Process event with retry logic
                await ProcessEventWithRetryAsync(handler, envelope, ct);

                // Update position to the event we just processed
                position = envelope.Position;
                _currentPosition = position;

                // Commit position after each event if configured
                if (_options.CommitStrategy == CommitStrategy.AfterEvent)
                    await _checkpointStore.WriteAsync(ConsumerId, position, ct);
            }

            // Commit position after entire batch if configured
            if (_options.CommitStrategy == CommitStrategy.AfterBatch)
                await _checkpointStore.WriteAsync(ConsumerId, position, ct);

            // Continue to next batch loop iteration
        }
    }

    /// <inheritdoc/>
    public async Task<StreamPosition?> GetPositionAsync(CancellationToken ct = default)
    {
        return await _checkpointStore.ReadAsync(ConsumerId, ct);
    }

    /// <inheritdoc/>
    public async Task ResetPositionAsync(StreamPosition position, CancellationToken ct = default)
    {
        // Delete the checkpoint to reset
        await _checkpointStore.DeleteAsync(ConsumerId, ct);

        // If position is not the start, write the new position
        if (position.Value > 0)
            await _checkpointStore.WriteAsync(ConsumerId, position, ct);
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_currentPosition.HasValue)
            await _checkpointStore.WriteAsync(ConsumerId, _currentPosition.Value, ct);
    }

    /// <summary>
    /// Process a single event with retry logic and error handling.
    /// </summary>
    private async Task ProcessEventWithRetryAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        int attemptCount = 0;

        while (true)
        {
            try
            {
                // Attempt to process the event
                await handler(envelope, ct);
                return;
            }
            catch (Exception) when (attemptCount < _options.MaxRetries)
            {
                // Retry if we haven't exceeded max retries
                attemptCount++;
                var delay = _options.RetryPolicy.GetDelay(attemptCount);
                await Task.Delay(delay, ct);
            }
            catch (Exception) when (attemptCount >= _options.MaxRetries)
            {
                // Handle error based on strategy
                switch (_options.ErrorStrategy)
                {
                    case ErrorHandlingStrategy.FailFast:
                        throw;
                    case ErrorHandlingStrategy.Skip:
                        // Skip this event and continue with the next
                        return;
                    case ErrorHandlingStrategy.DeadLetter:
                        throw new NotImplementedException("Dead-letter strategy not yet implemented");
                    default:
                        throw;
                }
            }
        }
    }
}
