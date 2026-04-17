using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.SqlServer;

/// <summary>
/// Health check for the SQL Server event store adapter.
/// Opens a connection and executes <c>SELECT 1</c>.
/// </summary>
public sealed class SqlServerEventStoreHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    /// <summary>Initializes the health check with the given connection string.</summary>
    public SqlServerEventStoreHealthCheck(string connectionString)
        => _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            #pragma warning disable MA0004
            await using var conn = new SqlConnection(_connectionString);
            #pragma warning restore MA0004
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
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
