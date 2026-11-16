using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ShinyCommander.Core.Database;

public enum SqliteObjectKind
{
    Table,
    View,
    Index,
    Trigger
}

/// <param name="Table">The table an index or trigger belongs to; for tables and views their own name.</param>
public sealed record SqliteObject(string Name, SqliteObjectKind Kind, string? Sql, string Table);

public sealed record SqliteColumn(string Name, string DeclaredType, bool NotNull, string? DefaultValue, int PrimaryKeyOrder)
{
    public bool IsPrimaryKey => this.PrimaryKeyOrder > 0;

    /// <summary>SQLite's type affinity rules (section 3.1 of the datatype docs), from the declared type.</summary>
    public string Affinity
    {
        get
        {
            var t = this.DeclaredType.ToUpperInvariant();
            if (t.Contains("INT"))
                return "INTEGER";
            if (t.Contains("CHAR") || t.Contains("CLOB") || t.Contains("TEXT"))
                return "TEXT";
            if (t.Length == 0 || t.Contains("BLOB"))
                return "BLOB";
            if (t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB"))
                return "REAL";
            return "NUMERIC";
        }
    }
}

/// <summary>
/// One row of a table: its values in column order and the key that finds it again for an update or delete - the
/// rowid, or the primary key values in a WITHOUT ROWID table.
/// </summary>
public sealed class SqliteRow(object?[] key, object?[] values)
{
    public object?[] Key { get; } = key;
    public object?[] Values { get; } = values;
}

public sealed record SqlitePage(IReadOnlyList<SqliteRow> Rows, long Total);

/// <summary>What a statement typed into the query box returned: rows, or how many rows it changed.</summary>
public sealed record SqliteQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, int RowsAffected, bool Truncated)
{
    public bool HasRows => this.Columns.Count > 0;
}

/// <summary>A SQLite database file for the database editor: its tables, a page of rows at a time, and edits.</summary>
public sealed partial class SqliteDatabase : IDisposable
{
    /// <summary>Above this many result rows a query stops reading - the grid isn't the place to page a million rows.</summary>
    public const int MaxQueryRows = 10_000;

    static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();
    readonly SqliteConnection connection;

    SqliteDatabase(SqliteConnection connection) => this.connection = connection;

    public static bool IsSqliteFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[16];
            return stream.Read(header) == 16 && header.SequenceEqual(Magic);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static SqliteDatabase Open(string path)
    {
        // no pooling: a pooled connection keeps the file open after Dispose, which blocks deleting or moving it and
        // hides the last write from the archive/server write-back
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(cs);
        connection.Open();
        return new SqliteDatabase(connection);
    }

    public string FileName => this.connection.DataSource;

