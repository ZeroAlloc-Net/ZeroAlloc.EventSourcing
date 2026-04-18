using System.Collections.Concurrent;
using Confluent.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Abstract base for Kafka stream consumers. Provides the poll loop, batch processing,
/// retry, dead-letter, per-partition checkpoint tracking, and commit logic.
/// Subclasses implement partition assignment (manual or consumer-group).
/// </summary>
public abstract class KafkaConsumerBase : IStreamConsumer, IDisposable
{
    /// <summary>The underlying Kafka consumer. Protected so subclasses can call Subscribe/Assign/Seek/Commit.</summary>
    protected readonly IConsumer<string, byte[]> _consumer;
    private readonly bool _ownsConsumer;

    /// <summary>The checkpoint store. Protected so subclasses can read checkpoints in InitializeAsync.</summary>
    protected readonly ICheckpointStore _checkpointStore;

    private readonly IEventSerializer _serializer;
    private readonly IEventTypeRegistry _registry;
    private readonly IDeadLetterStore? _deadLetterStore;
    private readonly string _topic;
    private readonly TimeSpan _pollTimeout;
    private readonly StreamConsumerOptions _options;

    /// <summary>Per-partition last-processed position. Updated after each message.</summary>
    protected readonly ConcurrentDictionary<int, StreamPosition> LastPositionPerPartition = new();

    /// <inheritdoc/>
    public abstract string ConsumerId { get; }

    /// <summary>Returns the currently assigned partition IDs.</summary>
    protected abstract IReadOnlyList<int> GetAssignedPartitions();

    /// <summary>Called once before the poll loop starts. Assign or subscribe partitions here.</summary>
    protected abstract Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Called synchronously from a Kafka rebalance revoke callback (or on shutdown).
    /// Must not call async code. Use synchronous Confluent.Kafka APIs only.
    /// </summary>
    protected abstract void OnRevoked(IReadOnlyList<TopicPartition> revoked);

    /// <summary>Checkpoint key format shared by all consumers: "{consumerId}:p{partition}"</summary>
    protected static string CheckpointKey(string consumerId, int partition)
        => $"{consumerId}:p{partition}";

    /// <summary>Production constructor — builds a Confluent.Kafka consumer internally.</summary>
    protected KafkaConsumerBase(
        string bootstrapServers,
        string? groupId,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        string topic,
        TimeSpan pollTimeout,
        StreamConsumerOptions options,
        IDeadLetterStore? deadLetterStore = null)
    {
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _serializer      = serializer      ?? throw new ArgumentNullException(nameof(serializer));
        _registry        = registry        ?? throw new ArgumentNullException(nameof(registry));
        _topic           = topic;
        _pollTimeout     = pollTimeout;
        _options         = options ?? new StreamConsumerOptions();
        _deadLetterStore = deadLetterStore;

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId          = groupId,
            AutoOffsetReset  = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        _consumer     = BuildConsumer(config);
        _ownsConsumer = true;
    }

    /// <summary>Internal constructor for testing — accepts an injected IConsumer.</summary>
    protected internal KafkaConsumerBase(
        IConsumer<string, byte[]> consumer,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        string topic,
        TimeSpan pollTimeout,
        StreamConsumerOptions options,
        IDeadLetterStore? deadLetterStore = null)
    {
        _consumer        = consumer        ?? throw new ArgumentNullException(nameof(consumer));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _serializer      = serializer      ?? throw new ArgumentNullException(nameof(serializer));
        _registry        = registry        ?? throw new ArgumentNullException(nameof(registry));
        _topic           = topic;
        _pollTimeout     = pollTimeout;
        _options         = options ?? new StreamConsumerOptions();
        _deadLetterStore = deadLetterStore;
        _ownsConsumer    = false;
    }

    /// <summary>Directly inserts a position into the per-partition tracking dict. For test stubs only.</summary>
    internal void SimulateProcessed(int partition, StreamPosition position)
        => LastPositionPerPartition[partition] = position;

