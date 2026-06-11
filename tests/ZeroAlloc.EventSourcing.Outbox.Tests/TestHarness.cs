using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

/// <summary>
/// Shared test wiring for the Outbox test suite. Centralizes the harness
/// construction, the wait-until polling helpers, and the JSON serializer /
/// event-type registry used across multiple test classes.
/// </summary>
internal static class TestHarness
{
    internal static (IEventStore store, ICheckpointStore checkpoints, RecordingDispatcher dispatcher) New()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var store = new EventStore(adapter, new OutboxJsonSerializer(), new OutboxTestTypeRegistry());
        var checkpoints = new InMemoryCheckpointStore();
        var dispatcher = new RecordingDispatcher();
        return (store, checkpoints, dispatcher);
    }

    internal static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25).ConfigureAwait(false);
        if (!predicate())
            throw new TimeoutException($"Predicate did not become true within {timeout}");
    }

    internal static async Task WaitUntil(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await predicate().ConfigureAwait(false) && DateTime.UtcNow < deadline)
            await Task.Delay(25).ConfigureAwait(false);
        if (!await predicate().ConfigureAwait(false))
            throw new TimeoutException($"Predicate did not become true within {timeout}");
    }

    internal sealed class OutboxJsonSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
            => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
    }

    internal sealed class OutboxTestTypeRegistry : IEventTypeRegistry
    {
        private readonly Dictionary<string, Type> _map = new()
        {
            [nameof(TestEventA)] = typeof(TestEventA),
            [nameof(TestEventB)] = typeof(TestEventB),
            [nameof(TestEventNotNotification)] = typeof(TestEventNotNotification),
        };

        public bool TryGetType(string eventType, out Type? type) => _map.TryGetValue(eventType, out type);

        public string GetTypeName(Type type) => type.Name;
    }
}
