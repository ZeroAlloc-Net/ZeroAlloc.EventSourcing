using Npgsql;
using NpgsqlTypes;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL implementation of <see cref="ISnapshotStore{TState}"/>.
/// Stores snapshots in a table with BYTEA payload and atomic ON CONFLICT upsert.
/// </summary>
/// <typeparam name="TState">The aggregate state type. Must match the type used in snapshots.</typeparam>
public sealed class PostgreSqlSnapshotStore<TState> : ISnapshotStore<TState> where TState : struct
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEventSerializer? _serializer;

    /// <summary>
    /// Initializes a new instance of <see cref="PostgreSqlSnapshotStore{TState}"/>.
    /// </summary>
    /// <param name="dataSource">The <see cref="NpgsqlDataSource"/> to use for connections.</param>
    /// <param name="serializer">Optional serializer for state objects. If null, serialization is not supported.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="dataSource"/> is null.</exception>
    public PostgreSqlSnapshotStore(NpgsqlDataSource dataSource, IEventSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _serializer = serializer;
    }

    /// <summary>
    /// Creates the snapshots table in PostgreSQL if it does not already exist.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        await SnapshotSchema.EnsurePostgreSqlSchemaAsync(_dataSource, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<(StreamPosition Position, TState State)?> ReadAsync(StreamId streamId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT position, state_type, payload
            FROM snapshots
            WHERE stream_id = @stream_id
            """;
        cmd.Parameters.AddWithValue("@stream_id", streamId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var position = new StreamPosition(reader.GetInt64(0));
            var stateType = reader.GetString(1);
            var payload = (byte[])reader.GetValue(2);

            // Type validation: if stateType doesn't match TState, return null (treat as missing)
            if (stateType != typeof(TState).FullName)
            {
                return null;
            }

            // If no serializer, we cannot deserialize
            if (_serializer == null)
            {
                return null;
            }

            var state = (TState)_serializer.Deserialize(new ReadOnlyMemory<byte>(payload), typeof(TState));
            return (position, state);
        }

        return null;
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(StreamId streamId, StreamPosition position, TState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // If no serializer, we cannot serialize
        if (_serializer == null)
        {
            return;
        }

        var serializedState = _serializer.Serialize(state);
        var stateType = typeof(TState).FullName ?? typeof(TState).Name;
        var createdAt = DateTime.UtcNow;

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots (stream_id, position, state_type, payload, created_at)
            VALUES (@stream_id, @position, @state_type, @payload, @created_at)
            ON CONFLICT (stream_id) DO UPDATE SET
                position = EXCLUDED.position,
                state_type = EXCLUDED.state_type,
                payload = EXCLUDED.payload,
                created_at = EXCLUDED.created_at
            """;

        cmd.Parameters.AddWithValue("@stream_id", streamId.Value);
        cmd.Parameters.AddWithValue("@position", position.Value);
        cmd.Parameters.AddWithValue("@state_type", stateType);
        cmd.Parameters.Add("@payload", NpgsqlDbType.Bytea).Value = serializedState.ToArray();
        cmd.Parameters.AddWithValue("@created_at", createdAt);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
