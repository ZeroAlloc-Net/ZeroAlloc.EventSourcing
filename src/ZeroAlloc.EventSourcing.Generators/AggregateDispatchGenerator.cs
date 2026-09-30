// src/ZeroAlloc.EventSourcing.Generators/AggregateDispatchGenerator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

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
                predicate: static (node, _) => PartialTypeDeclarations.IsPartialClassWithBaseSyntax(node),
                transform: static (ctx, ct) => GetAggregateInfoPublic(ctx, ct))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!);

        // Only this generator reports the discovery diagnostics. EventTypeRegistryGenerator shares the
        // discovery and would otherwise report each of them a second time.
        context.RegisterSourceOutput(aggregates, static (ctx, result) =>
        {
            if (result.Diagnostic is not null)
            {
                ctx.ReportDiagnostic(result.Diagnostic.ToDiagnostic());
                return;
            }
            var info = result.Info!;
            ctx.AddSource($"{info.HintPrefix}.ApplyEvent.g.cs", EmitApplyEvent(info));
        });
    }

    /// <summary>
    /// Semantic transform: returns an <see cref="AggregateInfo"/> for classes that inherit
    /// <c>Aggregate&lt;TId, TState&gt;</c> and whose state has <c>Apply(TEvent)</c> methods, or a
    /// diagnostic instead when such an aggregate cannot be generated: ZAES007 when it is file-local,
    /// ZAES005 when a containing type is not partial, ZAES006 when it is generic or one of its event types uses a type parameter.
    /// Returns <c>null</c> for non-aggregate classes, for those already providing a manual override,
    /// and for every declaration of a partial class except its primary one, so that a class split over
    /// several declarations is emitted once.
    /// Shared with <see cref="EventTypeRegistryGenerator"/>.
    /// NOTE: the <c>hasExplicitApplyEvent</c> guard below also suppresses registry generation for
    /// aggregates with a hand-written dispatcher. If those concerns need separating in the future,
    /// introduce a dedicated discovery method for the registry generator.
    /// </summary>
    internal static DiscoveryResult<AggregateInfo>? GetAggregateInfoPublic(GeneratorSyntaxContext ctx, CancellationToken cancellationToken)
    {
        var cls = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(cls, cancellationToken) as INamedTypeSymbol;
        if (symbol is null) return null;
        if (!PartialTypeDeclarations.IsPrimaryDeclaration(cls, symbol, cancellationToken)) return null;

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

        // Find internal Apply(TEvent) methods on the state struct.
        // Only internal accessibility is accepted: the generated ApplyEvent override lives in a partial
        // class file that is a different type from the state struct, so private methods on the struct
        // would produce a CS0122 inaccessible-member compiler error in the generated output.
        var applyMethods = stateType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.Name == "Apply"
                     && m.Parameters.Length == 1
                     && m.DeclaredAccessibility == Accessibility.Internal)
            .ToList();

        if (applyMethods.Count == 0) return null;

        // The event types also go into the registry, which sits at namespace level.
        var eventTypes = applyMethods.Select(m => m.Parameters[0].Type).ToList();
        var problem = PartialTypeDeclarations.Check(cls, symbol, eventTypes, cancellationToken);
        if (problem is not null) return DiscoveryResult<AggregateInfo>.Report(problem);

        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        return DiscoveryResult<AggregateInfo>.Generate(new AggregateInfo(
            PartialTypeDeclarations.HintPrefix(symbol),
            ns,
            PartialTypeDeclarations.DeclarationHeaders(symbol),
            PartialTypeDeclarations.NamespaceLevelName(symbol) + "EventTypeRegistry",
            PartialTypeDeclarations.Cref(symbol),
            stateType.Name,
            stateType.ToDisplayString(),
            eventTypes.Select(t => t.Name).ToList(),
            eventTypes.Select(t => t.ToDisplayString()).ToList()));
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

        var body = new List<string>
        {
            $"protected override {info.StateTypeFullName} ApplyEvent({info.StateTypeFullName} state, object @event)",
            "    => @event switch",
            "    {",
        };
        foreach (var eventType in info.EventTypeFullNames)
            body.Add($"        {eventType} __e => state.Apply(__e),");
        body.Add("        _ => state");
        body.Add("    };");

        PartialTypeDeclarations.AppendDeclarations(sb, info.DeclarationHeaders, body);
        return sb.ToString();
    }
}
