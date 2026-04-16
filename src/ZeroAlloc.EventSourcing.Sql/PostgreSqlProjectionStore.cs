using Npgsql;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL implementation of <see cref="IProjectionStore"/>.
/// Stores projection state in a <c>projection_states</c> table using atomic upsert.
/// </summary>
public sealed class PostgreSqlProjectionStore : IProjectionStore
{
    private readonly NpgsqlDataSource _dataSource;
    private const string TableName = "projection_states";

    /// <summary>
    /// Initializes a new instance of <see cref="PostgreSqlProjectionStore"/>.
    /// </summary>
    /// <param name="dataSource">NpgsqlDataSource for connection pooling.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="dataSource"/> is null.</exception>
    public PostgreSqlProjectionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Creates the <c>projection_states</c> table if it does not already exist.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {TableName} (
                projection_key VARCHAR(256) PRIMARY KEY,
                state TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );
        ";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask SaveAsync(string key, string state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            INSERT INTO {TableName} (projection_key, state, updated_at)
            VALUES (@projection_key, @state, NOW())
            ON CONFLICT (projection_key) DO UPDATE SET
                state = EXCLUDED.state,
                updated_at = EXCLUDED.updated_at;
        ";
        command.Parameters.AddWithValue("@projection_key", key);
        command.Parameters.AddWithValue("@state", state);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<string?> LoadAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT state FROM {TableName} WHERE projection_key = @projection_key";
        command.Parameters.AddWithValue("@projection_key", key);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null && result != DBNull.Value
            ? (string)result
            : null;
    }

    /// <inheritdoc/>
    public async ValueTask ClearAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE projection_key = @projection_key";
        command.Parameters.AddWithValue("@projection_key", key);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
