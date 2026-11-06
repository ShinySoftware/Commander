using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>TC marking: Insert, Space, Shift+arrows, select all / invert / by mask, and the status line that counts them.</summary>
public class SelectionTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task Insert_marks_and_moves_down()
    {
        await this.Start(l => l.File("a.txt").File("b.txt").File("c.txt"));
        await this.App.Cursor("a.txt");

        var s = await this.App.Keys("Insert", "Insert");
        s.Left.Marked.ShouldBe(["a.txt", "b.txt"]);
        s.Left.CursorName.ShouldBe("c.txt");
        s.Left.MarkedCount.ShouldBe(2);
    }

    [Fact]
    public async Task Space_toggles_the_mark_on_the_cursor_item()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));
        await this.App.Cursor("b.txt");

        (await this.App.Key("Space")).Left.Marked.ShouldBe(["b.txt"]);
        (await this.App.Key("Space")).Left.Marked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Space_types_a_space_while_the_command_line_has_text()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Type("echo");

        var s = await this.App.Key("Space");
        this.App.LastKeyHandled.ShouldBeFalse();
        s.Left.Marked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Space_on_a_folder_calculates_its_size()
    {
        await this.Start(l => l.Dir("big").File("big/one.bin", new string('x', 1000)).File("big/two.bin", new string('y', 500)));
        await this.App.Cursor("big");

        var s = await this.App.Key("Space");
        await this.App.WaitFor(x => x.Left.Item("big").SizeText.Replace(",", "") == "1500", "the folder size");
        s.Left.Marked.ShouldBe(["big"]);
    }

    [Fact]
    public async Task Shift_Down_marks_as_it_moves()
    {
        await this.Start(l => l.File("a.txt").File("b.txt").File("c.txt"));
        await this.App.Cursor("a.txt");

        var s = await this.App.Keys("Shift+Down", "Shift+Down");
        s.Left.Marked.ShouldBe(["a.txt", "b.txt"]);
    }

    [Fact]
    public async Task The_parent_row_is_never_marked()
    {
        await this.Start(l => l.File("a.txt"));

        var s = await this.App.Key("Insert"); // cursor on ".."
        s.Left.Marked.ShouldBeEmpty();
        s.Left.CursorName.ShouldBe("a.txt");
    }

    [Fact]
    public async Task Select_all_unselect_all_and_invert()
    {
        await this.Start(l => l.File("a.txt").File("b.md").Dir("folder"));

        (await this.App.Key("Primary+A")).Left.Marked.Count.ShouldBe(3);
        (await this.App.Key("Primary+Shift+A")).Left.Marked.ShouldBeEmpty();

        await this.App.Cursor("a.txt");
        await this.App.Key("Space");
        (await this.App.Key("Primary+I")).Left.Marked.ShouldBe(["b.md"]); // TC inverts files only
    }

    [Fact]
    public async Task Select_group_marks_by_mask()
    {
        await this.Start(l => l.File("a.txt").File("b.txt").File("c.md"));
        await this.App.Answer("*.txt");

        var s = await this.App.Key("Primary+G");
        s.Left.Marked.ShouldBe(["a.txt", "b.txt"]);
        s.Dialogs.ShouldContain(d => d.Kind == "prompt" && d.Title == "Select files");
    }

    [Fact]
    public async Task Unselect_group_unmarks_by_mask()
    {
        await this.Start(l => l.File("a.txt").File("b.txt").File("c.md"));
        await this.App.Key("Primary+A");
        await this.App.Answer("*.txt");

        (await this.App.Key("Primary+Shift+G")).Left.Marked.ShouldBe(["c.md"]);
    }

    [Fact]
    public async Task Same_extension_marks_every_file_like_the_cursor_one()
    {
        await this.Start(l => l.File("a.cs").File("b.cs").File("c.md"));
        await this.App.Cursor("b.cs");

        (await this.App.Key("Alt+Shift+E")).Left.Marked.ShouldBe(["a.cs", "b.cs"]);
    }

    [Fact]
    public async Task The_status_line_counts_marked_files_and_bytes()
    {
        await this.Start(l => l.File("a.txt", "12345").File("b.txt", "123").Dir("d"));
        await this.App.Cursor("a.txt");

        var s = await this.App.Key("Space");
        s.Left.Status.ShouldNotBeNull();
        s.Left.Status!.ShouldContain("1 / 2 file(s)");
        s.Left.Status.ShouldContain("0 / 1 dir(s)");
        s.Left.Status.ShouldStartWith("5 / 8");
    }
}
