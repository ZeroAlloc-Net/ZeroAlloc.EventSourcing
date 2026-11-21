using System.Data;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// SQL Server implementation of <see cref="IProjectionStore"/>.
/// Stores projection state in the <c>VARBINARY(MAX)</c> <c>state</c> column of a <c>dbo.projection_states</c>
/// table using atomic MERGE upsert.
/// </summary>
public sealed class SqlServerProjectionStore : IProjectionStore
{
    private readonly string _connectionString;
    private const string TableName = "dbo.projection_states";

    /// <summary>
    /// Initializes a new instance of <see cref="SqlServerProjectionStore"/>.
    /// </summary>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="connectionString"/> is null, empty, or whitespace-only.</exception>
    public SqlServerProjectionStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>
    /// Creates the <c>dbo.projection_states</c> table in SQL Server if it does not already exist,
    /// converts a <c>VARCHAR</c> <c>projection_key</c> left by an earlier version to <c>NVARCHAR</c>,
    /// and converts the <c>NVARCHAR(MAX)</c> <c>state</c> column of a 1.x table to <c>VARBINARY(MAX)</c>.
    /// This method is idempotent and safe to call multiple times, also from app instances that
    /// start together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Earlier versions stored the key as <c>VARCHAR</c>, which turns characters outside the
    /// database code page into <c>?</c>. Keys already stored that way cannot be recovered.
    /// </para>
    /// <para>
    /// The state conversion encodes each stored state as UTF-8, the bytes a text serializer such as
    /// System.Text.Json writes, so states saved by 1.x load unchanged. It needs SQL Server 2019 or
    /// later, for the UTF-8 collation it converts through.
    /// </para>
    /// </remarks>
    /// <param name="ct">A cancellation token.</param>
    public ValueTask EnsureSchemaAsync(CancellationToken ct = default) =>
        SqlServerSchema.EnsureTableAsync(
            _connectionString, "projection_states", CreateTableSql, Columns, ct, utf8BinaryColumn: "state");

    private static readonly SqlServerSchema.NVarCharColumn[] Columns = [new("projection_key", 256)];

    private const string CreateTableSql = """
        IF NOT EXISTS (
            SELECT 1 FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'dbo' AND t.name = 'projection_states'
        )
        BEGIN
            CREATE TABLE dbo.projection_states (
                projection_key NVARCHAR(256)  NOT NULL PRIMARY KEY,
                state          VARBINARY(MAX) NOT NULL,
                updated_at     DATETIME2      NOT NULL
            )
        END
        """;

    /// <inheritdoc/>
    public async ValueTask SaveAsync(string key, ReadOnlyMemory<byte> state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ValidateKey(key);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"""
            MERGE INTO {TableName} AS target
            USING (SELECT @projection_key AS projection_key) AS source
            ON target.projection_key = source.projection_key
            WHEN MATCHED THEN UPDATE SET
                state      = @state,
                updated_at = @updated_at
            WHEN NOT MATCHED THEN INSERT (projection_key, state, updated_at)
                VALUES (@projection_key, @state, @updated_at);
            """;
        cmd.Parameters.AddWithValue("@projection_key", key);
        cmd.Parameters.Add(StateParameter(state));
        cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ValidateKey(key);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"SELECT state FROM {TableName} WHERE projection_key = @projection_key";
        cmd.Parameters.AddWithValue("@projection_key", key);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is byte[] state ? (ReadOnlyMemory<byte>?)state : null;
    }

    /// <inheritdoc/>
    public async ValueTask ClearAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ValidateKey(key);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        #pragma warning disable MA0004
        using var cmd = conn.CreateCommand();
        #pragma warning restore MA0004
        cmd.CommandText = $"DELETE FROM {TableName} WHERE projection_key = @projection_key";
        cmd.Parameters.AddWithValue("@projection_key", key);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // SqlClient sends a byte[] parameter; Offset and Size select the saved bytes inside it, so a state
    // backed by an array is sent without a copy. SqlClient reads Size 0 as "not set" and then sends the
    // rest of the array from Offset, so an empty state is sent as an empty array instead.
    private static SqlParameter StateParameter(ReadOnlyMemory<byte> state)
    {
        var parameter = new SqlParameter("@state", SqlDbType.VarBinary, -1);
        if (state.IsEmpty)
        {
            parameter.Value = Array.Empty<byte>();
        }
        else if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(state, out var segment))
        {
            parameter.Value = segment.Array;
            parameter.Offset = segment.Offset;
            parameter.Size = segment.Count;
        }
        else
        {
            parameter.Value = state.ToArray();
        }

        return parameter;
    }

    private static void ValidateKey(string key)
    {
        if (key.Length > 256)
            throw new ArgumentException("Projection key must not exceed 256 characters.", nameof(key));
    }
}
