using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ShinyCommander.Core.Database;

public sealed record SqliteForeignKey(
    int Id,
    string Table,
    IReadOnlyList<string> Columns,
    string RefTable,
    IReadOnlyList<string> RefColumns,
    string OnUpdate,
    string OnDelete
);

/// <summary>A column in the table designer. <see cref="OriginalName"/> is null for a column being added.</summary>
public sealed class SqliteColumnDefinition
{
    public string Name { get; set; } = String.Empty;
    public string Type { get; set; } = String.Empty;
    public bool NotNull { get; set; }
    public bool PrimaryKey { get; set; }
    public bool Unique { get; set; }
    public string? Default { get; set; }
    public string? OriginalName { get; set; }

    public SqliteColumnDefinition Clone() => (SqliteColumnDefinition)this.MemberwiseClone();
}

public sealed class SqliteForeignKeyDefinition
{
    public List<string> Columns { get; set; } = [];
    public string RefTable { get; set; } = String.Empty;
    public List<string> RefColumns { get; set; } = [];
    public string OnDelete { get; set; } = "NO ACTION";
    public string OnUpdate { get; set; } = "NO ACTION";

    public SqliteForeignKeyDefinition Clone() => new()
    {
        Columns = [.. this.Columns],
        RefTable = this.RefTable,
        RefColumns = [.. this.RefColumns],
        OnDelete = this.OnDelete,
        OnUpdate = this.OnUpdate
    };

    // the same constraint, whatever the case or the defaults were spelled as
    internal string Signature => String.Join(",", this.Columns).ToLowerInvariant() + ">" + this.RefTable.ToLowerInvariant() + "(" + String.Join(",", this.RefColumns).ToLowerInvariant() + ")" + this.OnDelete + "/" + this.OnUpdate;
}

/// <summary>
/// What the table designer edits. Read with <see cref="SqliteDatabase.GetTableDefinition"/>, change, and hand to
/// <see cref="SqliteDatabase.AlterTable"/>.
/// </summary>
public sealed class SqliteTableDefinition
{
    public string Name { get; set; } = String.Empty;
    public List<SqliteColumnDefinition> Columns { get; set; } = [];
    public List<SqliteForeignKeyDefinition> ForeignKeys { get; set; } = [];
    public bool WithoutRowId { get; set; }
    public bool AutoIncrement { get; set; }
    public bool Strict { get; set; }

    /// <summary>
    /// Why the designer can't rebuild this table without losing something (CHECK constraints, collations, generated
    /// columns, multi-column UNIQUE) - null when it can. Renames, added and dropped columns still work; anything else
    /// has to go through editing the CREATE statement.
    /// </summary>
    public string? RebuildBlocker { get; set; }

    public SqliteTableDefinition Clone() => new()
    {
        Name = this.Name,
        Columns = this.Columns.Select(x => x.Clone()).ToList(),
        ForeignKeys = this.ForeignKeys.Select(x => x.Clone()).ToList(),
        WithoutRowId = this.WithoutRowId,
        AutoIncrement = this.AutoIncrement,
        Strict = this.Strict,
        RebuildBlocker = this.RebuildBlocker
    };
}

public sealed partial class SqliteDatabase
{
    public static readonly IReadOnlyList<string> ForeignKeyActions = ["NO ACTION", "RESTRICT", "SET NULL", "SET DEFAULT", "CASCADE"];

