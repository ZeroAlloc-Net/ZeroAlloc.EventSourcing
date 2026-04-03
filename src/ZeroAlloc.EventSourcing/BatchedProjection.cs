namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Abstract projection that batches event processing.
/// Accumulates events and flushes them in configurable batches.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BatchedProjection{TReadModel}"/> extends <see cref="FilteredProjection{TReadModel}"/>
/// to support efficient batch processing of events. Instead of applying each event individually,
/// events are accumulated in a list and flushed when the batch size is reached, or manually
/// via <see cref="FlushAsync"/>.
/// </para>
/// <para>
/// This pattern is useful for:
/// <list type="bullet">
/// <item><description>Reducing I/O overhead by batching database writes</description></item>
/// <item><description>Atomic processing of related events</description></item>
/// <item><description>Improving throughput in high-volume projection scenarios</description></item>
/// </list>
/// </para>
/// <para>
/// Subclasses must implement <see cref="FilteredProjection{TReadModel}.IncludeEvent"/> and
/// <see cref="FlushBatchAsync"/> to define filtering and batch flushing logic, respectively.
/// </para>
/// </remarks>
/// <typeparam name="TReadModel">The read model type. Can be a record, struct, or class.</typeparam>
/// <example>
/// <code>
/// public sealed class BatchedOrderProjection : BatchedProjection&lt;OrderSummary&gt;
/// {
///     private readonly IDatabase _db;
///
///     public BatchedOrderProjection(IDatabase db, int batchSize = 10) : base(batchSize)
///     {
///         _db = db;
///     }
///
///     protected override bool IncludeEvent(EventEnvelope @event)
///     {
///         return @event.Event is OrderEvent;
///     }
///
///     protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event)
///     {
///         return @event.Event switch
///         {
///             OrderPlaced e => current with { OrderId = e.OrderId, Amount = e.Amount },
///             OrderShipped e => current with { TrackingCode = e.TrackingCode },
///             _ => current
///         };
///     }
///
///     protected override async ValueTask FlushBatchAsync(IReadOnlyList&lt;EventEnvelope&gt; batch, CancellationToken ct)
///     {
///         // Batch insert projections to database
///         await _db.SaveProjectionsAsync(batch.Select(e => /* transform to projection */));
///     }
/// }
///
/// // Usage: Events are automatically flushed every 10 events
/// var projection = new BatchedOrderProjection(db, batchSize: 10);
/// var subscription = await eventStore.SubscribeAsync(streamId, StreamPosition.Start, projection.HandleAsync);
/// </code>
/// </example>
public abstract class BatchedProjection<TReadModel> : FilteredProjection<TReadModel>
{
    private readonly int _batchSize;
    private readonly List<EventEnvelope> _batch = new();

    /// <summary>
    /// Initializes a new batched projection with the specified batch size.
    /// </summary>
    /// <param name="batchSize">The number of events to accumulate before flushing. Must be >= 1.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="batchSize"/> is less than 1.</exception>
    protected BatchedProjection(int batchSize)
    {
        if (batchSize < 1)
            throw new ArgumentException("Batch size must be at least 1.", nameof(batchSize));

        _batchSize = batchSize;
    }

    /// <summary>
    /// Processes an inbound event: checks filtering, applies the event, and flushes
    /// when the batch reaches its configured size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This override wraps the base implementation to add batching. Events that pass
    /// the <see cref="FilteredProjection{TReadModel}.IncludeEvent"/> filter are added to the batch.
    /// When the batch reaches <c>batchSize</c>, it is flushed automatically.
    /// </para>
    /// <para>
    /// Thread-safety is <strong>not</strong> guaranteed. Ensure only one thread calls
    /// <see cref="HandleAsync"/> at a time, or implement external synchronization in subclasses.
    /// </para>
    /// </remarks>
    /// <param name="event">The event envelope to process.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Apply filtering and state update using base implementation
        if (IncludeEvent(@event))
        {
            Current = Apply(Current, @event);
            _batch.Add(@event);

            // Flush if batch is full
            if (_batch.Count >= _batchSize)
            {
                await FlushAsync(ct);
            }
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Manually flushes any pending batched events.
    /// </summary>
    /// <remarks>
    /// This method should be called at the end of processing or during graceful shutdown
    /// to ensure no events are left unprocessed in the batch.
    /// </remarks>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_batch.Count > 0)
        {
            await FlushBatchAsync(_batch.AsReadOnly(), ct);
            _batch.Clear();
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Override to define how batches of events are persisted or processed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is called when the batch reaches its configured size or when
    /// <see cref="FlushAsync"/> is explicitly invoked. Implement this to atomically
    /// persist the accumulated state changes (e.g., batch database writes, network requests).
    /// </para>
    /// <para>
    /// The batch is read-only and guaranteed to be non-empty. After this method returns,
    /// the batch is cleared internally.
    /// </para>
    /// </remarks>
    /// <param name="batch">The accumulated event envelopes to flush.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    protected abstract ValueTask FlushBatchAsync(IReadOnlyList<EventEnvelope> batch, CancellationToken ct = default);
}
