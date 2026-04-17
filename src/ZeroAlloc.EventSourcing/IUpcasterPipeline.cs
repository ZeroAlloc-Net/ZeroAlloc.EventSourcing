namespace ZeroAlloc.EventSourcing;

#pragma warning disable CS1574 // XML comment has cref attribute that could not be resolved
/// <summary>
/// Walks a chain of registered <see cref="IEventUpcaster{TOld,TNew}"/> instances and applies
/// them in sequence until no further upcaster is registered for the output type.
/// Implemented by <see cref="UpcasterPipeline"/>; registered automatically by
/// <see cref="ServiceCollectionExtensions.AddEventSourcing"/>.
/// </summary>
#pragma warning restore CS1574
public interface IUpcasterPipeline
{
    /// <summary>
    /// Attempts to upcast <paramref name="oldEvent"/> by walking the registered chain.
    /// Returns <see langword="true"/> and sets <paramref name="upgraded"/> when at least
    /// one upcaster applied; returns <see langword="false"/> when no upcaster is registered
    /// for <paramref name="oldEvent"/>'s type (event passes through unchanged).
    /// </summary>
    bool TryUpcast(object oldEvent, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? upgraded);
}
