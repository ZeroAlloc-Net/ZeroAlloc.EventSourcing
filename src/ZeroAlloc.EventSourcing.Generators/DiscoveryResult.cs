namespace ZeroAlloc.EventSourcing.Generators;

/// <summary>
/// The outcome of discovering one aggregate or projection: either the model to generate from, or a
/// diagnostic that explains why nothing is generated for it.
/// </summary>
internal sealed class DiscoveryResult<TInfo>
    where TInfo : class
{
    private DiscoveryResult(TInfo? info, DiagnosticInfo? diagnostic)
    {
        Info = info;
        Diagnostic = diagnostic;
    }

    public TInfo? Info { get; }
    public DiagnosticInfo? Diagnostic { get; }

    public static DiscoveryResult<TInfo> Generate(TInfo info) => new(info, null);

    public static DiscoveryResult<TInfo> Report(DiagnosticInfo diagnostic) => new(null, diagnostic);

    // Value equality keeps the incremental pipeline cached.
    public override bool Equals(object? obj)
        => obj is DiscoveryResult<TInfo> other
            && Equals(Info, other.Info)
            && Equals(Diagnostic, other.Diagnostic);

    public override int GetHashCode()
        => ((Info?.GetHashCode() ?? 0) * 31) + (Diagnostic?.GetHashCode() ?? 0);
}
