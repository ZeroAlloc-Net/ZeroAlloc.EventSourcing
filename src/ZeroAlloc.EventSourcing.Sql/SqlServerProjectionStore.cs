using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// SQL Server implementation of <see cref="IProjectionStore"/>.
/// Stores projection state in a <c>dbo.projection_states</c> table using atomic MERGE upsert.
/// </summary>
public sealed class SqlServerProjectionStore : IProjectionStore
{
    private readonly string _connectionString;
    private const string TableName = "dbo.projection_states";

    /// <summary>
    /// Initializes a new instance of <see cref="SqlServerProjectionStore"/>.
    /// </summary>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="connectionString"/> is null, empty, or whitespace-only.</exception>
    public SqlServerProjectionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>
    /// Creates the <c>dbo.projection_states</c> table in SQL Server if it does not already exist.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE s.name = 'dbo' AND t.name = 'projection_states'
            )
            BEGIN
                CREATE TABLE dbo.projection_states (
                    projection_key VARCHAR(256)   NOT NULL PRIMARY KEY,
                    state          NVARCHAR(MAX)  NOT NULL,
                    updated_at     DATETIME2      NOT NULL
                )
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask SaveAsync(string key, string state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"""
            MERGE INTO {TableName} AS target
            USING (SELECT @projection_key AS projection_key) AS source
            ON target.projection_key = source.projection_key
            WHEN MATCHED THEN UPDATE SET
                state      = @state,
                updated_at = @updated_at
            WHEN NOT MATCHED THEN INSERT (projection_key, state, updated_at)
                VALUES (@projection_key, @state, @updated_at);
            """;
        cmd.Parameters.AddWithValue("@projection_key", key);
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<string?> LoadAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"SELECT state FROM {TableName} WHERE projection_key = @projection_key";
        cmd.Parameters.AddWithValue("@projection_key", key);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null && result != DBNull.Value
            ? (string)result
            : null;
    }

    /// <inheritdoc/>
    public async ValueTask ClearAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"DELETE FROM {TableName} WHERE projection_key = @projection_key";
        cmd.Parameters.AddWithValue("@projection_key", key);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
