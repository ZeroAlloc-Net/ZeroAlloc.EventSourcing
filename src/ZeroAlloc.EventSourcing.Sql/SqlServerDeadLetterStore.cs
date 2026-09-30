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
    private readonly IEventTypeRegistry? _registry;

    /// <summary>
    /// Initializes a new instance of <see cref="SqlServerDeadLetterStore"/>.
    /// </summary>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <param name="serializer">The serializer that writes event payloads and reads them back.</param>
    /// <param name="registry">
    /// Maps the stored event type name to the CLR type that <see cref="ReadAllAsync"/> deserializes
    /// the payload into.
    /// </param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="connectionString"/> is null, empty, or whitespace-only.</exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="serializer"/> or <paramref name="registry"/> is null.
    /// </exception>
    public SqlServerDeadLetterStore(string connectionString, IEventSerializer serializer, IEventTypeRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SqlServerDeadLetterStore"/> that reads back the
    /// serialized payload instead of the event object.
    /// </summary>
    /// <remarks>
    /// Obsolete: <see cref="ReadAllAsync"/> puts the stored payload bytes in
    /// <see cref="EventEnvelope.Event"/>, where the in-memory store puts the event object, so replay
    /// code cannot treat the stores alike.
    /// </remarks>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <param name="serializer">The serializer used to convert event payloads to bytes.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="connectionString"/> is null, empty, or whitespace-only.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="serializer"/> is null.</exception>
    [Obsolete(DeadLetterPayload.ObsoleteConstructor, DiagnosticId = DeadLetterPayload.ObsoleteConstructorId)]
    public SqlServerDeadLetterStore(string connectionString, IEventSerializer serializer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <summary>
    /// Creates the <c>dbo.dead_letters</c> table in SQL Server if it does not already exist, and
    /// upgrades a table created by an earlier version: it adds the event metadata columns and
    /// converts the <c>VARCHAR</c> id and type name columns to <c>NVARCHAR</c>.
    /// This method is idempotent and safe to call multiple times, also from app instances that
    /// start together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The metadata columns are nullable because a migrated table already holds rows written without
    /// them. <see cref="ReadAllAsync"/> returns <see cref="Guid.Empty"/> as the event id and the
    /// failure time as the occurrence time for such rows.
    /// </para>
    /// <para>
    /// Earlier versions stored the consumer id, stream id, event type and exception type as
    /// <c>VARCHAR</c>, which turns characters outside the database code page into <c>?</c>. Values
    /// already stored that way cannot be recovered.
    /// </para>
    /// </remarks>
    /// <param name="ct">A cancellation token.</param>
    public ValueTask EnsureSchemaAsync(CancellationToken ct = default) =>
        SqlServerSchema.EnsureTableAsync(_connectionString, "dead_letters", CreateTableSql, Columns, ct);

    private static readonly SqlServerSchema.NVarCharColumn[] Columns =
    [
        new("consumer_id", 256),
        new("stream_id", 255),
        new("event_type", 500),
        new("exception_type", 500),
    ];

    private const string CreateTableSql = """
        IF NOT EXISTS (
            SELECT 1 FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'dbo' AND t.name = 'dead_letters'
        )
        BEGIN
            CREATE TABLE dbo.dead_letters (
                id                BIGINT IDENTITY(1,1) PRIMARY KEY,
                consumer_id       NVARCHAR(256)        NOT NULL,
                stream_id         NVARCHAR(255)        NOT NULL,
                position          BIGINT               NOT NULL,
                event_type        NVARCHAR(500)        NOT NULL,
                payload           VARBINARY(MAX)       NOT NULL,
                exception_type    NVARCHAR(500)        NOT NULL,
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

    /// <summary>
    /// Reads all dead-letter entries in the order they were written. Each envelope holds the
    /// event object, deserialized into the type the registry maps the stored event type name to.
    /// A store built with the obsolete constructor, which has no registry, returns the serialized
    /// payload as a <see cref="byte"/> array instead.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// An entry's event type is not registered in the <see cref="IEventTypeRegistry"/>. The message
    /// names the event type, stream id and position of that entry.
    /// </exception>
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
            var @event = DeadLetterPayload.Deserialize(_serializer, _registry, eventType, payload, streamId, position);
            var envelope = new EventEnvelope(new StreamId(streamId), new StreamPosition(position), @event, metadata);

            yield return new DeadLetterEntry(envelope, consumerId, exceptionType, exceptionMessage, failedAt);
        }
    }
}
