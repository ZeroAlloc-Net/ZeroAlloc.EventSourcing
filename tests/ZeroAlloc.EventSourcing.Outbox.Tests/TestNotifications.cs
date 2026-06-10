using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public sealed record TestEventA(int Value) : INotification;
public sealed record TestEventB(string Value) : INotification;
public sealed record TestEventNotNotification(int Value); // does NOT implement INotification

// Stub notification handlers — never actually invoked by the OutboxDispatcher tests
// (which use RecordingDispatcher directly), but required so the Mediator generator
// emits IMediator.Publish<T> methods, which in turn lets the bridge generator's
// GeneratedNotificationDispatcher compile cleanly.
internal sealed class TestEventAHandler : INotificationHandler<TestEventA>
{
    public ValueTask Handle(TestEventA notification, CancellationToken ct) => ValueTask.CompletedTask;
}

internal sealed class TestEventBHandler : INotificationHandler<TestEventB>
{
    public ValueTask Handle(TestEventB notification, CancellationToken ct) => ValueTask.CompletedTask;
}
