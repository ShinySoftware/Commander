using Microsoft.Maui.DevFlow.Driver;
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

    [Fact]
    public async Task A_locked_tab_stays_put_and_opens_folders_in_a_new_tab()
    {
        await this.Start(l => l.Dir("sub").File("sub/inside.txt"));

        // right-click the tab, "Lock tab"
        await this.TabMenu("left", "Lock tab");
        (await this.App.WaitFor(x => x.Left.LockedTabs.SequenceEqual([true]), "the tab to lock")).Left.Tabs.Count.ShouldBe(1);

        await this.App.Navigate(this.LeftTree["sub"]);
        var s = await this.App.WaitFor(x => x.Left.Tabs.Count == 2, "a new tab for the folder");
        s.Left.Tabs.ShouldBe([this.LeftTree.Path, this.LeftTree["sub"]]);
        s.Left.LockedTabs.ShouldBe([true, false]);
        s.Left.ActiveTab.ShouldBe(1);
        s.Left.Names.ShouldBe(["inside.txt"]);
    }

    [Fact]
    public async Task A_locked_tab_cannot_be_closed_until_unlocked()
    {
        await this.Start(l => l.Dir("sub"));
        await this.Ui.Tap("LeftNewTab");
        await this.App.WaitFor(x => x.Left.Tabs.Count == 2, "the + button to open a tab");
        await this.App.Key("Ctrl+Shift+Tab");

        await this.TabMenu("left", "Lock tab");
        await this.App.WaitFor(x => x.Left.LockedTabs.SequenceEqual([true, false]), "the first tab to lock");

        var s = await this.App.Key("Primary+W");
        s.Left.Tabs.Count.ShouldBe(2);
        s.Dialogs.ShouldContain(d => d.Kind == "toast" && d.Message!.Contains("locked"));

        await this.Ui.Tap("TabLock:" + this.LeftTree.Path); // the padlock unlocks
        await this.App.WaitFor(x => x.Left.LockedTabs.SequenceEqual([false, false]), "the tab to unlock");
        (await this.App.Key("Primary+W")).Left.Tabs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_tab_can_be_renamed_coloured_and_given_an_icon_from_its_menu()
    {
        await this.Start();

        await this.App.Answer("Work");
        await this.TabMenu("left", "Rename tab…");
        await this.App.WaitFor(x => x.Left.TabTitles.SequenceEqual(["Work"]), "the tab to take its new name");

        // colours and icons apply as they're picked, and the menu stays open for more
        await this.OpenTabMenu("Work");
        await this.Ui.Tap("TabColor:Blue");
        await this.App.WaitFor(x => x.Left.TabColors.SequenceEqual(["Blue"]), "the tab to take its colour");
        await this.Ui.Tap("TabIcon:star");
        await this.App.WaitFor(x => x.Left.TabIcons.SequenceEqual(["star"]), "the tab to take its icon");
        await this.Ui.Tap("LeftTabMenuBackdrop"); // a click off the menu closes it
        (await this.Ui.Exists(x => x.AutomationId == "LeftTabMenu")).ShouldBeFalse();

        // an empty name goes back to the folder's, None / Automatic take the colour and icon off
        await this.App.Answer("");
        await this.TabMenu("Work", "Rename tab…");
        await this.App.WaitFor(x => x.Left.TabTitles.SequenceEqual(["left"]), "the folder's name back");
        await this.OpenTabMenu("left");
        await this.Ui.Tap("TabColor:None");
        await this.Ui.Tap("TabIcon:Auto");
        await this.App.WaitFor(x => x.Left.TabColors.SequenceEqual(new string?[] { null }) && x.Left.TabIcons.SequenceEqual(new string?[] { null }), "no colour or icon");
        await this.Ui.Tap("LeftTabMenuBackdrop");
    }

    [Fact]
    public async Task With_tabs_turned_off_a_locked_tab_navigates_and_goes_home_when_they_come_back()
    {
        await this.Start(l => l.Dir("sub"));
        await this.TabMenu("left", "Lock tab");
        await this.App.WaitFor(x => x.Left.LockedTabs.SequenceEqual([true]), "the tab to lock");

        var s = await this.App.ShowTabs(false);
        s.Left.ShowTabs.ShouldBeFalse();
        var strip = (await this.Ui.All()).First(x => x.AutomationId == "LeftTabs");
        (await this.Ui.WhyHidden(strip)).ShouldNotBeNull("the tab row is turned off");

        s = await this.App.Navigate(this.LeftTree["sub"]);
        s.Left.Tabs.ShouldBe([this.LeftTree["sub"]]); // the lock doesn't apply - no new tab
        (await this.App.Key("Primary+T")).Left.Tabs.Count.ShouldBe(1);

        // back on: the locked tab goes home, and where it had got to opens next to it
        s = await this.App.ShowTabs(true);
        s.Left.Tabs.ShouldBe([this.LeftTree.Path, this.LeftTree["sub"]]);
        s.Left.LockedTabs.ShouldBe([true, false]);
        s.Left.ActiveTab.ShouldBe(1);
        await this.Ui.ShouldBeVisible(await this.Ui.ById("LeftTabs"), "the tab row");
    }

    // right-click a tab (by what it shows) for its menu
    async Task OpenTabMenu(string tab)
    {
        await this.RightClick(await this.Ui.ByText(tab, within: "LeftTabs"));
        await this.Ui.ById("LeftTabMenu");
    }

    async Task TabMenu(string tab, string item)
    {
        await this.OpenTabMenu(tab);
        await this.Ui.Click(item, within: "LeftTabMenu");
    }

    async Task RightClick(ElementInfo element)
    {
        var (x, y) = await this.Ui.Center(element);
        await this.App.ClickAt(x, y, secondary: true);
    }
}
