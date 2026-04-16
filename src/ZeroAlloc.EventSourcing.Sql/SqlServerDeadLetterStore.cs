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
    /// Creates the <c>dbo.dead_letters</c> table in SQL Server if it does not already exist.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
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
                    failed_at         DATETIMEOFFSET       NOT NULL
                )
            END
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
                (consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at)
            VALUES
                (@consumer_id, @stream_id, @position, @event_type, @payload, @exception_type, @exception_message, @failed_at)
            """;

        cmd.Parameters.AddWithValue("@consumer_id", consumerId);
        cmd.Parameters.AddWithValue("@stream_id", envelope.StreamId.Value);
        cmd.Parameters.AddWithValue("@position", envelope.Position.Value);
        cmd.Parameters.AddWithValue("@event_type", envelope.Metadata.EventType);
        cmd.Parameters.Add("@payload", System.Data.SqlDbType.VarBinary).Value = payload;
        cmd.Parameters.AddWithValue("@exception_type", exception.GetType().Name);
        cmd.Parameters.AddWithValue("@exception_message", exception.Message);
        cmd.Parameters.AddWithValue("@failed_at", failedAt);

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
            SELECT consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at
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

            var metadata = new EventMetadata(Guid.NewGuid(), eventType, failedAt, null, null);
            var envelope = new EventEnvelope(new StreamId(streamId), new StreamPosition(position), payload, metadata);

            yield return new DeadLetterEntry(envelope, consumerId, exceptionType, exceptionMessage, failedAt);
        }
    }
}
