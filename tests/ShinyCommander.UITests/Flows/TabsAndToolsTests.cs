using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>Folder tabs, the lister/help/find pages and the directory hotlist.</summary>
public class TabsAndToolsTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task Primary_T_opens_a_tab_on_the_folder_under_the_cursor_and_Primary_W_closes_it()
    {
        await this.Start(l => l.Dir("sub"));
        await this.App.Cursor("sub");

        var s = await this.App.Key("Primary+T");
        s.Left.Tabs.Count.ShouldBe(2);
        s.Left.ActiveTab.ShouldBe(1);
        s.Left.Path.ShouldBe(this.LeftTree["sub"]);

        s = await this.App.Key("Primary+W");
        s.Left.Tabs.Count.ShouldBe(1);
        s.Left.Path.ShouldBe(this.LeftTree.Path);
    }

    [Fact]
    public async Task Ctrl_Tab_cycles_tabs()
    {
        await this.Start(l => l.Dir("sub"));
        await this.App.Cursor("sub");
        await this.App.Key("Primary+T");

        (await this.App.Key("Ctrl+Tab")).Left.ActiveTab.ShouldBe(0);
        (await this.App.Key("Ctrl+Tab")).Left.ActiveTab.ShouldBe(1);
        (await this.App.Key("Ctrl+Shift+Tab")).Left.ActiveTab.ShouldBe(0);
    }

    [Fact]
    public async Task The_last_tab_cannot_be_closed()
    {
        await this.Start();
        (await this.App.Key("Primary+W")).Left.Tabs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task F3_opens_the_lister_and_Escape_closes_it()
    {
        await this.Start(l => l.File("a.txt", "lister text"));
        await this.App.Cursor("a.txt");

        (await this.App.Key("F3")).Page.ShouldBe("ViewerPage");
        (await this.App.Key("Escape")).Page.ShouldBe("MainPage");
    }

    [Fact]
    public async Task F3_on_a_file_inside_an_archive_opens_the_lister()
    {
        await this.Start(l => l.TarGz("t.tar.gz", ("inner.txt", "from the archive")));
        await this.App.Navigate(this.LeftTree["t.tar.gz"]);
        await this.App.Cursor("inner.txt");

        (await this.App.Key("F3")).Page.ShouldBe("ViewerPage");
    }

    [Fact]
    public async Task F1_opens_the_keyboard_reference()
    {
        await this.Start();
        (await this.App.Key("F1")).Page.ShouldBe("HelpPage");
    }

    [Fact]
    public async Task Alt_F7_opens_find_files()
    {
        await this.Start();
        (await this.App.Key("Alt+F7")).Page.ShouldBe("SearchPage");
    }

    [Fact]
    public async Task Primary_M_opens_multi_rename_for_the_marked_files()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));
        await this.App.Mark("a.txt", "b.txt");

        (await this.App.Key("Primary+M")).Page.ShouldBe("MultiRenamePage");
    }

    [Fact]
    public async Task Primary_Shift_S_opens_synchronize()
    {
        await this.Start();
        (await this.App.Key("Primary+Shift+S")).Page.ShouldBe("ComparePage");
    }

    [Fact]
    public async Task Hotlist_adds_the_current_folder_and_jumps_back_to_it()
    {
        await this.Start(l => l.Dir("fav"));
        await this.App.Navigate(this.LeftTree["fav"]);
        await this.App.Answer("Add current folder");
        var s = await this.App.Key("Primary+D");
        s.Dialogs.ShouldContain(d => d.Kind == "toast" && d.Message!.Contains("Added"));

        await this.App.Navigate(this.LeftTree.Path);
        await this.App.Answer(this.LeftTree["fav"]);
        s = await this.App.Key("Primary+D");
        s.Left.Path.ShouldBe(this.LeftTree["fav"]);
    }
}
