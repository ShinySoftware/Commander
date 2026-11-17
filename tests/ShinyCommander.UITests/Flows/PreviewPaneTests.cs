using System.Diagnostics;
using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>The preview panel (Ctrl+Q): docked beside or under both file panels, a preview plugin per file type.</summary>
public class PreviewPaneTests(AppFixture fixture) : UiTest(fixture)
{
    // the smallest file a PDF viewer accepts - a header is all the previewer checks
    const string TinyPdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj 3 0 obj<</Type/Page/MediaBox[0 0 200 200]/Parent 2 0 R>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n";

    static void Sqlite(string path, string sql)
    {
        var psi = new ProcessStartInfo("/usr/bin/sqlite3", [path]) { RedirectStandardInput = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(sql);
        p.StandardInput.Close();
        p.WaitForExit();
        p.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Ctrl_Q_opens_the_preview_beside_both_panels_and_it_follows_the_cursor()
    {
        await this.Start(l => l.File("one.txt", "the first file").File("two.md", "# second\n\nbody"));
        await this.App.Cursor("one.txt");

        var s = await this.App.Key("Ctrl+Q");
        s.Preview.IsOpen.ShouldBeTrue();
        s = await this.App.WaitFor(x => x.Preview.Text.Contains("first file"), "the text preview");
        s.Preview.Title.ShouldBe("one.txt");
        s.Preview.Kind.ShouldBe("Text");

        // both file panels stay, the preview is a third pane to their right
        var right = (await this.Ui.ById("RightPanel")).WindowBounds!;
        var pane = (await this.Ui.ById("PreviewPane")).WindowBounds!;
        pane.X.ShouldBeGreaterThan(right.X + right.Width - 1);

        await this.App.Key("Down");
        s = await this.App.WaitFor(x => x.Preview.Title == "two.md" && x.Preview.Previewer == "markdown", "the markdown previewer");
        s.Preview.Kind.ShouldBeNull();
    }

    [Fact]
    public async Task Ctrl_Q_again_closes_it_and_switching_panels_keeps_it_open()
    {
        await this.Start(l => l.File("a.txt", "left"), r => r.File("b.txt", "right side"));
        await this.App.Cursor("a.txt");

        await this.App.Key("Ctrl+Q");
        await this.App.Cursor("b.txt", "right");
        var s = await this.App.WaitFor(x => x.Preview.Text.Contains("right side"), "the preview to follow the active panel");
        s.Preview.IsOpen.ShouldBeTrue();
        s.ActiveSide.ShouldBe("right");

        (await this.App.Key("Ctrl+Q")).Preview.IsOpen.ShouldBeFalse();
        (await this.Fixture.Client.QueryAsync(automationId: "PreviewPane")).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_dock_button_moves_it_under_the_panels()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Key("Ctrl+Q");

        await this.Ui.Tap("PreviewDock");
        var s = await this.App.WaitFor(x => x.Preview.Position == "Bottom", "the preview docked at the bottom");
        var left = (await this.Ui.ById("LeftPanel")).WindowBounds!;
        var pane = (await this.Ui.ById("PreviewPane")).WindowBounds!;
        pane.Y.ShouldBeGreaterThan(left.Y + left.Height - 1);

        await this.Ui.Tap("PreviewDock");
        await this.App.WaitFor(x => x.Preview.Position == "Right", "back to the right");
    }

    [Fact]
    public async Task Settings_moves_it_and_turns_previewers_off()
    {
        await this.Start(l => l.File("notes.md", "# heading"));
        await this.App.Cursor("notes.md");
        await this.App.Key("Ctrl+Q");

        await this.App.Command("Settings");
        await this.App.WaitFor(x => x.Page == "SettingsPage", "the settings page");
        await this.Ui.Click("Under the file panels");
        (await this.App.WaitFor(x => x.Preview.Position == "Bottom", "the preview docked at the bottom")).Preview.IsOpen.ShouldBeTrue();
        await this.Shot("settings-preview");
        await this.Ui.Tap("SettingsPreviewer_markdown");

        await this.App.Key("Escape");
        await this.App.WaitFor(x => x.Page == "MainPage", "back to the panels");
        var s = await this.App.WaitFor(x => x.Preview.Kind == "Text", "markdown as text once its previewer is off");
        s.Preview.Position.ShouldBe("Bottom");
    }

    async Task Shot(string name)
    {
        if (Environment.GetEnvironmentVariable("SC_UITEST_SCREENS") is not ("1" or "true"))
            return;

        // the whole window: walking the element tree stalls for minutes while a spreadsheet is on screen
        await Task.Delay(600);
        if (await this.Fixture.Client.ScreenshotAsync() is { } png)
        {
            var dir = Path.Combine(TestConfig.ArtifactDir, "screens");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, name + ".png"), png);
        }
    }

