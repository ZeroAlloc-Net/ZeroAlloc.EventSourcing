using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Serialisation;

// The quick start from README.md, copied as it appears there between "--- snippet ---" markers and
// compiled with the real EventSourcing and Serialisation source generators. Its types live in
// their own namespace so they do not collide with the shared Order domain in Domain.cs. When the
// README quick start changes, change it here as well.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.ReadmeQuickStart;

// --- snippet: types ---
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)] public sealed record OrderPlaced(string Customer);
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)] public sealed record ItemAdded(decimal Price);
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(ItemAdded))]
internal sealed partial class OrderJson : JsonSerializerContext;

public readonly record struct OrderId(Guid Value);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public int Items { get; private set; }
    public decimal Total { get; private set; }

    internal OrderState Apply(OrderPlaced e) => this;
    internal OrderState Apply(ItemAdded e) => this with { Items = Items + 1, Total = Total + e.Price };
}

// ApplyEvent dispatch and OrderEventTypeRegistry are source-generated: no reflection.
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string customer) => Raise(new OrderPlaced(customer));
    public void AddItem(decimal price) => Raise(new ItemAdded(price));
}
// --- end snippet ---

public sealed class ReadmeQuickStartTests
{
    [Fact]
    public async Task QuickStart_SavesAndReloadsTheAggregate()
    {
        // --- snippet: program ---
        var services = new ServiceCollection()
            .AddOrderPlacedSerializer().AddItemAddedSerializer().AddSerializerDispatcher()
            .AddSingleton<IEventTypeRegistry, OrderEventTypeRegistry>(); // emitted by the generator
        services.AddEventSourcing()
            .UseInMemoryEventStore() // or UsePostgreSqlEventStore, UseSqlServerEventStore, UseSqliteEventStore
            .UseAggregateRepository<Order, OrderId>(() => new Order(), id => new StreamId($"order-{id.Value}"));
        await using var provider = services.BuildServiceProvider();
        var orders = provider.GetRequiredService<IAggregateRepository<Order, OrderId>>();

        var id = new OrderId(Guid.NewGuid());
        using (var order = new Order())
        {
            order.Place("alice");
            order.AddItem(9.95m);
            await orders.SaveAsync(order, id); // appends two events, checks the expected version
        }

        using var loaded = (await orders.LoadAsync(id)).Value; // replays the stream
        Console.WriteLine($"{loaded.State.Items} item, total {loaded.State.Total}"); // 1 item, total 9.95
        // --- end snippet ---

        loaded.Version.Value.Should().Be(2);
        loaded.State.Items.Should().Be(1);
        loaded.State.Total.Should().Be(9.95m);
    }
}
