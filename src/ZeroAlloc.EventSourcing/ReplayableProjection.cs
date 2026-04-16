namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Abstract projection that adds the ability to rebuild state from the event store.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReplayableProjection{TReadModel}"/> extends <see cref="Projection{TReadModel}"/>
/// to support rebuilding: clearing the current read model and replaying all events from the
/// event store to reconstruct a fresh state. This is useful for:
/// <list type="bullet">
/// <item><description>Recovering from corrupted or inconsistent projection state</description></item>
/// <item><description>Migrating projection logic while keeping the event stream intact</description></item>
/// <item><description>Periodic consistency checks and state repairs</description></item>
/// </list>
/// </para>
/// <para>
/// Subclasses must implement <see cref="GetProjectionKey"/> to provide a stable identifier
/// for storing projection state in <see cref="IProjectionStore"/>.
/// </para>
/// </remarks>
/// <typeparam name="TReadModel">The read model type. Can be a record, struct, or class.</typeparam>
/// <example>
/// <code>
/// public sealed class OrderProjection : ReplayableProjection&lt;OrderSummary&gt;
/// {
///     private readonly IProjectionStore _store;
///     private readonly IEventStore _eventStore;
///     private readonly StreamId _streamId;
///
///     public OrderProjection(IProjectionStore store, IEventStore eventStore, StreamId streamId)
///     {
///         _store = store;
///         _eventStore = eventStore;
///         _streamId = streamId;
///         Current = new OrderSummary(string.Empty, 0m, null);
///     }
///
///     public override string GetProjectionKey() => $"OrderProjection-{_streamId}";
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
/// }
///
/// // Usage: Rebuild state from events
/// var projection = new OrderProjection(store, eventStore, streamId);
/// await projection.RebuildAsync();
/// // projection.Current now contains the full reconstructed state
/// </code>
/// </example>
public abstract class ReplayableProjection<TReadModel> : Projection<TReadModel>
{
    /// <summary>
    /// Gets the unique key for this projection used to store/retrieve state from <see cref="IProjectionStore"/>.
    /// </summary>
    /// <remarks>
    /// Must return a stable, consistent identifier. Recommended format: "ClassName-AggregateId" or similar.
    /// </remarks>
    /// <returns>The projection key.</returns>
    public abstract string GetProjectionKey();

    /// <summary>
    /// Rebuilds the projection by clearing state and replaying all events from the event store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method:
    /// <list type="number">
    /// <item><description>Resets <see cref="Projection{TReadModel}.Current"/> to its default value</description></item>
    /// <item><description>Replays all events from the event store via the provided eventStore</description></item>
    /// <item><description>Saves the rebuilt state to the projection store</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The caller is responsible for providing both the event store and projection store instances.
    /// </para>
    /// </remarks>
    /// <param name="store">The projection store where rebuilt state is saved.</param>
    /// <param name="streamId">The stream to replay events from.</param>
    /// <param name="eventStore">The event store containing the events to replay.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public async ValueTask RebuildAsync(IProjectionStore store, StreamId streamId, IEventStore eventStore, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Clear the current state
        Current = default!;

        // Replay all events from the store
        await foreach (var @event in eventStore.ReadAsync(streamId, StreamPosition.Start, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            await HandleAsync(@event, ct).ConfigureAwait(false);
        }

        // Serialize and save the rebuilt state
        var key = GetProjectionKey();
        var serialized = System.Text.Json.JsonSerializer.Serialize(Current);
        await store.SaveAsync(key, serialized, ct).ConfigureAwait(false);
    }
}
