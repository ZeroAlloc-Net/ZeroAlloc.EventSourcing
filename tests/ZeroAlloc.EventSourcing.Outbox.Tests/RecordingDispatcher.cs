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

    /// <summary>
    /// Optional async hook invoked AFTER the throw simulation and BEFORE the event is
    /// recorded. Lets a test trigger follow-up work (e.g. appending another event to the
    /// store) inside the dispatch path. If the hook throws, the event is not recorded —
    /// matching the behaviour of a real failing handler.
    /// </summary>
    public Func<object, Task>? OnDispatch { get; set; }

    public async ValueTask DispatchAsync(object @event, CancellationToken ct)
    {
        var ex = ThrowFor?.Invoke(@event);
        if (ex is not null) throw ex;
        if (OnDispatch is not null) await OnDispatch(@event).ConfigureAwait(false);
        Dispatched.Add(@event);
    }
}
