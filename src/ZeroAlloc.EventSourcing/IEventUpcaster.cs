namespace ZeroAlloc.EventSourcing;

#pragma warning disable CS1574 // XML comment has cref attribute that could not be resolved
/// <summary>
/// Transforms an event of type <typeparamref name="TOld"/> to <typeparamref name="TNew"/>
/// during read. Registered via <see cref="EventSourcingBuilderExtensions.AddUpcaster{TOld,TNew}"/>.
/// </summary>
/// <typeparam name="TOld">The old (stored) event type.</typeparam>
/// <typeparam name="TNew">The new (current) event type.</typeparam>
#pragma warning restore CS1574
public interface IEventUpcaster<TOld, TNew>
{
    /// <summary>Transforms <paramref name="oldEvent"/> into the current event shape.</summary>
    /// <returns>The event expressed as <typeparamref name="TNew"/>.</returns>
    TNew Upcast(TOld oldEvent);
}
