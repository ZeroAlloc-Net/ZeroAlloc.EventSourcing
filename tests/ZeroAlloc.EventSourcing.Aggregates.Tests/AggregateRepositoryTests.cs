using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

// Reuse Order, OrderId, OrderState, OrderPlacedEvent, OrderShippedEvent from AggregateTests.cs

public class AggregateRepositoryTests
{
    private static (IEventStore store, AggregateRepository<Order, OrderId> repo) BuildRepo()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = new JsonAggregateSerializer();
        var registry = new OrderEventTypeRegistry();
        var store = new EventStore(adapter, serializer, registry);
        var repo = new AggregateRepository<Order, OrderId>(store, () => new Order(), id => new StreamId($"order-{id.Value}"));
        return (store, repo);
    }

    [Fact]
    public async Task Load_NonExistentStream_ReturnsNewEmptyAggregate()
    {
        var (_, repo) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        var result = await repo.LoadAsync(id);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.IsPlaced.Should().BeFalse();
        result.Value.Version.Should().Be(StreamPosition.Start);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsAggregate()
    {
        var (_, repo) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        var order = new Order();
        order.SetId(id);
        order.Place("ORD-1", 99.99m);

        await repo.SaveAsync(order, id);

        var loaded = await repo.LoadAsync(id);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.State.IsPlaced.Should().BeTrue();
        loaded.Value.State.Total.Should().Be(99.99m);
        loaded.Value.Version.Value.Should().Be(1);
        loaded.Value.OriginalVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task Save_SetsOriginalVersionAfterSave()
    {
        var (_, repo) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        var order = new Order();
        order.SetId(id);
        order.Place("ORD-1", 10m);

        var result = await repo.SaveAsync(order, id);

        result.IsSuccess.Should().BeTrue();
        // After save: uncommitted cleared, OriginalVersion reflects committed position
        order.DequeueUncommitted().Length.Should().Be(0);
        order.OriginalVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task Save_WithConcurrentModification_ReturnsConflict()
    {
        var (_, repo) = BuildRepo();
        var id = new OrderId(Guid.NewGuid());

        // Save first version
        var order1 = new Order();
        order1.SetId(id);
        order1.Place("ORD-1", 10m);
        await repo.SaveAsync(order1, id);

        // Simulate second writer — also at version 0 (stale)
        var order2 = new Order();
        order2.SetId(id);
        order2.Place("ORD-1", 20m);
        // order2.OriginalVersion is still 0 because we loaded nothing

        var result = await repo.SaveAsync(order2, id);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }
}

// Test helpers — inline serializer (registry is source-generated as OrderEventTypeRegistry)
internal sealed class JsonAggregateSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}