    /// <inheritdoc/>
    public async Task ConsumeAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        await InitializeAsync(ct).ConfigureAwait(false);
        try
        {
            await ProcessBatchesAsync(handler, ct).ConfigureAwait(false);
        }
        finally
        {
            CloseConsumer();
        }
    }

    /// <summary>
    /// Override in subclass to drain pending partition seeks before each Consume() call.
    /// Default implementation is a no-op.
    /// </summary>
    protected virtual Task DrainPendingSeeksAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ProcessBatchesAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await DrainPendingSeeksAsync(ct).ConfigureAwait(false);

            var batch = PollBatch();
            // No messages received within the poll timeout — the batch is complete; exit the loop.
            // This gives a catch-up consumer model: process all available messages, then stop.
            if (batch.Count == 0) break;

            var lastPerPartition = await ProcessBatchAsync(handler, batch, ct).ConfigureAwait(false);

            if (_options.CommitStrategy == CommitStrategy.AfterBatch)
            {
                foreach (var (partition, position) in lastPerPartition)
                    await _checkpointStore.WriteAsync(
                        CheckpointKey(ConsumerId, partition), position, ct).ConfigureAwait(false);
            }
        }
    }

    private List<ConsumeResult<string, byte[]>> PollBatch()
    {
        var batch = new List<ConsumeResult<string, byte[]>>();
        for (int i = 0; i < _options.BatchSize; i++)
        {
            var result = _consumer.Consume(_pollTimeout);
            if (result is null) break;
            batch.Add(result);
        }
        return batch;
    }

    private async Task<Dictionary<int, StreamPosition>> ProcessBatchAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        List<ConsumeResult<string, byte[]>> batch,
        CancellationToken ct)
    {
        var lastPerPartition = new Dictionary<int, StreamPosition>();

        foreach (var msg in batch)
        {
            var envelope  = KafkaMessageMapper.ToEnvelope(msg, _serializer, _registry);
            await ProcessEventWithRetryAsync(handler, envelope, ct).ConfigureAwait(false);

            var pos       = KafkaMessageMapper.ToStreamPosition(msg.Offset);
            var partition = msg.Partition.Value;
            LastPositionPerPartition[partition] = pos;
            lastPerPartition[partition]         = pos;

            if (_options.CommitStrategy == CommitStrategy.AfterEvent)
                await _checkpointStore.WriteAsync(
                    CheckpointKey(ConsumerId, partition), pos, ct).ConfigureAwait(false);
        }

        return lastPerPartition;
    }

    private async Task ProcessEventWithRetryAsync(
        Func<EventEnvelope, CancellationToken, Task> handler,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        int attempts = 0;
        while (true)
        {
            try
            {
                await handler(envelope, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempts < _options.MaxRetries)
            {
                attempts++;
                var delay = _options.RetryPolicy.GetDelay(attempts);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempts >= _options.MaxRetries)
            {
                switch (_options.ErrorStrategy)
                {
                    case ErrorHandlingStrategy.FailFast:
                        throw;
                    case ErrorHandlingStrategy.Skip:
                        return;
                    case ErrorHandlingStrategy.DeadLetter:
                        if (_deadLetterStore is null)
                            throw new InvalidOperationException(
                                "ErrorHandlingStrategy.DeadLetter requires IDeadLetterStore to be registered.");
                        await _deadLetterStore.WriteAsync(ConsumerId, envelope, ex, ct).ConfigureAwait(false);
                        return;
                    default:
                        throw;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<StreamPosition?> GetPositionAsync(CancellationToken ct = default)
    {
        var partitions = GetAssignedPartitions();
        if (partitions.Count == 0) return null;

        StreamPosition? min = null;
        foreach (var partition in partitions)
        {
            var pos = await _checkpointStore.ReadAsync(
                CheckpointKey(ConsumerId, partition), ct).ConfigureAwait(false);
            if (pos is null) return null;   // any unset checkpoint → null
            if (min is null || pos.Value.Value < min.Value.Value) min = pos;
        }
        return min;
    }

    /// <inheritdoc/>
    public async Task ResetPositionAsync(StreamPosition position, CancellationToken ct = default)
    {
        foreach (var partition in GetAssignedPartitions())
        {
            await _checkpointStore.DeleteAsync(CheckpointKey(ConsumerId, partition), ct).ConfigureAwait(false);

            if (position.Value > 0)
                await _checkpointStore.WriteAsync(
                    CheckpointKey(ConsumerId, partition), position, ct).ConfigureAwait(false);

            try
            {
                _consumer.Seek(new TopicPartitionOffset(
                    _topic, new Partition(partition), new Offset(position.Value)));
            }
            catch (InvalidOperationException) { /* not yet assigned — ok */ }
        }
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        foreach (var (partition, position) in LastPositionPerPartition)
            await _checkpointStore.WriteAsync(
                CheckpointKey(ConsumerId, partition), position, ct).ConfigureAwait(false);
    }

    private int _closed; // 0 = open, 1 = closed — Interlocked-guarded

    private void CloseConsumer()
    {
        if (!_ownsConsumer) return;
        if (Interlocked.Exchange(ref _closed, 1) == 1) return; // already closed
        try { _consumer.Close(); } catch (ObjectDisposedException) { }
        _consumer.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => CloseConsumer();

    private static IConsumer<string, byte[]> BuildConsumer(ConsumerConfig config)
        => new ConsumerBuilder<string, byte[]>(config).Build();
}
