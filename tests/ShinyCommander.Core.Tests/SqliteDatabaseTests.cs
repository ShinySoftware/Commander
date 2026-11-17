using Microsoft.Data.Sqlite;
using ShinyCommander.Core.Database;

namespace ShinyCommander.Core.Tests;

public class SqliteDatabaseTests
{
    static string Create(TempDir dir, string sql, string name = "test.db")
    {
        var path = Path.Combine(dir.Path, name);
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return path;
    }

    const string People = """
        CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER, score REAL);
        INSERT INTO people (name, age, score) VALUES ('Ann', 30, 1.5), ('Bob', 40, NULL), ('Cy', 25, 3);
        CREATE VIEW adults AS SELECT name FROM people WHERE age >= 30;
        """;

    [Fact]
    public void Recognises_sqlite_files_by_their_header()
    {
        using var dir = new TempDir();
        SqliteDatabase.IsSqliteFile(Create(dir, People)).ShouldBeTrue();
        SqliteDatabase.IsSqliteFile(dir.File("Thumbs.db", "not a database")).ShouldBeFalse();
    }

    [Fact]
    public void Opened_read_only_it_reads_but_never_writes()
    {
        using var dir = new TempDir();
        var path = Create(dir, People);
        using (var db = SqliteDatabase.Open(path, readOnly: true))
        {
            db.Execute("SELECT name FROM people ORDER BY id").Rows.Select(x => x[0]).ShouldBe(["Ann", "Bob", "Cy"]);
            Should.Throw<SqliteException>(() => db.Execute("DELETE FROM people"));
        }
        using var check = SqliteDatabase.Open(path);
        check.Count("people").ShouldBe(3);
    }

    [Fact]
    public void Lists_tables_and_views_with_their_columns()
    {
        using var dir = new TempDir();
        using var db = SqliteDatabase.Open(Create(dir, People));

        var objects = db.GetObjects();
        objects.Select(x => (x.Name, x.Kind)).ShouldBe([("people", SqliteObjectKind.Table), ("adults", SqliteObjectKind.View)]);

        var columns = db.GetColumns("people");
        columns.Select(x => x.Name).ShouldBe(["id", "name", "age", "score"]);
        columns[0].IsPrimaryKey.ShouldBeTrue();
        columns[1].NotNull.ShouldBeTrue();
        columns[3].Affinity.ShouldBe("REAL");
        db.GetKeyColumns(objects[0], columns).ShouldBe(["rowid"]);
        db.GetKeyColumns(objects[1], db.GetColumns("adults")).ShouldBeNull();
    }

    [Fact]
    public void Pages_and_sorts()
    {
        using var dir = new TempDir();
        using var db = SqliteDatabase.Open(Create(dir, People));
        var columns = db.GetColumns("people");

        var page = db.ReadPage("people", columns, ["rowid"], 1, 1, [("age", true)]);
        page.Total.ShouldBe(3);
        page.Rows.Count.ShouldBe(1);
        page.Rows[0].Values[1].ShouldBe("Ann");
    }

    [Fact]
    public void Insert_update_and_delete_by_rowid()
    {
        using var dir = new TempDir();
        using var db = SqliteDatabase.Open(Create(dir, People));
        var columns = db.GetColumns("people");

        db.Insert("people", new Dictionary<string, object?> { ["name"] = "Dee", ["age"] = 50L });
        db.Count("people").ShouldBe(4);

        var dee = db.ReadPage("people", columns, ["rowid"], 0, 10, []).Rows.Single(x => (string?)x.Values[1] == "Dee");
        db.Update("people", ["rowid"], dee.Key, new Dictionary<string, object?> { ["name"] = "Dee Dee", ["score"] = null }).ShouldBe(1);
        db.Execute("SELECT name FROM people WHERE age = 50").Rows.Single()[0].ShouldBe("Dee Dee");

        db.Delete("people", ["rowid"], dee.Key).ShouldBe(1);
        db.Count("people").ShouldBe(3);
    }

    [Fact]
    public void Without_rowid_tables_are_keyed_by_their_primary_key()
    {
        using var dir = new TempDir();
        using var db = SqliteDatabase.Open(Create(dir, """
            CREATE TABLE kv (k TEXT, part INTEGER, v TEXT, PRIMARY KEY (k, part)) WITHOUT ROWID;
            INSERT INTO kv VALUES ('a', 1, 'x'), ('a', 2, 'y');
            """));
        var table = db.GetObjects().Single();
        var columns = db.GetColumns("kv");
        var keys = db.GetKeyColumns(table, columns)!;
        keys.ShouldBe(["k", "part"]);

        var row = db.ReadPage("kv", columns, keys, 0, 10, [("part", false)]).Rows[1];
        db.Update("kv", keys, row.Key, new Dictionary<string, object?> { ["v"] = "z" }).ShouldBe(1);
        db.Execute("SELECT v FROM kv WHERE part = 2").Rows.Single()[0].ShouldBe("z");
    }

    [Fact]
    public void Execute_returns_rows_or_the_change_count()
    {
        using var dir = new TempDir();
        using var db = SqliteDatabase.Open(Create(dir, People));

        var select = db.Execute("SELECT name, age FROM people ORDER BY age");
        select.Columns.ShouldBe(["name", "age"]);
        select.Rows.Select(x => x[0]).ShouldBe(["Cy", "Ann", "Bob"]);

        var update = db.Execute("UPDATE people SET age = age + 1 WHERE age < 35");
        update.HasRows.ShouldBeFalse();
        update.RowsAffected.ShouldBe(2);

        Should.Throw<SqliteException>(() => db.Execute("SELECT * FROM nope"));
    }

    [Theory]
    [InlineData("INTEGER", "42", 42L)]
    [InlineData("INTEGER", "4x", "4x")]
    [InlineData("REAL", "1.25", 1.25)]
    [InlineData("TEXT", "42", "42")]
    [InlineData("TEXT", "NULL", null)]
    public void Editor_text_becomes_the_column_type(string type, string text, object? expected)
    {
        var column = new SqliteColumn("c", type, false, null, 0);
        SqliteDatabase.Parse(text, column).ShouldBe(expected);
    }

    [Fact]
    public void Closing_releases_the_file()
    {
        using var dir = new TempDir();
        var path = Create(dir, People);
        using (var db = SqliteDatabase.Open(path))
            db.Execute("UPDATE people SET age = 1");

        File.Delete(path);
        File.Exists(path).ShouldBeFalse();
    }
}
