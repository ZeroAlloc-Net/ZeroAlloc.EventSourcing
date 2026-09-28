using Microsoft.Data.SqlClient;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Reads column types and index definitions of a SQL Server table, and runs raw SQL, for the
/// tests that migrate a table created by an earlier version of a store.
/// </summary>
internal static class SqlServerSchemaInspector
{
    /// <summary>Non-Latin ids that differ only in characters outside the Windows-1252 code page.</summary>
    public const string JapanId = "日本-consumer";

    /// <inheritdoc cref="JapanId"/>
    public const string ChinaId = "中国-consumer";

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Returns <c>type(length)</c> per column, such as <c>nvarchar(256)</c>.</summary>
    public static async Task<Dictionary<string, string>> GetColumnTypesAsync(string connectionString, string table)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @table
            """;
        cmd.Parameters.AddWithValue("@table", table);

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var length = reader.IsDBNull(2) ? "" : $"({reader.GetInt32(2)})";
            var nullable = reader.GetString(3) == "YES" ? " null" : "";
            types[reader.GetString(0)] = reader.GetString(1) + length + nullable;
        }

        return types;
    }

    /// <summary>
    /// Returns one line per index on the table: its kind and its key and included columns, such
    /// as <c>PK CLUSTERED (consumer_id)</c>. Names are left out because SQL Server generates them.
    /// </summary>
    public static async Task<List<string>> GetIndexesAsync(string connectionString, string table)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.index_id, i.is_primary_key, i.is_unique, i.type_desc,
                   c.name, ic.is_included_column, ic.is_descending_key
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.' + @table)
            ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id
            """;
        cmd.Parameters.AddWithValue("@table", table);

        var rows = new List<(int Id, string Head, string Column, bool Included)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var kind = reader.GetBoolean(1) ? "PK" : reader.GetBoolean(2) ? "UNIQUE" : "INDEX";
            var column = reader.GetString(4) + (reader.GetBoolean(6) ? " DESC" : "");
            rows.Add((reader.GetInt32(0), $"{kind} {reader.GetString(3)}", column, reader.GetBoolean(5)));
        }

        return rows
            .GroupBy(r => r.Id)
            .Select(g =>
            {
                var keys = string.Join(", ", g.Where(r => !r.Included).Select(r => r.Column));
                var included = g.Where(r => r.Included).Select(r => r.Column).ToList();
                var include = included.Count == 0 ? "" : $" INCLUDE ({string.Join(", ", included)})";
                return $"{g.First().Head} ({keys}){include}";
            })
            .ToList();
    }

    public static async Task<long> CountRowsAsync(string connectionString, string table)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.{table}";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
