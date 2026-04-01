namespace ZeroAlloc.EventSourcing;

/// <summary>Strongly typed identifier for an event stream.</summary>
public readonly record struct StreamId
{
    /// <summary>The stream identifier string.</summary>
    public string Value { get; init; }

    /// <summary>Initialises a <see cref="StreamId"/> with a non-null, non-empty value.</summary>
    public StreamId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>Returns the string value of this stream identifier.</summary>
    public override string ToString() => Value;
}
