using AwesomeAssertions;
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
    public void UseInMemorySnapshotStoreOfTState_RegistersInMemorySnapshotStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseInMemorySnapshotStore<TestState>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISnapshotStore<TestState>>()
                .Should().BeOfType<InMemorySnapshotStore<TestState>>();
    }

    // NativeAOT refuses to build a value-type instantiation of an open-generic registration,
    // and TState is always a value type, so the registration must be closed over TState.
    // The JIT has no such check, so this pins the shape; the AOT smoke proves the resolve.
    [Fact]
    public void UseInMemorySnapshotStoreOfTState_RegistersClosedServiceType()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseInMemorySnapshotStore<TestState>();

        services.Should().ContainSingle(d => d.ServiceType == typeof(ISnapshotStore<TestState>));
        services.Should().NotContain(d => d.ServiceType == typeof(ISnapshotStore<>));
    }

    [Fact]
    public void UseInMemorySnapshotStoreOfTState_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UseInMemorySnapshotStore<TestState>().Should().BeSameAs(builder);
    }

    [Fact]
    public void UseInMemorySnapshotStoreOfTState_DoesNotOverwriteUserSnapshotStore()
    {
        var services = BaseServices();
        var custom = new StubSnapshotStore();
        services.AddSingleton<ISnapshotStore<TestState>>(custom);

        services.AddEventSourcing().UseInMemorySnapshotStore<TestState>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISnapshotStore<TestState>>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseInMemorySnapshotStore_Obsolete_RegistersOpenGeneric()
    {
        var services = BaseServices();
#pragma warning disable ZAES003 // the deprecated open-generic registration is still under test
        services.AddEventSourcing().UseInMemorySnapshotStore();
#pragma warning restore ZAES003

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
    public void UseInMemorySnapshotStore_Obsolete_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
#pragma warning disable ZAES003 // the deprecated open-generic registration is still under test
        builder.UseInMemorySnapshotStore().Should().BeSameAs(builder);
#pragma warning restore ZAES003
    }

    [Fact]
    public void UseInMemoryDeadLetterStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UseInMemoryDeadLetterStore().Should().BeSameAs(builder);
    }

    [Fact]
    public void UseInMemoryProjectionStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UseInMemoryProjectionStore().Should().BeSameAs(builder);
    }

    [Fact]
    public void UseInMemorySnapshotStore_Obsolete_DoesNotOverwriteUserSnapshotStore()
    {
        var services = BaseServices();
        // NSubstitute/Castle cannot proxy ISnapshotStore<TestState> when TestState is a private struct
        // (Castle DynamicProxy requires type parameter to be accessible). Use a hand-written stub.
        var custom = new StubSnapshotStore();
        services.AddSingleton<ISnapshotStore<TestState>>(custom);

#pragma warning disable ZAES003 // the deprecated open-generic registration is still under test
        services.AddEventSourcing().UseInMemorySnapshotStore();
#pragma warning restore ZAES003

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
