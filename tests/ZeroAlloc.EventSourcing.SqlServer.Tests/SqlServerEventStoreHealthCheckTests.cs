using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroAlloc.EventSourcing.SqlServer;
using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerEventStoreHealthCheckTests(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task AddSqlServerEventStore_RegistersHealthCheckUnderExpectedName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks()
            .AddSqlServerEventStore(_database.GetConnectionString());

        await using var provider = services.BuildServiceProvider();
        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        report.Entries.Should().ContainKey("sqlserver-event-store");
        report.Entries["sqlserver-event-store"].Status.Should().Be(HealthStatus.Healthy);
    }
}
