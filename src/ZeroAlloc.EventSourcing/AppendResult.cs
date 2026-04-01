namespace ZeroAlloc.EventSourcing;

/// <summary>Result returned after successfully appending events to a stream.</summary>
public readonly record struct AppendResult(
    StreamId StreamId,
    StreamPosition NextExpectedVersion);
