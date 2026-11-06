using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>Cursor movement, entering folders, history, panel switching and the command line's claim on keys.</summary>
public class NavigationTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task Starts_with_both_panels_on_their_folders_and_the_left_one_active()
    {
        var s = await this.Start(l => l.File("a.txt").Dir("sub"), r => r.File("b.txt"));

        s.Page.ShouldBe("MainPage");
        s.ActiveSide.ShouldBe("left");
        s.Left.Path.ShouldBe(this.LeftTree.Path);
        s.Right.Path.ShouldBe(this.RightTree.Path);
        s.Left.Names.ShouldBe(["sub", "a.txt"]); // folders first
        s.Right.Names.ShouldBe(["b.txt"]);
        s.Left.CursorName.ShouldBe("..");
    }

    [Fact]
    public async Task Down_and_Up_move_the_cursor()
    {
        await this.Start(l => l.File("a.txt").File("b.txt").File("c.txt"));

        (await this.App.Key("Down")).Left.CursorName.ShouldBe("a.txt");
        (await this.App.Keys("Down", "Down")).Left.CursorName.ShouldBe("c.txt");
        (await this.App.Key("Down")).Left.CursorName.ShouldBe("c.txt"); // clamps at the end
        (await this.App.Key("Up")).Left.CursorName.ShouldBe("b.txt");
    }

    [Fact]
    public async Task Home_and_End_jump_when_the_command_line_is_empty()
    {
        await this.Start(l => l.File("a.txt").File("b.txt").File("c.txt"));

        (await this.App.Key("End")).Left.CursorName.ShouldBe("c.txt");
        (await this.App.Key("Home")).Left.CursorName.ShouldBe("..");
    }

    [Fact]
    public async Task Home_and_End_belong_to_the_command_line_while_it_has_text()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));
        await this.App.Type("echo hi");

        var s = await this.App.Key("End");
        this.App.LastKeyHandled.ShouldBeFalse("End should be handed back to the command line");
        s.Left.CursorName.ShouldBe("..");
    }

    [Fact]
    public async Task Enter_opens_a_folder_and_Backspace_returns_with_the_cursor_on_it()
    {
        await this.Start(l => l.Dir("alpha").Dir("beta").File("beta/inner.txt"));
        await this.App.Cursor("beta");

        var inside = await this.App.Key("Enter");
        inside.Left.Path.ShouldBe(this.LeftTree["beta"]);
        inside.Left.Names.ShouldBe(["inner.txt"]);
        inside.Left.CursorName.ShouldBe("..");

        var back = await this.App.Key("Backspace");
        back.Left.Path.ShouldBe(this.LeftTree.Path);
        back.Left.CursorName.ShouldBe("beta");
    }

    [Fact]
    public async Task Enter_on_the_parent_row_goes_up()
    {
        await this.Start(l => l.Dir("sub").File("sub/x.txt"));
        await this.App.Navigate(this.LeftTree["sub"]);

        var s = await this.App.Key("Enter"); // cursor starts on ".."
        s.Left.Path.ShouldBe(this.LeftTree.Path);
        s.Left.CursorName.ShouldBe("sub");
    }

    [Fact]
    public async Task Backspace_edits_the_command_line_instead_of_navigating_while_it_has_text()
    {
        await this.Start(l => l.Dir("sub"));
        await this.App.Navigate(this.LeftTree["sub"]);
        await this.App.Type("ls");

        var s = await this.App.Key("Backspace");
        this.App.LastKeyHandled.ShouldBeFalse();
        s.Left.Path.ShouldBe(this.LeftTree["sub"]);
    }

    [Fact]
    public async Task Tab_switches_the_active_panel()
    {
        await this.Start(l => l.File("a.txt"), r => r.File("b.txt"));

        var s = await this.App.Key("Tab");
        s.ActiveSide.ShouldBe("right");
        s.Right.IsActive.ShouldBeTrue();
        s.Left.IsActive.ShouldBeFalse();

        (await this.App.Key("Tab")).ActiveSide.ShouldBe("left");
    }

    [Fact]
    public async Task Keys_act_on_the_active_panel_only()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"), r => r.File("c.txt").File("d.txt"));
        await this.App.Key("Tab");

        var s = await this.App.Keys("Down", "Down");
        s.Right.CursorName.ShouldBe("d.txt");
        s.Left.CursorName.ShouldBe("..");
    }

    [Fact]
    public async Task Alt_Left_and_Alt_Right_walk_the_history()
    {
        await this.Start(l => l.Dir("one").Dir("two"));
        await this.App.Navigate(this.LeftTree["one"]);
        await this.App.Navigate(this.LeftTree["two"]);

        (await this.App.Key("Alt+Left")).Left.Path.ShouldBe(this.LeftTree["one"]);
        (await this.App.Key("Alt+Left")).Left.Path.ShouldBe(this.LeftTree.Path);
        (await this.App.Key("Alt+Right")).Left.Path.ShouldBe(this.LeftTree["one"]);
    }

    [Fact]
    public async Task Primary_Right_opens_the_folder_under_the_cursor_in_the_other_panel()
    {
        await this.Start(l => l.Dir("docs").File("docs/readme.md"));
        await this.App.Cursor("docs");

        var s = await this.App.Key("Primary+Right");
        s.Right.Path.ShouldBe(this.LeftTree["docs"]);
        s.Left.Path.ShouldBe(this.LeftTree.Path);
    }

    [Fact]
    public async Task Cd_on_the_command_line_navigates_the_active_panel()
    {
        await this.Start(l => l.Dir("target").File("target/t.txt"));
        await this.App.Type("cd target");

        var s = await this.App.Key("Enter");
        s.Left.Path.ShouldBe(this.LeftTree["target"]);
        s.CommandText.ShouldBe("");
    }

    [Fact]
    public async Task Typed_command_runs_in_the_shell_with_output_in_the_console()
    {
        await this.Start(l => l.File("hello.txt", "hi there"));
        await this.App.Type("cat hello.txt");

        await this.App.Key("Enter");
        var s = await this.App.WaitFor(x => x.CommandOutput.Contains("hi there"), "the shell output");
        s.ShowOutput.ShouldBeTrue();

        (await this.App.Key("Escape")).ShowOutput.ShouldBeFalse();
    }

    [Fact]
    public async Task Primary_Backslash_goes_to_the_root()
    {
        await this.Start();
        (await this.App.Key("Primary+Backslash")).Left.Path.ShouldBe("/");
    }

    [Fact]
    public async Task Swap_panels_exchanges_the_folders()
    {
        await this.Start(l => l.File("a.txt"), r => r.File("b.txt"));

        var s = await this.App.Key("Primary+U");
        s.Left.Path.ShouldBe(this.RightTree.Path);
        s.Right.Path.ShouldBe(this.LeftTree.Path);
    }

    [Fact]
    public async Task Target_equals_source_points_the_other_panel_here()
    {
        await this.Start(l => l.File("a.txt"));
        (await this.App.Key("Primary+Period")).Right.Path.ShouldBe(this.LeftTree.Path);
    }

    [Fact]
    public async Task A_folder_created_outside_the_app_shows_up_by_itself()
    {
        await this.Start(l => l.File("a.txt"));
        this.LeftTree.File("appeared.txt");

        await this.App.WaitFor(x => x.Left.Names.Contains("appeared.txt"), "the watcher to refresh the panel", TimeSpan.FromSeconds(5));
    }
}
