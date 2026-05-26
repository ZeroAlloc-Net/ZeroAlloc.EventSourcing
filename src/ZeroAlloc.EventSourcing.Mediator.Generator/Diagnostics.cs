using Microsoft.CodeAnalysis;

namespace ZeroAlloc.EventSourcing.Mediator.Generator;

internal static class Diagnostics
{
    private const string Category = "ZeroAlloc.EventSourcing.Mediator";

    public static readonly DiagnosticDescriptor ZESM001_NoNotificationTypesFound = new(
        id: "ZESM001",
        title: "Bridge generator found zero INotification-implementing types",
        messageFormat: "No INotification-implementing types found in the consuming compilation — the bridge will silently skip every event at runtime",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
