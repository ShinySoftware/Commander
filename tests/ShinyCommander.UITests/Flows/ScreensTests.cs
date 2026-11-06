using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// Opt-in (<c>SC_UITEST_SCREENS=1</c>, or <c>run.sh --screens</c>): a screenshot of every screen and mode into
/// <c>artifacts/screens/</c> for a visual review - layout bugs (clipping, overlap, blank areas) don't show up in
/// state. Each capture first asserts the screen is the one expected.
/// </summary>
public class ScreensTests(AppFixture fixture) : UiTest(fixture)
{
    static bool Enabled => Environment.GetEnvironmentVariable("SC_UITEST_SCREENS") is "1" or "true";

    async Task Shot(string name)
    {
        await Task.Delay(600); // let motion icons and animations settle
        var current = (await this.App.StateNow()).Page;
        var page = (await this.Ui.All()).LastOrDefault(x => x.Type == current && x.Bounds is { Width: > 0 });
        page.ShouldNotBeNull("no page in the visual tree");
        var png = await this.Fixture.Client.ScreenshotAsync(elementId: page!.Id);
        png.ShouldNotBeNull($"screenshot of {page.Type} failed");
        var dir = Path.Combine(TestConfig.ArtifactDir, "screens");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, name + ".png"), png!);
    }

    void Populate(TestTree t)
    {
        t.Dir("Documents").Dir("Pictures").Dir("src").Dir("a folder with a really long name that should truncate nicely");
        t.File("readme.md", "# ShinyCommander\n\nA *dual pane* file manager.\n\n- one\n- two\n");
        t.File("notes.txt", String.Join('\n', Enumerable.Range(1, 60).Select(i => $"line {i}: the quick brown fox jumps over the lazy dog")));
        t.File("Program.cs", "Console.WriteLine(\"hi\");");
        t.Bytes("pixel.png", TestTree.Png1x1);
        t.Zip("bundle.zip", ("a.txt", "A"), ("docs/b.txt", "B"));
        t.TarGz("site.tar.gz", ("index.html", "<h1/>"));
        t.File("a very long file name that will not fit in the name column at all.txt");
        for (var i = 1; i <= 25; i++)
            t.File($"file{i:00}.log", new string('x', i * 100));
    }

    [Fact]
    public async Task Capture_every_screen()
    {
        Assert.SkipUnless(Enabled, "set SC_UITEST_SCREENS=1 (run.sh --screens)");

        await this.Start(this.Populate, this.Populate);
        await this.App.Cursor("notes.txt");
        await this.App.Mark("file03.log", "file04.log");
        await this.Shot("01-main-details");

        foreach (var tab in new[] { "select", "view", "go", "tools", "files" })
        {
            await this.Ui.Tap($"ribbon-tab-{tab}");
            await this.Shot($"02-ribbon-{tab}");
        }

        await this.App.Key("Primary+Shift+F1");
        await this.Shot("03-thumbnails");
        await this.App.Key("Primary+F2");

        await this.App.Key("Ctrl+Q");
        await this.Shot("04-quickview-text");
        await this.App.Cursor("readme.md");
        await this.Shot("05-quickview-markdown");
        await this.App.Cursor("Documents");
        await this.Shot("06-quickview-folder");
        await this.App.Key("Ctrl+Q");

        await this.App.Key("Primary+F8");
        await this.App.Key("Tab");
        await this.App.Key("Primary+F8");
        await this.Shot("07-trees");
        await this.App.Key("Primary+Shift+L");
        await this.Shot("08-stacked-with-trees");
        await this.App.Key("Primary+Shift+L");
        await this.App.Key("Primary+F8");
        await this.App.Key("Tab");
        await this.App.Key("Primary+F8");

        await this.App.Answer("Date created", "Done");
        await this.App.Key("Primary+Shift+K");
        await this.Shot("09-columns-created");

        await this.App.Navigate(this.LeftTree["site.tar.gz"]);
        await this.Shot("10-archive-readonly");
        await this.App.Navigate(this.LeftTree.Path);

        await this.App.Key("Primary+F");
        await this.Shot("11-quick-filter");
        await this.App.Key("Escape");

        await this.App.Type("ls -la");
        await this.App.Key("Enter");
        await this.App.WaitFor(x => x.CommandOutput.Length > 20, "shell output");
        await this.Shot("12-console");
        await this.App.Key("Escape");

        await this.App.RealDialogs();
        await this.App.Cursor("notes.txt");
        await this.App.Key("F2");
        await this.Shot("13-dialog-prompt");
        await this.Ui.ClickInDialog("Cancel");
        await this.App.Key("F8");
        await this.Shot("14-dialog-confirm");
        await this.Ui.ClickInDialog("Cancel");
        await this.App.Key("Primary+D");
        await this.Shot("15-dialog-choose");
        await this.Ui.ClickInDialog("Cancel");
        await this.App.RealDialogs(false);

        await this.App.Cursor("notes.txt");
        await this.App.Key("F3");
        await this.Shot("16-lister-text");
        await this.App.Key("Escape");
        await this.App.Cursor("readme.md");
        await this.App.Key("F3");
        await this.Shot("17-lister-markdown");
        await this.App.Key("Escape");

        await this.App.Cursor("notes.txt");
        await this.App.Key("F4");
        await this.Shot("18-editor");
        await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);

        await this.App.Key("F1");
        await this.Shot("19-help");
        await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);

        await this.App.Key("Alt+F7");
        await this.Shot("20-find");
        await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);

        await this.App.Mark("file01.log", "file02.log", "file03.log");
        await this.App.Key("Primary+M");
        await this.Shot("21-multi-rename");
        await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);

        await this.App.Key("Primary+Shift+S");
        await this.Shot("22-sync-dirs");
        await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);

        await this.App.Key("Primary+Comma");
        await this.Shot("23-settings");
        await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);
    }
}
