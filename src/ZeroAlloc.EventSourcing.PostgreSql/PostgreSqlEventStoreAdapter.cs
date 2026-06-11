using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.PostgreSql;

/// <summary>
/// PostgreSQL <see cref="IEventStoreAdapter"/> backed by a single <c>event_store</c> table.
/// Uses advisory transaction locks for optimistic concurrency — no separate streams table required.
/// Supports per-stream reads (cursor = per-stream <c>position</c>) and global reads via
/// <see cref="StreamId.Global"/> (cursor = <c>global_position</c>, a <c>BIGSERIAL</c> column).
/// </summary>
public sealed class PostgreSqlEventStoreAdapter : IEventStoreAdapter
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Initialises the adapter with a pre-built <see cref="NpgsqlDataSource"/>.</summary>
    public PostgreSqlEventStoreAdapter(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Creates the <c>event_store</c> table if it does not already exist, and upgrades legacy
    /// deployments that pre-date the <c>global_position</c> column.
    /// </summary>
    /// <remarks>
    /// Fresh installs get <c>global_position BIGSERIAL NOT NULL</c> from day one. Existing
    /// deployments are detected via <c>information_schema</c>; missing-column upgrade adds the
    /// column, backfills it via <c>ROW_NUMBER() OVER (occurred_at, stream_id, position)</c>,
    /// wires a <c>SEQUENCE</c> + <c>DEFAULT nextval(...)</c> so subsequent INSERTs auto-assign,
    /// and creates the supporting index. The migration runs inside a transaction with an
    /// <c>EXCLUSIVE</c> table lock so concurrent app starts cannot corrupt each other.
    /// Re-running on an already-upgraded table is a no-op.
    /// </remarks>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
#pragma warning disable MA0004
        await using var _ = conn;
#pragma warning restore MA0004

        await CreateFreshSchemaAsync(conn, ct).ConfigureAwait(false);

        if (!await HasGlobalPositionColumnAsync(conn, ct).ConfigureAwait(false))
            await MigrateAddGlobalPositionAsync(conn, ct).ConfigureAwait(false);

        // Index creation runs last so it succeeds on both fresh-install and post-migration paths.
        // Splitting it out of CREATE TABLE means a legacy table (no global_position) doesn't fail
        // when the no-op `CREATE TABLE IF NOT EXISTS` runs above.
        await EnsureGlobalPositionIndexAsync(conn, ct).ConfigureAwait(false);
    }

    private static async ValueTask CreateFreshSchemaAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS event_store (
                stream_id       TEXT          NOT NULL,
                position        BIGINT        NOT NULL,
                global_position BIGSERIAL     NOT NULL,
                event_type      TEXT          NOT NULL,
                event_id        UUID          NOT NULL,
                occurred_at     TIMESTAMPTZ   NOT NULL,
                correlation_id  UUID          NULL,
                causation_id    UUID          NULL,
                payload         BYTEA         NOT NULL,
                PRIMARY KEY (stream_id, position)
            )
            """;
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask EnsureGlobalPositionIndexAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS event_store_global_position_idx ON event_store (global_position)";
        await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask<bool> HasGlobalPositionColumnAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        using var detectCmd = conn.CreateCommand();
        detectCmd.CommandText = """
            SELECT 1 FROM information_schema.columns
            WHERE table_name = 'event_store' AND column_name = 'global_position'
            """;
        return await detectCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async ValueTask MigrateAddGlobalPositionAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Run the migration inside one transaction with an EXCLUSIVE table lock so concurrent
        // app starts cannot interleave the ADD COLUMN / backfill / SEQUENCE / DEFAULT steps.
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
#pragma warning disable MA0004
        await using var __ = tx;
#pragma warning restore MA0004

        try
        {
            using var migrateCmd = conn.CreateCommand();
            migrateCmd.Transaction = tx;
            migrateCmd.CommandText = """
                LOCK TABLE event_store IN EXCLUSIVE MODE;
                ALTER TABLE event_store ADD COLUMN IF NOT EXISTS global_position BIGINT NULL;
                UPDATE event_store SET global_position = sub.rn
                FROM (
                    SELECT stream_id, position,
                           ROW_NUMBER() OVER (ORDER BY occurred_at ASC, stream_id ASC, position ASC) AS rn
                    FROM event_store
                ) sub
                WHERE event_store.stream_id = sub.stream_id AND event_store.position = sub.position;
                ALTER TABLE event_store ALTER COLUMN global_position SET NOT NULL;
                CREATE SEQUENCE IF NOT EXISTS event_store_global_position_seq;
                SELECT setval('event_store_global_position_seq', COALESCE((SELECT MAX(global_position) FROM event_store), 0) + 1, false);
                ALTER TABLE event_store ALTER COLUMN global_position SET DEFAULT nextval('event_store_global_position_seq');
                ALTER SEQUENCE event_store_global_position_seq OWNED BY event_store.global_position;
                """;
            await migrateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* connection may already be dead */ }
            throw;
        }
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

        // Copy to array up-front: ReadOnlySpan cannot be preserved across await boundaries.
        var eventsArray = events.ToArray();

        var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
#pragma warning disable MA0004
        await using var _ = conn;
#pragma warning restore MA0004
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
#pragma warning disable MA0004
        await using var __ = tx;
#pragma warning restore MA0004

        try
        {
            // Acquire lock and check version
            await AcquireLockAsync(conn, tx, id, ct).ConfigureAwait(false);
            var current = await ReadCurrentVersionAsync(conn, tx, id, ct).ConfigureAwait(false);

            if (current != expectedVersion.Value)
            {
                // No explicit RollbackAsync needed — await using tx will rollback on dispose.
                return Result<AppendResult, StoreError>.Failure(
                    StoreError.Conflict(id, expectedVersion, new StreamPosition(current)));
            }

            // Insert events at successive positions
            await InsertEventsAsync(conn, tx, id, eventsArray, expectedVersion, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            var nextVersion = new StreamPosition(expectedVersion.Value + eventsArray.Length);
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, nextVersion));
        }
        catch
        {
            // Use CancellationToken.None so a cancelled ct doesn't prevent the rollback from completing.
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* connection may already be dead */ }
            throw;
        }
    }

    private async ValueTask AcquireLockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StreamId id, CancellationToken ct)
    {
        // Acquire an exclusive advisory lock scoped to this transaction.
        // hashtextextended() (available since PostgreSQL 11) maps the stream_id string to an int8 key.
        // Using int8 (64-bit) rather than hashtext()'s int4 (32-bit) makes birthday-paradox collisions
        // negligible even at millions of distinct stream IDs. A collision causes spurious lock contention
        // between unrelated streams (never data corruption), but should be avoided.
        using var lockCmd = conn.CreateCommand();
        lockCmd.Transaction = tx;
        lockCmd.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@streamId, 0))";
        lockCmd.Parameters.AddWithValue("streamId", id.Value);
        await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask<long> ReadCurrentVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StreamId id, CancellationToken ct)
    {
        using var versionCmd = conn.CreateCommand();
        versionCmd.Transaction = tx;
        versionCmd.CommandText = "SELECT COALESCE(MAX(position), 0) FROM event_store WHERE stream_id = @streamId";
        versionCmd.Parameters.AddWithValue("streamId", id.Value);
        return (long)(await versionCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    private async ValueTask InsertEventsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StreamId id, RawEvent[] eventsArray, StreamPosition expectedVersion, CancellationToken ct)
    {
        for (var i = 0; i < eventsArray.Length; i++)
        {
            var e = eventsArray[i];
            var position = expectedVersion.Value + i + 1;
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            // global_position is intentionally omitted — BIGSERIAL (or the post-upgrade SEQUENCE
            // DEFAULT) auto-assigns the next monotonic value.
            ins.CommandText = """
                INSERT INTO event_store (stream_id, position, event_type, event_id, occurred_at, correlation_id, causation_id, payload)
                VALUES (@streamId, @position, @eventType, @eventId, @occurredAt, @correlationId, @causationId, @payload)
                """;
            ins.Parameters.AddWithValue("streamId", id.Value);
            ins.Parameters.AddWithValue("position", position);
            ins.Parameters.AddWithValue("eventType", e.EventType);
            ins.Parameters.AddWithValue("eventId", e.Metadata.EventId);
            ins.Parameters.AddWithValue("occurredAt", e.Metadata.OccurredAt.UtcDateTime);
            ins.Parameters.Add(new NpgsqlParameter("correlationId", NpgsqlDbType.Uuid) { Value = (object?)e.Metadata.CorrelationId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("causationId", NpgsqlDbType.Uuid) { Value = (object?)e.Metadata.CausationId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea) { Value = e.Payload.ToArray() });
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
#pragma warning disable MA0004
        await using var _ = conn;
#pragma warning restore MA0004
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
            cmd.Parameters.AddWithValue("from", from.Value);
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
            cmd.Parameters.AddWithValue("streamId", id.Value);
            cmd.Parameters.AddWithValue("from", from.Value);
        }

        var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
#pragma warning disable MA0004
        await using var __ = reader;
#pragma warning restore MA0004
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var perStreamPos  = reader.GetInt64(0);
            var eventType     = reader.GetString(1);
            var eventId       = reader.GetGuid(2);
            var occurredAt    = new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero);
            var correlationId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
            var causationId   = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
            var payloadBytes  = (byte[])reader.GetValue(6);
            var globalPos     = reader.GetInt64(7);

            var metadata = new EventMetadata(eventId, eventType, occurredAt, correlationId, causationId);
            var position = id.IsGlobal ? new StreamPosition(globalPos) : new StreamPosition(perStreamPos);
            yield return new RawEvent(position, eventType, payloadBytes.AsMemory(), metadata);
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
}
