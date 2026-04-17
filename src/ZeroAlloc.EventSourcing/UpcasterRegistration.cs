namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Describes a single upcasting hop: from <see cref="FromType"/> to <see cref="ToType"/>.
/// Registered in DI by <see cref="EventSourcingBuilderUpcasterExtensions.AddUpcaster{TOld,TNew}"/> and
/// consumed by <see cref="UpcasterPipeline"/> at construction time.
/// </summary>
public sealed class UpcasterRegistration
{
    /// <summary>The CLR type being replaced.</summary>
    public Type FromType { get; }

    /// <summary>The CLR type produced after upcasting.</summary>
    public Type ToType { get; }

    /// <summary>Delegate that applies the upcast. Input is guaranteed to be an instance of <see cref="FromType"/>.</summary>
    public Func<object, object> Apply { get; }

    /// <summary>Initializes an upcaster registration.</summary>
    public UpcasterRegistration(Type fromType, Type toType, Func<object, object> apply)
    {
        FromType = fromType ?? throw new ArgumentNullException(nameof(fromType));
        ToType = toType ?? throw new ArgumentNullException(nameof(toType));
        Apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }
}
