using Microsoft.Maui.DevFlow.Driver;
using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// Things that are "there" in the state but not on screen: zero-size or scrolled-away content, squeezed columns.
/// Each looks at real element geometry from the visual tree.
/// </summary>
public class LayoutTests(AppFixture fixture) : UiTest(fixture)
{
    static void ShouldBeOnScreen(ElementInfo e, string what)
    {
        e.Bounds.ShouldNotBeNull($"{what} has no bounds");
        e.Bounds!.Width.ShouldBeGreaterThan(4, $"{what} has no width");
        e.Bounds.Height.ShouldBeGreaterThan(4, $"{what} has no height");
    }

    static bool Overlaps(BoundsInfo a, BoundsInfo b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    [Fact]
    public async Task Console_output_is_visible_in_the_console_pane()
    {
        await this.Start(l => l.File("hello.txt", "console-marker-text"));
        await this.App.Type("cat hello.txt");
        await this.App.Key("Enter");
        await this.App.WaitFor(x => x.CommandOutput.Contains("console-marker-text"), "the shell output");
        await Task.Delay(500);

        var all = await this.Ui.All();
        var label = all.FirstOrDefault(x => x.Text != null && x.Text.Contains("console-marker-text") && x.IsVisible);
        label.ShouldNotBeNull("no visible element shows the console output");
        await this.Ui.ShouldBeVisible(label!, "the console text");
    }

    [Fact]
    public async Task Choice_dialogs_show_their_options()
    {
        await this.Start(l => l.File("a.txt", "new"), r => r.File("a.txt", "old"));
        await this.App.Cursor("a.txt");
        await this.App.RealDialogs();

        await this.App.Key("F5");
        var prompt = await this.Ui.ByText("Copy", within: "type:DialogView", type: "Button");
        await this.Ui.ClickInDialog("Copy");

        foreach (var option in new[] { "Overwrite", "Skip", "Keep both" })
        {
            var b = await this.Ui.ByText(option, within: "type:DialogView");
            await this.Ui.ShouldBeVisible(b, $"the '{option}' option");
        }
        await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);
        _ = prompt;
    }

    [Fact]
    public async Task The_hotlist_shows_its_add_option()
    {
        await this.Start();
        await this.App.RealDialogs();

        await this.App.Key("Primary+D");
        try
        {
            var all = await this.Ui.All();
            var dialogTexts = all.Where(x => x.IsVisible && x.Text != null).Select(x => $"{x.Type}:{x.Text}").ToList();
            var add = all.FirstOrDefault(x => x.IsVisible && x.Text != null && x.Text.Contains("Add current folder"));
            add.ShouldNotBeNull("no 'Add current folder' option; visible texts: " + String.Join(" | ", dialogTexts.TakeLast(15)));
            await this.Ui.ShouldBeVisible(add!, "the add option");
        }
        finally
        {
            await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);
        }
    }

    [Fact]
    public async Task With_its_tree_open_a_panel_still_shows_readable_names()
    {
        await this.Start(l => l.File("quarterly-report.txt").File("b.txt"));
        await this.App.Key("Primary+F8");
        await Task.Delay(300);

        var name = await this.Ui.ByText("quarterly-report", within: "LeftPanel");
        await this.Ui.ShouldBeVisible(name, "the file name");
        name.Bounds!.Width.ShouldBeGreaterThan(110, "the Name column is squeezed when the tree is open");

        var header = (await this.Ui.All()).First(x => x.Text is { } t && t.StartsWith("Name") && x.Type == "Label" && x.IsVisible);
        header.Bounds!.Height.ShouldBeLessThan(22, "the Name header wraps onto two lines");
    }

    [Fact]
    public async Task The_path_box_shows_the_end_of_a_long_path()
    {
        await this.Start(l => l.Dir("a-rather-long-folder-name/another-long-folder-name/the-folder-you-are-in"));
        var deep = this.LeftTree["a-rather-long-folder-name/another-long-folder-name/the-folder-you-are-in"];
        await this.App.Navigate(deep);

        // the folder you are in must be what's readable: a head-truncated label while not editing
        var label = await this.Ui.ById("LeftPathLabel");
        label.Text.ShouldBe(deep);
        await this.Ui.ShouldBeVisible(label, "the path label");
    }
}
