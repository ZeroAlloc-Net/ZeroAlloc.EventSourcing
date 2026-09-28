using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlHealthCheckTests(PostgreSqlContainerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task AddPostgreSqlEventStore_RegistersHealthCheckUnderExpectedName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddPostgreSqlEventStore(_database.GetConnectionString());

        await using var provider = services.BuildServiceProvider();
        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        report.Entries.Should().ContainKey("postgresql-event-store");
        report.Entries["postgresql-event-store"].Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task AddPostgreSqlCheckpointStore_RegistersHealthCheckUnderExpectedName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddPostgreSqlCheckpointStore(_database.GetConnectionString());

        await using var provider = services.BuildServiceProvider();
        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        report.Entries.Should().ContainKey("postgresql-checkpoint-store");
        report.Entries["postgresql-checkpoint-store"].Status.Should().Be(HealthStatus.Healthy);
    }
}
