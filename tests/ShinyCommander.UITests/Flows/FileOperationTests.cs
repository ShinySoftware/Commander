using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>F5 copy, F6 move, F7 folder, F8 delete, F2 rename, Shift+F4 new file - with their prompts and conflict choices.</summary>
public class FileOperationTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task F5_copies_the_cursor_file_to_the_other_panel()
    {
        await this.Start(l => l.File("a.txt", "hello"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<default>");

        var s = await this.App.Key("F5");

        this.RightTree.Read("a.txt").ShouldBe("hello");
        this.LeftTree.Exists("a.txt").ShouldBeTrue();
        s.Right.Names.ShouldContain("a.txt");
        s.Dialogs.First(d => d.Kind == "prompt").Initial.ShouldBe(this.RightTree.Path + "/");
    }

    [Fact]
    public async Task F5_copies_every_marked_item_and_clears_the_marks()
    {
        await this.Start(l => l.File("a.txt").File("b.txt").Dir("sub").File("sub/c.txt").File("skip.txt"));
        await this.App.Mark("a.txt", "b.txt", "sub");
        await this.App.Answer("<default>");

        var s = await this.App.Key("F5");

        this.RightTree.Names().ShouldBe(["a.txt", "b.txt", "sub"]);
        this.RightTree.Exists("sub/c.txt").ShouldBeTrue();
        s.Left.Marked.ShouldBeEmpty();
    }

    [Fact]
    public async Task F5_with_nothing_selected_does_nothing()
    {
        await this.Start(l => l.File("a.txt"));

        var s = await this.App.Key("F5"); // cursor on ".."
        s.Dialogs.ShouldBeEmpty();
        this.RightTree.Names().ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_the_copy_prompt_copies_nothing()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<cancel>");

        await this.App.Key("F5");
        this.RightTree.Names().ShouldBeEmpty();
    }

    [Fact]
    public async Task Copy_onto_an_existing_file_asks_and_skip_keeps_it()
    {
        await this.Start(l => l.File("a.txt", "new"), r => r.File("a.txt", "old"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<default>", "Skip");

        var s = await this.App.Key("F5");

        this.RightTree.Read("a.txt").ShouldBe("old");
        s.Dialogs.ShouldContain(d => d.Kind == "choose" && d.Title.Contains("already exists"));
    }

    [Fact]
    public async Task Copy_onto_an_existing_file_overwrite_replaces_it()
    {
        await this.Start(l => l.File("a.txt", "new"), r => r.File("a.txt", "old"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<default>", "Overwrite");

        await this.App.Key("F5");
        this.RightTree.Read("a.txt").ShouldBe("new");
    }

    [Fact]
    public async Task Copy_onto_an_existing_file_keep_both_renames_the_copy()
    {
        await this.Start(l => l.File("a.txt", "new"), r => r.File("a.txt", "old"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<default>", "Keep both");

        await this.App.Key("F5");
        this.RightTree.Read("a.txt").ShouldBe("old");
        this.RightTree.Read("a (2).txt").ShouldBe("new");
    }

    [Fact]
    public async Task Copy_to_a_new_name_in_the_same_folder()
    {
        await this.Start(l => l.File("a.txt", "A"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("copy-of-a.txt");

        var s = await this.App.Key("F5");
        this.LeftTree.Read("copy-of-a.txt").ShouldBe("A");
        s.Left.Names.ShouldContain("copy-of-a.txt");
    }

    [Fact]
    public async Task F6_moves_to_the_other_panel()
    {
        await this.Start(l => l.File("a.txt", "A").File("b.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<default>");

        var s = await this.App.Key("F6");

        this.LeftTree.Exists("a.txt").ShouldBeFalse();
        this.RightTree.Read("a.txt").ShouldBe("A");
        s.Left.Names.ShouldBe(["b.txt"]);
        s.Right.Names.ShouldBe(["a.txt"]);
    }

    [Fact]
    public async Task F6_with_both_panels_on_the_same_folder_renames()
    {
        await this.Start(l => l.File("a.txt", "A"));
        await this.App.Navigate(this.LeftTree.Path, "right");
        await this.App.Cursor("a.txt", "left");
        await this.App.Answer("renamed.txt");

        var s = await this.App.Key("F6");
        s.Dialogs[0].Initial.ShouldBe("a.txt");
        this.LeftTree.Names().ShouldBe(["renamed.txt"]);
    }

    [Fact]
    public async Task F7_creates_a_folder_and_puts_the_cursor_on_it()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Answer("made");

        var s = await this.App.Key("F7");
        this.LeftTree.Exists("made").ShouldBeTrue();
        s.Left.CursorName.ShouldBe("made");
    }

    [Fact]
    public async Task F7_creates_nested_folders()
    {
        await this.Start();
        await this.App.Answer("one/two/three");

        var s = await this.App.Key("F7");
        Directory.Exists(this.LeftTree["one/two/three"]).ShouldBeTrue();
        s.Left.CursorName.ShouldBe("one");
    }

    [Fact]
    public async Task F8_deletes_after_confirming()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("yes");

        var s = await this.App.Key("F8");
        this.LeftTree.Names().ShouldBe(["b.txt"]);
        s.Left.Names.ShouldBe(["b.txt"]);
        s.Left.CursorName.ShouldBe("b.txt");
    }

    [Fact]
    public async Task F8_declined_deletes_nothing()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("no");

        await this.App.Key("F8");
        this.LeftTree.Exists("a.txt").ShouldBeTrue();
    }

    [Fact]
    public async Task F8_deletes_marked_folders_recursively()
    {
        await this.Start(l => l.Dir("x").File("x/deep/file.txt").File("keep.txt"));
        await this.App.Mark("x");
        await this.App.Answer("yes");

        await this.App.Key("F8");
        this.LeftTree.Names().ShouldBe(["keep.txt"]);
    }

    [Fact]
    public async Task F2_renames_and_keeps_the_cursor_on_the_new_name()
    {
        await this.Start(l => l.File("old.txt", "content"));
        await this.App.Cursor("old.txt");
        await this.App.Answer("new.txt");

        var s = await this.App.Key("F2");
        this.LeftTree.Read("new.txt").ShouldBe("content");
        s.Left.CursorName.ShouldBe("new.txt");
    }

    [Fact]
    public async Task Rename_onto_an_existing_name_is_refused_with_a_message()
    {
        await this.Start(l => l.File("a.txt", "A").File("b.txt", "B"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("b.txt");

        var s = await this.App.Key("F2");
        this.LeftTree.Read("b.txt").ShouldBe("B");
        this.LeftTree.Read("a.txt").ShouldBe("A");
        s.Dialogs.ShouldContain(d => d.Kind == "alert" && d.Title == "Rename");
    }

    [Fact]
    public async Task Shift_F4_creates_a_file_and_opens_the_editor()
    {
        await this.Start();
        await this.App.Answer("notes.txt");

        var s = await this.App.Key("Shift+F4");
        this.LeftTree.Exists("notes.txt").ShouldBeTrue();
        s.Page.ShouldBe("EditorPage");
    }
}
