using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// Finder / Explorer selection with the mouse (click, Shift+click, ⌘+click - real AppKit clicks with the keys held),
/// and drag and drop: what a drag carries and where a drop goes, through the app's real hit testing. AppKit's own
/// drag session isn't driven - it runs its own mouse loop.
/// </summary>
public class SelectionAndDragDropTests(AppFixture fixture) : UiTest(fixture)
{
    async Task<(double X, double Y)> Row(string name, string panel = "LeftPanel")
    {
        var r = (await this.Ui.ByText(name, within: panel)).WindowBounds!;
        return (r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    async Task Click(string name, string? modifiers = null, string panel = "LeftPanel")
    {
        var (x, y) = await this.Row(name, panel);
        await this.App.ClickAt(x, y, modifiers: modifiers);
        // a row's tap fires once AppKit has given up waiting for a second click (the row has a double-tap too)
        await Task.Delay(800);
    }

    [Fact]
    public async Task Shift_click_selects_the_range_from_the_last_click()
    {
        await this.Start(l => l.File("kiwi.txt").File("lemon.txt").File("mango.txt").File("plum.txt"));

        await this.Click("lemon");
        await this.Click("plum", "shift");
        var s = await this.App.State();
        s.Left.Marked.ShouldBe(["lemon.txt", "mango.txt", "plum.txt"]);
        s.Left.CursorName.ShouldBe("plum.txt");

        // the range shrinks back toward the anchor rather than growing from the new cursor
        await this.Click("mango", "shift");
        (await this.App.State()).Left.Marked.ShouldBe(["lemon.txt", "mango.txt"]);
    }

    [Fact]
    public async Task Cmd_click_adds_and_removes_single_rows()
    {
        await this.Start(l => l.File("kiwi.txt").File("lemon.txt").File("mango.txt").File("plum.txt"));

        await this.Click("kiwi");
        await this.Click("mango", "cmd");
        (await this.App.State()).Left.Marked.ShouldBe(["kiwi.txt", "mango.txt"]);

        await this.Click("kiwi", "cmd");
        (await this.App.State()).Left.Marked.ShouldBe(["mango.txt"]);
    }

    [Fact]
    public async Task A_plain_click_selects_only_that_row()
    {
        await this.Start(l => l.File("kiwi.txt").File("lemon.txt").File("mango.txt"));

        await this.Click("kiwi");
        await this.Click("mango", "shift");
        await this.Click("lemon");
        var s = await this.App.State();
        s.Left.Marked.ShouldBeEmpty();
        s.Left.CursorName.ShouldBe("lemon.txt");
    }

    [Fact]
    public async Task Dragging_a_selected_row_carries_the_selection()
    {
        await this.Start(l => l.File("kiwi.txt").File("lemon.txt").File("mango.txt"));
        await this.Click("kiwi");
        await this.Click("mango", "cmd");

        var (x, y) = await this.Row("mango");
        var paths = await this.App.DragFrom(x, y);
        paths.ShouldBe([this.LeftTree["kiwi.txt"], this.LeftTree["mango.txt"]], ignoreOrder: true);
    }

    [Fact]
    public async Task Dragging_an_unselected_row_carries_it_alone()
    {
        await this.Start(l => l.File("kiwi.txt").File("lemon.txt").File("mango.txt"));
        await this.Click("kiwi");
        await this.Click("lemon", "cmd");

        var (x, y) = await this.Row("mango");
        (await this.App.DragFrom(x, y)).ShouldBe([this.LeftTree["mango.txt"]]);
        (await this.App.State()).Left.Marked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dropping_on_the_other_panel_moves_into_its_folder()
    {
        await this.Start(l => l.File("kiwi.txt").File("lemon.txt"), r => r.File("faraway.txt"));

        var (x, y) = await this.Row("faraway", "RightPanel");
        // a file row isn't a folder - the drop goes into the panel's folder
        var op = await this.App.DropAt(x, y, [this.LeftTree["kiwi.txt"]]);
        op.ShouldBe("move");
        await this.App.WaitFor(s => s.Right.Names.Contains("kiwi.txt") && !s.Left.Names.Contains("kiwi.txt"), "the move to finish");
        File.Exists(this.RightTree["kiwi.txt"]).ShouldBeTrue();
        File.Exists(this.LeftTree["kiwi.txt"]).ShouldBeFalse();
    }

    [Fact]
    public async Task Dropping_on_a_folder_row_goes_into_that_folder()
    {
        await this.Start(l => l.File("kiwi.txt"), r => r.Dir("inboxdir"));

        var (x, y) = await this.Row("inboxdir", "RightPanel");
        (await this.App.DropAt(x, y, [this.LeftTree["kiwi.txt"]], allowed: "copy")).ShouldBe("copy");
        await this.App.WaitFor(_ => File.Exists(this.RightTree["inboxdir/kiwi.txt"]), "the copy into the folder");
        File.Exists(this.LeftTree["kiwi.txt"]).ShouldBeTrue();
    }

    [Fact]
    public async Task Dropping_back_where_the_files_are_does_nothing()
    {
        await this.Start(l => l.File("kiwi.txt").File("lemon.txt"));

        var (x, y) = await this.Row("lemon");
        (await this.App.DropAt(x, y, [this.LeftTree["kiwi.txt"]])).ShouldBe("none");
    }
}