    public IReadOnlyList<SqliteObject> GetObjects()
    {
        using var cmd = this.Command();
        // indexes SQLite makes itself (for a PRIMARY KEY or UNIQUE) have no SQL - they go with their constraint
        cmd.CommandText = """
            SELECT name, type, sql, tbl_name FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%' AND (type IN ('table', 'view') OR sql IS NOT NULL)
            ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 WHEN 'index' THEN 2 ELSE 3 END, name COLLATE NOCASE
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<SqliteObject>();
        while (reader.Read())
        {
            var kind = reader.GetString(1) switch
            {
                "view" => SqliteObjectKind.View,
                "index" => SqliteObjectKind.Index,
                "trigger" => SqliteObjectKind.Trigger,
                _ => SqliteObjectKind.Table
            };
            list.Add(new SqliteObject(reader.GetString(0), kind, reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        }
        return list;
    }

    public IReadOnlyList<SqliteColumn> GetColumns(string table)
    {
        using var cmd = this.Command();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = cmd.ExecuteReader();
        var list = new List<SqliteColumn>();
        while (reader.Read())
        {
            list.Add(new SqliteColumn(
                reader.GetString(1),
                reader.IsDBNull(2) ? String.Empty : reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5)
            ));
        }
        return list;
    }

    /// <summary>
    /// How rows are found again: by rowid in an ordinary table, by the primary key in a WITHOUT ROWID table. Views have
    /// neither - their rows can't be edited.
    /// </summary>
    public IReadOnlyList<string>? GetKeyColumns(SqliteObject obj, IReadOnlyList<SqliteColumn> columns)
    {
        if (obj.Kind != SqliteObjectKind.Table)
            return null;

        var withoutRowId = obj.Sql != null && obj.Sql.Replace(" ", String.Empty).Contains("WITHOUTROWID", StringComparison.OrdinalIgnoreCase);
        if (!withoutRowId)
            return ["rowid"];

        var pk = columns.Where(x => x.IsPrimaryKey).OrderBy(x => x.PrimaryKeyOrder).Select(x => x.Name).ToList();
        return pk.Count == 0 ? null : pk;
    }

    public long Count(string table)
    {
        using var cmd = this.Command();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}";
        return (long)cmd.ExecuteScalar()!;
    }

    public SqlitePage ReadPage(
        string table,
        IReadOnlyList<SqliteColumn> columns,
        IReadOnlyList<string>? keyColumns,
        int offset,
        int limit,
        IReadOnlyList<(string Column, bool Descending)> orderBy)
    {
        var keys = keyColumns ?? [];
        var select = keys.Concat(columns.Select(x => x.Name)).Select(Quote);
        var sql = $"SELECT {String.Join(", ", select)} FROM {Quote(table)}";
        if (orderBy.Count > 0)
            sql += " ORDER BY " + String.Join(", ", orderBy.Select(x => Quote(x.Column) + (x.Descending ? " DESC" : String.Empty)));
        sql += " LIMIT $limit OFFSET $offset";

        using var cmd = this.Command();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader();
        var rows = new List<SqliteRow>();
        while (reader.Read())
        {
            var key = new object?[keys.Count];
            var values = new object?[columns.Count];
            for (var i = 0; i < key.Length; i++)
                key[i] = Read(reader, i);
            for (var i = 0; i < values.Length; i++)
                values[i] = Read(reader, keys.Count + i);
            rows.Add(new SqliteRow(key, values));
        }
        return new SqlitePage(rows, this.Count(table));
    }

    public int Update(string table, IReadOnlyList<string> keyColumns, object?[] key, IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count == 0)
            return 0;

        using var cmd = this.Command();
        var sets = values.Keys.Select((c, i) => $"{Quote(c)} = $v{i}").ToList();
        cmd.CommandText = $"UPDATE {Quote(table)} SET {String.Join(", ", sets)} WHERE {Where(cmd, keyColumns, key)}";
        var n = 0;
        foreach (var value in values.Values)
            cmd.Parameters.AddWithValue($"$v{n++}", value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts a row; columns left out take their defaults.</summary>
    public void Insert(string table, IReadOnlyDictionary<string, object?> values)
    {
        using var cmd = this.Command();
        if (values.Count == 0)
        {
            cmd.CommandText = $"INSERT INTO {Quote(table)} DEFAULT VALUES";
        }
        else
        {
            var names = String.Join(", ", values.Keys.Select(Quote));
            var parameters = String.Join(", ", values.Keys.Select((_, i) => $"$v{i}"));
            cmd.CommandText = $"INSERT INTO {Quote(table)} ({names}) VALUES ({parameters})";
            var n = 0;
            foreach (var value in values.Values)
                cmd.Parameters.AddWithValue($"$v{n++}", value ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    public int Delete(string table, IReadOnlyList<string> keyColumns, object?[] key)
    {
        using var cmd = this.Command();
        cmd.CommandText = $"DELETE FROM {Quote(table)} WHERE {Where(cmd, keyColumns, key)}";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Runs whatever was typed - several statements are fine; the rows of the last one that returns any come back.</summary>
    public SqliteQueryResult Execute(string sql)
    {
        using var cmd = this.Command();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        var rows = new List<object?[]>();
        var truncated = false;
        do
        {
            if (reader.FieldCount > 0)
            {
                columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                rows = [];
                truncated = false;
                while (!truncated && reader.Read())
                {
                    if (rows.Count >= MaxQueryRows)
                    {
                        truncated = true;
                    }
                    else
                    {
                        var row = new object?[reader.FieldCount];
                        for (var i = 0; i < row.Length; i++)
                            row[i] = Read(reader, i);
                        rows.Add(row);
                    }
                }
            }
        }
        while (reader.NextResult());

        return new SqliteQueryResult(columns, rows, reader.RecordsAffected, truncated);
    }

    /// <summary>
    /// Text from an editor cell as the value to store: a number when the column wants one and the text is one,
    /// <c>NULL</c> as null, otherwise the text itself (SQLite keeps whatever it's given).
    /// </summary>
    public static object? Parse(string? text, SqliteColumn column)
    {
        if (text == null || text == "NULL")
            return null;

        switch (column.Affinity)
        {
            case "INTEGER":
                if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return l;
                break;

            case "REAL":
            case "NUMERIC":
                if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return n;
                if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                break;
        }
        return text;
    }

    /// <summary>A value as the grid and the editor show it: NULL, a number in invariant form, a blob's size.</summary>
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] b => $"BLOB ({b.Length:N0} bytes)",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? String.Empty
    };

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    static string Where(SqliteCommand cmd, IReadOnlyList<string> keyColumns, object?[] key)
    {
        var parts = new List<string>();
        for (var i = 0; i < keyColumns.Count; i++)
        {
            if (key[i] == null)
            {
                parts.Add($"{Quote(keyColumns[i])} IS NULL");
            }
            else
            {
                parts.Add($"{Quote(keyColumns[i])} = $k{i}");
                cmd.Parameters.AddWithValue($"$k{i}", key[i]);
            }
        }
        return String.Join(" AND ", parts);
    }

    static object? Read(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

    /// <summary>A command in the transaction that's open, if one is - SQLite refuses a command that isn't.</summary>
    SqliteCommand Command()
    {
        var cmd = this.connection.CreateCommand();
        cmd.Transaction = this.transaction;
        return cmd;
    }

    SqliteTransaction? transaction;

    Scope Begin()
    {
        this.transaction = this.connection.BeginTransaction();
        return new Scope(this);
    }

    sealed class Scope(SqliteDatabase db) : IDisposable
    {
        public void Commit() => db.transaction!.Commit();

        public void Dispose()
        {
            db.transaction?.Dispose();
            db.transaction = null;
        }
    }

    public void Dispose() => this.connection.Dispose();
}
