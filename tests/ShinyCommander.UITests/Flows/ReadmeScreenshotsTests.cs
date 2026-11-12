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

        // the main window: a few marked files on the right. No folder tree - it would show whatever else sits beside
        // the repo on this machine
        await this.App.Cursor("MainPage.xaml", "right");
        await this.App.Mark("FilePanelView.xaml", "FilePanelView.xaml.cs", "MainPage.xaml", "MainPage.xaml.cs");
        await this.App.Cursor("README.md", "left");
        await this.Shot("main");

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
}
