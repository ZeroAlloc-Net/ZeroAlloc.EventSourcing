using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.Sqlite;

/// <summary>
/// Health check for the SQLite event store adapter.
/// Opens a connection and executes <c>SELECT 1</c>.
/// </summary>
public sealed class SqliteEventStoreHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    /// <summary>Initializes the health check with the given connection string.</summary>
    public SqliteEventStoreHealthCheck(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
#pragma warning disable MA0004
            await using var conn = new SqliteConnection(_connectionString);
#pragma warning restore MA0004
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
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