    [Fact]
    public async Task Images_html_and_pdf_go_to_their_previewers()
    {
        await this.Start(l => l
            .Bytes("dot.png", TestTree.Png1x1)
            .File("page.html", "<html><body><h1>Hello</h1><script>document.title='ran'</script></body></html>")
            .File("doc.pdf", TinyPdf));
        await this.App.Cursor("dot.png");
        await this.App.Key("Ctrl+Q");
        await this.App.WaitFor(x => x.Preview.Previewer == "image", "the image previewer");

        await this.App.Cursor("page.html");
        var s = await this.App.WaitFor(x => x.Preview.Previewer == "html", "the html previewer");
        s.Preview.ViewType.ShouldBe("WebPreviewView");
        // rendered, and its script didn't run - a preview opens on a cursor move alone
        var text = await Poll(() => this.App.PreviewJs("document.body ? document.body.innerText : ''"), x => x?.Contains("Hello") == true);
        text.ShouldContain("Hello");
        (await this.App.PreviewJs("document.title")).ShouldNotBe("ran");

        await this.App.Cursor("doc.pdf");
        s = await this.App.WaitFor(x => x.Preview.Previewer == "pdf", "the pdf previewer");
        s.Preview.ViewType.ShouldBe("WebPreviewView");
        // the file itself, loaded from disk (LoadFileUrl on macOS)
        var location = await Poll(() => this.App.PreviewJs("location.href"), x => x?.EndsWith("doc.pdf") == true);
        location.ShouldEndWith("doc.pdf");
    }

    [Fact]
    public async Task Audio_and_video_go_to_the_media_previewer()
    {
        await this.Start(l => l
            .Bytes("tone.wav", Wav(2))
            .Bytes("clip.mp4", File.ReadAllBytes(SampleFile("clip.mp4")))
            .File("broken.mkv", "not a video"));
        await this.App.Cursor("tone.wav");
        await this.App.Key("Ctrl+Q");
        var s = await this.App.WaitFor(x => x.Preview.Previewer == "media", "the media previewer");
        s.Preview.ViewType.ShouldBe("MediaPreviewView");
        (await this.Ui.ById("PreviewMedia")).ShouldNotBeNull();
        // no tags - its name stands in for the title
        (await this.Ui.ByText("tone", within: "PreviewPane")).ShouldNotBeNull();
        await this.Shot("preview-audio");

        await this.App.Cursor("clip.mp4");
        s = await this.App.WaitFor(x => x.Preview.Previewer == "media" && x.Preview.Title == "clip.mp4", "the video");
        s.Preview.ViewType.ShouldBe("MediaPreviewView");
        await this.Shot("preview-video");

        // what the platform can't decode says so instead of showing a dead transport bar
        await this.App.Cursor("broken.mkv");
        await this.App.WaitFor(x => x.Preview.Title == "broken.mkv" && x.Preview.Previewer == "media", "the mkv");
        (await this.Ui.ById("PreviewMediaFailed", TimeSpan.FromSeconds(10))).ShouldNotBeNull();
    }

