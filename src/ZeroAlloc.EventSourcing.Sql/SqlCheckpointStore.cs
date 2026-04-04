using Npgsql;
using System.Data;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL checkpoint store for persisting consumer positions.
/// Uses atomic upsert for last-write-wins semantics.
/// </summary>
public sealed class PostgreSqlCheckpointStore : ICheckpointStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private const string TableName = "consumer_checkpoints";

    /// <summary>
    /// Create checkpoint store with connection pool.
    /// </summary>
    /// <param name="dataSource">NpgsqlDataSource for connection pooling</param>
    public PostgreSqlCheckpointStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>
    /// Create consumer_checkpoints table if it doesn't exist.
    /// </summary>
    public async Task EnsureSchemaAsync()
    {
        using var connection = await _dataSource.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {TableName} (
                consumer_id VARCHAR(256) PRIMARY KEY,
                position BIGINT NOT NULL,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
        ";
        await command.ExecuteNonQueryAsync();
    }

    /// <inheritdoc/>
    public async Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var connection = await _dataSource.OpenConnectionAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT position FROM {TableName} WHERE consumer_id = @consumer_id";
        command.Parameters.AddWithValue("@consumer_id", consumerId);

        var result = await command.ExecuteScalarAsync(ct);
        return result != null && result != DBNull.Value
            ? new StreamPosition((long)result)
            : null;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string consumerId, StreamPosition position, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var connection = await _dataSource.OpenConnectionAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            INSERT INTO {TableName} (consumer_id, position, updated_at)
            VALUES (@consumer_id, @position, CURRENT_TIMESTAMP)
            ON CONFLICT (consumer_id) DO UPDATE SET
                position = EXCLUDED.position,
                updated_at = CURRENT_TIMESTAMP;
        ";
        command.Parameters.AddWithValue("@consumer_id", consumerId);
        command.Parameters.AddWithValue("@position", position.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string consumerId, CancellationToken ct = default)
    {
        ValidateConsumerId(consumerId);

        using var connection = await _dataSource.OpenConnectionAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE consumer_id = @consumer_id";
        command.Parameters.AddWithValue("@consumer_id", consumerId);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateConsumerId(string consumerId)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));
        if (consumerId.Length > 256)
            throw new ArgumentException("Consumer ID cannot exceed 256 characters", nameof(consumerId));
    }

    /// <inheritdoc/>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
