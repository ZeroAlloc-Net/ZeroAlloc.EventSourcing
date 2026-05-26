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
/// Emit is unconditional w.r.t. discovered notification types: even with zero discovered
/// notification types the extension is available (the dispatcher's switch falls through to
/// the default arm). <c>ZESM001</c> warns the consumer in the empty case.
/// </summary>
/// <remarks>
/// The emitted code references <c>ZeroAlloc.Mediator.IMediator</c>, which is itself emitted
/// by the Mediator package's source generator into the consuming compilation (it is NOT in
/// the published marker DLL). If the consuming compilation has no <c>IMediator</c> in scope
/// (e.g., the bridge's own runtime project, or a consumer that hasn't yet wired the Mediator
/// generator), emit is skipped to avoid producing uncompilable code.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class EventSourcingMediatorGenerator : IIncrementalGenerator
{
    private const string MediatorMarkerTypeFullName = "ZeroAlloc.Mediator.IMediator";

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

        // Probe the compilation for IMediator (emitted by the Mediator generator, not in the marker DLL).
        var hasIMediator = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.GetTypeByMetadataName(MediatorMarkerTypeFullName) is not null);

        var combined = notifications.Combine(hasIMediator);

        context.RegisterSourceOutput(combined, (spc, pair) =>
        {
            var (notifs, hasMediator) = (pair.Left, pair.Right);

            // No IMediator visible in this compilation → the consumer hasn't wired Mediator yet
            // (e.g. the bridge's own runtime project). Skip emit entirely to avoid CS0246 errors.
            if (!hasMediator) return;

            var deduped = notifs.IsDefaultOrEmpty
                ? ImmutableArray<NotificationTypeInfo>.Empty
                : notifs.Distinct().OrderBy(n => n.FullyQualifiedName, System.StringComparer.Ordinal).ToImmutableArray();

            if (deduped.IsEmpty)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ZESM001_NoNotificationTypesFound,
                    location: null));
            }

            // Emit the dispatcher + extension. Empty notification set still produces a working
            // extension (the dispatcher's switch only has the default arm — silent skip).
            spc.AddSource(
                "EventSourcingMediatorGenerated.g.cs",
                SourceText.From(DispatcherEmitter.Emit(deduped), Encoding.UTF8));
        });
    }
}
