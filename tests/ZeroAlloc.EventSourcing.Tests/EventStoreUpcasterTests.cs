using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public class EventStoreUpcasterTests
{
    private record OrderCreatedV1(string OrderId);
    private record OrderCreated(string OrderId, string CustomerId);

    // Reuse the JsonEventSerializer and TestTypeRegistry from EventStoreTests.cs,
    // but define a local registry that knows about both V1 and current types.
    private sealed class UpcasterTestTypeRegistry : IEventTypeRegistry
    {
        private readonly Dictionary<string, Type> _map = new()
        {
            [nameof(OrderCreatedV1)] = typeof(OrderCreatedV1),
            [nameof(OrderCreated)]   = typeof(OrderCreated),
        };

        public bool TryGetType(string eventType, out Type? type) => _map.TryGetValue(eventType, out type);
        public string GetTypeName(Type type) => type.Name;
    }

    [Fact]
    public async Task ReadAsync_UpcasesV1EventToCurrentType()
    {
        // Arrange — wire up EventStore directly (no DI complexity with serializer)
        var adapter  = new InMemoryEventStoreAdapter();
        var serializer  = new JsonEventSerializer();
        var registry    = new UpcasterTestTypeRegistry();
        var pipeline    = new UpcasterPipeline([
            new UpcasterRegistration(
                typeof(OrderCreatedV1),
                typeof(OrderCreated),
                o => { var v = (OrderCreatedV1)o; return new OrderCreated(v.OrderId, "unknown"); })
        ]);

        var store = new EventStore(adapter, serializer, registry, pipeline);

        // Write a V1 event
        var v1 = new OrderCreatedV1("order-1");
        await store.AppendAsync(
            new StreamId("orders-1"),
            new ReadOnlyMemory<object>([v1]),
            StreamPosition.Start);

        // Act — read back
        var events = new List<EventEnvelope>();
        await foreach (var e in store.ReadAsync(new StreamId("orders-1")))
            events.Add(e);

        // Assert
        events.Should().ContainSingle();
        events[0].Event.Should().BeOfType<OrderCreated>()
            .Which.CustomerId.Should().Be("unknown");
    }

    [Fact]
    public void AddUpcaster_WiresUpcasterPipelineInDI()
    {
        var services = new ServiceCollection();
        services.AddEventSourcing()
            .AddUpcaster<OrderCreatedV1, OrderCreated>(v1 => new OrderCreated(v1.OrderId, "x"));

        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IUpcasterPipeline>();
        pipeline.Should().BeOfType<UpcasterPipeline>();

        var result = pipeline.TryUpcast(new OrderCreatedV1("y"), out var upgraded);
        result.Should().BeTrue();
        upgraded.Should().BeOfType<OrderCreated>().Which.OrderId.Should().Be("y");
    }

    [Fact]
    public async Task SubscribeAsync_UpcasesV1EventToCurrentType()
    {
        // Arrange — build EventStore directly to avoid DI serializer wiring complexity
        var adapter  = new InMemoryEventStoreAdapter();
        var serializer  = new JsonEventSerializer();
        var registry    = new UpcasterTestTypeRegistry();
        var pipeline    = new UpcasterPipeline([
            new UpcasterRegistration(
                typeof(OrderCreatedV1),
                typeof(OrderCreated),
                o => { var v = (OrderCreatedV1)o; return new OrderCreated(v.OrderId, "subbed"); })
        ]);

        var store = new EventStore(adapter, serializer, registry, pipeline);

        // Write a V1 event before subscribing
        var v1 = new OrderCreatedV1("order-2");
        await store.AppendAsync(
            new StreamId("orders-2"),
            new ReadOnlyMemory<object>([v1]),
            StreamPosition.Start);

        // Act — subscribe and collect the first delivered event
        var received = new List<EventEnvelope>();
        var tcs = new TaskCompletionSource();

        var sub = await store.SubscribeAsync(
            new StreamId("orders-2"),
            StreamPosition.Start,
            (e, _) =>
            {
                received.Add(e);
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await sub.StartAsync();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — the V1 event must arrive as the current OrderCreated type with the mapped CustomerId
        received.Should().ContainSingle();
        received[0].Event.Should().BeOfType<OrderCreated>()
            .Which.CustomerId.Should().Be("subbed");
    }
}
