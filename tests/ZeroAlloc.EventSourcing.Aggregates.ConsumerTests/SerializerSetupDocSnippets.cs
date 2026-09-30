using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Serialisation;

// The ZeroAlloc.Serialisation setup from docs/core-concepts/events.md and
// docs/getting-started/installation.md, copied as it appears there between "--- snippet ---"
// markers and compiled with the real ZeroAlloc.Serialisation source generator. The test runs it
// end to end: events go through ZeroAllocEventSerializer into the store and back. When a snippet
// changes in the docs, change it here as well. See issue #410.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.SerializerSetupDocs;

// --- snippet: "ZeroAllocEventSerializer" types ---
// 1. Mark your event types with the chosen serialization format
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public record OrderPlacedEvent(string OrderId, decimal Total);

// 2. Provide a JsonSerializerContext so System.Text.Json can serialize without reflection.
//    The generator reports ZASZ004 for a SystemTextJson type that no context lists.
[JsonSerializable(typeof(OrderPlacedEvent))]
internal partial class DomainJsonContext : JsonSerializerContext { }
// --- end snippet ---

public sealed class SerializerSetupDocTests
{
    [Fact]
    public async Task GeneratedRegistration_RoundTripsThroughTheStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventTypeRegistry, SingleTypeRegistry>();

        // --- snippet: "ZeroAllocEventSerializer" registration ---
        // 3. In your composition root. The generator emits Add{EventType}Serializer() for each
        //    annotated type and AddSerializerDispatcher() for the assembly.
        services
            .AddOrderPlacedEventSerializer()
            .AddSerializerDispatcher()  // generated at compile time — no reflection
            .AddEventSourcing()         // registers IEventSerializer → ZeroAllocEventSerializer
            .UseInMemoryEventStore();   // swap for .UsePostgreSqlEventStore(cs) or .UseSqlServerEventStore(cs) in production
        // --- end snippet ---

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventSerializer>().Should().BeOfType<ZeroAllocEventSerializer>();

        var store = provider.GetRequiredService<IEventStore>();
        var stream = new StreamId("order-1");
        (await store.AppendAsync(stream, new object[] { new OrderPlacedEvent("1", 9.5m) }, StreamPosition.Start))
            .IsSuccess.Should().BeTrue();

        var read = new List<object>();
        await foreach (var envelope in store.ReadAsync(stream))
            read.Add(envelope.Event);
        read.Should().Equal(new OrderPlacedEvent("1", 9.5m));
    }

    private sealed class SingleTypeRegistry : IEventTypeRegistry
    {
        public bool TryGetType(string eventType, out Type? type)
        {
            type = eventType == nameof(OrderPlacedEvent) ? typeof(OrderPlacedEvent) : null;
            return type is not null;
        }

        public string GetTypeName(Type type) => type.Name;
    }
}
