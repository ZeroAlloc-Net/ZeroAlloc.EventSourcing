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

    /// <summary>
    /// Initializes a new instance of <see cref="PostgreSqlDeadLetterStore"/>.
    /// </summary>
    /// <param name="dataSource">The <see cref="NpgsqlDataSource"/> to use for connections.</param>
    /// <param name="serializer">The serializer used to convert event payloads to bytes.</param>
    /// <exception cref="ArgumentNullException">Thrown if either parameter is null.</exception>
    public PostgreSqlDeadLetterStore(NpgsqlDataSource dataSource, IEventSerializer serializer)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <summary>
    /// Creates the <c>dead_letters</c> table in PostgreSQL if it does not already exist.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
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
                failed_at        TIMESTAMPTZ     NOT NULL
            )
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
                (consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at)
            VALUES
                (@consumer_id, @stream_id, @position, @event_type, @payload, @exception_type, @exception_message, @failed_at)
            """;

        command.Parameters.AddWithValue("@consumer_id", consumerId);
        command.Parameters.AddWithValue("@stream_id", envelope.StreamId.Value);
        command.Parameters.AddWithValue("@position", envelope.Position.Value);
        command.Parameters.AddWithValue("@event_type", envelope.Metadata.EventType);
        command.Parameters.Add("@payload", NpgsqlDbType.Bytea).Value = payload;
        command.Parameters.AddWithValue("@exception_type", exception.GetType().Name);
        command.Parameters.AddWithValue("@exception_message", exception.Message);
        command.Parameters.AddWithValue("@failed_at", failedAt);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DeadLetterEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        #pragma warning disable MA0004
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at
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

            var metadata = new EventMetadata(Guid.NewGuid(), eventType, failedAt, null, null);
            var envelope = new EventEnvelope(new StreamId(streamId), new StreamPosition(position), payload, metadata);

            yield return new DeadLetterEntry(envelope, consumerId, exceptionType, exceptionMessage, failedAt);
        }
    }
}
