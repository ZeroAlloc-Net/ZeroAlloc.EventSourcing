using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAlloc.EventSourcing.Generators;

/// <summary>Diagnostics reported by the aggregate and projection generators.</summary>
internal static class Diagnostics
{
    private const string Category = "ZeroAlloc.EventSourcing.Generators";

    /// <summary>
    /// A nested aggregate or projection whose containing type is not <c>partial</c>. The generated code
    /// has to reopen every containing type, so nothing is generated for it.
    /// </summary>
    public static readonly DiagnosticDescriptor ZAES005_ContainingTypeNotPartial = new(
        id: "ZAES005",
        title: "Containing type of an aggregate or projection is not partial",
        messageFormat: "Aggregate or projection '{0}' is nested in '{1}', which is not partial, "
            + "so no code is generated for it. Declare every containing type partial.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/diagnostics.md#zaes005");

    /// <summary>
    /// A generic aggregate or projection, or an aggregate whose event types use a type parameter of a
    /// containing type. The event type registry sits at namespace level and cannot name open types, so
    /// nothing is generated for it.
    /// </summary>
    public static readonly DiagnosticDescriptor ZAES006_GenericType = new(
        id: "ZAES006",
        title: "Generic aggregate or projection is not generated",
        messageFormat: "Aggregate or projection '{0}' is generic, or handles events whose types use a type "
            + "parameter, so no code is generated for it. Write its dispatch method by hand.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/diagnostics.md#zaes006");
}

/// <summary>
/// A diagnostic in a form that the incremental pipeline can compare by value, so a discovery result
/// that carries one is still cached. It keeps the file path and spans rather than a <see cref="Location"/>,
/// which would hold on to the syntax tree and compare by reference.
/// </summary>
internal sealed class DiagnosticInfo
{
    private readonly string _filePath;
    private readonly Microsoft.CodeAnalysis.Text.TextSpan _span;
    private readonly Microsoft.CodeAnalysis.Text.LinePositionSpan _lineSpan;

    public DiagnosticInfo(DiagnosticDescriptor descriptor, Location location, params string[] arguments)
    {
        Descriptor = descriptor;
        _filePath = location.SourceTree?.FilePath ?? string.Empty;
        _span = location.SourceSpan;
        _lineSpan = location.GetLineSpan().Span;
        Arguments = arguments;
    }

    public DiagnosticDescriptor Descriptor { get; }
    public IReadOnlyList<string> Arguments { get; }

    public Diagnostic ToDiagnostic()
        => Diagnostic.Create(
            Descriptor,
            Location.Create(_filePath, _span, _lineSpan),
            Arguments.Cast<object>().ToArray());

    public override bool Equals(object? obj)
        => obj is DiagnosticInfo other
            && Descriptor.Id == other.Descriptor.Id
            && _filePath == other._filePath
            && _span == other._span
            && _lineSpan == other._lineSpan
            && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + Descriptor.Id.GetHashCode();
        hash = hash * 31 + _filePath.GetHashCode();
        hash = hash * 31 + _span.GetHashCode();
        foreach (var argument in Arguments)
            hash = hash * 31 + argument.GetHashCode();
        return hash;
    }
}
