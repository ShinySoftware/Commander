using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// Opt-in (<c>SC_README_SCREENSHOTS=1</c>, or <c>run.sh --readme-screenshots</c>): the README's screenshots, into
/// <c>docs/screenshots/</c>. The panels show this repo, so the pictures are real content and rerunning this keeps them
/// current with the UI. Settings are the test run's in-memory ones - the developer's own never show.
/// </summary>
public class ReadmeScreenshotsTests(AppFixture fixture) : UiTest(fixture)
{
    static bool Enabled => Environment.GetEnvironmentVariable("SC_README_SCREENSHOTS") is "1" or "true";
    static string OutDir => Path.Combine(TestConfig.RepoRoot, "docs", "screenshots");

    async Task Shot(string name, string expectedPage = "MainPage")
    {
        await Task.Delay(1500); // motion icons, markdown and thumbnails settle
        (await this.App.StateNow()).Page.ShouldBe(expectedPage);
        var png = await this.Fixture.Client.ScreenshotAsync();
        png.ShouldNotBeNull($"screenshot for {name} failed");
        Directory.CreateDirectory(OutDir);
        await File.WriteAllBytesAsync(Path.Combine(OutDir, name + ".png"), png!);
    }

    async Task RightClick(string text, string within)
    {
        var (x, y) = await this.Ui.Center(await this.Ui.ByText(text, within: within));
        await this.App.ClickAt(x, y, secondary: true);
    }

    // a tab's colour and icon, from its menu
    async Task StyleTab(string side, string tab, string color, string? icon = null)
    {
        await this.RightClick(tab, side + "Tabs");
        await this.Ui.ById(side + "TabMenu");
        await this.Ui.Tap("TabColor:" + color);
        if (icon != null)
            await this.Ui.Tap("TabIcon:" + icon);
        await this.Ui.Tap(side + "TabMenuBackdrop");
    }

