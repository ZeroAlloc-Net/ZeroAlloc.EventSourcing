using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using System.Text;
using System.Threading;

namespace ZeroAlloc.EventSourcing.Generators;

/// <summary>
/// Discovery helpers shared by the generators. The syntax provider visits declarations, not types,
/// so a class split over several <c>partial</c> declarations is seen once per declaration. These
/// helpers let each generator emit exactly once per type, under a hint name that is unique per type.
/// </summary>
internal static class PartialTypeDeclarations
{
    /// <summary>Syntactic predicate: a partial class declaration with a base type list.</summary>
    internal static bool IsPartialClassWithBaseSyntax(SyntaxNode node)
        => node is ClassDeclarationSyntax cls
            && cls.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))
            && cls.BaseList?.Types.Count > 0;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="declaration"/> is the first declaration of
    /// <paramref name="symbol"/> that passes <see cref="IsPartialClassWithBaseSyntax"/>.
    /// Every other declaration of the same type returns <c>false</c>, so the type is emitted once
    /// whichever of its declarations repeat the base list.
    /// </summary>
    internal static bool IsPrimaryDeclaration(
        ClassDeclarationSyntax declaration, INamedTypeSymbol symbol, CancellationToken cancellationToken)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var node = reference.GetSyntax(cancellationToken);
            if (!IsPartialClassWithBaseSyntax(node)) continue;
            return node.SyntaxTree == declaration.SyntaxTree && node.Span == declaration.Span;
        }
        return false;
    }

    /// <summary>
    /// Builds a hint-name prefix that is unique per type: the namespace, every containing type and
    /// the type itself, joined with dots, with a backtick and the arity after each generic type.
    /// For a non-generic top-level type this is <c>Namespace.ClassName</c>.
    /// </summary>
    internal static string HintPrefix(INamedTypeSymbol symbol)
    {
        var sb = new StringBuilder();
        if (!symbol.ContainingNamespace.IsGlobalNamespace)
            sb.Append(symbol.ContainingNamespace.ToDisplayString());
        AppendType(sb, symbol);
        return sb.ToString();
    }

    private static void AppendType(StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.ContainingType is not null)
            AppendType(sb, type.ContainingType);
        if (sb.Length > 0) sb.Append('.');
        sb.Append(type.Name);
        if (type.Arity > 0) sb.Append('`').Append(type.Arity);
    }
}
