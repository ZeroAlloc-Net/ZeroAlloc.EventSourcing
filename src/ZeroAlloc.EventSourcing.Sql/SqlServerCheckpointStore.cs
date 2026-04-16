using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// SQL Server implementation of <see cref="ICheckpointStore"/>.
/// Stores consumer positions in a <c>dbo.consumer_checkpoints</c> table using atomic MERGE upsert.
/// </summary>
public sealed class SqlServerCheckpointStore : ICheckpointStore
{
    private readonly string _connectionString;
    private const string TableName = "dbo.consumer_checkpoints";

    /// <summary>
    /// Initializes a new instance of <see cref="SqlServerCheckpointStore"/>.
    /// </summary>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="connectionString"/> is null, empty, or whitespace-only.</exception>
    public SqlServerCheckpointStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>
    /// Creates the <c>dbo.consumer_checkpoints</c> table in SQL Server if it does not already exist.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
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
                WHERE s.name = 'dbo' AND t.name = 'consumer_checkpoints'
            )
            BEGIN
                CREATE TABLE dbo.consumer_checkpoints (
                    consumer_id VARCHAR(256) NOT NULL PRIMARY KEY,
                    position    BIGINT       NOT NULL,
                    updated_at  DATETIME2    NOT NULL
                )
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"SELECT position FROM {TableName} WHERE consumer_id = @consumer_id";
        cmd.Parameters.AddWithValue("@consumer_id", consumerId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null && result != DBNull.Value
            ? new StreamPosition((long)result)
            : null;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string consumerId, StreamPosition position, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"""
            MERGE INTO {TableName} AS target
            USING (SELECT @consumer_id AS consumer_id) AS source
            ON target.consumer_id = source.consumer_id
            WHEN MATCHED THEN UPDATE SET
                position   = @position,
                updated_at = @updated_at
            WHEN NOT MATCHED THEN INSERT (consumer_id, position, updated_at)
                VALUES (@consumer_id, @position, @updated_at);
            """;
        cmd.Parameters.AddWithValue("@consumer_id", consumerId);
        cmd.Parameters.AddWithValue("@position", position.Value);
        cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string consumerId, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"DELETE FROM {TableName} WHERE consumer_id = @consumer_id";
        cmd.Parameters.AddWithValue("@consumer_id", consumerId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void ValidateConsumerId(string consumerId)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));
        if (consumerId.Length > 256)
            throw new ArgumentException("Consumer ID cannot exceed 256 characters", nameof(consumerId));
    }
}
