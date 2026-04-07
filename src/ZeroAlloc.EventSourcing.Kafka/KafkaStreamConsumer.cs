using Confluent.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Kafka-based stream consumer implementing IStreamConsumer.
/// Consumes events from a Kafka topic and applies configurable retry, error handling, and commit strategies.
/// </summary>
public sealed class KafkaStreamConsumer : IStreamConsumer, IDisposable
{
    private readonly KafkaConsumerOptions _options;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IEventSerializer _serializer;
    private readonly IEventTypeRegistry _registry;
    private readonly IConsumer<string, byte[]> _consumer;
    private readonly bool _ownsConsumer;  // true if we created the consumer internally
    private StreamPosition? _currentPosition;

    /// <inheritdoc/>
    public string ConsumerId => _options.ConsumerId ?? _options.GroupId;

    /// <summary>
    /// Creates a new Kafka stream consumer that builds its own IConsumer internally.
    /// </summary>
    /// <param name="options">Kafka and consumer configuration.</param>
    /// <param name="checkpointStore">Checkpoint store for position persistence.</param>
    /// <param name="serializer">Event serializer for deserializing message payloads.</param>
    /// <param name="registry">Event type registry for type resolution.</param>
    /// <exception cref="ArgumentNullException">If any parameter is null.</exception>
    public KafkaStreamConsumer(
        KafkaConsumerOptions options,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        // Validate options
        _options.Validate();

        // Build the Kafka consumer internally
        _consumer = BuildConsumer(_options);
        _ownsConsumer = true;
    }

    /// <summary>
    /// Internal constructor for testing. Allows injection of IConsumer.
    /// </summary>
    internal KafkaStreamConsumer(
        KafkaConsumerOptions options,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        IConsumer<string, byte[]> consumer)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));

        // Validate options
        _options.Validate();

        _ownsConsumer = false;  // don't close/dispose injected consumer
    }

    /// <inheritdoc/>
    public async Task ConsumeAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        try
        {
            // Read the last checkpoint position, default to start
            var position = await _checkpointStore.ReadAsync(ConsumerId, ct) ?? StreamPosition.Start;
            _currentPosition = position;

            // Assign the partition and seek to start position
            _consumer.Assign(new TopicPartitionOffset(
                _options.Topic,
                _options.Partition,
                new Offset(position.Value)));

            // Continuously process batches until no more events or cancellation requested
            while (!ct.IsCancellationRequested)
            {
                var batch = new List<ConsumeResult<string, byte[]>>();

                // Poll up to BatchSize messages
                for (int i = 0; i < _options.ConsumerOptions.BatchSize; i++)
                {
                    var result = _consumer.Consume(_options.PollTimeout);
                    if (result == null)
                        break;  // No more messages at this moment

                    batch.Add(result);
                }

                // If no events were read, exit the batch processing loop
                if (batch.Count == 0)
                    break;

                // Process each message in the batch
                foreach (var kafkaMessage in batch)
                {
                    // Map Kafka message to EventEnvelope
                    var envelope = KafkaMessageMapper.ToEnvelope(kafkaMessage, _serializer, _registry);

                    // Process event with retry logic
                    await ProcessEventWithRetryAsync(handler, envelope, ct);

                    // Update position to the event we just processed
                    var messagePosition = KafkaMessageMapper.ToStreamPosition(kafkaMessage.Offset);
                    _currentPosition = messagePosition;

                    // Commit position after each event if configured
                    if (_options.ConsumerOptions.CommitStrategy == CommitStrategy.AfterEvent)
                        await _checkpointStore.WriteAsync(ConsumerId, messagePosition, ct);
                }

                // Commit position after entire batch if configured
                if (_options.ConsumerOptions.CommitStrategy == CommitStrategy.AfterBatch && _currentPosition.HasValue)
                    await _checkpointStore.WriteAsync(ConsumerId, _currentPosition.Value, ct);

                // Continue to next batch loop iteration
            }
        }
        finally
        {
            // Close the consumer if we own it
            if (_ownsConsumer)
            {
                try
                {
                    _consumer.Close();
                }
                catch (ObjectDisposedException)
                {
                    // Handle already destroyed (e.g., by test container cleanup)
                    // Safe to ignore as the connection is already gone
                }
            }
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

        // Seek to the new position in Kafka (only valid if partition is assigned)
        try
        {
            _consumer.Seek(new TopicPartitionOffset(
                _options.Topic,
                _options.Partition,
                new Offset(position.Value)));
        }
        catch (InvalidOperationException)
        {
            // Partition not assigned yet, will be assigned in next ConsumeAsync call
        }
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
            catch (Exception) when (attemptCount < _options.ConsumerOptions.MaxRetries)
            {
                // Retry if we haven't exceeded max retries
                attemptCount++;
                var delay = _options.ConsumerOptions.RetryPolicy.GetDelay(attemptCount);
                await Task.Delay(delay, ct);
            }
            catch (Exception) when (attemptCount >= _options.ConsumerOptions.MaxRetries)
            {
                // Handle error based on strategy
                switch (_options.ConsumerOptions.ErrorStrategy)
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

    /// <summary>
    /// Builds a Confluent.Kafka IConsumer from KafkaConsumerOptions.
    /// </summary>
    private static IConsumer<string, byte[]> BuildConsumer(KafkaConsumerOptions options)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,  // Manual commit control via CommitStrategy
        };

        return new ConsumerBuilder<string, byte[]>(config).Build();
    }

    /// <summary>
    /// Disposes the consumer if we own it.
    /// </summary>
    public void Dispose()
    {
        if (_ownsConsumer)
        {
            try
            {
                _consumer.Close();
            }
            catch (ObjectDisposedException)
            {
                // Handle already destroyed (e.g., by test container cleanup)
                // Safe to ignore as the connection is already gone
            }

            _consumer.Dispose();
        }
    }
}
