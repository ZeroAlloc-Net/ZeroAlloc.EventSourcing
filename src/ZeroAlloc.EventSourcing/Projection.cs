namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Base class for event-driven read models. Provides a thin, allocation-efficient layer for
/// consuming events from <see cref="IEventSubscription"/> and building materialized views.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Projection{TReadModel}"/> enables decoupling of read and write models in event-sourced systems.
/// Inherit from this class and implement <see cref="Apply"/> to define how events transform your read model state.
/// </para>
/// <para>
/// The design is intentionally simple:
/// <list type="bullet">
/// <item><description>
/// <see cref="Current"/> holds the materialized read model (initialized to <c>default</c>)
/// </description></item>
/// <item><description>
/// <see cref="Apply"/> routes events to typed handlers and returns the new state
/// </description></item>
/// <item><description>
/// <see cref="HandleAsync"/> integrates with <see cref="IEventSubscription"/> via event callbacks
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Persistence is intentionally out of scope.</strong> Consumers own the entire read model lifecycle:
/// storage, rebuilds, snapshots, schema migrations, etc. This allows projections to remain framework-agnostic
/// and avoids imposing a specific storage technology.
/// </para>
/// <para>
/// <strong>Source generator support:</strong> The source generator (Phase 5, Task 5) can emit typed
/// <c>Apply</c> overloads for each event type, enabling fully compiled, reflection-free dispatch.
/// Until then, manually implement <see cref="Apply"/> using a switch expression or virtual dispatch.
/// </para>
/// </remarks>
/// <typeparam name="TReadModel">The read model type. Can be a record, struct, or class.</typeparam>
/// <example>
/// <code>
/// // Define events
/// public record OrderPlaced(string OrderId, decimal Amount);
/// public record OrderShipped(string TrackingCode);
///
/// // Define the read model
/// public record OrderSummary(string OrderId, decimal Amount, string? TrackingCode);
///
/// // Implement the projection
/// public class OrderProjection : Projection&lt;OrderSummary&gt;
/// {
///     protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event)
///     {
///         return @event.Event switch
///         {
///             OrderPlaced e => current with
///             {
///                 OrderId = e.OrderId,
///                 Amount = e.Amount
///             },
///             OrderShipped e => current with
///             {
///                 TrackingCode = e.TrackingCode
///             },
///             _ => current
///         };
///     }
/// }
///
/// // Usage in a subscription handler
/// var projection = new OrderProjection();
/// await eventStore.ReadAsync(streamId, async envelope =>
/// {
///     await projection.HandleAsync(envelope);
/// });
/// // projection.Current now contains the rebuilt read model
/// </code>
/// </example>
public abstract class Projection<TReadModel>
{
    /// <summary>
    /// The current read model state. Updated by <see cref="HandleAsync"/> via <see cref="Apply"/>.
    /// Initialize this to a sensible default in the constructor if needed (default struct/record
    /// initialization may be insufficient for your domain).
    /// </summary>
    public TReadModel Current { get; protected set; } = default!;

    /// <summary>
    /// Routes an event to the correct state transition. Implement this to define how your
    /// read model evolves as events arrive. Return the updated state (or current state if
    /// the event is unknown/ignored).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the core extension point for the projection. Subclasses should use a switch
    /// expression on <see cref="EventEnvelope.Event"/> to dispatch to typed handlers.
    /// </para>
    /// <para>
    /// In Phase 5 Task 5, a source generator will emit overloads of this method for each
    /// event type in your domain, enabling fully compiled dispatch without reflection.
    /// </para>
    /// </remarks>
    /// <param name="current">The current read model state.</param>
    /// <param name="event">The event envelope containing the domain event and metadata.</param>
    /// <returns>The updated read model, or the unchanged current state if the event is ignored.</returns>
    protected abstract TReadModel Apply(TReadModel current, EventEnvelope @event);

    /// <summary>
    /// Processes an inbound event: checks for cancellation, applies the event to the read model,
    /// and updates <see cref="Current"/>. Designed to be used as a callback for
    /// <see cref="IEventSubscription"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is virtual to support advanced scenarios (e.g., batching, filtering, metrics)
    /// where subclasses may want to wrap or modify event processing behavior. The default
    /// implementation respects cancellation and delegates to <see cref="Apply"/>.
    /// </para>
    /// <para>
    /// Thread-safety is <strong>not</strong> guaranteed. Ensure only one thread calls
    /// <see cref="HandleAsync"/> at a time, or implement external synchronization in subclasses.
    /// </para>
    /// </remarks>
    /// <param name="event">The event envelope to apply to the read model.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/> if the event was processed, or a faulted one if <paramref name="ct"/> signals cancellation.</returns>
    public virtual async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Current = Apply(Current, @event);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
