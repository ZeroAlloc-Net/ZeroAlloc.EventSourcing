namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Transforms an event of type <typeparamref name="TOld"/> to <typeparamref name="TNew"/>
/// during read. Registered via <see cref="EventSourcingBuilderUpcasterExtensions.AddUpcaster{TOld,TNew}"/>.
/// </summary>
/// <typeparam name="TOld">The old (stored) event type.</typeparam>
/// <typeparam name="TNew">The new (current) event type.</typeparam>
public interface IEventUpcaster<TOld, TNew>
{
    /// <summary>Transforms <paramref name="oldEvent"/> into the current event shape.</summary>
    /// <returns>The event expressed as <typeparamref name="TNew"/>.</returns>
    TNew Upcast(TOld oldEvent);
}
