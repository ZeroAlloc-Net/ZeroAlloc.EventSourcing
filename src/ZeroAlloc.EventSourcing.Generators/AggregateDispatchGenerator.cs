// src/ZeroAlloc.EventSourcing.Generators/AggregateDispatchGenerator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ZeroAlloc.EventSourcing.Generators;

/// <summary>
/// Roslyn incremental source generator that emits a type-switch <c>ApplyEvent</c> override
/// for every <c>partial</c> class that inherits <c>Aggregate&lt;TId, TState&gt;</c>.
/// </summary>
[Generator]
public sealed class AggregateDispatchGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var aggregates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsPartialClassWithBaseSyntax(node),
                transform: static (ctx, _) => GetAggregateInfoPublic(ctx))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        context.RegisterSourceOutput(aggregates, static (ctx, info) =>
        {
            var source = EmitApplyEvent(info);
            // Qualify the hint name with the namespace to avoid collisions when two aggregates share a short name.
            var hint = string.IsNullOrEmpty(info.Namespace)
                ? $"{info.ClassName}.ApplyEvent.g.cs"
                : $"{info.Namespace}.{info.ClassName}.ApplyEvent.g.cs";
            ctx.AddSource(hint, source);
        });
    }

    /// <summary>Syntactic predicate: returns true for partial class declarations with a base type list. Shared with <see cref="EventTypeRegistryGenerator"/>.</summary>
    internal static bool IsPartialClassWithBaseSyntax(SyntaxNode node)
        => node is ClassDeclarationSyntax cls
            && cls.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))
            && cls.BaseList?.Types.Count > 0;

    /// <summary>
    /// Semantic transform: returns an <see cref="AggregateInfo"/> for classes that inherit
    /// <c>Aggregate&lt;TId, TState&gt;</c> and whose state has <c>Apply(TEvent)</c> methods.
    /// Returns <c>null</c> for non-aggregate classes or those already providing a manual override.
    /// Shared with <see cref="EventTypeRegistryGenerator"/>.
    /// NOTE: the <c>hasExplicitApplyEvent</c> guard below also suppresses registry generation for
    /// aggregates with a hand-written dispatcher. If those concerns need separating in the future,
    /// introduce a dedicated discovery method for the registry generator.
    /// </summary>
    internal static AggregateInfo? GetAggregateInfoPublic(GeneratorSyntaxContext ctx)
    {
        var cls = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(cls) as INamedTypeSymbol;
        if (symbol is null) return null;

        // Skip if the class already has an explicit ApplyEvent override declared directly on it.
        // This prevents a duplicate-member error when Order (in tests) defines ApplyEvent manually.
        var hasExplicitApplyEvent = symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(m => m.Name == "ApplyEvent"
                   && m.Parameters.Length == 2
                   && m.IsOverride);
        if (hasExplicitApplyEvent) return null;

        // Walk base types to find Aggregate<TId, TState>
        var baseType = symbol.BaseType;
        while (baseType is not null)
        {
            if (baseType.OriginalDefinition.ToDisplayString() ==
                "ZeroAlloc.EventSourcing.Aggregates.Aggregate<TId, TState>")
                break;
            baseType = baseType.BaseType;
        }
        if (baseType is null || baseType.TypeArguments.Length < 2) return null;

        var stateType = baseType.TypeArguments[1] as INamedTypeSymbol;
        if (stateType is null) return null;

        // Find internal or private Apply(TEvent) methods on the state struct.
        // Both accessibilities are accepted: internal is the idiomatic choice, private also works
        // because the generated code calls state.Apply(...) on the struct value (not on the aggregate),
        // so accessibility is relative to the state type, not the call site.
        var applyMethods = stateType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.Name == "Apply"
                     && m.Parameters.Length == 1
                     && (m.DeclaredAccessibility == Accessibility.Internal
                      || m.DeclaredAccessibility == Accessibility.Private))
            .ToList();

        if (applyMethods.Count == 0) return null;

        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        return new AggregateInfo(
            ns,
            symbol.Name,
            stateType.Name,
            stateType.ToDisplayString(),
            applyMethods.Select(m => m.Parameters[0].Type.Name).ToList(),
            applyMethods.Select(m => m.Parameters[0].Type.ToDisplayString()).ToList());
    }

    private static string EmitApplyEvent(AggregateInfo info)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(info.Namespace))
        {
            sb.AppendLine($"namespace {info.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"partial class {info.ClassName}");
        sb.AppendLine("{");
        sb.AppendLine($"    protected override {info.StateTypeFullName} ApplyEvent({info.StateTypeFullName} state, object @event)");
        sb.AppendLine("        => @event switch");
        sb.AppendLine("        {");

        for (var i = 0; i < info.EventTypeFullNames.Count; i++)
        {
            sb.AppendLine($"            {info.EventTypeFullNames[i]} __e => state.Apply(__e),");
        }

        sb.AppendLine("            _ => state");
        sb.AppendLine("        };");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
