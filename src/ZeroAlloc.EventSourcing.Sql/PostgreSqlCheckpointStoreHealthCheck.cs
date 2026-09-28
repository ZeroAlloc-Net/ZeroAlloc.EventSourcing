using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Health check for the PostgreSQL checkpoint store.
/// Executes <c>SELECT 1</c> via the provided <see cref="NpgsqlDataSource"/>.
/// </summary>
public sealed class PostgreSqlCheckpointStoreHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly string? _connectionString;

    /// <summary>Initializes the health check with the given data source.</summary>
    public PostgreSqlCheckpointStoreHealthCheck(NpgsqlDataSource dataSource)
        => _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Initializes the health check to open a connection from Npgsql's shared pool for
    /// <paramref name="connectionString"/> on each check, and return it afterwards.
    /// </summary>
    /// <remarks>
    /// The health check system creates a new instance for every run and never disposes it, so
    /// this instance must not own a <see cref="NpgsqlDataSource"/>: each one would leave its
    /// pooled physical connection open.
    /// </remarks>
    internal PostgreSqlCheckpointStoreHealthCheck(string connectionString)
        => _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            #pragma warning disable MA0004
            await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is not null)
            return await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
