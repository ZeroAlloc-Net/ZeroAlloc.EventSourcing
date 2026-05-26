using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ZeroAlloc.EventSourcing.Mediator.Generator;

/// <summary>
/// Incremental generator that emits:
/// <list type="bullet">
/// <item><description>A <c>GeneratedNotificationDispatcher</c> in <c>ZeroAlloc.EventSourcing.Mediator.Generated</c> switching over every discovered <c>INotification</c> type.</description></item>
/// <item><description>An <c>internal static class EventSourcingBuilderMediatorExtensions</c> exposing <c>PublishViaMediator(EventSourcingBuilder, StreamId)</c>.</description></item>
/// </list>
/// Emit is unconditional: even with zero discovered notification types the extension is
/// emitted (the dispatcher's switch falls through to the default arm — silent skip).
/// <c>ZESM001</c> warns the consumer in the empty case.
/// </summary>
/// <remarks>
/// The emitted code references <c>ZeroAlloc.Mediator.IMediator</c>, which is itself emitted
/// by the Mediator package's source generator into the consuming compilation (it is NOT in
/// the published marker DLL). Consumers must therefore reference both <c>ZeroAlloc.Mediator</c>
/// AND <c>ZeroAlloc.Mediator.Generator</c> for this bridge generator's emit to compile.
/// The bridge's own runtime project must NOT attach this generator as an Analyzer (the
/// runtime project doesn't consume Mediator's generator); the project reference uses
/// <c>ReferenceOutputAssembly="false"</c> with no <c>OutputItemType</c> there.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class EventSourcingMediatorGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var notifications = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (n, _) => n is TypeDeclarationSyntax,
                transform: NotificationDiscovery.Transform)
            .Where(static n => n is not null)
            .Select(static (n, _) => n!)
            .Collect();

        context.RegisterSourceOutput(notifications, (spc, notifs) =>
        {
            var deduped = notifs.IsDefaultOrEmpty
                ? ImmutableArray<NotificationTypeInfo>.Empty
                : notifs.Distinct().OrderBy(n => n.FullyQualifiedName, System.StringComparer.Ordinal).ToImmutableArray();

            if (deduped.IsEmpty)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ZESM001_NoNotificationTypesFound,
                    location: null));
            }

            // ALWAYS emit — even when empty, so PublishViaMediator extension is available.
            // Empty dispatcher's switch has only the default arm (silent skip).
            spc.AddSource(
                "EventSourcingMediatorGenerated.g.cs",
                SourceText.From(DispatcherEmitter.Emit(deduped), Encoding.UTF8));
        });
    }
}
