using Microsoft.Data.SqlClient;
using Npgsql;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Defines the shared snapshot table schema for both PostgreSQL and SQL Server,
/// with idempotent schema creation methods.
/// </summary>
public static class SnapshotSchema
{
    /// <summary>
    /// PostgreSQL CREATE TABLE statement for the snapshots table.
    /// </summary>
    public const string PostgreSqlCreateTable = """
        CREATE TABLE IF NOT EXISTS snapshots (
            stream_id   VARCHAR(255)   NOT NULL,
            position    BIGINT         NOT NULL,
            state_type  VARCHAR(500)   NOT NULL,
            payload     BYTEA          NOT NULL,
            created_at  TIMESTAMPTZ    NOT NULL,
            PRIMARY KEY (stream_id)
        )
        """;

    /// <summary>
    /// SQL Server CREATE TABLE statement for the snapshots table.
    /// </summary>
    public const string SqlServerCreateTable = """
        IF NOT EXISTS (
            SELECT 1 FROM sys.tables
            WHERE name = 'snapshots' AND schema_id = SCHEMA_ID('dbo')
        )
        BEGIN
            CREATE TABLE dbo.snapshots (
                stream_id   VARCHAR(255)       NOT NULL,
                position    BIGINT             NOT NULL,
                state_type  VARCHAR(500)       NOT NULL,
                payload     VARBINARY(MAX)     NOT NULL,
                created_at  DATETIMEOFFSET     NOT NULL,
                CONSTRAINT PK_snapshots PRIMARY KEY (stream_id)
            )
        END
        """;

    /// <summary>
    /// Creates the snapshots table in PostgreSQL if it does not already exist.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    /// <param name="dataSource">The <see cref="NpgsqlDataSource"/> to use for the connection.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async ValueTask EnsurePostgreSqlSchemaAsync(
        NpgsqlDataSource dataSource,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = PostgreSqlCreateTable;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the snapshots table in SQL Server if it does not already exist.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async ValueTask EnsureSqlServerSchemaAsync(
        string connectionString,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SqlServerCreateTable;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
