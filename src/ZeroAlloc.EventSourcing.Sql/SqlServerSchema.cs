using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Creates a SQL Server store table and converts the string columns that earlier versions
/// created as <c>VARCHAR</c> to <c>NVARCHAR</c>, ZeroAlloc-Net/ZeroAlloc.EventSourcing#384.
/// </summary>
/// <remarks>
/// <para>
/// Under a code-page collation, the SQL Server default included, a <c>VARCHAR</c> column turns every
/// character outside the code page into <c>?</c>. Two ids that differ only in such characters were
/// stored as the same key. Values already stored that way cannot be recovered; the conversion keeps
/// them as they are.
/// </para>
/// <para>
/// The whole step runs in one transaction that first takes an exclusive <c>sp_getapplock</c> on a
/// per-table resource, the pattern <c>SqlServerEventStoreAdapter</c> uses for its own migration.
/// App instances that start together therefore create and convert the table one after another:
/// the first does the work and the others find it done. A failure rolls the whole step back.
/// </para>
/// </remarks>
internal static class SqlServerSchema
{
    /// <summary>A string column that must be <c>NVARCHAR(<see cref="Length"/>) NOT NULL</c>.</summary>
    internal readonly record struct NVarCharColumn(string Name, int Length);

    /// <summary>
    /// Runs <paramref name="createTableSql"/> and converts any listed column of
    /// <c>dbo.<paramref name="table"/></c> that is still <c>VARCHAR</c>, under the table's lock.
    /// </summary>
    /// <param name="connectionString">A SQL Server connection string.</param>
    /// <param name="table">The table name in the <c>dbo</c> schema.</param>
    /// <param name="createTableSql">Idempotent SQL that creates the table and adds missing columns.</param>
    /// <param name="columns">The columns that must be <c>NVARCHAR</c>.</param>
    /// <param name="ct">A cancellation token.</param>
    internal static async ValueTask EnsureTableAsync(
        string connectionString,
        string table,
        string createTableSql,
        NVarCharColumn[] columns,
        CancellationToken ct)
    {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await AcquireLockAsync(conn, tx, table, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, tx, createTableSql, ct).ConfigureAwait(false);

        var varcharColumns = await ReadVarCharColumnsAsync(conn, tx, table, columns, ct).ConfigureAwait(false);
        if (varcharColumns.Count > 0)
            await ConvertAsync(conn, tx, table, varcharColumns, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask AcquireLockAsync(SqlConnection conn, SqlTransaction tx, string table, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DECLARE @result INT;
            EXEC @result = sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction';
            SELECT @result;
            """;
        cmd.Parameters.AddWithValue("@resource", "ZeroAlloc.EventSourcing.Sql.schema:dbo." + table);

        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (result < 0)
            throw new InvalidOperationException(
                $"Could not acquire the schema lock for dbo.{table}: sp_getapplock returned {result}.");
    }

    private static async ValueTask<List<(NVarCharColumn Column, string? Collation)>> ReadVarCharColumnsAsync(
        SqlConnection conn, SqlTransaction tx, string table, NVarCharColumn[] columns, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT c.name, c.collation_name
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(@table) AND TYPE_NAME(c.system_type_id) = 'varchar'
            """;
        cmd.Parameters.AddWithValue("@table", "dbo." + table);

        var found = new List<(NVarCharColumn, string?)>();
        #pragma warning disable MA0004
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var collation = reader.IsDBNull(1) ? null : reader.GetString(1);
            foreach (var column in columns)
            {
                if (string.Equals(column.Name, name, StringComparison.Ordinal))
                    found.Add((column, collation));
            }
        }

        return found;
    }

    private static async ValueTask ConvertAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string table,
        List<(NVarCharColumn Column, string? Collation)> columns,
        CancellationToken ct)
    {
        // SQL Server cannot change the type of a column that an index or key covers, so every
        // index touching a converted column is dropped and then recreated as it was. Names are
        // read from the catalog: an inline PRIMARY KEY gets a generated name.
        var names = new HashSet<string>(columns.Select(c => c.Column.Name), StringComparer.Ordinal);
        var indexes = (await ReadIndexesAsync(conn, tx, table, ct).ConfigureAwait(false))
            .Where(i => i.Columns.Exists(c => names.Contains(c.Name)))
            .ToList();

        var qualified = "dbo." + Quote(table);
        var sql = new StringBuilder();

        // Nonclustered first, so dropping the clustered index does not rebuild them in between.
        foreach (var index in indexes.OrderBy(i => i.IsClustered))
        {
            sql.Append(index.IsConstraint
                ? $"ALTER TABLE {qualified} DROP CONSTRAINT {Quote(index.Name)};\n"
                : $"DROP INDEX {Quote(index.Name)} ON {qualified};\n");
        }

        foreach (var (column, collation) in columns)
        {
            var collate = collation is null ? "" : " COLLATE " + collation;
            sql.Append(CultureInfo.InvariantCulture,
                $"ALTER TABLE {qualified} ALTER COLUMN {Quote(column.Name)} NVARCHAR({column.Length}){collate} NOT NULL;\n");
        }

        foreach (var index in indexes.OrderByDescending(i => i.IsClustered))
            sql.Append(index.CreateSql(qualified)).Append(";\n");

        await ExecuteAsync(conn, tx, sql.ToString(), ct).ConfigureAwait(false);
    }

