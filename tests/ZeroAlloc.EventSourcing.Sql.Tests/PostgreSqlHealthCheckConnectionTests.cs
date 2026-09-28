using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ZeroAlloc.EventSourcing.Testing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// The health check system builds a new health check instance for every run and never disposes
/// it. A connection-string health check that owned an <see cref="NpgsqlDataSource"/> therefore
/// left one physical connection open per run. These tests run the checks repeatedly and count
/// the server-side connections they leave behind.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlHealthCheckConnectionTests(PostgreSqlContainerFixture fixture) : IAsyncLifetime
{
    private const int Runs = 10;

    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task EventStoreCheck_WithConnectionString_DoesNotLeaveAConnectionOpenPerRun()
        => (await OpenConnectionsAfterRuns("hc-event-store", (b, cs) => b.AddPostgreSqlEventStore(cs)))
            .Should().BeLessThanOrEqualTo(1);

    [Fact]
    public async Task CheckpointStoreCheck_WithConnectionString_DoesNotLeaveAConnectionOpenPerRun()
        => (await OpenConnectionsAfterRuns("hc-checkpoint-store", (b, cs) => b.AddPostgreSqlCheckpointStore(cs)))
            .Should().BeLessThanOrEqualTo(1);

    private async Task<long> OpenConnectionsAfterRuns(
        string applicationName, Action<IHealthChecksBuilder, string> add)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_database.GetConnectionString())
        {
            ApplicationName = applicationName,
        }.ConnectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        add(services.AddHealthChecks(), connectionString);
        await using var provider = services.BuildServiceProvider();
        var healthChecks = provider.GetRequiredService<HealthCheckService>();

        for (var i = 0; i < Runs; i++)
        {
            var report = await healthChecks.CheckHealthAsync();
            report.Status.Should().Be(HealthStatus.Healthy);
        }

        var probe = new NpgsqlConnectionStringBuilder(_database.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(probe);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name", connection);
        command.Parameters.AddWithValue("name", applicationName);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
