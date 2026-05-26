using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZeroAlloc.EventSourcing.Mediator.Generator;

/// <summary>
/// Discovers every type in the consuming compilation that implements
/// <c>ZeroAlloc.Mediator.INotification</c>. Returns null for non-matching nodes; the pipeline
/// filters nulls via Where.
/// </summary>
internal static class NotificationDiscovery
{
    public const string NotificationInterfaceFullName = "ZeroAlloc.Mediator.INotification";

    public static NotificationTypeInfo? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not TypeDeclarationSyntax decl) return null;
        if (ctx.SemanticModel.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol symbol) return null;

        // Skip abstract types and interfaces — they can't be instantiated as events.
        if (symbol.IsAbstract) return null;
        if (symbol.TypeKind == TypeKind.Interface) return null;

        var hasInterface = symbol.AllInterfaces
            .Any(i => i.ToDisplayString() == NotificationInterfaceFullName);
        if (!hasInterface) return null;

        return new NotificationTypeInfo(
            FullyQualifiedName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }
}

internal sealed record NotificationTypeInfo(string FullyQualifiedName);
