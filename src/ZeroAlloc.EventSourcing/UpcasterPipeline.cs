namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Default <see cref="IUpcasterPipeline"/> implementation. Walks the registered
/// <see cref="UpcasterRegistration"/> chain until no further upcaster is available.
/// </summary>
public sealed class UpcasterPipeline : IUpcasterPipeline
{
    private readonly Dictionary<Type, (Func<object, object> Apply, Type ToType)> _chain;
    private const int MaxDepth = 32;

    /// <summary>Initialises a pipeline from the given set of registrations.</summary>
    public UpcasterPipeline(IEnumerable<UpcasterRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var list = registrations.ToList();
        var duplicates = list
            .GroupBy(r => r.FromType)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.FullName ?? g.Key.Name)
            .ToList();

        if (duplicates.Count > 0)
            throw new ArgumentException(
                $"Duplicate upcaster registrations for type(s): {string.Join(", ", duplicates)}. " +
                "Only one upcaster may be registered per source type.",
                nameof(registrations));

        _chain = list.ToDictionary(
            r => r.FromType,
            r => (r.Apply, r.ToType));
    }

    /// <inheritdoc/>
    public bool TryUpcast(object oldEvent, out object upgraded)
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
