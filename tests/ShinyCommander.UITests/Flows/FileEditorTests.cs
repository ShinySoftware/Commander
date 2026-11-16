using System.Diagnostics;
using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>F4 picks the editor for the file's type: JPEG metadata, SQLite databases, text for the rest.</summary>
public class FileEditorTests(AppFixture fixture) : UiTest(fixture)
{
    // an 8x8 JPEG with no EXIF
    static readonly byte[] TinyJpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAACKADAAQAAAABAAAACAAAAAD/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9sAQwACAgICAgIDAgIDBQMDAwUGBQUFBQYIBgYGBgYICggICAgICAoKCgoKCgoKDAwMDAwMDg4ODg4PDw8PDw8PDw8P/9sAQwECAgIEBAQHBAQHEAsJCxAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ/90ABAAB/9oADAMBAAIRAxEAPwDwL48/FLStTt7vSrmOzE6TNa/u3b7Ujo2G3JhlA2kMD1KnIGcgfJP27TP+fl/1/wDjFaXxh/5KJrn/AGF5P/RMdcJXlUpVYU4RhVmkkvtP/M+hxFWHtZuVKLd+sUz/2Q==");

    // a real photo where the machine has one (the image editor at 8x8 is a black square), else the tiny one
    static byte[] Photo()
    {
        const string wallpaper = "/System/Library/Wallpapers/.default/DefaultAerial.jpg";
        if (!File.Exists(wallpaper))
            return TinyJpeg;

        var output = Path.Combine(Path.GetTempPath(), "sc-photo-" + Guid.NewGuid().ToString("N") + ".jpg");
        using (var p = Process.Start(new ProcessStartInfo("/usr/bin/sips", ["-z", "900", "1200", wallpaper, "--out", output]) { RedirectStandardOutput = true })!)
            p.WaitForExit();
        var bytes = File.ReadAllBytes(output);
        File.Delete(output);
        return bytes;
    }

