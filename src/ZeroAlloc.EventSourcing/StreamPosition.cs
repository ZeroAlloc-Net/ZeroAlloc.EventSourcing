namespace ZeroAlloc.EventSourcing;

/// <summary>Represents the position of an event within a stream.</summary>
public readonly record struct StreamPosition(long Value)
{
    /// <summary>The beginning of a stream (position 0).</summary>
    public static readonly StreamPosition Start = new(0);

    /// <summary>Sentinel value representing the end of a stream (position -1).</summary>
    public static readonly StreamPosition End = new(-1);

    /// <summary>Returns a new <see cref="StreamPosition"/> incremented by one.</summary>
    public StreamPosition Next() => new(Value + 1);
}
