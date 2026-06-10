using System;
using System.Collections.Generic;

namespace ZeroAlloc.EventSourcing.Outbox;

/// <summary>
/// Configuration for the outbox dispatcher. Construct directly and mutate
/// via the chainable <see cref="Exclude{TEvent}"/> method or assign properties.
/// </summary>
public sealed class OutboxOptions
{
    private string _consumerId = "outbox";
    private int _batchSize = 100;
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(1);
    private int _maxRetries = 3;
    private IRetryPolicy _retryPolicy = new ExponentialBackoffRetryPolicy();

    /// <summary>Consumer identifier used as the checkpoint-store key. Defaults to <c>"outbox"</c>. Must be non-null and non-whitespace.</summary>
    public string ConsumerId
    {
        get => _consumerId;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _consumerId = value;
        }
    }

    /// <summary>Maximum events read per poll iteration. Defaults to 100. Must be at least 1.</summary>
    public int BatchSize
    {
        get => _batchSize;
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value), "BatchSize must be at least 1");
            _batchSize = value;
        }
    }

    /// <summary>Delay between empty-batch polls. Defaults to 1 second. Must be non-negative.</summary>
    public TimeSpan PollInterval
    {
        get => _pollInterval;
        set
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), "PollInterval cannot be negative");
            _pollInterval = value;
        }
    }

    /// <summary>How to handle handler exceptions after retry exhaustion. Defaults to <see cref="ErrorHandlingStrategy.DeadLetter"/>.</summary>
    public ErrorHandlingStrategy ErrorStrategy { get; set; } = ErrorHandlingStrategy.DeadLetter;

    /// <summary>When to write the checkpoint position. Defaults to <see cref="CommitStrategy.AfterEvent"/>.</summary>
    public CommitStrategy CommitStrategy { get; set; } = CommitStrategy.AfterEvent;

    /// <summary>Maximum retry attempts before applying <see cref="ErrorStrategy"/>. Defaults to 3. Must be non-negative (0 means do not retry).</summary>
    public int MaxRetries
    {
        get => _maxRetries;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxRetries cannot be negative");
            _maxRetries = value;
        }
    }

    /// <summary>Retry-delay policy. Defaults to <see cref="ExponentialBackoffRetryPolicy"/> (initial 100ms, max 30s). Must not be null.</summary>
    public IRetryPolicy RetryPolicy
    {
        get => _retryPolicy;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _retryPolicy = value;
        }
    }

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
