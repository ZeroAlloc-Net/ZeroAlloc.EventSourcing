using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.SqlServer;

/// <summary>
/// SQL Server <see cref="IEventStoreAdapter"/> backed by a single <c>dbo.event_store</c> table.
/// Uses <c>UPDLOCK</c> + <c>HOLDLOCK</c> table hints for optimistic concurrency — no separate
/// streams table required.
/// </summary>
public sealed class SqlServerEventStoreAdapter : IEventStoreAdapter
{
    private readonly string _connectionString;

    /// <summary>Initialises the adapter with a SQL Server connection string.</summary>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionString"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public SqlServerEventStoreAdapter(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>
    /// Creates the <c>dbo.event_store</c> table if it does not already exist.
    /// Call once during application startup or test setup.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables
                WHERE name = 'event_store' AND schema_id = SCHEMA_ID('dbo')
            )
            BEGIN
                CREATE TABLE dbo.event_store (
                    stream_id       NVARCHAR(255)     NOT NULL,
                    position        BIGINT            NOT NULL,
                    event_type      NVARCHAR(500)     NOT NULL,
                    event_id        UNIQUEIDENTIFIER  NOT NULL,
                    occurred_at     DATETIMEOFFSET    NOT NULL,
                    correlation_id  UNIQUEIDENTIFIER  NULL,
                    causation_id    UNIQUEIDENTIFIER  NULL,
                    payload         VARBINARY(MAX)    NOT NULL,
                    CONSTRAINT PK_event_store PRIMARY KEY (stream_id, position)
                    -- TODO(perf): add a covering non-clustered index on (stream_id) INCLUDE (position) for O(log n) MAX(position) version-check scans on high-event-count streams
                )
            END
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

        // Copy to array up-front: ReadOnlySpan cannot be preserved across await boundaries.
        var eventsArray = events.ToArray();

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var txTask = conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        using var tx = (SqlTransaction)await txTask;

        try
        {
            // Read current version with UPDLOCK + HOLDLOCK to lock the range and hold until
            // transaction end, preventing phantom inserts between the SELECT and INSERT.
            var current = await ReadCurrentVersionAsync(conn, tx, id, ct).ConfigureAwait(false);

            if (current != expectedVersion.Value)
            {
                // No explicit RollbackAsync needed — using tx will rollback on dispose.
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

    private async ValueTask<long> ReadCurrentVersionAsync(SqlConnection conn, SqlTransaction tx, StreamId id, CancellationToken ct)
    {
        using var versionCmd = conn.CreateCommand();
        versionCmd.Transaction = tx;
        versionCmd.CommandText = """
            SELECT ISNULL(MAX(position), 0)
            FROM dbo.event_store WITH (UPDLOCK, HOLDLOCK)
            WHERE stream_id = @streamId
            """;
        versionCmd.Parameters.AddWithValue("@streamId", id.Value);
        return (long)(await versionCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    private async ValueTask InsertEventsAsync(SqlConnection conn, SqlTransaction tx, StreamId id, RawEvent[] eventsArray, StreamPosition expectedVersion, CancellationToken ct)
    {
        for (var i = 0; i < eventsArray.Length; i++)
        {
            var e = eventsArray[i];
            var position = expectedVersion.Value + i + 1;
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO dbo.event_store
                    (stream_id, position, event_type, event_id, occurred_at, correlation_id, causation_id, payload)
                VALUES
                    (@streamId, @position, @eventType, @eventId, @occurredAt, @correlationId, @causationId, @payload)
                """;
            ins.Parameters.AddWithValue("@streamId", id.Value);
            ins.Parameters.AddWithValue("@position", position);
            ins.Parameters.AddWithValue("@eventType", e.EventType);
            ins.Parameters.AddWithValue("@eventId", e.Metadata.EventId);
            ins.Parameters.AddWithValue("@occurredAt", e.Metadata.OccurredAt);
            ins.Parameters.Add(new SqlParameter("@correlationId", SqlDbType.UniqueIdentifier) { Value = (object?)e.Metadata.CorrelationId ?? DBNull.Value });
            ins.Parameters.Add(new SqlParameter("@causationId", SqlDbType.UniqueIdentifier) { Value = (object?)e.Metadata.CausationId ?? DBNull.Value });
            ins.Parameters.Add(new SqlParameter("@payload", SqlDbType.VarBinary, -1) { Value = e.Payload.ToArray() });
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload
            FROM dbo.event_store
            WHERE stream_id = @streamId AND position >= @from
            ORDER BY position ASC
            """;
        cmd.Parameters.AddWithValue("@streamId", id.Value);
        cmd.Parameters.AddWithValue("@from", from.Value);

        // SequentialAccess is required for efficient VARBINARY(MAX) streaming.
        // Columns MUST be read in index order when using SequentialAccess.
        var readerTask = cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        using var reader = await readerTask;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var position      = new StreamPosition(reader.GetInt64(0));
            var eventType     = reader.GetString(1);
            var eventId       = reader.GetGuid(2);
            var occurredAt    = reader.GetDateTimeOffset(3);
            var correlationId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
            var causationId   = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);

            // Read VARBINARY(MAX) payload: first call returns the length, second reads the bytes.
            var payloadLength = (int)reader.GetBytes(6, 0, null, 0, 0);
            var payloadBytes  = new byte[payloadLength];
            reader.GetBytes(6, 0, payloadBytes, 0, payloadLength);

            var metadata = new EventMetadata(eventId, eventType, occurredAt, correlationId, causationId);
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
