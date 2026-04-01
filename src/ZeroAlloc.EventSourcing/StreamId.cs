namespace ZeroAlloc.EventSourcing;

/// <summary>Strongly typed identifier for an event stream.</summary>
public readonly record struct StreamId(string Value)
{
    /// <summary>Returns the string value of this stream identifier.</summary>
    public override string ToString() => Value;
}
