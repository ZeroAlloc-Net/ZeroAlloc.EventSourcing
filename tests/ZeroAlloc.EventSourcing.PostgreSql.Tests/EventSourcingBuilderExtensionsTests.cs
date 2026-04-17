using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.PostgreSql.Tests;

public class EventSourcingBuilderExtensionsTests
{
    private const string FakeConnectionString = "Host=localhost;Database=test;Username=test;Password=test";

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventTypeRegistry>());
        services.AddSingleton(Substitute.For<ISerializerDispatcher>());
        return services;
    }

    [Fact]
    public void UsePostgreSqlEventStore_RegistersIEventStoreAdapter()
    {
        var services = BaseServices();
        services.AddEventSourcing().UsePostgreSqlEventStore(FakeConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStoreAdapter>().Should().BeOfType<PostgreSqlEventStoreAdapter>();
    }

    [Fact]
    public void UsePostgreSqlEventStore_RegistersIEventStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UsePostgreSqlEventStore(FakeConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStore>().Should().BeOfType<EventStore>();
    }

    [Fact]
    public void UsePostgreSqlEventStore_DoesNotOverwriteUserAdapter()
    {
        var services = BaseServices();
        var custom = Substitute.For<IEventStoreAdapter>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UsePostgreSqlEventStore(FakeConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStoreAdapter>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UsePostgreSqlEventStore_DoesNotOverwriteUserEventStore()
    {
        var services = BaseServices();
        var custom = Substitute.For<IEventStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UsePostgreSqlEventStore(FakeConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UsePostgreSqlEventStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();

        var result = builder.UsePostgreSqlEventStore(FakeConnectionString);

        result.Should().BeSameAs(builder);
    }
}
