namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Projection that filters which events are processed.
/// Only events matching IncludeEvent predicate are applied to the read model.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FilteredProjection{TReadModel}"/> extends <see cref="Projection{TReadModel}"/>
/// to add selective event processing. By implementing <see cref="IncludeEvent"/>, subclasses
/// can choose which events trigger state transitions and which are silently skipped.
/// </para>
/// <para>
/// This is useful for scenarios where:
/// <list type="bullet">
/// <item><description>
/// A projection only cares about a subset of events in the stream
/// </description></item>
/// <item><description>
/// Multiple projections share the same event stream but track different aggregates
/// </description></item>
/// <item><description>
/// You want to filter on event type, tenant ID, aggregate ID, or any custom predicate
/// </description></item>
/// </list>
/// </para>
/// <para>
/// The filtering decision is made in <see cref="HandleAsync"/> before the protected Apply
/// method is called, so excluded events are never processed by your Apply implementation.
/// </para>
/// </remarks>
/// <typeparam name="TReadModel">The read model type. Can be a record, struct, or class.</typeparam>
/// <example>
/// <code>
/// // Define events
/// public record OrderPlaced(string OrderId, decimal Amount);
/// public record OrderShipped(string TrackingCode);
/// public record InventoryReserved(string SkuId, int Quantity);
///
/// // Define the read model (only cares about orders, not inventory)
/// public record OrderSummary(string OrderId, decimal Amount, string? TrackingCode);
///
/// // Implement a filtered projection that only processes order events
/// public class OrderProjection : FilteredProjection&lt;OrderSummary&gt;
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
///
///     protected override bool IncludeEvent(EventEnvelope @event)
///     {
///         // Skip inventory events; only process order events
///         return @event.Event is not InventoryReserved;
///     }
/// }
///
/// // Usage: only order events are processed
/// var projection = new OrderProjection();
/// await eventStore.ReadAsync(streamId, async envelope =>
/// {
///     await projection.HandleAsync(envelope);
/// });
/// </code>
/// </example>
public abstract class FilteredProjection<TReadModel> : Projection<TReadModel>
{
    /// <summary>
    /// Override to filter which events trigger event processing.
    /// Return true to process the event and update the read model, false to skip it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is called for every event passed to <see cref="HandleAsync"/>.
    /// The decision should be fast and deterministic. Avoid side effects or I/O.
    /// </para>
    /// <para>
    /// Common patterns:
    /// <list type="bullet">
    /// <item><description>
    /// Type check: <c>@event.Event is OrderEvent</c>
    /// </description></item>
    /// <item><description>
    /// Aggregate ID: <c>@event.StreamId.Value == expectedId</c>
    /// </description></item>
    /// <item><description>
    /// Custom predicate: <c>@event.Event is PaymentProcessed p &amp;&amp; p.Amount > 0</c>
    /// </description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="event">The event envelope to evaluate.</param>
    /// <returns>true if the event should be processed; false to skip it.</returns>
    protected abstract bool IncludeEvent(EventEnvelope @event);

    /// <summary>
    /// Processes an inbound event: checks for cancellation, filters based on
    /// <see cref="IncludeEvent"/>, and if included, applies the event to the read model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filtering decision happens before event processing.
    /// If <see cref="IncludeEvent"/> returns false, the protected Apply method is never invoked
    /// and the read model remains unchanged.
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

        if (IncludeEvent(@event))
        {
            Current = Apply(Current, @event);
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
