using System.Collections.Concurrent;
using Confluent.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Kafka stream consumer that uses consumer-group subscription with dynamic partition assignment.
/// Kafka assigns partitions via rebalancing; this class handles rebalancing without blocking
/// async code (no <c>.GetAwaiter().GetResult()</c>).
/// </summary>
/// <remarks>
/// <strong>Rebalance strategy:</strong><br/>
/// • Assigned callback (sync): enqueues newly assigned partitions into <see cref="_pendingSeeks"/>.<br/>
/// • Poll loop (async): drains the queue before each <c>Consume()</c> call, reads checkpoints async, seeks.<br/>
/// • Revoked callback (sync): calls synchronous <c>consumer.Commit()</c> (Confluent.Kafka sync API),
///   then removes revoked partitions from tracking.
/// </remarks>
public sealed class KafkaConsumerGroupConsumer : KafkaConsumerBase
{
    private readonly KafkaConsumerGroupOptions _options;

    // For the test path this is the field-initialized queue.
    // For the production path this is replaced with the queue captured by the rebalance closures.
    private readonly ConcurrentQueue<List<TopicPartition>> _pendingSeeks = new();

    /// <inheritdoc/>
    public override string ConsumerId => _options.ConsumerId;

    /// <inheritdoc/>
    protected override IReadOnlyList<int> GetAssignedPartitions()
        => [.. LastPositionPerPartition.Keys];

    // ── Production constructor ────────────────────────────────────────────────

    /// <summary>Production constructor — builds Kafka consumer internally with rebalance handlers.</summary>
    public KafkaConsumerGroupConsumer(
        KafkaConsumerGroupOptions options,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        IDeadLetterStore? deadLetterStore = null)
        : this(Validated(options), checkpointStore, serializer, registry,
               new RebalanceBox(), deadLetterStore)
    {
    }

    /// <summary>Chaining constructor: wires a RebalanceBox to enable circular handler-to-instance binding.</summary>
    private KafkaConsumerGroupConsumer(
        KafkaConsumerGroupOptions options,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        RebalanceBox box,
        IDeadLetterStore? deadLetterStore)
        : base(
            BuildConsumerWithHandlers(options, box),
            checkpointStore, serializer, registry,
            options.Topic, options.PollTimeout, options.ConsumerOptions, deadLetterStore,
            ownsConsumer: true)
    {
        _options = options;
        // After base construction, wire the box to the actual instance queues and dictionaries.
        // Callbacks that fire hereafter will operate directly on the live instance state.
        _pendingSeeks = box.PendingSeeks;
        box.PartitionTracking = LastPositionPerPartition;
    }

    // ── Internal test constructor ─────────────────────────────────────────────

    /// <summary>Internal constructor for testing — accepts an injected IConsumer.</summary>
    internal KafkaConsumerGroupConsumer(
        KafkaConsumerGroupOptions options,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        IConsumer<string, byte[]> consumer,
        IDeadLetterStore? deadLetterStore = null)
        : base(consumer, checkpointStore, serializer, registry,
               Validated(options).Topic,
               options.PollTimeout, options.ConsumerOptions, deadLetterStore)
    {
        _options = options;
    }

    private static KafkaConsumerGroupOptions Validated(KafkaConsumerGroupOptions options)
    {
        (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        return options;
    }

    // ── Test-seam methods ─────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues assigned partitions for async seeking in the poll loop.
    /// Called by the Kafka assigned callback (production) or directly in tests.
    /// </summary>
    internal void SimulatePartitionsAssigned(List<TopicPartition> partitions)
        => _pendingSeeks.Enqueue([.. partitions]);

    /// <summary>
    /// Commits synchronously and removes revoked partitions from tracking.
    /// Called by the Kafka revoked callback (production) or directly in tests.
    /// </summary>
    internal void SimulatePartitionsRevoked(List<TopicPartition> partitions)
    {
        try { _consumer.Commit(); } catch (KafkaException) { /* best-effort */ }
        foreach (var tp in partitions)
            LastPositionPerPartition.TryRemove(tp.Partition.Value, out _);
    }

    // ── KafkaConsumerBase overrides ───────────────────────────────────────────

    /// <inheritdoc/>
    protected override Task InitializeAsync(CancellationToken ct)
    {
        _consumer.Subscribe(_options.Topic);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drains pending partition assignments from <see cref="_pendingSeeks"/> before each poll.
    /// For each assigned partition, reads the checkpoint and seeks to that offset (or beginning).
    /// This runs in the async poll loop, so awaiting is safe here.
    /// </summary>
    protected override async Task DrainPendingSeeksAsync(CancellationToken ct)
    {
        while (_pendingSeeks.TryDequeue(out var partitions))
        {
            foreach (var tp in partitions)
            {
                var partition = tp.Partition.Value;
                var pos = await _checkpointStore.ReadAsync(
                    CheckpointKey(ConsumerId, partition), ct).ConfigureAwait(false);
                var offset = pos.HasValue ? new Offset(pos.Value.Value) : Offset.Beginning;

                _consumer.Seek(new TopicPartitionOffset(tp.Topic, tp.Partition, offset));

                // Register in tracking so GetAssignedPartitions() sees this partition immediately.
                LastPositionPerPartition.TryAdd(partition, pos ?? StreamPosition.Start);
            }
        }
    }

    // ── Consumer builder (production) ─────────────────────────────────────────

    /// <summary>
    /// Mutable box that bridges the rebalance handler closures (created at builder time, before
    /// the subclass constructor body runs) to the instance state (available only after the base
    /// constructor completes). The box is populated immediately after <c>base()</c> returns.
    /// </summary>
    private sealed class RebalanceBox
    {
        /// <summary>Queue that the assigned handler enqueues partitions into.</summary>
        public readonly ConcurrentQueue<List<TopicPartition>> PendingSeeks = new();

        /// <summary>Partition-tracking dictionary used by the revoked handler. Set after base() completes.</summary>
        public ConcurrentDictionary<int, StreamPosition>? PartitionTracking { get; set; }
    }

    /// <summary>
    /// Builds a Kafka consumer with rebalance handlers that operate via the given <paramref name="box"/>.
    /// The box's <see cref="RebalanceBox.PartitionTracking"/> property is null until the constructor body
    /// wires it; the revoked handler guards against null.
    /// </summary>
    private static IConsumer<string, byte[]> BuildConsumerWithHandlers(
        KafkaConsumerGroupOptions options,
        RebalanceBox box)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId          = options.GroupId,
            AutoOffsetReset  = AutoOffsetReset.Earliest,
            EnableAutoCommit      = false,
            EnableAutoOffsetStore = false, // StoreOffset is called manually in ProcessBatchAsync
        };

        return new ConsumerBuilder<string, byte[]>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
                box.PendingSeeks.Enqueue([.. partitions]))
            .SetPartitionsRevokedHandler((c, partitions) =>
            {
                try { c.Commit(); } catch (KafkaException) { /* best-effort */ }
                if (box.PartitionTracking is { } tracking)
                    foreach (var tp in partitions)
                        tracking.TryRemove(tp.Partition.Value, out _);
            })
            .Build();
    }
}
