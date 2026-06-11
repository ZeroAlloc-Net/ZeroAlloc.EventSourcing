// samples/ZeroAlloc.EventSourcing.Outbox.AotSmoke/Program.cs
//
// AOT smoke for ZeroAlloc.EventSourcing.Outbox. Boots a Host, wires
// InMemoryEventStoreAdapter + InMemoryCheckpointStore + a hand-rolled
// INotificationDispatcher + OutboxDispatcher (via AddOutbox), appends one
// SmokeEvent, lets the polling loop run for ~2 seconds, asserts the handler
// was invoked exactly once.
//
// PublishAot=true + WarningsAsErrors on IL2026/IL2067/IL2075/IL2091/IL3050/IL3051
// gates this assembly's dispatch path against any reflection escape.
//
// NOTE on the hand-rolled dispatcher: in production, INotificationDispatcher
// is emitted by ZeroAlloc.EventSourcing.Mediator's bundled source generator
// (validated by the sibling ZeroAlloc.EventSourcing.Mediator.AotSmoke). This
// smoke focuses on OutboxDispatcher's path — the StreamConsumer poll →
// envelope → INotificationDispatcher.DispatchAsync call site — and uses a
// hand-rolled dispatcher to avoid pulling the bridge generator into this csproj.
// Hand-rolled also means zero reflection by construction, which is exactly
// what we want to assert.
//
// NOTE on the hand-rolled IEventSerializer + IEventTypeRegistry: mirrors the
// Mediator smoke. Avoids System.Text.Json so the AOT publish produces zero
// IL2026/IL3050 warnings.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Mediator;
using ZeroAlloc.EventSourcing.Outbox;
using ZeroAlloc.EventSourcing.Outbox.AotSmoke;
using ZeroAlloc.Mediator;

// HostBuilder() instead of Host.CreateDefaultBuilder() — AOT-leaner: avoids
// the JSON configuration providers and provider-discovery reflection that
// CreateDefaultBuilder wires in by default. This smoke only needs DI + the
// outbox hosted service, so the bare HostBuilder keeps the IL trimmer happy.
var host = new HostBuilder()
    .ConfigureServices(services =>
    {
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IEventSerializer, SmokeEventSerializer>();
        services.AddSingleton<IEventTypeRegistry, SmokeTypeRegistry>();
        services.AddSingleton<SmokeHandler>();
        services.AddSingleton<INotificationDispatcher, SmokeDispatcher>();

        services.AddEventSourcing()
                .UseInMemoryCheckpointStore()
                .UseInMemoryEventStore()
                .AddOutbox(opts =>
                {
                    opts.ConsumerId = "aot-smoke";
                    // 50ms (vs the 1s default) so the 2-second smoke window below
                    // catches at least one — and deterministically only one — poll
                    // cycle. Keeps the test fast without racing the dispatcher.
                    opts.PollInterval = TimeSpan.FromMilliseconds(50);
                });
    })
    .Build();
// Cast to IAsyncDisposable so the host + DI container + console logger sink
// are flushed and disposed on scope exit (IHost itself only surfaces
// IDisposable; the concrete Host implements IAsyncDisposable).
await using var _hostScope = (IAsyncDisposable)host;

var store = host.Services.GetRequiredService<IEventStore>();
var appendResult = await store.AppendAsync(
    new StreamId("test-1"),
    new object[] { new SmokeEvent(42) }.AsMemory(),
    StreamPosition.Start);

if (!appendResult.IsSuccess)
{
    Console.Error.WriteLine($"AOT smoke FAIL: append returned error: {appendResult.Error}");
    return 1;
}

try
{
    await host.StartAsync();
    // 2s ≫ PollInterval (50ms); single append cannot fire handler twice (checkpoint advances).
    await Task.Delay(TimeSpan.FromSeconds(2));
    await host.StopAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"AOT smoke FAIL: {ex.Message}");
    return 1;
}

var handler = host.Services.GetRequiredService<SmokeHandler>();
if (handler.Calls != 1)
{
    Console.Error.WriteLine($"AOT smoke FAIL: expected 1 handler call, got {handler.Calls}");
    return 1;
}

Console.WriteLine("Outbox AOT smoke PASS");
return 0;

namespace ZeroAlloc.EventSourcing.Outbox.AotSmoke
{
    /// <summary>Domain event for the smoke test.</summary>
    public sealed record SmokeEvent(int Value) : INotification;

    /// <summary>Handler that counts how many times the dispatcher delivered a SmokeEvent.</summary>
    public sealed class SmokeHandler : INotificationHandler<SmokeEvent>
    {
        private int _calls;

        /// <summary>Number of times <see cref="Handle"/> has been invoked.</summary>
        public int Calls => _calls;

        /// <inheritdoc/>
        public ValueTask Handle(SmokeEvent notification, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Hand-rolled <see cref="INotificationDispatcher"/> that switches over the known
    /// notification types in this compilation. Mirrors the shape of the generator-emitted
    /// dispatcher (see ZeroAlloc.EventSourcing.Mediator.Generator.DispatcherEmitter) — no
    /// reflection, no boxing for the switch arms.
    /// </summary>
    internal sealed class SmokeDispatcher : INotificationDispatcher
    {
        private readonly SmokeHandler _handler;

        public SmokeDispatcher(SmokeHandler handler) => _handler = handler;

        public ValueTask DispatchAsync(object @event, CancellationToken ct) => @event switch
        {
            SmokeEvent e => _handler.Handle(e, ct),
            _ => ValueTask.CompletedTask,
        };
    }

    /// <summary>
    /// Hand-rolled UTF-8 serializer for SmokeEvent. Avoids System.Text.Json so the
    /// AOT publish produces zero IL2026/IL3050 trim warnings.
    /// </summary>
    internal sealed class SmokeEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        {
            if (@event is SmokeEvent evt)
                return Encoding.UTF8.GetBytes(evt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            throw new NotSupportedException($"Unsupported event type {typeof(TEvent).FullName}");
        }

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        {
            if (eventType == typeof(SmokeEvent))
            {
                var value = int.Parse(Encoding.UTF8.GetString(payload.Span), System.Globalization.CultureInfo.InvariantCulture);
                return new SmokeEvent(value);
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
