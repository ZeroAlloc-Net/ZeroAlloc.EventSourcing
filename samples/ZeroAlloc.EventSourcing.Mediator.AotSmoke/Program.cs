using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Mediator;
using ZeroAlloc.EventSourcing.Mediator.AotSmoke;
using ZeroAlloc.Mediator;

// Smoke test: boot a Host, wire the bridge via the generator-emitted
// .PublishViaMediator(streamId) extension, append two SmokeEvents, assert the handler got both.
// PublishAot=true validates the bundled generator emits AOT-clean typed dispatch (no reflection).
// NO PartialDeclarationShim — the bridge generator emits the full extension method body.

var host = new HostBuilder()
    .ConfigureServices(services =>
    {
        services.AddLogging();
        services.AddSingleton<IEventSerializer, SmokeEventSerializer>();
        services.AddSingleton<IEventTypeRegistry, SmokeTypeRegistry>();

        services.AddEventSourcing()
            .UseInMemoryEventStore()
            .PublishViaMediator(new StreamId("smoke"));   // generator-emitted, NO shim

        // Mediator wiring — explicit (no assembly scanning) for AOT cleanliness.
        services.AddMediator();
        // MediatorService resolves handlers as concrete types via GetRequiredService<THandler>.
        services.AddSingleton<SmokeHandler>();
    })
    .Build();

await host.StartAsync();

// SmokeEvent is a struct with a nullable value-type field: one event carries a value, the
// other carries null. Both must arrive intact, in order, through the generated dispatch.
var store = host.Services.GetRequiredService<IEventStore>();
var appended = await store.AppendAsync(
    new StreamId("smoke"),
    new object[] { new SmokeEvent("hello", 3), new SmokeEvent("again", null) }.AsMemory(),
    StreamPosition.Start);
if (!appended.IsSuccess)
{
    Console.Error.WriteLine($"AOT smoke: FAIL - append returned {appended.Error}");
    await host.StopAsync();
    return 1;
}

// The bridge dispatches from a background subscription; wait for it rather than for a fixed time.
var handler = host.Services.GetRequiredService<SmokeHandler>();
var deadline = DateTime.UtcNow.AddSeconds(10);
while (handler.Received.Count < 2 && DateTime.UtcNow < deadline)
    await Task.Delay(20);

await host.StopAsync();

var received = handler.Received;
if (received.Count != 2)
{
    Console.Error.WriteLine($"AOT smoke: FAIL - expected 2 delivered events, got {received.Count}");
    return 1;
}
if (received[0] != new SmokeEvent("hello", 3))
{
    Console.Error.WriteLine($"AOT smoke: FAIL - first event expected (hello, 3), got {received[0]}");
    return 1;
}
if (received[1] != new SmokeEvent("again", null))
{
    Console.Error.WriteLine($"AOT smoke: FAIL - second event expected (again, null), got {received[1]}");
    return 1;
}

Console.WriteLine($"Bridge delivered: {received[0].Message} ({received[0].Attempt}), {received[1].Message} (null)");
return 0;

namespace ZeroAlloc.EventSourcing.Mediator.AotSmoke
{
    /// <summary>Test event for the bridge smoke check: a struct with a nullable value-type field.</summary>
    public readonly record struct SmokeEvent(string Message, int? Attempt) : INotification;

    /// <summary>Handler that records every SmokeEvent the bridge delivers, in order.</summary>
    public sealed class SmokeHandler : INotificationHandler<SmokeEvent>
    {
        private readonly List<SmokeEvent> _received = new();
        private readonly Lock _gate = new();

        /// <summary>A copy of the events received so far.</summary>
        public IReadOnlyList<SmokeEvent> Received
        {
            get { lock (_gate) return _received.ToArray(); }
        }

        /// <inheritdoc/>
        public ValueTask Handle(SmokeEvent notification, CancellationToken ct)
        {
            lock (_gate) _received.Add(notification);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Hand-rolled UTF-8 serializer for SmokeEvent. Avoids System.Text.Json so the smoke
    /// sample produces 0 IL2026/IL3050 trim warnings — the goal is to verify the bridge's
    /// dispatch path is AOT-clean, not to demonstrate a production serializer.
    /// </summary>
    internal sealed class SmokeEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        {
            // "<attempt>|<message>", with an empty attempt for null.
            if (@event is SmokeEvent evt)
                return Encoding.UTF8.GetBytes(
                    $"{evt.Attempt?.ToString(CultureInfo.InvariantCulture)}|{evt.Message}");
            throw new NotSupportedException($"Unsupported event type {typeof(TEvent).FullName}");
        }

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        {
            if (eventType == typeof(SmokeEvent))
            {
                var text = Encoding.UTF8.GetString(payload.Span);
                var bar = text.IndexOf('|', StringComparison.Ordinal);
                int? attempt = bar == 0 ? null : int.Parse(text.AsSpan(0, bar), CultureInfo.InvariantCulture);
                return new SmokeEvent(text[(bar + 1)..], attempt);
            }
            throw new NotSupportedException($"Unsupported event type {eventType.FullName}");
        }
    }

    /// <summary>Minimal type registry mapping SmokeEvent's name back to its CLR type.</summary>
    internal sealed class SmokeTypeRegistry : IEventTypeRegistry
    {
        public bool TryGetType(string eventType, out Type? type)
        {
            if (eventType == nameof(SmokeEvent))
            {
                type = typeof(SmokeEvent);
                return true;
            }
            type = null;
            return false;
        }

        public string GetTypeName(Type type) => type.Name;
    }
}
