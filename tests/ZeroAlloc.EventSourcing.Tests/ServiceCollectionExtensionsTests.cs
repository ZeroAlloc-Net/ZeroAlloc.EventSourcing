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
        var serializer = provider.GetRequiredService<IEventSerializer>();
        Assert.IsType<ZeroAllocEventSerializer>(serializer);
    }

    [Fact]
    public void AddEventSourcing_DoesNotOverwriteUserSerializer()
    {
        var services = new ServiceCollection();
        var custom = Substitute.For<IEventSerializer>();
        services.AddSingleton(custom);

        services.AddEventSourcing();

        var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IEventSerializer>();
        Assert.Same(custom, serializer);
    }

    [Fact]
    public void AddEventSourcing_ReturnsServices_ForChaining()
    {
        var services = new ServiceCollection();
        var result = services.AddEventSourcing();
        Assert.Same(services, result);
    }
}
