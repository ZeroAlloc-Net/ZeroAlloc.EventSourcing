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
    private int _currentPartition;

    /// <inheritdoc/>
    public string ConsumerId => _options.ConsumerId ?? _options.GroupId;

    private string CheckpointKey(int partition) => $"{ConsumerId}:p{partition}";

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
            // Read the last checkpoint position per partition, default to start
            var topicPartitions = new List<TopicPartitionOffset>(_options.Partitions.Length);
            foreach (var partition in _options.Partitions)
            {
                var position = await _checkpointStore.ReadAsync(CheckpointKey(partition), ct).ConfigureAwait(false) ?? StreamPosition.Start;
                topicPartitions.Add(new TopicPartitionOffset(_options.Topic, new Partition(partition), new Offset(position.Value)));
            }

            _consumer.Assign(topicPartitions);

            // Continuously process batches until no more events or cancellation requested
            await ProcessBatchesAsync(handler, ct).ConfigureAwait(false);
        }
        finally
        {
            CloseConsumer();
        }
    }

    /// <summary>
    /// Process batches of messages from Kafka until cancellation or no more messages.
    /// </summary>
    private async Task ProcessBatchesAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var batch = PollBatch();

            // If no events were read, exit the batch processing loop
            if (batch.Count == 0)
                break;

            var lastPositionPerPartition = await ProcessBatchAsync(handler, batch, ct).ConfigureAwait(false);

            // Commit position after entire batch if configured
            if (_options.ConsumerOptions.CommitStrategy == CommitStrategy.AfterBatch)
            {
                foreach (var (partition, position) in lastPositionPerPartition)
                    await _checkpointStore.WriteAsync(CheckpointKey(partition), position, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Poll for a batch of messages from Kafka.
    /// </summary>
    private List<ConsumeResult<string, byte[]>> PollBatch()
    {
        var batch = new List<ConsumeResult<string, byte[]>>();
        for (int i = 0; i < _options.ConsumerOptions.BatchSize; i++)
        {
            var result = _consumer.Consume(_options.PollTimeout);
            if (result == null)
                break;  // No more messages at this moment

            batch.Add(result);
        }

        return batch;
    }

    /// <summary>
    /// Process a batch of Kafka messages.
    /// Returns the last processed position per partition for AfterBatch checkpointing.
    /// </summary>
    private async Task<Dictionary<int, StreamPosition>> ProcessBatchAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        List<ConsumeResult<string, byte[]>> batch,
        CancellationToken ct)
    {
        var lastPositionPerPartition = new Dictionary<int, StreamPosition>();

        foreach (var kafkaMessage in batch)
        {
            // Map Kafka message to EventEnvelope
            var envelope = KafkaMessageMapper.ToEnvelope(kafkaMessage, _serializer, _registry);

            // Process event with retry logic
            await ProcessEventWithRetryAsync(handler, envelope, ct).ConfigureAwait(false);

            // Update position to the event we just processed
            var messagePosition = KafkaMessageMapper.ToStreamPosition(kafkaMessage.Offset);
            var messagePartition = kafkaMessage.Partition.Value;
            _currentPosition = messagePosition;
            _currentPartition = messagePartition;
            lastPositionPerPartition[messagePartition] = messagePosition;

            // Commit position after each event if configured
            if (_options.ConsumerOptions.CommitStrategy == CommitStrategy.AfterEvent)
                await _checkpointStore.WriteAsync(CheckpointKey(messagePartition), messagePosition, ct).ConfigureAwait(false);
        }

        return lastPositionPerPartition;
    }

    /// <summary>
    /// Close the Kafka consumer if we own it.
    /// </summary>
    private void CloseConsumer()
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
        }
    }

    /// <inheritdoc/>
    public async Task<StreamPosition?> GetPositionAsync(CancellationToken ct = default)
    {
        return await _checkpointStore.ReadAsync(CheckpointKey(_options.Partitions[0]), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ResetPositionAsync(StreamPosition position, CancellationToken ct = default)
    {
        // Delete and rewrite checkpoints for all partitions
        foreach (var partition in _options.Partitions)
        {
            await _checkpointStore.DeleteAsync(CheckpointKey(partition), ct).ConfigureAwait(false);

            if (position.Value > 0)
                await _checkpointStore.WriteAsync(CheckpointKey(partition), position, ct).ConfigureAwait(false);

            // Seek to the new position in Kafka (only valid if partition is assigned)
            try
            {
                _consumer.Seek(new TopicPartitionOffset(
                    _options.Topic,
                    new Partition(partition),
                    new Offset(position.Value)));
            }
            catch (InvalidOperationException)
            {
                // Partition not assigned yet, will be assigned in next ConsumeAsync call
            }
        }
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_currentPosition.HasValue)
            await _checkpointStore.WriteAsync(CheckpointKey(_currentPartition), _currentPosition.Value, ct).ConfigureAwait(false);
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
                await handler(envelope, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (attemptCount < _options.ConsumerOptions.MaxRetries)
            {
                // Retry if we haven't exceeded max retries
                attemptCount++;
                var delay = _options.ConsumerOptions.RetryPolicy.GetDelay(attemptCount);
                await Task.Delay(delay, ct).ConfigureAwait(false);
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
                        throw new NotSupportedException("Dead-letter strategy not yet implemented");
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
