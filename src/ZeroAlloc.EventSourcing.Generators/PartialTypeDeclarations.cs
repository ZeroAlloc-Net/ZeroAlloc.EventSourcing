using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
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

    /// <summary>
    /// Returns the diagnostic that stops generation for <paramref name="symbol"/>, or <c>null</c> when
    /// it can be generated: ZAES006 when it is generic or one of <paramref name="namespaceLevelTypes"/>
    /// uses a type parameter, otherwise ZAES005 when a containing type is not partial. The diagnostic
    /// is located on the identifier of <paramref name="declaration"/>.
    /// </summary>
    internal static DiagnosticInfo? Check(
        ClassDeclarationSyntax declaration,
        INamedTypeSymbol symbol,
        IEnumerable<ITypeSymbol> namespaceLevelTypes,
        CancellationToken cancellationToken)
    {
        var location = declaration.Identifier.GetLocation();
        // Arity, not IsGenericType: IsGenericType is also true for a type nested in a generic type.
        if (symbol.Arity > 0 || namespaceLevelTypes.Any(UsesTypeParameter))
            return new DiagnosticInfo(Diagnostics.ZAES006_GenericType, location, symbol.ToDisplayString());

        var nonPartial = FirstNonPartialContainingType(symbol, cancellationToken);
        if (nonPartial is not null)
        {
            return new DiagnosticInfo(
                Diagnostics.ZAES005_ContainingTypeNotPartial,
                location,
                symbol.ToDisplayString(),
                nonPartial.ToDisplayString());
        }
        return null;
    }

    /// <summary>
    /// Writes <paramref name="headers"/> as nested declarations, outermost first, with
    /// <paramref name="body"/> inside the innermost one, indented four spaces per level.
    /// </summary>
    internal static void AppendDeclarations(StringBuilder sb, IReadOnlyList<string> headers, IEnumerable<string> body)
    {
        for (var depth = 0; depth < headers.Count; depth++)
        {
            var indent = new string(' ', depth * 4);
            sb.Append(indent).AppendLine(headers[depth]);
            sb.Append(indent).AppendLine("{");
        }
        var bodyIndent = new string(' ', headers.Count * 4);
        foreach (var line in body)
            sb.Append(bodyIndent).AppendLine(line);
        for (var depth = headers.Count - 1; depth >= 0; depth--)
            sb.Append(new string(' ', depth * 4)).AppendLine("}");
    }

    /// <summary>
    /// The outermost containing type of <paramref name="symbol"/> that is not declared <c>partial</c>,
    /// or <c>null</c> when all of them are. The generated code reopens every containing type, which
    /// only a partial type allows.
    /// </summary>
    internal static INamedTypeSymbol? FirstNonPartialContainingType(
        INamedTypeSymbol symbol, CancellationToken cancellationToken)
    {
        INamedTypeSymbol? outermost = null;
        for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            if (!IsPartial(type, cancellationToken)) outermost = type;
        }
        return outermost;
    }

    // A type parameter can only come from a containing type here: the type itself is not generic.
    private static bool UsesTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => UsesTypeParameter(array.ElementType),
        IPointerTypeSymbol pointer => UsesTypeParameter(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Any(UsesTypeParameter)
            || (named.ContainingType is not null && UsesTypeParameter(named.ContainingType)),
        _ => false,
    };

    /// <summary>
    /// The headers of the partial declarations that generated members go into: those of the
    /// containing types, outermost first, then the type itself. Each carries the accessibility, the
    /// kind and the type parameter names, for example <c>public partial record struct Orders&lt;T&gt;</c>.
    /// Constraints are left out, which partial parts allow.
    /// </summary>
    internal static IReadOnlyList<string> DeclarationHeaders(INamedTypeSymbol symbol)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var type = symbol; type is not null; type = type.ContainingType) chain.Add(type);
        chain.Reverse();

        var headers = new List<string>(chain.Count);
        foreach (var type in chain)
        {
            var sb = new StringBuilder();
            var accessibility = AccessibilityKeyword(type);
            if (accessibility.Length > 0) sb.Append(accessibility).Append(' ');
            if (type.IsRefLikeType) sb.Append("ref ");
            sb.Append("partial ").Append(KindKeyword(type)).Append(' ').Append(Identifier(type.Name));
            if (type.TypeParameters.Length > 0)
            {
                sb.Append('<');
                sb.Append(string.Join(", ", type.TypeParameters.Select(p => Identifier(p.Name))));
                sb.Append('>');
            }
            headers.Add(sb.ToString());
        }
        return headers;
    }

    /// <summary>
    /// A name for a namespace-level type generated for <paramref name="symbol"/> that is unique per
    /// type: the containing types and the type itself, joined with underscores. A generic type is
    /// followed by its arity, as the backtick arity in <see cref="HintPrefix"/> but valid in an
    /// identifier: <c>Module1_Order</c> for <c>Module&lt;T&gt;.Order</c> and <c>Module2_Order</c> for
    /// <c>Module&lt;T, U&gt;.Order</c>, which would otherwise get the same name. For a top-level type
    /// this is its name.
    /// </summary>
    internal static string NamespaceLevelName(INamedTypeSymbol symbol)
    {
        var names = new List<string>();
        for (var type = symbol; type is not null; type = type.ContainingType)
            names.Add(type.Arity > 0 ? type.Name + type.Arity : type.Name);
        names.Reverse();
        return string.Join("_", names);
    }

    /// <summary>
    /// A <c>cref</c> to <paramref name="symbol"/> that resolves from its namespace, for example
    /// <c>Module{TTenant}.Order</c>.
    /// </summary>
    internal static string Cref(INamedTypeSymbol symbol)
    {
        var sb = new StringBuilder();
        AppendCref(sb, symbol);
        return sb.ToString();
    }

    private static void AppendCref(StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.ContainingType is not null)
        {
            AppendCref(sb, type.ContainingType);
            sb.Append('.');
        }
        sb.Append(Identifier(type.Name));
        if (type.TypeParameters.Length > 0)
        {
            sb.Append('{');
            sb.Append(string.Join(", ", type.TypeParameters.Select(p => Identifier(p.Name))));
            sb.Append('}');
        }
    }

    private static bool IsPartial(INamedTypeSymbol type, CancellationToken cancellationToken)
        => type.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration
            && declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

    private static string KindKeyword(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        { IsRecord: true } => "record",
        { TypeKind: TypeKind.Struct } => "struct",
        { TypeKind: TypeKind.Interface } => "interface",
        _ => "class",
    };

    // A file-local type has no accessibility modifier to repeat, and a partial part may leave it out.
    private static string AccessibilityKeyword(INamedTypeSymbol type)
    {
        if (type.IsFileLocal) return string.Empty;
        return type.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => string.Empty,
        };
    }

    private static string Identifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
}
