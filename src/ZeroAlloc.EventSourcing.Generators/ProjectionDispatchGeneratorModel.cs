using System.Collections.Generic;
using System.Linq;

namespace ZeroAlloc.EventSourcing.Generators;

/// <summary>
/// Shared discovery model used by <see cref="ProjectionDispatchGenerator"/>.
/// Carries all information needed to emit the <c>ApplyTyped</c> dispatch switch expression.
/// </summary>
internal sealed class ProjectionInfo
{
    public ProjectionInfo(
        string @namespace,
        string className,
        string readModelType,
        string readModelTypeFullName,
        IReadOnlyList<ApplyMethodInfo> applyMethods)
    {
        Namespace = @namespace;
        ClassName = className;
        ReadModelType = readModelType;
        ReadModelTypeFullName = readModelTypeFullName;
        ApplyMethods = applyMethods;
    }

    public string Namespace { get; }
    public string ClassName { get; }
    public string ReadModelType { get; }
    public string ReadModelTypeFullName { get; }
    public IReadOnlyList<ApplyMethodInfo> ApplyMethods { get; }

    // Value equality is required for Roslyn's incremental pipeline to cache results correctly.
    public override bool Equals(object? obj)
        => obj is ProjectionInfo other
            && Namespace == other.Namespace
            && ClassName == other.ClassName
            && ReadModelType == other.ReadModelType
            && ReadModelTypeFullName == other.ReadModelTypeFullName
            && ApplyMethods.SequenceEqual(other.ApplyMethods);

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + Namespace.GetHashCode();
        hash = hash * 31 + ClassName.GetHashCode();
        hash = hash * 31 + ReadModelType.GetHashCode();
        hash = hash * 31 + ReadModelTypeFullName.GetHashCode();
        foreach (var method in ApplyMethods)
            hash = hash * 31 + method.GetHashCode();
        return hash;
    }
}

/// <summary>Represents a single Apply method overload with its event type information.</summary>
internal sealed class ApplyMethodInfo
{
    public ApplyMethodInfo(string eventTypeName, string eventTypeFullName)
    {
        EventTypeName = eventTypeName;
        EventTypeFullName = eventTypeFullName;
    }

    public string EventTypeName { get; }
    public string EventTypeFullName { get; }

    public override bool Equals(object? obj)
        => obj is ApplyMethodInfo other
            && EventTypeName == other.EventTypeName
            && EventTypeFullName == other.EventTypeFullName;

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + EventTypeName.GetHashCode();
        hash = hash * 31 + EventTypeFullName.GetHashCode();
        return hash;
    }
}