    [Fact]
    public async Task Word_excel_and_powerpoint_open_read_only_in_their_viewers()
    {
        await this.Start(l => l
            .Bytes("report.docx", OfficeSamples.Docx("<h1>Quarterly report</h1><p>Revenue grew <b>12%</b> on the quarter.</p><ul><li>North</li><li>South</li></ul>"))
            .Bytes("budget.xlsx", OfficeSamples.Xlsx("Budget", [["Item", "Q1", "Q2"], ["Rent", 1200, 1200], ["Travel", 340, 610]]))
            .Bytes("talk.pptx", OfficeSamples.Pptx(("Shiny Commander", "Two panels, one preview"), ("Previews", "Word, Excel and PowerPoint")))
            .File("fake.docx", "not a zip"));
        await this.App.Cursor("report.docx");
        await this.App.Key("Ctrl+Q");
        var s = await this.App.WaitFor(x => x.Preview.Previewer == "word", "the word previewer");
        s.Preview.ViewType.ShouldBe("DocumentView");
        await this.Shot("preview-word");

        await this.App.Cursor("budget.xlsx");
        s = await this.App.WaitFor(x => x.Preview.Previewer == "excel", "the excel previewer");
        s.Preview.ViewType.ShouldBe("SpreadsheetView");
        await this.Shot("preview-excel");

        await this.App.Cursor("talk.pptx");
        s = await this.App.WaitFor(x => x.Preview.Previewer == "powerpoint", "the powerpoint previewer");
        s.Preview.ViewType.ShouldBe("SlideView");
        await this.Shot("preview-powerpoint");

        // not a zip, so not Word: text
        await this.App.Cursor("fake.docx");
        (await this.App.WaitFor(x => x.Preview.Title == "fake.docx" && x.Preview.Kind == "Text", "the fake docx as text")).Preview.Previewer.ShouldBeNull();
    }

    static string SampleFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "ShinyCommander.Core.Tests")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "ShinyCommander.Core.Tests", "Fixtures", name);
    }

    static byte[] Wav(double seconds, int sampleRate = 8000)
    {
        var data = (int)(sampleRate * seconds) * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data);
        w.Write(new byte[data]);
        return ms.ToArray();
    }

    // a web view loads after the state settles - ask until it answers
    static async Task<string?> Poll(Func<Task<string?>> ask, Func<string?, bool> done)
    {
        string? last = null;
        for (var i = 0; i < 25 && !done(last); i++)
        {
            await Task.Delay(200);
            last = await ask();
        }
        return last;
    }

    [Fact]
    public async Task A_sqlite_database_shows_its_tables_and_a_plain_db_shows_as_text()
    {
        await this.Start(l => l.File("Thumbs.db", "not sqlite"));
        Sqlite(this.LeftTree["app.db"], """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO people (name) VALUES ('Ann'), ('Bob');
            CREATE VIEW names AS SELECT name FROM people;
            """);
        await this.App.Navigate(this.LeftTree.Path);

        await this.App.Cursor("app.db");
        await this.App.Key("Ctrl+Q");
        var s = await this.App.WaitFor(x => x.Preview.Previewer == "sqlite", "the sqlite previewer");
        s.Preview.ViewType.ShouldBe("SqlitePreviewView");
        await this.Ui.ById("PreviewDbObject_people", timeout: TimeSpan.FromSeconds(5));
        await this.Ui.ByText("Ann", timeout: TimeSpan.FromSeconds(5));

        await this.App.Cursor("Thumbs.db");
        s = await this.App.WaitFor(x => x.Preview.Text.Contains("not sqlite"), "a non-SQLite .db as text");
        s.Preview.Previewer.ShouldBeNull();
    }

    [Fact]
    public async Task A_previewer_turned_off_falls_back_to_text()
    {
        await this.Start(l => l.File("notes.md", "# heading"));
        await this.App.Cursor("notes.md");
        await this.App.Key("Ctrl+Q");
        await this.App.WaitFor(x => x.Preview.Previewer == "markdown", "the markdown previewer");

        await this.App.Previewer("markdown", on: false);
        var s = await this.App.WaitFor(x => x.Preview.Kind == "Text", "plain text once markdown is off");
        s.Preview.Text.ShouldContain("# heading");
    }

    [Fact]
    public async Task Folders_get_a_summary()
    {
        await this.Start(l => l.Dir("box").File("box/a.txt", "12345").File("box/b.txt", "1"));
        await this.App.Cursor("box");

        await this.App.Key("Ctrl+Q");
        var s = await this.App.WaitFor(x => x.Preview.Info.Contains("2 file(s)"), "the folder summary");
        s.Preview.Kind.ShouldBeNull();
        s.Preview.Previewer.ShouldBeNull();
    }
}