    async Task Back()
    {
        await this.Ui.Tap("PageBack");
        await this.App.WaitFor(x => x.Page == "MainPage", "back on the main page", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Capture_readme_screenshots()
    {
        Assert.SkipUnless(Enabled, "set SC_README_SCREENSHOTS=1 (run.sh --readme-screenshots)");

        var root = TestConfig.RepoRoot;
        var views = Path.Combine(root, "src", "ShinyCommander.Shared", "Views");
        await this.Fixture.Client.ResizeAsync(1440, 900);
        await this.App.Reset(root, views);

        // tabs: the repo itself locked, blue, starred; docs and tests beside it; the Core project on the right
        var repoName = Path.GetFileName(root);
        await this.RightClick(repoName, "LeftTabs");
        await this.Ui.Click("Lock tab", within: "LeftTabMenu");
        await this.StyleTab("Left", repoName, "Blue", "star");
        foreach (var (folder, color) in new[] { ("docs", "Green"), ("tests", "Orange") })
        {
            await this.Ui.Tap("LeftNewTab"); // a new, unlocked tab on the same folder - then off to its own
            await this.App.Navigate(Path.Combine(root, folder), "left");
            await this.StyleTab("Left", folder, color);
        }
        await this.Ui.Click(repoName, within: "LeftTabs");

        await this.Ui.Tap("RightNewTab");
        await this.App.Navigate(Path.Combine(root, "src", "ShinyCommander.Core"), "right");
        await this.StyleTab("Right", "ShinyCommander.Core", "Purple", "database");
        await this.Ui.Click("Views", within: "RightTabs");

        // the main window: a few marked files on the right. No folder tree - it would show whatever else sits beside
        // the repo on this machine
        await this.App.Cursor("MainPage.xaml", "right");
        await this.App.Mark("FilePanelView.xaml", "FilePanelView.xaml.cs", "MainPage.xaml", "MainPage.xaml.cs");
        await this.App.Cursor("README.md", "left");
        await this.Shot("main");

        // a tab's menu: lock, name, colour, icon
        await this.RightClick(repoName, "LeftTabs");
        await this.Ui.ById("LeftTabMenu");
        await this.Shot("tab-menu");
        await this.Ui.Tap("LeftTabMenuBackdrop");

        // the breadcrumbs: a crumb drops down the folders inside it
        await this.Ui.Tap(await this.Ui.Tappable(await this.Ui.ById("CrumbMenu:" + Path.Combine(root, "src"))));
        await this.Ui.ById("RightCrumbMenu");
        await this.Shot("breadcrumbs");
        await this.App.Navigate(Path.Combine(root, "src"), "right"); // a folder change closes the dropdown
        await this.App.Navigate(views, "right");

        // an archive, browsed and changed like a folder: the docs folder packed to 7z
        // into the UI tests' (ignored) artifacts folder, so the path reads as part of the repo
        var packed = Path.Combine(root, "tests", "ShinyCommander.UITests", "artifacts", "docs.7z");
        if (File.Exists(packed))
            File.Delete(packed);
        await this.App.Cursor("docs", "left");
        await this.App.Answer(packed);
        await this.App.Key("Alt+F5");
        for (var i = 0; i < 100 && !File.Exists(packed); i++)
            await Task.Delay(100);
        await this.App.WaitFor(x => x.Jobs.Count > 0 && x.Jobs.All(j => j.State != "Running"), "the pack to finish", TimeSpan.FromSeconds(30));
        await this.App.Navigate(Path.Combine(packed, "docs"), "right");
        await this.App.Cursor("screenshots", "right");
        await this.Shot("archive");
        await this.App.Navigate(views, "right");
        await this.App.Cursor("README.md", "left");

        // quick view: the other panel renders the file under the cursor
        await this.App.Command("QuickView");
        await this.Shot("quick-view");
        await this.App.Command("QuickView");

        await this.App.Command("View");
        await this.Shot("viewer", "ViewerPage");
        await this.Back();

        await this.App.Cursor("MainPage.xaml", "right");
        await this.App.Command("MultiRename");
        await this.Ui.Fill(await this.Ui.ById("RenameMask"), "[N]-old");
        await this.Shot("multi-rename", "MultiRenamePage");
        await this.Back();

        await this.App.Command("Find");
        await this.Shot("find", "SearchPage");
        await this.Back();

        await this.App.Command("Settings");
        await this.Shot("settings", "SettingsPage");
        await this.Back();

        await this.App.Command("CustomizeToolbar");
        await this.Shot("customize-toolbar", "CustomizeToolbarPage");
        await this.Back();

        // thumbnails of the screenshots just taken
        await this.App.Navigate(OutDir, "right");
        await this.App.Cursor("main.png", "right");
        await this.App.Command("ThumbnailView");
        await this.Shot("thumbnails");
        await this.App.Command("DetailsView");

        await this.App.Navigate(views, "right");
        await this.App.Cursor("README.md", "left");
        await this.Fixture.Client.SetThemeAsync(Microsoft.Maui.DevFlow.Driver.DevFlowTheme.Dark);
        try
        {
            await this.Shot("dark");
        }
        finally
        {
            await this.Fixture.Client.SetThemeAsync(Microsoft.Maui.DevFlow.Driver.DevFlowTheme.Light);
        }
    }

    /// <summary>
    /// The remote file system pictures: an S3 bucket in a panel, Connections and the connection editor. Needs an
    /// S3-compatible server holding a "website" bucket - <c>SC_README_S3=http://127.0.0.1:5001</c> (moto, see
    /// docs/testing.md) - and shows whatever Bonjour finds in the network list.
    /// </summary>
    [Fact]
    public async Task Capture_remote_screenshots()
    {
        var s3 = Environment.GetEnvironmentVariable("SC_README_S3");
        Assert.SkipUnless(Enabled && s3 != null, "set SC_README_SCREENSHOTS=1 and SC_README_S3=<endpoint>");

        var root = TestConfig.RepoRoot;
        await this.Fixture.Client.ResizeAsync(1440, 900);
        await this.App.Reset(Path.Combine(root, "website"), Path.Combine(root, "src", "ShinyCommander.Shared", "Views"));

        // only the S3 one is opened - the rest are saved, as they'd be on any machine
        var saved = new object[]
        {
            new { protocol = "S3", name = "Cloud Storage", user = "AKIAIOSFODNN7EXAMPLE", secret = "example", endpoint = s3, region = "us-east-1" },
            new { protocol = "Sftp", name = "Production", host = "deploy.example.com", user = "deploy", keyFile = "~/.ssh/id_ed25519", start = "/var/www" },
            new { protocol = "WebDavs", name = "Team Nextcloud", host = "cloud.example.com", user = "allan", start = "/remote.php/dav/files/allan" },
            new { protocol = "AzureBlob", name = "Offsite Backups", host = "contosobackups" },
            new { protocol = "Ftps", name = "Print Shop", host = "ftp.example.com", user = "orders" }
        };
        foreach (var connection in saved)
            await this.App.Connection(connection);
        try
        {
            // the site on the left, its bucket on the right
            await this.App.Navigate("s3://cloud-storage/website", "right");
            await this.App.Cursor("index.html", "right");
            await this.App.Mark("styles.css", "site.js");
            await this.App.Cursor("index.html", "left");
            await this.Shot("remote");

            await this.App.Command("Connections");
            await Task.Delay(4500); // the Bonjour scan
            await this.Shot("connections", "ConnectionsPage");

            await this.Ui.Tap("NewConnection");
            await this.App.WaitFor(x => x.Page == "ConnectionEditPage", "the connection editor", TimeSpan.FromSeconds(10));
            await this.Ui.Fill(await this.Ui.ById("ConnName"), "Staging");
            await this.Ui.Fill(await this.Ui.ById("ConnHost"), "staging.example.com");
            await this.Ui.Fill(await this.Ui.ById("ConnUser"), "deploy");
            await this.Ui.Fill(await this.Ui.ById("ConnKeyFile"), "~/.ssh/id_ed25519");
            await this.Ui.Fill(await this.Ui.ById("ConnStartPath"), "/var/www/site");
            await this.Shot("connection-edit", "ConnectionEditPage");
            await this.Ui.Tap("PageBack");
            await this.App.WaitFor(x => x.Page == "ConnectionsPage", "back on Connections", TimeSpan.FromSeconds(10));
            await this.Back();
        }
        finally
        {
            foreach (var name in new[] { "Cloud Storage", "Production", "Team Nextcloud", "Offsite Backups", "Print Shop" })
                await this.App.RemoveConnection(name);
        }
    }
}
