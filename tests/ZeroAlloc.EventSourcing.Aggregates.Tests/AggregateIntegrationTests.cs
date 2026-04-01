using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

/// <summary>
/// Full roundtrip: aggregate command → save → load → verify state.
/// Uses the source-generated ApplyEvent (ProductAggregate from SourceGeneratorTests.cs)
/// and the source-generated ProductAggregateEventTypeRegistry — no manual boilerplate.
/// </summary>
public class AggregateIntegrationTests
{
    private static (AggregateRepository<ProductAggregate, ProductId> repo, IEventStore store) BuildRepo()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var registry = new ProductAggregateEventTypeRegistry(); // source-generated
        var serializer = new JsonAggregateSerializer();         // from AggregateRepositoryTests.cs
        var store = new EventStore(adapter, serializer, registry);
        var repo = new AggregateRepository<ProductAggregate, ProductId>(
            store,
            () => new ProductAggregate(),
            id => new StreamId($"product-{id.Value}"));
        return (repo, store);
    }

    [Fact]
    public async Task FullRoundtrip_CreateProduct_SaveAndLoad_StateRestored()
    {
        var (repo, _) = BuildRepo();
        var id = new ProductId(Guid.NewGuid());

        // 1. Command
        var product = new ProductAggregate();
        product.SetId(id);
        product.Create("Widget Pro");

        // 2. Save
        var saveResult = await repo.SaveAsync(product, id);
        saveResult.IsSuccess.Should().BeTrue();

        // 3. Load fresh
        var loadResult = await repo.LoadAsync(id);
        loadResult.IsSuccess.Should().BeTrue();

        var loaded = loadResult.Value;
        loaded.State.IsCreated.Should().BeTrue();
        loaded.State.Name.Should().Be("Widget Pro");
        loaded.Version.Value.Should().Be(1);
        loaded.OriginalVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task FullRoundtrip_MultipleEvents_AllStatesRestored()
    {
        var (repo, _) = BuildRepo();
        var id = new ProductId(Guid.NewGuid());

        var product = new ProductAggregate();
        product.SetId(id);
        product.Create("Gadget");
        product.Discontinue();

        await repo.SaveAsync(product, id);

        var loaded = (await repo.LoadAsync(id)).Value;
        loaded.State.IsCreated.Should().BeTrue();
        loaded.State.IsDiscontinued.Should().BeTrue();
        loaded.Version.Value.Should().Be(2);
    }

    [Fact]
    public async Task FullRoundtrip_SaveTwice_SecondSaveFails_WithConflict()
    {
        var (repo, _) = BuildRepo();
        var id = new ProductId(Guid.NewGuid());

        var p1 = new ProductAggregate();
        p1.SetId(id);
        p1.Create("A");
        await repo.SaveAsync(p1, id);

        // Stale aggregate — OriginalVersion is still 0
        var p2 = new ProductAggregate();
        p2.SetId(id);
        p2.Create("B");

        var result = await repo.SaveAsync(p2, id);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task FullRoundtrip_SaveUpdatesOriginalVersion_CanSaveAgain()
    {
        var (repo, _) = BuildRepo();
        var id = new ProductId(Guid.NewGuid());

        var product = new ProductAggregate();
        product.SetId(id);
        product.Create("Widget");
        await repo.SaveAsync(product, id);

        // OriginalVersion should now be 1 — next save should succeed
        product.Discontinue();
        var result = await repo.SaveAsync(product, id);
        result.IsSuccess.Should().BeTrue();

        var loaded = (await repo.LoadAsync(id)).Value;
        loaded.State.IsDiscontinued.Should().BeTrue();
        loaded.Version.Value.Should().Be(2);
    }
}
