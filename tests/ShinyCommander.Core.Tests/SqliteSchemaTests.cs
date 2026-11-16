using Microsoft.Data.Sqlite;
using ShinyCommander.Core.Database;

namespace ShinyCommander.Core.Tests;

public class SqliteSchemaTests
{
    const string Shop = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, email TEXT UNIQUE);
        CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers(id) ON DELETE CASCADE, total REAL DEFAULT 0);
        CREATE INDEX ix_orders_customer ON orders(customer_id);
        CREATE TRIGGER trg_orders AFTER INSERT ON orders BEGIN SELECT 1; END;
        CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
        INSERT INTO customers (name, email) VALUES ('Ann', 'ann@x'), ('Bob', 'bob@x');
        INSERT INTO orders (customer_id, total) VALUES (1, 50), (1, 150), (2, 10);
        """;

    static SqliteDatabase Open(TempDir dir, string sql = Shop)
    {
        var path = Path.Combine(dir.Path, "shop.db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        return SqliteDatabase.Open(path);
    }

    static long Scalar(SqliteDatabase db, string sql) => Convert.ToInt64(db.Execute(sql).Rows.Single()[0]);

    [Fact]
    public void Lists_indexes_and_triggers_with_their_table()
    {
        using var dir = new TempDir();
        using var db = Open(dir);
        var objects = db.GetObjects();
        objects.ShouldContain(x => x.Name == "ix_orders_customer" && x.Kind == SqliteObjectKind.Index && x.Table == "orders");
        objects.ShouldContain(x => x.Name == "trg_orders" && x.Kind == SqliteObjectKind.Trigger && x.Table == "orders");
        // the index behind UNIQUE has no SQL of its own
        objects.ShouldNotContain(x => x.Name.StartsWith("sqlite_autoindex"));
    }

    [Fact]
    public void Reads_foreign_keys_and_the_table_definition()
    {
        using var dir = new TempDir();
        using var db = Open(dir);

        var fk = db.GetForeignKeys("orders").Single();
        fk.Columns.ShouldBe(["customer_id"]);
        fk.RefTable.ShouldBe("customers");
        fk.RefColumns.ShouldBe(["id"]);
        fk.OnDelete.ShouldBe("CASCADE");

        var def = db.GetTableDefinition("customers");
        def.AutoIncrement.ShouldBeTrue();
        def.RebuildBlocker.ShouldBeNull();
        def.Columns.Single(x => x.Name == "email").Unique.ShouldBeTrue();
        def.Columns.Single(x => x.Name == "id").PrimaryKey.ShouldBeTrue();
    }

    [Fact]
    public void Renaming_adding_and_dropping_columns_keeps_the_rows()
    {
        using var dir = new TempDir();
        using var db = Open(dir);
        var def = db.GetTableDefinition("orders");
        def.Columns.Single(x => x.Name == "total").Name = "amount";
        def.Columns.Add(new SqliteColumnDefinition { Name = "note", Type = "TEXT" });

        db.AlterTable("orders", def);

        db.GetColumns("orders").Select(x => x.Name).ShouldBe(["id", "customer_id", "amount", "note"]);
        Scalar(db, "SELECT SUM(amount) FROM orders").ShouldBe(210);
    }

    [Fact]
    public void Changing_a_type_rebuilds_and_keeps_rows_indexes_triggers_and_views()
    {
        using var dir = new TempDir();
        using var db = Open(dir);
        var def = db.GetTableDefinition("orders");
        var total = def.Columns.Single(x => x.Name == "total");
        total.Type = "NUMERIC";
        total.NotNull = true;

        db.AlterTable("orders", def);

        db.GetColumns("orders").Single(x => x.Name == "total").DeclaredType.ShouldBe("NUMERIC");
        Scalar(db, "SELECT COUNT(*) FROM orders").ShouldBe(3);
        Scalar(db, "SELECT COUNT(*) FROM big_orders").ShouldBe(1);
        var names = db.GetObjects().Select(x => x.Name).ToList();
        names.ShouldContain("ix_orders_customer");
        names.ShouldContain("trg_orders");
        names.ShouldNotContain(x => x.StartsWith("_shiny_old_"));
        db.GetForeignKeys("orders").Single().RefTable.ShouldBe("customers");
    }

    [Fact]
    public void Rebuilding_a_referenced_table_keeps_the_other_tables_foreign_keys()
    {
        using var dir = new TempDir();
        using var db = Open(dir);
        var def = db.GetTableDefinition("customers");
        def.Columns.Single(x => x.Name == "name").Type = "VARCHAR(100)";

        db.AlterTable("customers", def);

        db.GetForeignKeys("orders").Single().RefTable.ShouldBe("customers");
        db.GetTableDefinition("customers").AutoIncrement.ShouldBeTrue();
        Scalar(db, "SELECT COUNT(*) FROM customers").ShouldBe(2);
    }

    [Fact]
    public void Adding_and_removing_a_foreign_key()
    {
        using var dir = new TempDir();
        using var db = Open(dir, """
            CREATE TABLE a (id INTEGER PRIMARY KEY);
            CREATE TABLE b (id INTEGER PRIMARY KEY, a_id INTEGER);
            INSERT INTO a VALUES (1); INSERT INTO b VALUES (1, 1);
            """);
        var fk = new SqliteForeignKeyDefinition { Columns = ["a_id"], RefTable = "a", RefColumns = ["id"], OnDelete = "SET NULL" };

        db.AddForeignKey("b", fk);
        db.GetAllForeignKeys().Single().ShouldSatisfyAllConditions(
            x => x.Table.ShouldBe("b"),
            x => x.RefTable.ShouldBe("a"),
            x => x.OnDelete.ShouldBe("SET NULL"));

        db.RemoveForeignKey("b", db.GetTableDefinition("b").ForeignKeys.Single());
        db.GetAllForeignKeys().ShouldBeEmpty();
        Scalar(db, "SELECT COUNT(*) FROM b").ShouldBe(1);
    }

    [Fact]
    public void A_foreign_key_the_rows_break_is_refused_and_nothing_changes()
    {
        using var dir = new TempDir();
        using var db = Open(dir, """
            CREATE TABLE a (id INTEGER PRIMARY KEY);
            CREATE TABLE b (id INTEGER PRIMARY KEY, a_id INTEGER);
            INSERT INTO b VALUES (1, 99);
            """);

        Should.Throw<InvalidOperationException>(() => db.AddForeignKey("b", new SqliteForeignKeyDefinition { Columns = ["a_id"], RefTable = "a", RefColumns = ["id"] }));
        db.GetForeignKeys("b").ShouldBeEmpty();
        Scalar(db, "SELECT COUNT(*) FROM b").ShouldBe(1);
    }

    [Fact]
    public void Tables_the_designer_cannot_rebuild_say_why_but_still_take_renames()
    {
        using var dir = new TempDir();
        using var db = Open(dir, "CREATE TABLE t (id INTEGER PRIMARY KEY, n INTEGER CHECK (n > 0), s TEXT COLLATE NOCASE);");
        var def = db.GetTableDefinition("t");
        def.RebuildBlocker.ShouldNotBeNull();
        def.RebuildBlocker!.ShouldContain("CHECK");

        def.Columns.Single(x => x.Name == "s").Name = "label";
        db.AlterTable("t", def);
        db.GetColumns("t").Select(x => x.Name).ShouldContain("label");
        db.GetObjects().Single().Sql!.ShouldContain("CHECK");

        var again = db.GetTableDefinition("t");
        again.Columns.Single(x => x.Name == "n").Type = "REAL";
        Should.Throw<InvalidOperationException>(() => db.AlterTable("t", again));
    }

    [Fact]
    public void Create_drop_and_replace_objects()
    {
        using var dir = new TempDir();
        using var db = Open(dir);

        db.CreateTable(new SqliteTableDefinition
        {
            Name = "tags",
            Columns =
            [
                new SqliteColumnDefinition { Name = "id", Type = "INTEGER", PrimaryKey = true },
                new SqliteColumnDefinition { Name = "label", Type = "TEXT", NotNull = true, Default = "new" }
            ]
        });
        db.Execute("INSERT INTO tags DEFAULT VALUES");
        db.Execute("SELECT label FROM tags").Rows.Single()[0].ShouldBe("new");

        var view = db.GetObjects().Single(x => x.Name == "big_orders");
        db.ReplaceObject(view, "CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 10");
        Scalar(db, "SELECT COUNT(*) FROM big_orders").ShouldBe(2);

        // a table's CREATE statement edited by hand: the rows of the columns still there come across
        var orders = db.GetObjects().Single(x => x.Name == "orders");
        db.ReplaceObject(orders, "CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER, total REAL CHECK (total >= 0))");
        Scalar(db, "SELECT SUM(total) FROM orders").ShouldBe(210);
        db.GetObjects().ShouldContain(x => x.Name == "ix_orders_customer");

        // ...but not when an index still needs a column the new statement leaves out
        var error = Should.Throw<InvalidOperationException>(() => db.ReplaceObject(db.GetObjects().Single(x => x.Name == "orders"), "CREATE TABLE orders (id INTEGER PRIMARY KEY, total REAL)"));
        error.Message.ShouldContain("ix_orders_customer");
        db.GetColumns("orders").Select(x => x.Name).ShouldContain("customer_id");

        db.DropObject(db.GetObjects().Single(x => x.Name == "tags"));
        db.GetObjects().ShouldNotContain(x => x.Name == "tags");

        // a bad statement leaves the old object alone
        Should.Throw<SqliteException>(() => db.ReplaceObject(db.GetObjects().Single(x => x.Name == "big_orders"), "CREATE VIEW big_orders AS SELEC nonsense"));
        db.GetObjects().ShouldContain(x => x.Name == "big_orders");
    }

    [Fact]
    public void Composite_keys_and_without_rowid_round_trip_through_create_sql()
    {
        var def = new SqliteTableDefinition
        {
            Name = "kv",
            WithoutRowId = true,
            Columns =
            [
                new SqliteColumnDefinition { Name = "k", Type = "TEXT", PrimaryKey = true },
                new SqliteColumnDefinition { Name = "part", Type = "INTEGER", PrimaryKey = true },
                new SqliteColumnDefinition { Name = "v", Type = "TEXT", Default = "it's" }
            ]
        };
        var sql = SqliteDatabase.ToCreateSql(def);
        sql.ShouldContain("PRIMARY KEY (\"k\", \"part\")");
        sql.ShouldContain("DEFAULT 'it''s'");
        sql.ShouldEndWith("WITHOUT ROWID");

        using var dir = new TempDir();
        using var db = Open(dir, sql);
        var read = db.GetTableDefinition("kv");
        read.WithoutRowId.ShouldBeTrue();
        read.Columns.Count(x => x.PrimaryKey).ShouldBe(2);
    }
}
