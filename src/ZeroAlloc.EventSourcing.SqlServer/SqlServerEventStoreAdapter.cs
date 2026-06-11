using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.SqlServer;

/// <summary>
/// SQL Server <see cref="IEventStoreAdapter"/> backed by a single <c>dbo.event_store</c> table.
/// Uses <c>UPDLOCK</c> + <c>HOLDLOCK</c> table hints for optimistic concurrency — no separate
/// streams table required. Supports per-stream reads (cursor = per-stream <c>position</c>) and
/// global reads via <see cref="StreamId.Global"/> (cursor = <c>global_position</c>, an
/// <c>IDENTITY</c> column on fresh installs or a <c>SEQUENCE</c>+<c>DEFAULT</c> on upgraded
/// deployments).
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
    /// Creates the <c>dbo.event_store</c> table if it does not already exist, and upgrades legacy
    /// deployments that pre-date the <c>global_position</c> column.
    /// </summary>
    /// <remarks>
    /// Fresh installs get <c>global_position BIGINT IDENTITY(1,1) NOT NULL</c> from day one.
    /// SQL Server does NOT support <c>ALTER COLUMN ... IDENTITY</c>, so existing deployments take
    /// the <c>sp_getapplock</c>-protected upgrade path: ADD COLUMN, backfill via
    /// <c>ROW_NUMBER() OVER (occurred_at, stream_id, position)</c>, ALTER NOT NULL, CREATE
    /// SEQUENCE starting at MAX + 1, ADD DEFAULT CONSTRAINT NEXT VALUE FOR. From the adapter's
    /// perspective both mechanisms behave identically — INSERT omits <c>global_position</c>, DB
    /// auto-assigns. Re-running on an already-upgraded table is a no-op.
    /// </remarks>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await CreateFreshSchemaAsync(conn, ct).ConfigureAwait(false);

        if (!await HasGlobalPositionColumnAsync(conn, ct).ConfigureAwait(false))
            await MigrateAddGlobalPositionAsync(conn, ct).ConfigureAwait(false);

        // Index creation runs last so it succeeds on both fresh-install and post-migration paths.
        // Splitting it out of CREATE TABLE means a legacy table (no global_position) doesn't fail
        // when the no-op CREATE TABLE IF NOT EXISTS runs above.
        await EnsureGlobalPositionIndexAsync(conn, ct).ConfigureAwait(false);
    }

    private static async ValueTask CreateFreshSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables
                WHERE name = 'event_store' AND schema_id = SCHEMA_ID('dbo')
            )
            BEGIN
                CREATE TABLE dbo.event_store (
                    stream_id       NVARCHAR(255)     NOT NULL,
                    position        BIGINT            NOT NULL,
                    global_position BIGINT            IDENTITY(1,1) NOT NULL,
                    event_type      NVARCHAR(500)     NOT NULL,
                    event_id        UNIQUEIDENTIFIER  NOT NULL,
                    occurred_at     DATETIMEOFFSET    NOT NULL,
                    correlation_id  UNIQUEIDENTIFIER  NULL,
                    causation_id    UNIQUEIDENTIFIER  NULL,
                    payload         VARBINARY(MAX)    NOT NULL,
                    CONSTRAINT PK_event_store PRIMARY KEY (stream_id, position)
                )
            END
            """;
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask<bool> HasGlobalPositionColumnAsync(SqlConnection conn, CancellationToken ct)
    {
        using var detectCmd = conn.CreateCommand();
        detectCmd.CommandText = """
            SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.event_store') AND name = 'global_position'
            """;
        return await detectCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    // Run the migration inside one transaction with sp_getapplock so concurrent app starts
    // serialize through the ADD COLUMN / backfill / SEQUENCE / DEFAULT steps. The lock owner is
    // 'Transaction' so it releases automatically on COMMIT/ROLLBACK.
    //
    // All schema-touching statements that reference global_position must run via sp_executesql.
    // SQL Server compiles each top-level batch as one unit and binds column references against
    // the *current* metadata; an ALTER TABLE ADD column followed in the same batch by a query
    // that references that column fails with "Invalid column name". Dynamic SQL forces each
    // sub-batch to bind against the metadata at execution time.
    private const string MigrateAddGlobalPositionSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DECLARE @lockResult INT;
        EXEC @lockResult = sp_getapplock
            @Resource = 'event_store_global_position_migration',
            @LockMode = 'Exclusive',
            @LockOwner = 'Transaction';
        IF @lockResult < 0
        BEGIN
            ROLLBACK TRANSACTION;
            THROW 50001, 'Could not acquire migration lock', 1;
        END

        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.event_store') AND name = 'global_position')
        BEGIN
            EXEC sp_executesql N'ALTER TABLE dbo.event_store ADD global_position BIGINT NULL';

            EXEC sp_executesql N'
                WITH ordered AS (
                    SELECT stream_id, position,
                           ROW_NUMBER() OVER (ORDER BY occurred_at ASC, stream_id ASC, position ASC) AS rn
                    FROM dbo.event_store
                )
                UPDATE e SET global_position = o.rn
                FROM dbo.event_store e
                INNER JOIN ordered o ON e.stream_id = o.stream_id AND e.position = o.position
                WHERE e.global_position IS NULL;';

            EXEC sp_executesql N'ALTER TABLE dbo.event_store ALTER COLUMN global_position BIGINT NOT NULL';

            DECLARE @nextSeq BIGINT;
            EXEC sp_executesql
                N'SELECT @out = ISNULL(MAX(global_position), 0) + 1 FROM dbo.event_store',
                N'@out BIGINT OUTPUT',
                @out = @nextSeq OUTPUT;

            DECLARE @createSeq NVARCHAR(MAX) =
                N'CREATE SEQUENCE dbo.event_store_global_position_seq AS BIGINT START WITH '
                + CAST(@nextSeq AS NVARCHAR(20))
                + N' INCREMENT BY 1';
            EXEC sp_executesql @createSeq;

            EXEC sp_executesql N'
                ALTER TABLE dbo.event_store
                ADD CONSTRAINT DF_event_store_global_position
                DEFAULT (NEXT VALUE FOR dbo.event_store_global_position_seq) FOR global_position';
        END

        COMMIT TRANSACTION;
        """;

    private static async ValueTask MigrateAddGlobalPositionAsync(SqlConnection conn, CancellationToken ct)
    {
        using var migrateCmd = conn.CreateCommand();
        migrateCmd.CommandText = MigrateAddGlobalPositionSql;
        await migrateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask EnsureGlobalPositionIndexAsync(SqlConnection conn, CancellationToken ct)
    {
        using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE name = 'event_store_global_position_idx' AND object_id = OBJECT_ID('dbo.event_store')
            )
                CREATE INDEX event_store_global_position_idx ON dbo.event_store (global_position)
            """;
        await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
            // global_position is intentionally omitted — IDENTITY (fresh install) or the
            // post-upgrade SEQUENCE DEFAULT auto-assigns the next monotonic value.
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
        if (id.IsGlobal)
        {
            // EXCLUSIVE `> @from` semantics: required by StreamConsumer, which checkpoints to the
            // position of the last delivered event and re-reads from that cursor. INCLUSIVE
            // (`>= @from`) would cause infinite re-delivery of the boundary event.
            cmd.CommandText = """
                SELECT position, event_type, event_id, occurred_at, correlation_id, causation_id, payload, global_position
                FROM dbo.event_store
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
                FROM dbo.event_store
                WHERE stream_id = @streamId AND position > @from
                ORDER BY position ASC
                """;
            cmd.Parameters.AddWithValue("@streamId", id.Value);
            cmd.Parameters.AddWithValue("@from", from.Value);
        }

        // SequentialAccess is required for efficient VARBINARY(MAX) streaming.
        // Columns MUST be read in index order when using SequentialAccess.
        var readerTask = cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        using var reader = await readerTask;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var perStreamPos  = reader.GetInt64(0);
            var eventType     = reader.GetString(1);
            var eventId       = reader.GetGuid(2);
            var occurredAt    = reader.GetDateTimeOffset(3);
            var correlationId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
            var causationId   = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);

            // Read VARBINARY(MAX) payload: first call returns the length, second reads the bytes.
            var payloadLength = (int)reader.GetBytes(6, 0, null, 0, 0);
            var payloadBytes  = new byte[payloadLength];
            reader.GetBytes(6, 0, payloadBytes, 0, payloadLength);

            var globalPos = reader.GetInt64(7);

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
