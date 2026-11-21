using Npgsql;
using NpgsqlTypes;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL implementation of <see cref="IProjectionStore"/>.
/// Stores projection state in the <c>BYTEA</c> <c>state</c> column of a <c>projection_states</c> table
/// using atomic upsert.
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
    /// Creates the <c>projection_states</c> table if it does not already exist, and converts the
    /// <c>TEXT</c> <c>state</c> column of a table created by a 1.x version to <c>BYTEA</c>.
    /// This method is idempotent and safe to call multiple times, also from app instances that
    /// start together.
    /// </summary>
    /// <remarks>
    /// The conversion encodes each stored state as UTF-8, the bytes a text serializer such as
    /// System.Text.Json writes, so states saved by 1.x load unchanged. It runs in one transaction
    /// under a transaction-scoped advisory lock, so app instances that start together convert the
    /// table once between them.
    /// </remarks>
    /// <param name="ct">A cancellation token.</param>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = EnsureSchemaSql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private const string EnsureSchemaSql = $"""
        SELECT pg_advisory_xact_lock(hashtextextended('ZeroAlloc.EventSourcing.Sql.schema:{TableName}', 0));

        CREATE TABLE IF NOT EXISTS {TableName} (
            projection_key VARCHAR(256) PRIMARY KEY,
            state BYTEA NOT NULL,
            updated_at TIMESTAMPTZ NOT NULL
        );

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = '{TableName}'
                  AND column_name = 'state'
                  AND data_type = 'text'
            ) THEN
                ALTER TABLE {TableName} ALTER COLUMN state TYPE BYTEA USING convert_to(state, 'UTF8');
            END IF;
        END $$;
        """;

    /// <inheritdoc/>
    public async ValueTask SaveAsync(string key, ReadOnlyMemory<byte> state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ValidateKey(key);

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
        command.Parameters.Add(new NpgsqlParameter<ReadOnlyMemory<byte>>("@state", NpgsqlDbType.Bytea) { TypedValue = state });

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ValidateKey(key);

        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT state FROM {TableName} WHERE projection_key = @projection_key";
        command.Parameters.AddWithValue("@projection_key", key);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is byte[] state ? (ReadOnlyMemory<byte>?)state : null;
    }

    /// <inheritdoc/>
    public async ValueTask ClearAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ValidateKey(key);

        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE projection_key = @projection_key";
        command.Parameters.AddWithValue("@projection_key", key);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void ValidateKey(string key)
    {
        if (key.Length > 256)
            throw new ArgumentException("Projection key must not exceed 256 characters.", nameof(key));
    }
}
