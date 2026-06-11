using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Sqlite;

/// <summary>
/// SQLite <see cref="IEventStoreAdapter"/> backed by a single <c>event_store</c> table.
/// Supports per-stream reads (cursor = per-stream position) and global reads via
/// <see cref="StreamId.Global"/> (cursor = global_position). SQLite cannot make a
/// composite-PK column AUTOINCREMENT, so <c>global_position</c> is hand-managed
/// inside each <c>BEGIN IMMEDIATE</c> write transaction.
/// </summary>
public sealed class SqliteEventStoreAdapter : IEventStoreAdapter
{
    private readonly string _connectionString;

    /// <summary>Initialises the adapter with a SQLite connection string.</summary>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=events.db</c>).</param>
    public SqliteEventStoreAdapter(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>
    /// Creates the <c>event_store</c> table and supporting indices if they do not already exist.
    /// Call once during application startup or test setup.
    /// </summary>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
#pragma warning disable MA0004
        await using var _ = conn;
#pragma warning restore MA0004
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS event_store (
                stream_id        TEXT     NOT NULL,
                position         INTEGER  NOT NULL,
                global_position  INTEGER  NOT NULL,
                event_type       TEXT     NOT NULL,
                event_id         TEXT     NOT NULL,
                occurred_at      TEXT     NOT NULL,
                correlation_id   TEXT     NULL,
                causation_id     TEXT     NULL,
                payload          BLOB     NOT NULL,
                PRIMARY KEY (stream_id, position)
            );
            CREATE INDEX IF NOT EXISTS event_store_global_position_idx ON event_store (global_position);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        if (events.Length == 0)
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, expectedVersion));

        // Copy to array up-front: ReadOnlyMemory access across awaits is fine but iteration is cleaner with an array.
        var eventsArray = events.ToArray();

        var conn = new SqliteConnection(_connectionString);
#pragma warning disable MA0004
        await using var _ = conn;
#pragma warning restore MA0004
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // IsolationLevel.Serializable on Microsoft.Data.Sqlite issues BEGIN IMMEDIATE,
        // acquiring the write lock up-front. This avoids "database is locked" races on
        // concurrent appenders; SQLite is single-writer and BEGIN IMMEDIATE ensures correct ordering.
        var tx = (SqliteTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
#pragma warning disable MA0004
        await using var __ = tx;
#pragma warning restore MA0004

        try
        {
            var current = await ReadCurrentVersionAsync(conn, tx, id, ct).ConfigureAwait(false);
            if (current != expectedVersion.Value)
            {
                return Result<AppendResult, StoreError>.Failure(
                    StoreError.Conflict(id, expectedVersion, new StreamPosition(current)));
            }

            var globalCurrent = await ReadCurrentGlobalPositionAsync(conn, tx, ct).ConfigureAwait(false);

            for (var i = 0; i < eventsArray.Length; i++)
            {
                var position = expectedVersion.Value + i + 1;
                var globalPosition = globalCurrent + i + 1;
                await InsertEventAsync(conn, tx, id, eventsArray[i], position, globalPosition, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            var nextVersion = new StreamPosition(expectedVersion.Value + eventsArray.Length);
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, nextVersion));
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* connection may be dead */ }
            throw;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
#pragma warning disable MA0004
        await using var _ = conn;
#pragma warning restore MA0004
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        if (id.IsGlobal)
        {
            // EXCLUSIVE `> @from` semantics: required by StreamConsumer, which checkpoints to the
            // position of the last delivered event and re-reads from that cursor. INCLUSIVE
            // (`>= @from`) would cause infinite re-delivery of the boundary event.
            cmd.CommandText = """
                SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload, global_position
                FROM event_store
                WHERE global_position > @from
                ORDER BY global_position ASC
                """;
            cmd.Parameters.AddWithValue("@from", from.Value);
        }
        else
        {
            // EXCLUSIVE `> @from` semantics — see global-branch comment above.
            cmd.CommandText = """
                SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload, global_position
                FROM event_store
                WHERE stream_id = @streamId AND position > @from
                ORDER BY position ASC
                """;
            cmd.Parameters.AddWithValue("@streamId", id.Value);
            cmd.Parameters.AddWithValue("@from", from.Value);
        }

        var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
#pragma warning disable MA0004
        await using var __ = reader;
#pragma warning restore MA0004
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var perStreamPos  = reader.GetInt64(0);
            var eventType     = reader.GetString(1);
            var eventId       = Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
            var occurredAt    = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var correlationId = reader.IsDBNull(4) ? (Guid?)null : Guid.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
            var causationId   = reader.IsDBNull(5) ? (Guid?)null : Guid.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
            var payload       = (byte[])reader.GetValue(6);
            var globalPos     = reader.GetInt64(7);

            var metadata = new EventMetadata(eventId, eventType, occurredAt, correlationId, causationId);
            var position = id.IsGlobal ? new StreamPosition(globalPos) : new StreamPosition(perStreamPos);
            yield return new RawEvent(position, eventType, payload.AsMemory(), metadata);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sub = new PollingEventSubscription(this, id, from, handler, PollingEventSubscription.DefaultPollInterval);
        return ValueTask.FromResult<IEventSubscription>(sub);
    }

    private static async ValueTask<long> ReadCurrentVersionAsync(SqliteConnection conn, SqliteTransaction tx, StreamId id, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(position), 0) FROM event_store WHERE stream_id = @streamId";
        cmd.Parameters.AddWithValue("@streamId", id.Value);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0L : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> ReadCurrentGlobalPositionAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(global_position), 0) FROM event_store";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0L : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async ValueTask InsertEventAsync(SqliteConnection conn, SqliteTransaction tx, StreamId id, RawEvent e, long position, long globalPosition, CancellationToken ct)
    {
        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO event_store (stream_id, position, global_position, event_type, event_id, occurred_at, correlation_id, causation_id, payload)
            VALUES (@streamId, @position, @globalPosition, @eventType, @eventId, @occurredAt, @correlationId, @causationId, @payload)
            """;
        ins.Parameters.AddWithValue("@streamId", id.Value);
        ins.Parameters.AddWithValue("@position", position);
        ins.Parameters.AddWithValue("@globalPosition", globalPosition);
        ins.Parameters.AddWithValue("@eventType", e.EventType);
        ins.Parameters.AddWithValue("@eventId", e.Metadata.EventId.ToString("D", CultureInfo.InvariantCulture));
        ins.Parameters.AddWithValue("@occurredAt", e.Metadata.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        ins.Parameters.AddWithValue("@correlationId", (object?)e.Metadata.CorrelationId?.ToString("D", CultureInfo.InvariantCulture) ?? DBNull.Value);
        ins.Parameters.AddWithValue("@causationId", (object?)e.Metadata.CausationId?.ToString("D", CultureInfo.InvariantCulture) ?? DBNull.Value);
        ins.Parameters.AddWithValue("@payload", e.Payload.ToArray());
        await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
