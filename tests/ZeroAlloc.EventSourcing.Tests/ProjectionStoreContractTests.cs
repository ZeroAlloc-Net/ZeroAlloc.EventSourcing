using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public abstract class ProjectionStoreContractTests
{
    protected abstract IProjectionStore CreateStore();

    [Fact]
    public async Task Load_NonExistentKey_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.LoadAsync("missing-key");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrips()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", """{"count":5}""");
        var result = await store.LoadAsync("orders");
        result.Should().Be("""{"count":5}""");
    }

    [Fact]
    public async Task Save_Overwrites_ExistingState()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", """{"count":1}""");
        await store.SaveAsync("orders", """{"count":2}""");
        var result = await store.LoadAsync("orders");
        result.Should().Be("""{"count":2}""");
    }

    [Fact]
    public async Task Clear_RemovesState()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", """{"count":1}""");
        await store.ClearAsync("orders");
        var result = await store.LoadAsync("orders");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Clear_NonExistentKey_DoesNotThrow()
    {
        var store = CreateStore();
        var act = async () => await store.ClearAsync("nonexistent");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MultipleKeys_AreIndependent()
    {
        var store = CreateStore();
        await store.SaveAsync("proj-a", "state-a");
        await store.SaveAsync("proj-b", "state-b");
        (await store.LoadAsync("proj-a")).Should().Be("state-a");
        (await store.LoadAsync("proj-b")).Should().Be("state-b");
    }
}
