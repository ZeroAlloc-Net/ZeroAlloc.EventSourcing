using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// SQL Server implementation of <see cref="IDeadLetterStore"/>.
/// Stores permanently-failed events in a <c>dbo.dead_letters</c> table.
/// </summary>
public sealed class SqlServerDeadLetterStore : IDeadLetterStore
{
    private readonly string _connectionString;
    private readonly IEventSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of <see cref="SqlServerDeadLetterStore"/>.
    /// </summary>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <param name="serializer">The serializer used to convert event payloads to bytes.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="connectionString"/> is null, empty, or whitespace-only.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="serializer"/> is null.</exception>
    public SqlServerDeadLetterStore(string connectionString, IEventSerializer serializer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <summary>
    /// Creates the <c>dbo.dead_letters</c> table in SQL Server if it does not already exist, and adds
    /// the event metadata columns to a table created by an earlier version.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    /// <remarks>
    /// The metadata columns are nullable because a migrated table already holds rows written without
    /// them. <see cref="ReadAllAsync"/> returns <see cref="Guid.Empty"/> as the event id and the
    /// failure time as the occurrence time for such rows.
    /// </remarks>
    /// <param name="ct">A cancellation token.</param>
    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
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
                WHERE s.name = 'dbo' AND t.name = 'dead_letters'
            )
            BEGIN
                CREATE TABLE dbo.dead_letters (
                    id                BIGINT IDENTITY(1,1) PRIMARY KEY,
                    consumer_id       VARCHAR(256)         NOT NULL,
                    stream_id         VARCHAR(255)         NOT NULL,
                    position          BIGINT               NOT NULL,
                    event_type        VARCHAR(500)         NOT NULL,
                    payload           VARBINARY(MAX)       NOT NULL,
                    exception_type    VARCHAR(500)         NOT NULL,
                    exception_message NVARCHAR(MAX)        NOT NULL,
                    failed_at         DATETIMEOFFSET       NOT NULL,
                    event_id          UNIQUEIDENTIFIER     NULL,
                    occurred_at       DATETIMEOFFSET       NULL,
                    correlation_id    UNIQUEIDENTIFIER     NULL,
                    causation_id      UNIQUEIDENTIFIER     NULL
                )
            END

            IF COL_LENGTH('dbo.dead_letters', 'event_id') IS NULL
                ALTER TABLE dbo.dead_letters ADD event_id UNIQUEIDENTIFIER NULL;
            IF COL_LENGTH('dbo.dead_letters', 'occurred_at') IS NULL
                ALTER TABLE dbo.dead_letters ADD occurred_at DATETIMEOFFSET NULL;
            IF COL_LENGTH('dbo.dead_letters', 'correlation_id') IS NULL
                ALTER TABLE dbo.dead_letters ADD correlation_id UNIQUEIDENTIFIER NULL;
            IF COL_LENGTH('dbo.dead_letters', 'causation_id') IS NULL
                ALTER TABLE dbo.dead_letters ADD causation_id UNIQUEIDENTIFIER NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string consumerId, EventEnvelope envelope, Exception exception, CancellationToken ct = default)
    {
        var payload = _serializer.Serialize(envelope.Event).ToArray();
        var failedAt = DateTimeOffset.UtcNow;

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = """
            INSERT INTO dbo.dead_letters
                (consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at,
                 event_id, occurred_at, correlation_id, causation_id)
            VALUES
                (@consumer_id, @stream_id, @position, @event_type, @payload, @exception_type, @exception_message, @failed_at,
                 @event_id, @occurred_at, @correlation_id, @causation_id)
            """;

        cmd.Parameters.AddWithValue("@consumer_id", consumerId);
        cmd.Parameters.AddWithValue("@stream_id", envelope.StreamId.Value);
        cmd.Parameters.AddWithValue("@position", envelope.Position.Value);
        cmd.Parameters.AddWithValue("@event_type", envelope.Metadata.EventType);
        cmd.Parameters.Add("@payload", System.Data.SqlDbType.VarBinary).Value = payload;
        cmd.Parameters.AddWithValue("@exception_type", exception.GetType().Name);
        cmd.Parameters.AddWithValue("@exception_message", exception.Message);
        cmd.Parameters.AddWithValue("@failed_at", failedAt);
        cmd.Parameters.Add("@event_id", System.Data.SqlDbType.UniqueIdentifier).Value = envelope.Metadata.EventId;
        cmd.Parameters.Add("@occurred_at", System.Data.SqlDbType.DateTimeOffset).Value = envelope.Metadata.OccurredAt;
        cmd.Parameters.Add("@correlation_id", System.Data.SqlDbType.UniqueIdentifier).Value =
            (object?)envelope.Metadata.CorrelationId ?? DBNull.Value;
        cmd.Parameters.Add("@causation_id", System.Data.SqlDbType.UniqueIdentifier).Value =
            (object?)envelope.Metadata.CausationId ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DeadLetterEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = """
            SELECT consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at,
                   event_id, occurred_at, correlation_id, causation_id
            FROM dbo.dead_letters
            ORDER BY id
            """;

        #pragma warning disable MA0004
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var consumerId = reader.GetString(0);
            var streamId = reader.GetString(1);
            var position = reader.GetInt64(2);
            var eventType = reader.GetString(3);
            var payload = (byte[])reader.GetValue(4);
            var exceptionType = reader.GetString(5);
            var exceptionMessage = reader.GetString(6);
            var failedAt = reader.GetFieldValue<DateTimeOffset>(7);
            var eventId = await reader.IsDBNullAsync(8, ct).ConfigureAwait(false) ? Guid.Empty : reader.GetGuid(8);
            var occurredAt = await reader.IsDBNullAsync(9, ct).ConfigureAwait(false)
                ? failedAt
                : reader.GetFieldValue<DateTimeOffset>(9);
            Guid? correlationId = await reader.IsDBNullAsync(10, ct).ConfigureAwait(false) ? null : reader.GetGuid(10);
            Guid? causationId = await reader.IsDBNullAsync(11, ct).ConfigureAwait(false) ? null : reader.GetGuid(11);

            var metadata = new EventMetadata(eventId, eventType, occurredAt, correlationId, causationId);
            var envelope = new EventEnvelope(new StreamId(streamId), new StreamPosition(position), payload, metadata);

            yield return new DeadLetterEntry(envelope, consumerId, exceptionType, exceptionMessage, failedAt);
        }
    }
}