    public IReadOnlyList<SqliteForeignKey> GetForeignKeys(string table)
    {
        using var cmd = this.Command();
        cmd.CommandText = $"PRAGMA foreign_key_list({Quote(table)})";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(int Id, int Seq, string RefTable, string From, string? To, string OnUpdate, string OnDelete)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)
            ));
        }

        return rows
            .GroupBy(x => x.Id)
            .Select(g =>
            {
                var parts = g.OrderBy(x => x.Seq).ToList();
                var first = parts[0];
                // REFERENCES t with no column list means t's primary key
                var refColumns = parts.Select(x => x.To).ToList();
                var resolved = refColumns.All(x => x != null)
                    ? refColumns.Select(x => x!).ToList()
                    : this.PrimaryKeyOf(first.RefTable);
                return new SqliteForeignKey(g.Key, table, parts.Select(x => x.From).ToList(), first.RefTable, resolved, first.OnUpdate, first.OnDelete);
            })
            .OrderBy(x => x.Id)
            .ToList();
    }

    /// <summary>Every foreign key in the database - the diagram's connections.</summary>
    public IReadOnlyList<SqliteForeignKey> GetAllForeignKeys() => this.GetObjects()
        .Where(x => x.Kind == SqliteObjectKind.Table)
        .SelectMany(x => this.GetForeignKeys(x.Name))
        .ToList();

    IReadOnlyList<string> PrimaryKeyOf(string table)
    {
        var pk = this.GetColumns(table).Where(x => x.IsPrimaryKey).OrderBy(x => x.PrimaryKeyOrder).Select(x => x.Name).ToList();
        return pk.Count > 0 ? pk : ["rowid"];
    }

    public SqliteTableDefinition GetTableDefinition(string table)
    {
        var obj = this.GetObjects().Single(x => x.Kind == SqliteObjectKind.Table && x.Name.Equals(table, StringComparison.OrdinalIgnoreCase));
        var sql = obj.Sql ?? String.Empty;
        var upper = sql.ToUpperInvariant();
        var uniques = this.UniqueConstraints(table);

        var def = new SqliteTableDefinition
        {
            Name = obj.Name,
            WithoutRowId = Regex.IsMatch(upper, @"\)\s*(STRICT\s*,\s*)?WITHOUT\s+ROWID"),
            Strict = Regex.IsMatch(upper, @"\)\s*(WITHOUT\s+ROWID\s*,\s*)?STRICT\b"),
            AutoIncrement = upper.Contains("AUTOINCREMENT"),
            Columns = this.GetColumns(obj.Name)
                .Select(c => new SqliteColumnDefinition
                {
                    Name = c.Name,
                    OriginalName = c.Name,
                    Type = c.DeclaredType,
                    NotNull = c.NotNull,
                    PrimaryKey = c.IsPrimaryKey,
                    Default = c.DefaultValue,
                    Unique = uniques.Any(u => u.Count == 1 && u[0].Equals(c.Name, StringComparison.OrdinalIgnoreCase))
                })
                .ToList(),
            ForeignKeys = this.GetForeignKeys(obj.Name)
                .Select(f => new SqliteForeignKeyDefinition
                {
                    Columns = [.. f.Columns],
                    RefTable = f.RefTable,
                    RefColumns = [.. f.RefColumns],
                    OnDelete = f.OnDelete,
                    OnUpdate = f.OnUpdate
                })
                .ToList()
        };

        var blockers = new List<string>();
        if (Regex.IsMatch(upper, @"\bCHECK\s*\("))
            blockers.Add("CHECK");
        if (Regex.IsMatch(upper, @"\bCOLLATE\b"))
            blockers.Add("COLLATE");
        if (Regex.IsMatch(upper, @"\bGENERATED\b|\bAS\s*\(") || this.HasHiddenColumns(obj.Name))
            blockers.Add("generated columns");
        if (uniques.Any(u => u.Count > 1))
            blockers.Add("multi-column UNIQUE");
        if (Regex.IsMatch(upper, @"\bON\s+CONFLICT\b|\bDEFERRABLE\b"))
            blockers.Add("ON CONFLICT / DEFERRABLE");
        def.RebuildBlocker = blockers.Count == 0 ? null : String.Join(", ", blockers);
        return def;
    }

    List<List<string>> UniqueConstraints(string table)
    {
        var names = new List<string>();
        using (var cmd = this.Command())
        {
            cmd.CommandText = $"PRAGMA index_list({Quote(table)})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(3) == "u")
                    names.Add(reader.GetString(1));
            }
        }

        var result = new List<List<string>>();
        foreach (var name in names)
        {
            using var cmd = this.Command();
            cmd.CommandText = $"PRAGMA index_info({Quote(name)})";
            using var reader = cmd.ExecuteReader();
            var cols = new List<string>();
            while (reader.Read())
                cols.Add(reader.IsDBNull(2) ? String.Empty : reader.GetString(2));
            result.Add(cols);
        }
        return result;
    }

    bool HasHiddenColumns(string table)
    {
        using var cmd = this.Command();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_xinfo({QuoteLiteral(table)}) WHERE hidden <> 0";
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>The CREATE TABLE statement for a definition.</summary>
    public static string ToCreateSql(SqliteTableDefinition def)
    {
        var pk = def.Columns.Where(x => x.PrimaryKey).ToList();
        // a lone INTEGER PRIMARY KEY stays inline - that's what makes it the rowid (and what AUTOINCREMENT needs)
        var inlinePk = pk.Count == 1 && pk[0].Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && !def.WithoutRowId;

        var lines = new List<string>();
        foreach (var c in def.Columns)
        {
            var sb = new StringBuilder("    ").Append(Quote(c.Name));
            if (!String.IsNullOrWhiteSpace(c.Type))
                sb.Append(' ').Append(c.Type.Trim());
            if (inlinePk && c.PrimaryKey)
                sb.Append(" PRIMARY KEY").Append(def.AutoIncrement ? " AUTOINCREMENT" : String.Empty);
            if (c.NotNull)
                sb.Append(" NOT NULL");
            if (c.Unique && !c.PrimaryKey)
                sb.Append(" UNIQUE");
            if (!String.IsNullOrWhiteSpace(c.Default))
                sb.Append(" DEFAULT ").Append(DefaultExpression(c.Default!));
            lines.Add(sb.ToString());
        }
        if (pk.Count > 0 && !inlinePk)
            lines.Add($"    PRIMARY KEY ({String.Join(", ", pk.Select(x => Quote(x.Name)))})");
        foreach (var fk in def.ForeignKeys)
        {
            var line = $"    FOREIGN KEY ({String.Join(", ", fk.Columns.Select(Quote))}) REFERENCES {Quote(fk.RefTable)}";
            if (fk.RefColumns.Count > 0)
                line += $" ({String.Join(", ", fk.RefColumns.Select(Quote))})";
            if (fk.OnDelete != "NO ACTION")
                line += " ON DELETE " + fk.OnDelete;
            if (fk.OnUpdate != "NO ACTION")
                line += " ON UPDATE " + fk.OnUpdate;
            lines.Add(line);
        }

        var options = new List<string>();
        if (def.WithoutRowId)
            options.Add("WITHOUT ROWID");
        if (def.Strict)
            options.Add("STRICT");
        return $"CREATE TABLE {Quote(def.Name)} (\n{String.Join(",\n", lines)}\n){(options.Count > 0 ? " " + String.Join(", ", options) : String.Empty)}";
    }

    // what PRAGMA table_info reports is already an expression ('abc', 0, CURRENT_TIMESTAMP); typed plain text gets quoted
    static string DefaultExpression(string value)
    {
        var v = value.Trim();
        var isExpression = v.StartsWith('\'') || v.StartsWith('(') || v.StartsWith('"')
            || Double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            || v.ToUpperInvariant() is "NULL" or "TRUE" or "FALSE" or "CURRENT_TIME" or "CURRENT_DATE" or "CURRENT_TIMESTAMP";
        return isExpression ? v : QuoteLiteral(v);
    }

    public void CreateTable(SqliteTableDefinition def) => this.ExecuteInTransaction(ToCreateSql(def));

    /// <summary>
    /// Changes a table to look like <paramref name="def"/>. Renames, added and dropped columns are plain ALTER TABLE
    /// statements, so the rest of the table (and anything the designer can't show) stays as it was. A changed type,
    /// NOT NULL, default, key or foreign key needs SQLite's create-copy-drop rebuild, which keeps the rows, indexes
    /// and triggers.
    /// </summary>
    public void AlterTable(string table, SqliteTableDefinition def)
    {
        var current = this.GetTableDefinition(table);
        var kept = def.Columns.Where(x => x.OriginalName != null).ToList();
        var dropped = current.Columns.Where(c => !kept.Any(k => k.OriginalName!.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        var added = def.Columns.Where(x => x.OriginalName == null).ToList();

        var changedColumn = kept.Any(k =>
        {
            var was = current.Columns.Single(c => c.Name.Equals(k.OriginalName, StringComparison.OrdinalIgnoreCase));
            return !was.Type.Equals(k.Type.Trim(), StringComparison.OrdinalIgnoreCase)
                || was.NotNull != k.NotNull
                || was.PrimaryKey != k.PrimaryKey
                || was.Unique != k.Unique
                || (was.Default ?? String.Empty) != (k.Default ?? String.Empty);
        });
        var fksChanged = !current.ForeignKeys.Select(x => x.Signature).Order()
            .SequenceEqual(def.ForeignKeys.Select(x => Renamed(x, kept).Signature).Order());
        // ADD COLUMN can't add a key, a UNIQUE, or a NOT NULL without a default
        var addNeedsRebuild = added.Any(x => x.PrimaryKey || x.Unique || (x.NotNull && String.IsNullOrWhiteSpace(x.Default)));
        var optionsChanged = current.WithoutRowId != def.WithoutRowId || current.AutoIncrement != def.AutoIncrement || current.Strict != def.Strict;

        if (changedColumn || fksChanged || addNeedsRebuild || optionsChanged)
        {
            if (current.RebuildBlocker != null)
                throw new InvalidOperationException($"This table uses {current.RebuildBlocker}, which the designer can't keep - change it by editing its CREATE statement instead.");

            var map = kept.ToDictionary(x => x.Name, x => x.OriginalName!);
            var newName = def.Name;
            def.Name = table;
            this.RebuildTable(table, ToCreateSql(def), map);
            def.Name = newName;
            if (!newName.Equals(table, StringComparison.Ordinal))
                this.ExecuteInTransaction($"ALTER TABLE {Quote(table)} RENAME TO {Quote(newName)}");
            return;
        }

        var statements = new List<string>();
        foreach (var c in kept.Where(k => !k.Name.Equals(k.OriginalName, StringComparison.Ordinal)))
            statements.Add($"ALTER TABLE {Quote(table)} RENAME COLUMN {Quote(c.OriginalName!)} TO {Quote(c.Name)}");
        foreach (var c in dropped)
            statements.Add($"ALTER TABLE {Quote(table)} DROP COLUMN {Quote(c.Name)}");
        foreach (var c in added)
        {
            var single = new SqliteTableDefinition { Name = "x", Columns = [c] };
            var columnSql = ToCreateSql(single).Split('\n')[1].Trim();
            statements.Add($"ALTER TABLE {Quote(table)} ADD COLUMN {columnSql}");
        }
        if (!def.Name.Equals(table, StringComparison.Ordinal))
            statements.Add($"ALTER TABLE {Quote(table)} RENAME TO {Quote(def.Name)}");

        if (statements.Count > 0)
            this.ExecuteInTransaction(String.Join(";\n", statements));
    }

    // foreign keys in the designer name columns as they're called now; compare them as the table has them
    static SqliteForeignKeyDefinition Renamed(SqliteForeignKeyDefinition fk, List<SqliteColumnDefinition> kept)
    {
        var copy = fk.Clone();
        copy.Columns = fk.Columns
            .Select(c => kept.FirstOrDefault(k => k.Name.Equals(c, StringComparison.OrdinalIgnoreCase))?.OriginalName ?? c)
            .ToList();
        return copy;
    }

    /// <summary>
    /// Replaces a table's CREATE statement, keeping its rows: the columns whose names are in both come across (an
    /// explicit <paramref name="columnMap"/> of new name to old name for renames). Indexes and triggers are
    /// recreated. This is the procedure from SQLite's ALTER TABLE docs, section 7.
    /// </summary>
    public void RebuildTable(string table, string createSql, IReadOnlyDictionary<string, string>? columnMap = null)
    {
        var oldColumns = this.GetColumns(table).Select(x => x.Name).ToList();
        var dependents = this.GetObjects()
            .Where(x => x.Kind is SqliteObjectKind.Index or SqliteObjectKind.Trigger && x.Table.Equals(table, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var temp = "_shiny_old_" + Guid.NewGuid().ToString("N")[..8];

        this.WithForeignKeysOff(() =>
        {
            using var tx = this.Begin();
            // legacy rename: views, triggers and other tables' REFERENCES keep naming the table, not the temporary name
            this.Run("PRAGMA legacy_alter_table = ON");
            this.Run($"ALTER TABLE {Quote(table)} RENAME TO {Quote(temp)}");
            var before = this.TableNames();
            this.Run(createSql);

            var created = this.TableNames().Except(before, StringComparer.OrdinalIgnoreCase).SingleOrDefault()
                ?? throw new InvalidOperationException("The statement has to create one table.");
            var newColumns = this.GetColumns(created).Select(x => x.Name).ToList();
            var pairs = newColumns
                .Select(n => (New: n, Old: columnMap != null && columnMap.TryGetValue(n, out var o) ? o : n))
                .Where(x => oldColumns.Contains(x.Old, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (pairs.Count > 0)
                this.Run($"INSERT INTO {Quote(created)} ({String.Join(", ", pairs.Select(x => Quote(x.New)))}) SELECT {String.Join(", ", pairs.Select(x => Quote(x.Old)))} FROM {Quote(temp)}");

            this.Run($"DROP TABLE {Quote(temp)}");
            // an index or trigger on a column that's gone fails here, and the whole change with it - nothing is lost quietly
            foreach (var dependent in dependents)
            {
                try
                {
                    this.Run(dependent.Sql!);
                }
                catch (SqliteException ex)
                {
                    throw new InvalidOperationException($"The {Keyword(dependent.Kind).ToLowerInvariant()} \"{dependent.Name}\" doesn't fit the new table ({ex.Message}) - drop or change it first.", ex);
                }
            }
            this.Run("PRAGMA legacy_alter_table = OFF");
            this.CheckForeignKeys();
            tx.Commit();
        });
    }

    List<string> TableNames()
    {
        using var cmd = this.Command();
        cmd.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table'";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>A view, index or trigger with new SQL - the old one dropped and the new one created together.</summary>
    public void ReplaceObject(SqliteObject obj, string sql)
    {
        if (obj.Kind == SqliteObjectKind.Table)
        {
            this.RebuildTable(obj.Name, sql);
            return;
        }
        this.ExecuteInTransaction($"DROP {Keyword(obj.Kind)} {Quote(obj.Name)};\n{sql}");
    }

    public void DropObject(SqliteObject obj) => this.ExecuteInTransaction($"DROP {Keyword(obj.Kind)} {Quote(obj.Name)}");

    public void AddForeignKey(string table, SqliteForeignKeyDefinition fk)
    {
        var def = this.GetTableDefinition(table);
        def.ForeignKeys.Add(fk);
        this.AlterTable(table, def);
    }

    public void RemoveForeignKey(string table, SqliteForeignKeyDefinition fk)
    {
        var def = this.GetTableDefinition(table);
        def.ForeignKeys.RemoveAll(x => x.Signature == fk.Signature);
        this.AlterTable(table, def);
    }

    /// <summary>DDL (or anything) as one unit - all of it, or none of it if a statement fails.</summary>
    public void ExecuteInTransaction(string sql)
    {
        using var tx = this.Begin();
        this.Run(sql);
        tx.Commit();
    }

    void WithForeignKeysOff(Action work)
    {
        // can't change inside a transaction, so it goes round it
        var on = this.Scalar("PRAGMA foreign_keys") is long l && l != 0;
        if (on)
            this.Run("PRAGMA foreign_keys = OFF");
        try
        {
            work();
        }
        finally
        {
            if (on)
                this.Run("PRAGMA foreign_keys = ON");
        }
    }

    void CheckForeignKeys()
    {
        using var cmd = this.Command();
        cmd.CommandText = "PRAGMA foreign_key_check";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            throw new InvalidOperationException($"Rows in \"{reader.GetString(0)}\" would break a foreign key to \"{reader.GetString(2)}\".");
    }

    void Run(string sql)
    {
        using var cmd = this.Command();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    object? Scalar(string sql)
    {
        using var cmd = this.Command();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    static string Keyword(SqliteObjectKind kind) => kind switch
    {
        SqliteObjectKind.View => "VIEW",
        SqliteObjectKind.Index => "INDEX",
        SqliteObjectKind.Trigger => "TRIGGER",
        _ => "TABLE"
    };

    public static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
