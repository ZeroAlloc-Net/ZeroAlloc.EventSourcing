using System.Collections.Generic;

namespace ZeroAlloc.EventSourcing.Generators;

internal sealed class AggregateInfo
{
    public AggregateInfo(
        string @namespace,
        string className,
        string stateTypeName,
        string stateTypeFullName,
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> eventTypeFullNames)
    {
        Namespace = @namespace;
        ClassName = className;
        StateTypeName = stateTypeName;
        StateTypeFullName = stateTypeFullName;
        EventTypeNames = eventTypeNames;
        EventTypeFullNames = eventTypeFullNames;
    }

    public string Namespace { get; }
    public string ClassName { get; }
    public string StateTypeName { get; }
    public string StateTypeFullName { get; }
    public IReadOnlyList<string> EventTypeNames { get; }
    public IReadOnlyList<string> EventTypeFullNames { get; }
}
