namespace ZeroAlloc.EventSourcing;

/// <summary>Represents a single event that failed processing after all retries were exhausted.</summary>
public sealed record DeadLetterEntry(
    EventEnvelope Envelope,
    string ConsumerId,
    string ExceptionType,
    string ExceptionMessage,
    DateTimeOffset FailedAt);
