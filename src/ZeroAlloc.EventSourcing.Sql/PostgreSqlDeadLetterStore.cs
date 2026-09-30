using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL implementation of <see cref="IDeadLetterStore"/>.
/// Stores permanently-failed events in a <c>dead_letters</c> table.
/// </summary>
public sealed class PostgreSqlDeadLetterStore : IDeadLetterStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEventSerializer _serializer;
    private readonly IEventTypeRegistry? _registry;

    /// <summary>
    /// Initializes a new instance of <see cref="PostgreSqlDeadLetterStore"/>.
    /// </summary>
    /// <param name="dataSource">The <see cref="NpgsqlDataSource"/> to use for connections.</param>
    /// <param name="serializer">The serializer that writes event payloads and reads them back.</param>
    /// <param name="registry">
    /// Maps the stored event type name to the CLR type that <see cref="ReadAllAsync"/> deserializes
    /// the payload into.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if any parameter is null.</exception>
    public PostgreSqlDeadLetterStore(NpgsqlDataSource dataSource, IEventSerializer serializer, IEventTypeRegistry registry)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PostgreSqlDeadLetterStore"/> that reads back the
    /// serialized payload instead of the event object.
    /// </summary>
    /// <remarks>
    /// Obsolete: <see cref="ReadAllAsync"/> puts the stored payload bytes in
    /// <see cref="EventEnvelope.Event"/>, where the in-memory store puts the event object, so replay
    /// code cannot treat the stores alike.
    /// </remarks>
    /// <param name="dataSource">The <see cref="NpgsqlDataSource"/> to use for connections.</param>
    /// <param name="serializer">The serializer used to convert event payloads to bytes.</param>
    /// <exception cref="ArgumentNullException">Thrown if either parameter is null.</exception>
    [Obsolete(DeadLetterPayload.ObsoleteConstructor, DiagnosticId = DeadLetterPayload.ObsoleteConstructorId)]
    public PostgreSqlDeadLetterStore(NpgsqlDataSource dataSource, IEventSerializer serializer)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <summary>
    /// Creates the <c>dead_letters</c> table in PostgreSQL if it does not already exist, and adds the
    /// event metadata columns to a table created by an earlier version.
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
        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS dead_letters (
                id               BIGSERIAL       PRIMARY KEY,
                consumer_id      VARCHAR(256)    NOT NULL,
                stream_id        VARCHAR(255)    NOT NULL,
                position         BIGINT          NOT NULL,
                event_type       VARCHAR(500)    NOT NULL,
                payload          BYTEA           NOT NULL,
                exception_type   VARCHAR(500)    NOT NULL,
                exception_message TEXT           NOT NULL,
                failed_at        TIMESTAMPTZ     NOT NULL,
                event_id         UUID            NULL,
                occurred_at      TIMESTAMPTZ     NULL,
                correlation_id   UUID            NULL,
                causation_id     UUID            NULL
            );

            ALTER TABLE dead_letters ADD COLUMN IF NOT EXISTS event_id       UUID        NULL;
            ALTER TABLE dead_letters ADD COLUMN IF NOT EXISTS occurred_at    TIMESTAMPTZ NULL;
            ALTER TABLE dead_letters ADD COLUMN IF NOT EXISTS correlation_id UUID        NULL;
            ALTER TABLE dead_letters ADD COLUMN IF NOT EXISTS causation_id   UUID        NULL;
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string consumerId, EventEnvelope envelope, Exception exception, CancellationToken ct = default)
    {
        var payload = _serializer.Serialize(envelope.Event).ToArray();
        var failedAt = DateTimeOffset.UtcNow;

        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dead_letters
                (consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at,
                 event_id, occurred_at, correlation_id, causation_id)
            VALUES
                (@consumer_id, @stream_id, @position, @event_type, @payload, @exception_type, @exception_message, @failed_at,
                 @event_id, @occurred_at, @correlation_id, @causation_id)
            """;

        command.Parameters.AddWithValue("@consumer_id", consumerId);
        command.Parameters.AddWithValue("@stream_id", envelope.StreamId.Value);
        command.Parameters.AddWithValue("@position", envelope.Position.Value);
        command.Parameters.AddWithValue("@event_type", envelope.Metadata.EventType);
        command.Parameters.Add("@payload", NpgsqlDbType.Bytea).Value = payload;
        command.Parameters.AddWithValue("@exception_type", exception.GetType().Name);
        command.Parameters.AddWithValue("@exception_message", exception.Message);
        command.Parameters.AddWithValue("@failed_at", failedAt);
        command.Parameters.Add("@event_id", NpgsqlDbType.Uuid).Value = envelope.Metadata.EventId;
        command.Parameters.Add("@occurred_at", NpgsqlDbType.TimestampTz).Value = envelope.Metadata.OccurredAt.ToUniversalTime();
        command.Parameters.Add("@correlation_id", NpgsqlDbType.Uuid).Value =
            (object?)envelope.Metadata.CorrelationId ?? DBNull.Value;
        command.Parameters.Add("@causation_id", NpgsqlDbType.Uuid).Value =
            (object?)envelope.Metadata.CausationId ?? DBNull.Value;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at,
                   event_id, occurred_at, correlation_id, causation_id
            FROM dead_letters
            ORDER BY id
            """;

        #pragma warning disable MA0004
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
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
