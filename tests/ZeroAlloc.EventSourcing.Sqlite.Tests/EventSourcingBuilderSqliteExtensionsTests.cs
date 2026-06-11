using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.Sqlite.Tests;

public class EventSourcingBuilderSqliteExtensionsTests
{
    private static (IServiceCollection services, SqliteConnection keepAlive, string connectionString) BaseServices()
    {
        var connectionString = $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventTypeRegistry>());
        services.AddSingleton(Substitute.For<ISerializerDispatcher>());
        return (services, keepAlive, connectionString);
    }

    [Fact]
    public void UseSqliteEventStore_registers_adapter_as_singleton()
    {
        var (services, keepAlive, connectionString) = BaseServices();
        using var _ = keepAlive;

        services.AddEventSourcing().UseSqliteEventStore(connectionString, ensureSchema: true);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IEventStoreAdapter>();
        resolved.Should().BeOfType<SqliteEventStoreAdapter>();
    }

    [Fact]
    public void UseSqliteEventStore_throws_on_null_builder()
    {
        EventSourcingBuilder? builder = null;
        var act = () => builder!.UseSqliteEventStore("Data Source=:memory:");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UseSqliteEventStore_throws_on_null_or_whitespace_connection_string()
    {
        var services = new ServiceCollection();
        var builder = services.AddEventSourcing();
        var act = () => builder.UseSqliteEventStore("   ");
        act.Should().Throw<ArgumentException>();
    }
}
