using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public class EventSourcingBuilderExtensionsTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventTypeRegistry>());
        services.AddSingleton(Substitute.For<ISerializerDispatcher>());
        return services;
    }

    [Fact]
    public void UseInMemoryEventStore_RegistersIEventStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseInMemoryEventStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStore>().Should().BeOfType<EventStore>();
    }

    [Fact]
    public void UseInMemoryEventStore_RegistersIEventStoreAdapter()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseInMemoryEventStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStoreAdapter>().Should().BeOfType<InMemoryEventStoreAdapter>();
    }

    [Fact]
    public void UseInMemoryEventStore_DoesNotOverwriteUserAdapter()
    {
        var services = BaseServices();
        var custom = Substitute.For<IEventStoreAdapter>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseInMemoryEventStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStoreAdapter>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseInMemoryEventStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();

        var result = builder.UseInMemoryEventStore();

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void UseInMemorySnapshotStore_RegistersOpenGeneric()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseInMemorySnapshotStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISnapshotStore<TestState>>()
                .Should().BeOfType<InMemorySnapshotStore<TestState>>();
    }

    [Fact]
    public void UseInMemoryDeadLetterStore_RegistersInMemoryDeadLetterStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseInMemoryDeadLetterStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDeadLetterStore>().Should().BeOfType<InMemoryDeadLetterStore>();
    }

    [Fact]
    public void UseInMemoryProjectionStore_RegistersInMemoryProjectionStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseInMemoryProjectionStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionStore>().Should().BeOfType<InMemoryProjectionStore>();
    }

    [Fact]
    public void UseInMemoryEventStore_DoesNotOverwriteUserEventStore()
    {
        var services = BaseServices();
        var custom = Substitute.For<IEventStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseInMemoryEventStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseInMemorySnapshotStore_DoesNotOverwriteUserSnapshotStore()
    {
        var services = BaseServices();
        // NSubstitute/Castle cannot proxy ISnapshotStore<TestState> when TestState is a private struct
        // (Castle DynamicProxy requires type parameter to be accessible). Use a hand-written stub.
        var custom = new StubSnapshotStore();
        services.AddSingleton<ISnapshotStore<TestState>>(custom);

        services.AddEventSourcing().UseInMemorySnapshotStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISnapshotStore<TestState>>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseInMemoryDeadLetterStore_DoesNotOverwriteUserDeadLetterStore()
    {
        var services = BaseServices();
        var custom = Substitute.For<IDeadLetterStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseInMemoryDeadLetterStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDeadLetterStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseInMemoryProjectionStore_DoesNotOverwriteUserProjectionStore()
    {
        var services = BaseServices();
        var custom = Substitute.For<IProjectionStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseInMemoryProjectionStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionStore>().Should().BeSameAs(custom);
    }

    private struct TestState { }

    private sealed class StubSnapshotStore : ISnapshotStore<TestState>
    {
        public ValueTask<(StreamPosition Position, TestState State)?> ReadAsync(StreamId streamId, CancellationToken ct = default)
            => ValueTask.FromResult<(StreamPosition, TestState)?>(null);

        public ValueTask WriteAsync(StreamId streamId, StreamPosition position, TestState state, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}