    private static async ValueTask<List<IndexDefinition>> ReadIndexesAsync(
        SqlConnection conn, SqlTransaction tx, string table, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT i.index_id, i.name, i.type_desc, i.is_primary_key, i.is_unique_constraint, i.is_unique,
                   i.filter_definition, c.name, ic.is_descending_key, ic.is_included_column
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND i.type IN (1, 2)
            ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id
            """;
        cmd.Parameters.AddWithValue("@table", "dbo." + table);

        var indexes = new List<IndexDefinition>();
        #pragma warning disable MA0004
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        #pragma warning restore MA0004
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt32(0);
            if (indexes.Count == 0 || indexes[^1].Id != id)
            {
                indexes.Add(new IndexDefinition(
                    id,
                    reader.GetString(1),
                    string.Equals(reader.GetString(2), "CLUSTERED", StringComparison.Ordinal),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    []));
            }

            indexes[^1].Columns.Add(new IndexColumn(reader.GetString(7), reader.GetBoolean(8), reader.GetBoolean(9)));
        }

        return indexes;
    }

    private static async ValueTask ExecuteAsync(SqlConnection conn, SqlTransaction tx, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private readonly record struct IndexColumn(string Name, bool IsDescending, bool IsIncluded);

    private sealed record IndexDefinition(
        int Id,
        string Name,
        bool IsClustered,
        bool IsPrimaryKey,
        bool IsUniqueConstraint,
        bool IsUnique,
        string? Filter,
        List<IndexColumn> Columns)
    {
        public bool IsConstraint => IsPrimaryKey || IsUniqueConstraint;

        public string CreateSql(string qualifiedTable)
        {
            var kind = IsClustered ? "CLUSTERED" : "NONCLUSTERED";
            var keys = string.Join(", ", Columns
                .Where(c => !c.IsIncluded)
                .Select(c => Quote(c.Name) + (c.IsDescending ? " DESC" : " ASC")));

            if (IsConstraint)
            {
                var constraint = IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
                return $"ALTER TABLE {qualifiedTable} ADD CONSTRAINT {Quote(Name)} {constraint} {kind} ({keys})";
            }

            var included = Columns.Where(c => c.IsIncluded).Select(c => Quote(c.Name)).ToList();
            var include = included.Count == 0 ? "" : $" INCLUDE ({string.Join(", ", included)})";
            var where = Filter is null ? "" : " WHERE " + Filter;
            var unique = IsUnique ? "UNIQUE " : "";
            return $"CREATE {unique}{kind} INDEX {Quote(Name)} ON {qualifiedTable} ({keys}){include}{where}";
        }
    }
}
