using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Mediator.Tests;

#pragma warning disable MA0048 // co-located test fixtures for a single bridge scenario

// Notification events — must implement INotification so the bridge's filter accepts them
// AND so the generator discovers them when emitting GeneratedNotificationDispatcher.
public readonly record struct UserCreated(string Name) : INotification;
public readonly record struct OrderPlaced(decimal Total) : INotification;

// Non-notification event — used to verify the bridge silently skips events
// without the INotification marker.
public readonly record struct PlainEvent(string Payload);

/// <summary>
/// Process-wide event sink for bridge tests. Each test uses a unique event payload
/// (e.g. "alice-test1") to disambiguate. xUnit serialises test methods in this class
/// by default (no [Collection] parallelism) so cross-test interference is avoided by
/// payload uniqueness rather than sink scoping.
/// </summary>
public static class TestSink
{
    public static ConcurrentQueue<string> Events { get; } = new();
}

/// <summary>
/// A handler that records each UserCreated dispatched through Mediator. Requires a
/// parameterless constructor for Mediator's static dispatch (ZAM008).
/// </summary>
public sealed class UserCreatedHandler : INotificationHandler<UserCreated>
{
    public ValueTask Handle(UserCreated notification, CancellationToken ct)
    {
        TestSink.Events.Enqueue("user:" + notification.Name);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A handler for OrderPlaced that always throws — used to verify the bridge swallows
/// handler exceptions and keeps the subscription alive.
/// </summary>
public sealed class OrderPlacedThrowingHandler : INotificationHandler<OrderPlaced>
{
    public ValueTask Handle(OrderPlaced notification, CancellationToken ct)
    {
        TestSink.Events.Enqueue("order-attempted:" + notification.Total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        throw new InvalidOperationException("simulated handler failure");
    }
}

#pragma warning restore MA0048
