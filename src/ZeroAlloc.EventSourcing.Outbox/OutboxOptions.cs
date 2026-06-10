using System;
using System.Collections.Generic;

namespace ZeroAlloc.EventSourcing.Outbox;

/// <summary>
/// Configuration for the outbox dispatcher. Construct directly and mutate
/// via the chainable <see cref="Exclude{TEvent}"/> method or assign properties.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Consumer identifier used as the checkpoint-store key. Defaults to <c>"outbox"</c>.</summary>
    public string ConsumerId { get; set; } = "outbox";

    /// <summary>Maximum events read per poll iteration. Defaults to 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Delay between empty-batch polls. Defaults to 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How to handle handler exceptions after retry exhaustion. Defaults to <see cref="ErrorHandlingStrategy.DeadLetter"/>.</summary>
    public ErrorHandlingStrategy ErrorStrategy { get; set; } = ErrorHandlingStrategy.DeadLetter;

    /// <summary>When to write the checkpoint position. Defaults to <see cref="CommitStrategy.AfterEvent"/>.</summary>
    public CommitStrategy CommitStrategy { get; set; } = CommitStrategy.AfterEvent;

    /// <summary>Maximum retry attempts before applying <see cref="ErrorStrategy"/>. Defaults to 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Retry-delay policy. Defaults to <see cref="ExponentialBackoffRetryPolicy"/> (initial 100ms, max 30s).</summary>
    public IRetryPolicy RetryPolicy { get; set; } = new ExponentialBackoffRetryPolicy();

    /// <summary>
    /// Event types that should NOT be dispatched through the outbox, even if they implement
    /// <c>ZeroAlloc.Mediator.INotification</c>. Use for events that exist only for
    /// aggregate-internal reasons (snapshot markers, etc.).
    /// </summary>
    public IReadOnlyCollection<Type> ExcludedTypes => _excluded;

    private readonly HashSet<Type> _excluded = new();

    /// <summary>Adds <typeparamref name="TEvent"/> to <see cref="ExcludedTypes"/>. Idempotent. Returns <c>this</c> for chaining.</summary>
    public OutboxOptions Exclude<TEvent>() where TEvent : class
    {
        _excluded.Add(typeof(TEvent));
        return this;
    }
}
