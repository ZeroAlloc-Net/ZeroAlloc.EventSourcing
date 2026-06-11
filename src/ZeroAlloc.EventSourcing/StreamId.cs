namespace ZeroAlloc.EventSourcing;

/// <summary>Strongly typed identifier for an event stream.</summary>
public readonly record struct StreamId
{
    /// <summary>The stream identifier string.</summary>
    public string Value { get; init; }

    /// <summary>
    /// The global stream singleton. Reads/subscriptions against <see cref="Global"/>
    /// return events from ALL streams ordered by <c>global_position</c>.
    /// </summary>
    public static StreamId Global { get; } = new("*");

    /// <summary>
    /// True when this stream id refers to the global stream. Adapters dispatch on this
    /// to select the global-position read branch instead of the per-stream filter.
    /// </summary>
    public bool IsGlobal => string.Equals(Value, "*", System.StringComparison.Ordinal);

    /// <summary>Initialises a <see cref="StreamId"/> with a non-null, non-empty value.</summary>
    public StreamId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>Returns the string value of this stream identifier.</summary>
    public override string ToString() => Value;
}
