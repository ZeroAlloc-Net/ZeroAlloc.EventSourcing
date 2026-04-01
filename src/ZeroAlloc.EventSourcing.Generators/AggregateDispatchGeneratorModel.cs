using System.Collections.Generic;
using System.Linq;

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
        // EventTypeNames — short names (e.g. "OrderPlaced"). Used by EventTypeRegistryGenerator (Task 6).
        EventTypeNames = eventTypeNames;
        EventTypeFullNames = eventTypeFullNames;
    }

    public string Namespace { get; }
    public string ClassName { get; }
    public string StateTypeName { get; }
    public string StateTypeFullName { get; }
    public IReadOnlyList<string> EventTypeNames { get; }
    public IReadOnlyList<string> EventTypeFullNames { get; }

    // Value equality is required for Roslyn's incremental pipeline to cache results correctly.
    // Without it, reference equality causes the source output step to re-run on every keystroke.
    public override bool Equals(object? obj)
        => obj is AggregateInfo other
            && Namespace == other.Namespace
            && ClassName == other.ClassName
            && StateTypeName == other.StateTypeName
            && StateTypeFullName == other.StateTypeFullName
            && EventTypeNames.SequenceEqual(other.EventTypeNames)
            && EventTypeFullNames.SequenceEqual(other.EventTypeFullNames);

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + Namespace.GetHashCode();
        hash = hash * 31 + ClassName.GetHashCode();
        hash = hash * 31 + StateTypeFullName.GetHashCode();
        foreach (var name in EventTypeFullNames)
            hash = hash * 31 + name.GetHashCode();
        return hash;
    }
}
