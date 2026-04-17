using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddEventSourcing_RegistersZeroAllocEventSerializer()
    {
        var services = new ServiceCollection();
        var dispatcher = Substitute.For<ISerializerDispatcher>();
        services.AddSingleton(dispatcher);

        services.AddEventSourcing();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventSerializer>().Should().BeOfType<ZeroAllocEventSerializer>();
    }

    [Fact]
    public void AddEventSourcing_DoesNotOverwriteUserSerializer()
    {
        var services = new ServiceCollection();
        var custom = Substitute.For<IEventSerializer>();
        services.AddSingleton(custom);

        services.AddEventSourcing();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventSerializer>().Should().BeSameAs(custom);
    }

    [Fact]
    public void AddEventSourcing_ReturnsBuilder_WithSameServices()
    {
        var services = new ServiceCollection();

        var builder = services.AddEventSourcing();

        builder.Should().BeOfType<EventSourcingBuilder>();
        builder.Services.Should().BeSameAs(services);
    }

    [Fact]
    public void UseInMemoryCheckpointStore_RegistersInMemoryCheckpointStore()
    {
        var services = new ServiceCollection();

        services.AddEventSourcing().UseInMemoryCheckpointStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICheckpointStore>().Should().BeOfType<InMemoryCheckpointStore>();
    }

    [Fact]
    public void UseInMemoryCheckpointStore_DoesNotOverwriteUserRegistration()
    {
        var services = new ServiceCollection();
        var custom = Substitute.For<ICheckpointStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseInMemoryCheckpointStore();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICheckpointStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseInMemoryCheckpointStore_ReturnsBuilder_ForChaining()
    {
        var services = new ServiceCollection();
        var builder = services.AddEventSourcing();

        var result = builder.UseInMemoryCheckpointStore();

        result.Should().BeSameAs(builder);
    }
}
