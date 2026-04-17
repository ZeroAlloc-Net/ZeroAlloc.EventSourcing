namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Default <see cref="IUpcasterPipeline"/> implementation. Walks the registered
/// <see cref="UpcasterRegistration"/> chain until no further upcaster is available.
/// </summary>
public sealed class UpcasterPipeline : IUpcasterPipeline
{
    private readonly Dictionary<Type, (Func<object, object> Apply, Type ToType)> _chain;
    private const int MaxDepth = 32;

    /// <summary>Initializes a pipeline from the given set of registrations.</summary>
    public UpcasterPipeline(IEnumerable<UpcasterRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _chain = registrations.ToDictionary(
            r => r.FromType,
            r => (r.Apply, r.ToType));
    }

    /// <inheritdoc/>
    public bool TryUpcast(object oldEvent, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? upgraded)
    {
        ArgumentNullException.ThrowIfNull(oldEvent);

        var current = oldEvent;
        var depth = 0;

        while (_chain.TryGetValue(current.GetType(), out var entry))
        {
            if (++depth > MaxDepth)
                throw new InvalidOperationException(
                    $"Upcaster chain exceeded maximum depth of {MaxDepth}. " +
                    "Check for a cycle in upcaster registrations.");

            current = entry.Apply(current);
        }

        upgraded = current;
        return !ReferenceEquals(current, oldEvent);
    }
}