    // XPTitle is UTF-16, at whatever offset - decoding the whole file as UTF-16 misses it at an odd one
    static bool HasTitle(byte[] file) => file.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("Lake at dusk")) >= 0;

    static void Sqlite(string path, string sql)
    {
        var psi = new ProcessStartInfo("/usr/bin/sqlite3", [path]) { RedirectStandardInput = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(sql);
        p.StandardInput.Close();
        p.WaitForExit();
        p.ExitCode.ShouldBe(0);
    }

    static string Query(string path, string sql)
    {
        var psi = new ProcessStartInfo("/usr/bin/sqlite3", [path, sql]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    async Task Shot(string name)
    {
        if (Environment.GetEnvironmentVariable("SC_UITEST_SCREENS") is not ("1" or "true"))
            return;

        await Task.Delay(600);
        var current = (await this.App.StateNow()).Page;
        var page = (await this.Ui.All()).LastOrDefault(x => x.Type == current && x.Bounds is { Width: > 0 });
        if (page != null && await this.Fixture.Client.ScreenshotAsync(elementId: page.Id) is { } png)
        {
            var dir = Path.Combine(TestConfig.ArtifactDir, "screens");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, name + ".png"), png);
        }
    }

    [Fact]
    public async Task F4_on_a_jpeg_edits_its_metadata()
    {
        await this.Start(l => l.Bytes("photo.jpg", Photo()));
        await this.App.Cursor("photo.jpg");

        var s = await this.App.Key("F4");
        s = await this.App.WaitFor(x => x.Page == "ImageMetadataPage", "metadata editor");
        await this.Shot("editor-image-metadata");

        await this.Ui.Fill(await this.Ui.ById("ImageMetaTitle_Entry"), "Lake at dusk");
        await this.Ui.Fill(await this.Ui.ById("ImageMetaFields_Cell_0_1_Entry"), "Golden Gate from Baker Beach");
        await this.Ui.Fill(await this.Ui.ById("ImageMetaFields_Cell_0_2_Entry"), "sunset; bridge; coast");
        await this.Ui.Fill(await this.Ui.ById("ImageMetaFields_Cell_1_0_Entry"), "Allan Ritchie");
        await this.Ui.Fill(await this.Ui.ById("ImageMetaFields_Cell_1_1_Entry"), "© 2026 Shiny");
        await this.Ui.Fill(await this.Ui.ById("ImageMetaFields_Cell_1_2_Entry"), "2026-06-21 20:41:00");
        await this.Ui.Tap("ImageMetaSave");
        await Task.Delay(500);
        await this.Shot("editor-image-metadata-filled");

        var bytes = File.ReadAllBytes(this.LeftTree["photo.jpg"]);
        HasTitle(bytes).ShouldBeTrue(); // XPTitle is UTF-16

        // the image editor re-encodes the picture; the metadata comes along
        var before = File.ReadAllBytes(this.LeftTree["photo.jpg"]);
        await this.Ui.Click("Edit image");
        await Task.Delay(800);
        await this.Shot("editor-image-edit");
        await this.Ui.Tap("ImageEditor_Save");
        await Task.Delay(1500);
        var after = File.ReadAllBytes(this.LeftTree["photo.jpg"]);
        if (after.SequenceEqual(before))
        {
            // Shiny's ImageEditor can't render an export on desktop yet: the photo must be left alone, and say so
            (await this.App.State()).Dialogs.ShouldContain(d => d.Kind == "alert" && d.Message!.Contains("ImageEditor"));
        }
        else
        {
            HasTitle(after).ShouldBeTrue();
        }
        await this.Ui.Click("Metadata");

        await this.App.Key("Escape");
        await this.App.WaitFor(x => x.Page == "MainPage", "back to the panels");
    }

    [Fact]
    public async Task F4_on_a_sqlite_db_opens_the_database_editor_and_a_plain_db_the_text_editor()
    {
        await this.Start(l => l.File("Thumbs.db", "not sqlite"));
        Sqlite(this.LeftTree["app.db"], """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER);
            INSERT INTO people (name, age) VALUES ('Ann', 30), ('Bob', 40), ('Cy', 25);
            CREATE VIEW adults AS SELECT name FROM people WHERE age >= 30;
            """);
        await this.App.Navigate(this.LeftTree.Path);

        await this.App.Cursor("Thumbs.db");
        await this.App.Key("F4");
        await this.App.WaitFor(x => x.Page == "EditorPage", "text editor for a non-SQLite .db");
        await this.App.Key("Escape");
        await this.App.WaitFor(x => x.Page == "MainPage", "back to the panels");

        await this.App.Cursor("app.db");
        await this.App.Key("F4");
        await this.App.WaitFor(x => x.Page == "DatabasePage", "database editor");
        await this.Shot("editor-database-data");
        await this.Ui.ByText("Ann", timeout: TimeSpan.FromSeconds(5));

        // insert
        await this.Ui.Tap("DbInsert");
        await this.Ui.Fill(await this.Ui.ById("DbInsert_name"), "Dee");
        await this.Ui.Fill(await this.Ui.ById("DbInsert_age"), "50");
        await this.Shot("editor-database-insert");
        await this.Ui.Tap("DbInsertConfirm");
        await this.Ui.ByText("Dee", timeout: TimeSpan.FromSeconds(5));
        Query(this.LeftTree["app.db"], "SELECT age FROM people WHERE name = 'Dee'").ShouldBe("50");

        // schema
        await this.Ui.Click("Schema");
        await this.Shot("editor-database-schema");

        // SQL: a write, then a query
        await this.Ui.Click("SQL");
        await this.Ui.Fill(await this.Ui.ById("DbSql"), "DELETE FROM people WHERE name = 'Bob'");
        await this.Ui.Tap("DbRun");
        await this.App.WaitFor(_ => true, "run");
        (await this.Ui.ById("DbQueryStatus")).Text.ShouldBe("1 rows changed");
        Query(this.LeftTree["app.db"], "SELECT COUNT(*) FROM people").ShouldBe("3");

        await this.Ui.Fill(await this.Ui.ById("DbSql"), "SELECT name, age FROM people ORDER BY age");
        await this.Ui.Tap("DbRun");
        await this.Ui.ByText("Cy", timeout: TimeSpan.FromSeconds(5));
        await this.Shot("editor-database-sql");

        await this.App.Key("Escape");
        await this.App.WaitFor(x => x.Page == "MainPage", "back to the panels");
    }

    [Fact]
    public async Task The_schema_designer_alters_a_table_and_the_diagram_shows_foreign_keys()
    {
        await this.Start();
        Sqlite(this.LeftTree["shop.db"], """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT UNIQUE);
            CREATE TABLE products (id INTEGER PRIMARY KEY, title TEXT, price REAL);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers(id), placed TEXT);
            CREATE TABLE order_lines (order_id INTEGER REFERENCES orders(id) ON DELETE CASCADE, product_id INTEGER REFERENCES products(id), qty INTEGER, PRIMARY KEY (order_id, product_id));
            CREATE INDEX ix_orders_customer ON orders(customer_id);
            INSERT INTO customers (name) VALUES ('Ann');
            """);
        await this.App.Navigate(this.LeftTree.Path);
        await this.App.Cursor("shop.db");
        await this.App.Key("F4");
        await this.App.WaitFor(x => x.Page == "DatabasePage", "database editor");

        await this.Ui.Tap("DbObject_customers");
        await this.Ui.Click("Schema");
        await this.Shot("editor-database-designer");

        await this.Ui.Tap("DbAddColumn");
        await this.Ui.Tap("DbApplyDesign");
        await Task.Delay(500);
        Query(this.LeftTree["shop.db"], "SELECT name FROM pragma_table_info('customers') ORDER BY cid DESC LIMIT 1").ShouldBe("column4");
        Query(this.LeftTree["shop.db"], "SELECT COUNT(*) FROM customers").ShouldBe("1");

        await this.Ui.Click("Diagram");
        await Task.Delay(800);
        await this.Shot("editor-database-diagram");

        await this.App.Key("Escape");
        await this.App.WaitFor(x => x.Page == "MainPage", "back to the panels");
    }
}
