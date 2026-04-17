#pragma warning disable CS1574 // XML comment has cref attribute that could not be resolved
namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Transforms an event of type <typeparamref name="TOld"/> to <typeparamref name="TNew"/>
/// during read. Registered via <see cref="EventSourcingBuilderExtensions.AddUpcaster{TOld,TNew}"/>.
/// </summary>
/// <typeparam name="TOld">The old (stored) event type.</typeparam>
/// <typeparam name="TNew">The new (current) event type.</typeparam>
public interface IEventUpcaster<TOld, TNew>
{
    /// <summary>Transforms <paramref name="oldEvent"/> into the current event shape.</summary>
    TNew Upcast(TOld oldEvent);
}
