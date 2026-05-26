using System;
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
// .PublishViaMediator(streamId) extension, append one SmokeEvent, assert the handler fired.
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

var store = host.Services.GetRequiredService<IEventStore>();
await store.AppendAsync(
    new StreamId("smoke"),
    new object[] { new SmokeEvent("hello") }.AsMemory(),
    StreamPosition.Start);

// Give the bridge a moment to dispatch.
await Task.Delay(500);

var handler = host.Services.GetRequiredService<SmokeHandler>();
if (handler.LastMessage is null)
{
    Console.Error.WriteLine("AOT smoke: FAIL - handler.LastMessage is null; bridge did not deliver event");
    await host.StopAsync();
    return 1;
}

Console.WriteLine($"Bridge delivered: {handler.LastMessage}");
await host.StopAsync();
return 0;

namespace ZeroAlloc.EventSourcing.Mediator.AotSmoke
{
    /// <summary>Test event for the bridge smoke check.</summary>
    public readonly record struct SmokeEvent(string Message) : INotification;

    /// <summary>Handler that captures the most recent SmokeEvent message.</summary>
    public sealed class SmokeHandler : INotificationHandler<SmokeEvent>
    {
        /// <summary>Last received message; null until the bridge delivers an event.</summary>
        public string? LastMessage { get; private set; }

        /// <inheritdoc/>
        public ValueTask Handle(SmokeEvent notification, CancellationToken ct)
        {
            LastMessage = notification.Message;
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
            if (@event is SmokeEvent evt)
                return Encoding.UTF8.GetBytes(evt.Message);
            throw new NotSupportedException($"Unsupported event type {typeof(TEvent).FullName}");
        }

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        {
            if (eventType == typeof(SmokeEvent))
                return new SmokeEvent(Encoding.UTF8.GetString(payload.Span));
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
