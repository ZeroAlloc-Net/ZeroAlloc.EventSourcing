using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

/// <summary>
/// Test double for <see cref="INotificationDispatcher"/>. Records every dispatched event
/// for assertion; optionally throws to simulate handler failures.
/// </summary>
public sealed class RecordingDispatcher : INotificationDispatcher
{
    public ConcurrentBag<object> Dispatched { get; } = new();
    public Func<object, Exception?>? ThrowFor { get; set; }

    public ValueTask DispatchAsync(object @event, CancellationToken ct)
    {
        var ex = ThrowFor?.Invoke(@event);
        if (ex is not null) throw ex;
        Dispatched.Add(@event);
        return ValueTask.CompletedTask;
    }
}
