namespace ZeroAlloc.EventSourcing;

/// <summary>Describes a failure that occurred during an event store operation.</summary>
public sealed class StoreError
{
    private StoreError(string code, string message) { Code = code; Message = message; }

    /// <summary>Machine-readable error code identifying the category of failure.</summary>
    public string Code { get; }

    /// <summary>Human-readable description of what went wrong.</summary>
    public string Message { get; }

    /// <summary>
    /// Creates a <see cref="StoreError"/> representing an optimistic concurrency conflict.
    /// </summary>
    /// <param name="id">The stream on which the conflict occurred.</param>
    /// <param name="expected">The version that the caller expected the stream to be at.</param>
    /// <param name="actual">The version the stream was actually at.</param>
    /// <returns>A <see cref="StoreError"/> with code <c>CONFLICT</c>.</returns>
    public static StoreError Conflict(StreamId id, StreamPosition expected, StreamPosition actual)
        => new("CONFLICT", $"Stream '{id}' expected version {expected.Value} but was {actual.Value}.");

    /// <summary>
    /// Creates a <see cref="StoreError"/> indicating the requested stream does not exist.
    /// </summary>
    /// <param name="id">The stream that could not be found.</param>
    /// <returns>A <see cref="StoreError"/> with code <c>STREAM_NOT_FOUND</c>.</returns>
    public static StoreError StreamNotFound(StreamId id)
        => new("STREAM_NOT_FOUND", $"Stream '{id}' does not exist.");

    /// <summary>
    /// Creates a <see cref="StoreError"/> for an unclassified failure.
    /// </summary>
    /// <param name="message">A message describing the unexpected error.</param>
    /// <returns>A <see cref="StoreError"/> with code <c>UNKNOWN</c>.</returns>
    public static StoreError Unknown(string message)
        => new("UNKNOWN", message);

    /// <summary>Returns a formatted string containing the error code and message.</summary>
    public override string ToString() => $"[{Code}] {Message}";
}
