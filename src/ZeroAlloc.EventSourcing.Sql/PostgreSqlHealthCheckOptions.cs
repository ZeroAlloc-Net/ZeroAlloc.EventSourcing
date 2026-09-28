using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Configures a PostgreSQL health check registered through
/// <see cref="EventSourcingBuilderExtensions.AddPostgreSqlEventStore(IHealthChecksBuilder, Action{PostgreSqlHealthCheckOptions})"/>
/// or
/// <see cref="EventSourcingBuilderExtensions.AddPostgreSqlCheckpointStore(IHealthChecksBuilder, Action{PostgreSqlHealthCheckOptions})"/>.
/// </summary>
/// <remarks>
/// Set exactly one of <see cref="ConnectionString"/> and <see cref="DataSource"/>.
/// </remarks>
public sealed class PostgreSqlHealthCheckOptions
{
    /// <summary>
    /// PostgreSQL connection string. Each health check invocation creates a new
    /// <see cref="NpgsqlDataSource"/> from it.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>An existing <see cref="NpgsqlDataSource"/> to share with the health check.</summary>
    public NpgsqlDataSource? DataSource { get; set; }

    /// <summary>
    /// Health check registration name. When <see langword="null"/>, the registering method's
    /// default is used: <c>postgresql-event-store</c> or <c>postgresql-checkpoint-store</c>.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Status to report on failure. When <see langword="null"/>, the health check system reports
    /// <see cref="HealthStatus.Unhealthy"/>.
    /// </summary>
    public HealthStatus? FailureStatus { get; set; }

    /// <summary>Tags for filtering health checks.</summary>
    public IEnumerable<string>? Tags { get; set; }
}
