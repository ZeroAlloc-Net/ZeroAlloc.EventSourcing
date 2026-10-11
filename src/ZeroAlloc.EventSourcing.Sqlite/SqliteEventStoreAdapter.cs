using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

            await InsertEventsAsync(conn, tx, id, events, expectedVersion.Value, globalCurrent, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            var nextVersion = new StreamPosition(expectedVersion.Value + events.Length);
            return Result<AppendResult, StoreError>.Success(new AppendResult(id, nextVersion));
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* connection may be dead */ }
            throw;
        }
    }

    private const string ReadColumns =
        "SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload, global_position FROM event_store ";
    private const string GlobalReadSql = ReadColumns + "WHERE global_position > @from ORDER BY global_position ASC";
    private const string GlobalReadBatchSql = GlobalReadSql + " LIMIT @limit";
    private const string StreamReadSql = ReadColumns + "WHERE stream_id = @streamId AND position > @from ORDER BY position ASC";
    private const string StreamReadBatchSql = StreamReadSql + " LIMIT @limit";

    /// <inheritdoc/>
    public IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        CancellationToken ct = default)
        => ReadCoreAsync(id, from, maxCount: null, ct);

    /// <inheritdoc/>
    /// <remarks>The query is bounded with <c>LIMIT</c>, so it reads at most <paramref name="maxCount"/> rows.</remarks>
    public IAsyncEnumerable<RawEvent> ReadBatchAsync(
        StreamId id,
        StreamPosition from,
        int maxCount,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        return ReadCoreAsync(id, from, maxCount, ct);
    }

    private async IAsyncEnumerable<RawEvent> ReadCoreAsync(
        StreamId id,
        StreamPosition from,
        int? maxCount,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
#pragma warning disable MA0004
        await using var _ = conn;
#pragma warning restore MA0004
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        // EXCLUSIVE `> @from` semantics: required by StreamConsumer, which checkpoints to the
        // position of the last delivered event and re-reads from that cursor. INCLUSIVE
        // (`>= @from`) would cause infinite re-delivery of the boundary event.
        if (id.IsGlobal)
        {
            cmd.CommandText = maxCount is null ? GlobalReadSql : GlobalReadBatchSql;
        }
        else
        {
            cmd.CommandText = maxCount is null ? StreamReadSql : StreamReadBatchSql;
            cmd.Parameters.AddWithValue("@streamId", id.Value);
        }

        cmd.Parameters.AddWithValue("@from", from.Value);
        if (maxCount is { } limit)
            cmd.Parameters.AddWithValue("@limit", limit);

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

    private const string InsertSql = """
        INSERT INTO event_store (stream_id, position, global_position, event_type, event_id, occurred_at, correlation_id, causation_id, payload)
        VALUES (@streamId, @position, @globalPosition, @eventType, @eventId, @occurredAt, @correlationId, @causationId, @payload)
        """;

    private static async ValueTask InsertEventsAsync(SqliteConnection conn, SqliteTransaction tx, StreamId id, ReadOnlyMemory<RawEvent> events, long expectedVersion, long globalCurrent, CancellationToken ct)
    {
        // One command for the whole append: each event sets the parameter values and executes it
        // again, so SQLite prepares the statement once per append rather than once per event.
        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = InsertSql;
        ins.Parameters.AddWithValue("@streamId", id.Value);
        var position = ins.Parameters.Add("@position", SqliteType.Integer);
        var globalPosition = ins.Parameters.Add("@globalPosition", SqliteType.Integer);
        var eventType = ins.Parameters.Add("@eventType", SqliteType.Text);
        var eventId = ins.Parameters.Add("@eventId", SqliteType.Text);
        var occurredAt = ins.Parameters.Add("@occurredAt", SqliteType.Text);
        var correlationId = ins.Parameters.Add("@correlationId", SqliteType.Text);
        var causationId = ins.Parameters.Add("@causationId", SqliteType.Text);
        var payload = ins.Parameters.Add("@payload", SqliteType.Blob);

        for (var i = 0; i < events.Length; i++)
        {
            var e = events.Span[i];
            position.Value = expectedVersion + i + 1;
            globalPosition.Value = globalCurrent + i + 1;
            eventType.Value = e.EventType;
            eventId.Value = e.Metadata.EventId.ToString("D", CultureInfo.InvariantCulture);
            occurredAt.Value = e.Metadata.OccurredAt.ToString("O", CultureInfo.InvariantCulture);
            correlationId.Value = (object?)e.Metadata.CorrelationId?.ToString("D", CultureInfo.InvariantCulture) ?? DBNull.Value;
            causationId.Value = (object?)e.Metadata.CausationId?.ToString("D", CultureInfo.InvariantCulture) ?? DBNull.Value;
            payload.Value = PayloadArray(e.Payload);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The payload as the byte array Microsoft.Data.Sqlite binds as a blob. A payload that is a
    /// whole array, as most serializers return, is bound as that array without a copy. The
    /// provider binds byte arrays only, so only a slice of a larger array is copied.
    /// </summary>
    private static byte[] PayloadArray(ReadOnlyMemory<byte> payload)
        => MemoryMarshal.TryGetArray(payload, out var segment)
           && segment.Array is { } array
           && segment.Offset == 0
           && segment.Count == array.Length
            ? array
            : payload.ToArray();
}
