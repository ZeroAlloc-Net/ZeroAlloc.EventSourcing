using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Health check for PostgreSQL-backed event store adapters.
/// Executes <c>SELECT 1</c> via the provided <see cref="NpgsqlDataSource"/>.
/// </summary>
public sealed class PostgreSqlEventStoreHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Initializes the health check with the given data source.</summary>
    public PostgreSqlEventStoreHealthCheck(NpgsqlDataSource dataSource)
        => _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Initializes the health check by creating an owned data source from <paramref name="connectionString"/>.</summary>
    internal PostgreSqlEventStoreHealthCheck(string connectionString)
        : this(NpgsqlDataSource.Create(connectionString ?? throw new ArgumentNullException(nameof(connectionString))))
    {
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            #pragma warning disable MA0004
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            #pragma warning restore MA0004
            #pragma warning disable MA0004
            await using var cmd = conn.CreateCommand();
            #pragma warning restore MA0004
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }
    }
}
