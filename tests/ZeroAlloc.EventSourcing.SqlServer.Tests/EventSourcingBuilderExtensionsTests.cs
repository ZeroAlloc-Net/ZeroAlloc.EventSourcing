using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

public class EventSourcingBuilderExtensionsTests
{
    private const string FakeConnectionString =
        "Server=localhost;Database=test;User Id=sa;Password=test;TrustServerCertificate=true";

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventTypeRegistry>());
        services.AddSingleton(Substitute.For<ISerializerDispatcher>());
        return services;
    }

    [Fact]
    public void UseSqlServerEventStore_RegistersIEventStoreAdapter()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseSqlServerEventStore(FakeConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStoreAdapter>().Should().BeOfType<SqlServerEventStoreAdapter>();
    }

    [Fact]
    public void UseSqlServerEventStore_RegistersIEventStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseSqlServerEventStore(FakeConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStore>().Should().BeOfType<EventStore>();
    }

    [Fact]
    public void UseSqlServerEventStore_DoesNotOverwriteUserAdapter()
    {
        var services = BaseServices();
        var custom = Substitute.For<IEventStoreAdapter>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseSqlServerEventStore(FakeConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStoreAdapter>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseSqlServerEventStore_DoesNotOverwriteUserEventStore()
    {
        var services = BaseServices();
        var custom = Substitute.For<IEventStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseSqlServerEventStore(FakeConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseSqlServerEventStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();

        var result = builder.UseSqlServerEventStore(FakeConnectionString);

        result.Should().BeSameAs(builder);
    }
}
