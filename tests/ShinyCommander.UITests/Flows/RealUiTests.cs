using Microsoft.Maui.DevFlow.Driver;
using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// The parts the hooks skip on purpose, driven like a user: mouse clicks on rows and tiles, the ribbon, the
/// function key bar, the path box, the per-panel tree, and the real Shiny dialogs (not the scripted ones).
/// </summary>
public class RealUiTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task Clicking_a_row_in_the_other_panel_activates_it_and_moves_the_cursor()
    {
        await this.Start(l => l.File("a.txt"), r => r.File("first.txt").File("second.txt"));

        await this.Ui.Click("second", within: "RightPanel");
        var s = await this.App.State();
        s.ActiveSide.ShouldBe("right");
        s.Right.CursorName.ShouldBe("second.txt");
    }

    [Fact]
    public async Task Double_clicking_a_folder_opens_it()
    {
        await this.Start(l => l.Dir("inside").File("inside/deep.txt"));

        await this.Ui.DoubleClick("inside", within: "LeftPanel");
        var s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["inside"], "the folder to open");
        s.Left.Names.ShouldBe(["deep.txt"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Double_clicking_an_image_opens_it_in_its_app(bool thumbnails, bool preview)
    {
        await this.Start(l => l.Bytes("photo.png", TestTree.Png1x1).File("notes.txt"));
        if (thumbnails)
            await this.App.Key("Primary+Shift+F1");
        // the preview swaps in the picture on the first click - the second must still count
        if (preview)
            await this.App.Command("QuickView");
        try
        {
            // a real AppKit double-click on the row / tile, not the recognizer invoked directly
            // on the tile's picture (where you'd click), or the row's name
            var label = await this.Ui.ByText(thumbnails ? "photo.png" : "photo", within: "LeftPanel");
            var r = label.WindowBounds!;
            var y = thumbnails ? r.Y - 50 : r.Y + r.Height / 2;
            var hit = await this.App.HitAt(r.X + r.Width / 2, y);
            await this.App.ClickAt(r.X + r.Width / 2, y, clicks: 2);
            await this.App.WaitFor(x => x.Opened.Contains(this.LeftTree["photo.png"]), $"the image handed to its app", TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (thumbnails)
                await this.App.Key("Primary+F2");
        }
    }

    [Fact]
    public async Task The_path_row_arrows_go_up_back_and_forward()
    {
        await this.Start(l => l.Dir("inside").File("inside/deep.txt"));
        await this.App.Navigate(this.LeftTree["inside"], "left");

        await this.Ui.Tap("LeftUp");
        await this.App.WaitFor(x => x.Left.Path == this.LeftTree.Path, "up");

        await this.Ui.Tap("LeftBack");
        await this.App.WaitFor(x => x.Left.Path == this.LeftTree["inside"], "back");

        await this.Ui.Tap("LeftForward");
        await this.App.WaitFor(x => x.Left.Path == this.LeftTree.Path, "forward");
    }

    // real clicks: a DevFlow tap skips AppKit's hit-testing, and a tap recognizer on the path row once took every click in it
    [Fact]
    public async Task A_mouse_click_on_the_up_arrow_goes_up()
    {
        await this.Start(l => l.Dir("inside").File("inside/deep.txt"));
        await this.App.Navigate(this.LeftTree["inside"], "left");

        var (x, y) = await this.Ui.Center(await this.Ui.ById("LeftUp"));
        await this.App.ClickAt(x, y);
        await this.App.WaitFor(s => s.Left.Path == this.LeftTree.Path, $"the click on up (hit: {this.App.LastResult.GetProperty("element").GetString()})", TimeSpan.FromSeconds(5));
    }

    // a real double-click, AppKit's click count and all: a folder opens, a file goes to its app (recorded in test mode)
    [Fact]
    public async Task A_double_click_opens_a_folder_and_hands_a_file_to_its_app()
    {
        await this.Start(l => l.File("notes.txt", "hello").Dir("inside"), r => r.File("other.txt", "there"));

        var file = (await this.Ui.ByText("notes", within: "LeftPanel")).WindowBounds!;
        await this.App.ClickAt(file.X + file.Width / 2, file.Y + file.Height / 2, clicks: 2);
        await this.App.WaitFor(s => s.Opened.Contains(this.LeftTree["notes.txt"]), $"the file handed to its app (hit: {this.App.LastResult.GetProperty("element").GetString()})", TimeSpan.FromSeconds(5));

        // the same with the preview panel open - the first click shows the file in it
        await this.App.Key("Ctrl+Q");
        await this.App.Cursor("inside");
        file = (await this.Ui.ByText("notes", within: "LeftPanel")).WindowBounds!;
        await this.App.ClickAt(file.X + file.Width / 2, file.Y + file.Height / 2, clicks: 2);
        await this.App.WaitFor(s => s.Opened.Count == 2, $"the file handed to its app with the preview open (hit: {this.App.LastResult.GetProperty("element").GetString()})", TimeSpan.FromSeconds(5));
        await this.App.Key("Ctrl+Q");

        // on the row's drawn columns (size, date) and on its icon - not only the name
        var panel = (await this.Ui.ById("LeftPanel")).WindowBounds!;
        file = (await this.Ui.ByText("notes", within: "LeftPanel")).WindowBounds!;
        await this.App.ClickAt(panel.X + panel.Width - 60, file.Y + file.Height / 2, clicks: 2);
        await this.App.WaitFor(s => s.Opened.Count == 3, $"a double-click on the size column (hit: {this.App.LastResult.GetProperty("element").GetString()})", TimeSpan.FromSeconds(5));
        await this.App.ClickAt(file.X - 14, file.Y + file.Height / 2, clicks: 2);
        await this.App.WaitFor(s => s.Opened.Count == 4, $"a double-click on the icon (hit: {this.App.LastResult.GetProperty("element").GetString()})", TimeSpan.FromSeconds(5));

        // in the other panel - the first click also makes it the active one
        var other = (await this.Ui.ByText("other", within: "RightPanel")).WindowBounds!;
        await this.App.ClickAt(other.X + other.Width / 2, other.Y + other.Height / 2, clicks: 2);
        await this.App.WaitFor(s => s.Opened.Count == 5 && s.Opened.Contains(this.RightTree["other.txt"]), $"the inactive panel's file handed to its app (hit: {this.App.LastResult.GetProperty("element").GetString()})", TimeSpan.FromSeconds(5));
        await this.App.Key("Tab");

        var folder = (await this.Ui.ByText("inside", within: "LeftPanel")).WindowBounds!;
        await this.App.ClickAt(folder.X + folder.Width / 2, folder.Y + folder.Height / 2, clicks: 2);
        await this.App.WaitFor(s => s.Left.Path == this.LeftTree["inside"], "the folder opened", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_mouse_click_on_a_pane_toolbar_button_runs_it()
    {
        await this.Start(l => l.File("a.txt"));

        var (x, y) = await this.Ui.Center(await this.Ui.ById("LeftViewMode"));
        await this.App.ClickAt(x, y);
        await this.App.WaitFor(s => s.Left.ViewMode == "Thumbnails", $"the click on the view mode button (hit: {this.App.LastResult.GetProperty("element").GetString()})", TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    public async Task A_mouse_click_on_a_panes_preview_button_opens_the_preview(string side)
    {
        await this.Start(l => l.File("a.txt", "A"), r => r.File("b.txt", "B"));
        try
        {
            var (x, y) = await this.Ui.Center(await this.Ui.ById(side + "ToolbarQuickView"));
            var hit = (await this.App.HitAt(x, y)).ToString();
            var element = this.App.LastResult.GetProperty("element").GetString();
            await this.App.ClickAt(x, y);
            await this.App.WaitFor(s => s.Preview.IsOpen, $"the preview to open (hit: {element})", TimeSpan.FromSeconds(5));
        }
        finally
        {
            await this.App.Command("QuickView");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_mouse_click_on_the_ribbons_preview_button_opens_the_preview(bool onIcon)
    {
        await this.Start(l => l.File("a.txt", "A"));
        await this.Ui.Tap("ribbon-tab-view");
        try
        {
            // a large button: icon on top, label under it
            var b = (await this.Ui.ById("ribbon-QuickView")).WindowBounds!;
            var (x, y) = (b.X + b.Width / 2, onIcon ? b.Y + 18 : b.Y + b.Height - 10);
            await this.App.HitAt(x, y);
            var element = this.App.LastResult.GetProperty("element").GetString();
            await this.App.ClickAt(x, y);
            await this.App.WaitFor(s => s.Preview.IsOpen, $"the preview to open (hit: {element})", TimeSpan.FromSeconds(5));
        }
        finally
        {
            await this.App.Command("QuickView");
        }
    }

    [Theory]
    [InlineData("ribbon-Copy", "files")]
    [InlineData("ribbon-ToggleTree", "view")]
    public async Task A_mouse_click_on_a_ribbon_buttons_icon_runs_it(string button, string tab)
    {
        await this.Start(l => l.File("a.txt", "A"));
        await this.App.Cursor("a.txt");
        await this.Ui.Tap($"ribbon-tab-{tab}");
        var tree = (await this.App.State()).Left.ShowTree;
        await this.App.Answer("<cancel>");

        // the icon is what you aim at - left third of a small button, the top of a large one
        var b = (await this.Ui.ById(button)).WindowBounds!;
        var (x, y) = button == "ribbon-Copy" ? (b.X + b.Width / 2, b.Y + 18) : (b.X + 12, b.Y + b.Height / 2);
        await this.App.HitAt(x, y);
        var element = this.App.LastResult.GetProperty("element").GetString();
        await this.App.ClickAt(x, y);
        if (button == "ribbon-Copy")
            await this.App.WaitFor(s => s.Dialogs.Count > 0, $"the copy prompt (hit: {element})", TimeSpan.FromSeconds(5));
        else
        {
            await this.App.WaitFor(s => s.Left.ShowTree != tree, $"the tree to toggle (hit: {element})", TimeSpan.FromSeconds(5));
            await this.App.Command("ToggleTree");
        }
    }

    [Fact]
    public async Task The_function_key_bar_runs_its_command()
    {
        await this.Start(l => l.File("a.txt", "A"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<default>");

        await this.Ui.Click("Copy");
        await this.App.WaitFor(_ => this.RightTree.Exists("a.txt"), "the copy");
    }

    [Theory]
    [InlineData("files", "ribbon-Copy")]
    [InlineData("select", "ribbon-SelectAll")]
    [InlineData("view", "ribbon-ThumbnailView")]
    [InlineData("go", "ribbon-GoHome")]
    [InlineData("tools", "ribbon-Find")]
    public async Task Each_ribbon_tab_shows_its_buttons(string tab, string button)
    {
        await this.Start();

        await this.Ui.Tap($"ribbon-tab-{tab}");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var b = await this.Ui.ById(button);
        while (b.Bounds is not { Width: > 10, Height: > 10 } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            b = await this.Ui.ById(button);
        }
        b.Bounds.ShouldNotBeNull();
        b.Bounds!.Width.ShouldBeGreaterThan(10, $"{button} has no size after its tab opened");
        b.Bounds.Height.ShouldBeGreaterThan(10);
        (await this.App.State()).RibbonTab.ShouldBe(tab);
    }

    [Fact]
    public async Task Ribbon_buttons_run_their_commands()
    {
        await this.Start(l => l.File("a.txt"));

        await this.Ui.Tap("ribbon-tab-view");
        await this.Ui.Tap("ribbon-ThumbnailView");
        (await this.App.State()).Left.ViewMode.ShouldBe("Thumbnails");

        await this.Ui.Tap("ribbon-TogglePanelLayout");
        (await this.App.State()).StackPanels.ShouldBeTrue();
    }

    [Fact]
    public async Task Ribbon_toggles_are_pressed_while_on_for_the_active_panel()
    {
        await this.Start(l => l.File("a.txt"));
        await this.Ui.Tap("ribbon-tab-view");
        var tree = (await this.App.State()).Left.ShowTree;

        await this.Ui.Tap("ribbon-ToggleTree");
        var s = await this.App.WaitFor(x => x.Left.ShowTree != tree, "the tree to toggle");
        s.RibbonPressed.Contains("ribbon-ToggleTree").ShouldBe(!tree);
        s.RibbonPressed.ShouldContain("ribbon-DetailsView");
        s.RibbonPressed.ShouldNotContain("ribbon-ThumbnailView");

        // the other panel has its own tree - the button follows the active one
        await this.App.Key("Tab");
        s = await this.App.WaitFor(x => x.ActiveSide == "right", "the right panel to be active");
        s.RibbonPressed.Contains("ribbon-ToggleTree").ShouldBe(s.Right.ShowTree);
        await this.App.Key("Tab");

        await this.Ui.Tap("ribbon-ThumbnailView");
        s = await this.App.WaitFor(x => x.RibbonPressed.Contains("ribbon-ThumbnailView"), "Thumbnails to be pressed");
        s.RibbonPressed.ShouldNotContain("ribbon-DetailsView");

        await this.Ui.Tap("ribbon-DetailsView");
        await this.App.WaitFor(x => x.RibbonPressed.Contains("ribbon-DetailsView") && !x.RibbonPressed.Contains("ribbon-ThumbnailView"), "Details to be pressed again");

        // put the tree back as it was - the test app is shared
        await this.Ui.Tap("ribbon-ToggleTree");
        await this.App.WaitFor(x => x.Left.ShowTree == tree, "the tree to toggle back");
    }

    [Fact]
    public async Task Typing_a_path_in_the_path_box_and_pressing_Enter_navigates()
    {
        await this.Start(l => l.Dir("dest").File("dest/here.txt"));

        await this.Ui.Tap("LeftPathEdit"); // click past the breadcrumbs to edit the path
        var path = await this.Ui.ById("LeftPath");
        await this.Ui.Fill(path, this.LeftTree["dest"]);
        await this.Ui.Press(path, "Enter");

        var s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["dest"], "the path box to navigate");
        s.Left.Names.ShouldBe(["here.txt"]);
        await this.Ui.ByText("dest", within: "LeftPathCrumbs"); // back to the breadcrumbs, ending in the new folder
    }

    [Fact]
    public async Task Clicking_a_breadcrumb_goes_to_that_folder()
    {
        await this.Start(l => l.Dir("outer/inner").File("outer/there.txt"));
        await this.App.Navigate(this.LeftTree["outer/inner"]);

        await this.Ui.Click("outer", within: "LeftPathCrumbs");
        var s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["outer"], "the crumb to navigate");
        s.Left.Names.ShouldContain("there.txt");
    }

    [Fact]
    public async Task A_breadcrumb_drops_down_its_folders()
    {
        await this.Start(l => l.Dir("outer/inner").Dir("outer/sibling").File("outer/sibling/here.txt").File("outer/not-a-folder.txt"));
        await this.App.Navigate(this.LeftTree["outer/inner"]);

        await this.Ui.Tap(await this.Ui.Tappable(await this.Ui.ById("CrumbMenu:" + this.LeftTree["outer"])));
        await this.Ui.ByText("inner", within: "LeftCrumbMenu"); // the way to here is listed too
        (await this.Ui.Exists(x => x.Text == "not-a-folder.txt" && x.IsVisible)).ShouldBeFalse(); // folders only

        await this.Ui.Click("sibling", within: "LeftCrumbMenu");
        var s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["outer/sibling"], "the dropdown to navigate");
        s.Left.Names.ShouldBe(["here.txt"]);
    }

    [Fact]
    public async Task The_panel_tree_button_shows_a_tree_that_navigates_its_own_panel()
    {
        await this.Start();

        await this.Ui.Tap("RightTreeToggle");
        (await this.App.State()).Right.ShowTree.ShouldBeTrue();

        // the tree opens on the panel's own folder - its sibling "left" is right there
        await this.App.WaitFor(x => x.Right.TreePath == this.RightTree.Path, "the tree to reveal the panel's folder", TimeSpan.FromSeconds(10));
        await this.Ui.Click("left", within: "RightTree");
        var s = await this.App.WaitFor(x => x.Right.Path == this.LeftTree.Path, "the tree to navigate");
        s.Left.Path.ShouldBe(this.LeftTree.Path);
        s.ActiveSide.ShouldBe("right");
    }

    [Fact]
    public async Task A_tree_chevron_collapses_and_expands_its_folder()
    {
        // the disk's root: near the top of the tree, so its row is on screen without scrolling
        await this.Start();
        await this.App.Navigate("/", "left");
        // the app is shared between tests - the tree may still be on from an earlier one
        if (!(await this.App.State()).Left.ShowTree)
            await this.Ui.Tap("LeftTreeToggle");
        try
        {
            await this.App.WaitFor(x => x.Left.TreePath == "/", "the tree to reveal the root", TimeSpan.FromSeconds(10));

            // folders an earlier test left open can push the root's row out of sight - fold everything first
            for (var i = 0; i < 20; i++)
            {
                var open = (await this.Ui.All()).FirstOrDefault(e => e.AutomationId?.StartsWith("TreeChevron:", StringComparison.Ordinal) == true && e.Text == "▾" && e.IsVisible);
                if (open == null)
                    break;
                await this.ClickChevron(open.AutomationId!["TreeChevron:".Length..]);
            }

            // real clicks through AppKit's hit testing - the row's own tap used to take the chevron's clicks
            await this.ClickChevron("/");
            await this.WaitForTreeRow("/Users", true);

            await this.ClickChevron("/");
            await this.WaitForTreeRow("/Users", false);
            (await this.App.State()).Left.Path.ShouldBe("/"); // the chevron doesn't navigate
        }
        finally
        {
            await this.Ui.Tap("LeftTreeToggle");
        }
    }

    // window bounds: a list row's own bounds don't say where in the list it is. Each click replaces the tree's rows -
    // give the list a moment to lay them out before the next one is looked for
    async Task ClickChevron(string path)
    {
        var at = (await this.Ui.ById("TreeChevron:" + path)).WindowBounds!;
        await this.App.ClickAt(at.X + at.Width / 2, at.Y + at.Height / 2);
        await Task.Delay(400);
    }

    // by the row's chevron - the folder's name is in the breadcrumbs too
    async Task WaitForTreeRow(string path, bool shown)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await this.Ui.Exists(e => e.AutomationId == "TreeChevron:" + path && e.IsVisible) != shown)
        {
            if (DateTime.UtcNow > deadline)
                throw new ShouldAssertException($"the tree row for {path} should be {(shown ? "shown" : "gone")}");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Both_panels_have_a_tree_that_follows_their_own_folder()
    {
        await this.Start(l => l.Dir("left-a/left-b"), r => r.Dir("right-x/right-y"));
        await this.App.Navigate(this.LeftTree["left-a/left-b"], "left");
        await this.App.Navigate(this.RightTree["right-x/right-y"], "right");

        await this.Ui.Tap("LeftTreeToggle");
        await this.Ui.Tap("RightTreeToggle");
        AppState s;
        try
        {
            s = await this.App.WaitFor(x => x.Left.TreePath == this.LeftTree["left-a/left-b"] && x.Right.TreePath == this.RightTree["right-x/right-y"],
                "both trees to expand to their panel's folder", TimeSpan.FromSeconds(60));
        }
        catch (ShouldAssertException)
        {
            var now = await this.App.State();
            throw new ShouldAssertException($"trees stopped at left={now.Left.TreePath ?? "(none)"} right={now.Right.TreePath ?? "(none)"}");
        }
        s.Left.ShowTree.ShouldBeTrue();
        s.Right.ShowTree.ShouldBeTrue();

        // deep folders indent past the default width - drag the splitter to make room
        var before = s.Left.TreeWidth;
        (await this.Fixture.Client.GestureDetailedAsync("pan", (await this.Ui.ById("LeftTreeSplitter")).Id, "right", 220)).Success.ShouldBeTrue();
        s = await this.App.State();
        s.Left.TreeWidth.ShouldBeGreaterThan(before + 100, "dragging the splitter should widen the tree");
        s.Right.TreeWidth.ShouldBe(240, "the other panel's tree keeps its width");

        await this.Ui.ShouldBeVisible(await this.Ui.ByText("left-b", within: "LeftTree"), "the left panel's folder in its tree");
        (await this.Fixture.Client.GestureDetailedAsync("pan", (await this.Ui.ById("RightTreeSplitter")).Id, "right", 220)).Success.ShouldBeTrue();
        await this.Ui.ShouldBeVisible(await this.Ui.ByText("right-y", within: "RightTree"), "the right panel's folder in its tree");

        // and it keeps following
        await this.App.Navigate(this.LeftTree["left-a"], "left");
        await this.App.WaitFor(x => x.Left.TreePath == this.LeftTree["left-a"], "the left tree to follow");
    }

    [Fact]
    public async Task Thumbnail_tiles_render_with_names_and_respond_to_clicks()
    {
        await this.Start(l => l.Bytes("pic.png", TestTree.Png1x1).File("doc.txt"));
        await this.App.Key("Primary+Shift+F1");

        await this.Ui.ByText("pic.png", within: "LeftThumbnails");
        await this.Ui.Click("doc.txt", within: "LeftThumbnails");
        (await this.App.State()).Left.CursorName.ShouldBe("doc.txt");
    }

    [Fact]
    public async Task A_real_prompt_dialog_takes_typing_and_OK()
    {
        await this.Start();
        await this.App.RealDialogs();

        await this.App.Key("F7");
        var entry = await this.DialogEntry();
        await this.Ui.Fill(entry, "typed-in-dialog");
        await this.Ui.ClickInDialog("OK");

        await this.App.WaitFor(_ => this.LeftTree.Exists("typed-in-dialog"), "the folder from the real prompt");
    }

    [Fact]
    public async Task A_real_prompt_dialog_has_keyboard_focus_when_it_opens()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.RealDialogs();

        await this.App.Key("F7");
        await this.DialogEntry();
        try
        {
            // native first responder, not MAUI's IsFocused (which the AppKit backend doesn't keep current)
            await this.App.WaitFor(x => x.Focus == "dialog", "the new folder prompt's text box to take the keyboard", TimeSpan.FromSeconds(3));
        }
        finally
        {
            await this.Ui.ClickInDialog("Cancel");
        }
    }

    [Fact]
    public async Task A_second_dialog_opens_right_after_the_first_is_cancelled()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.RealDialogs();

        await this.App.Key("F7");
        await this.DialogEntry();
        await this.Ui.ClickInDialog("Cancel");

        var s = await this.App.Key("F8");
        try
        {
            await this.Ui.ByText("Delete permanently", within: "type:DialogView", timeout: TimeSpan.FromSeconds(3));
        }
        catch (ShouldAssertException)
        {
            throw new ShouldAssertException($"F8 after a cancelled prompt showed no confirm. Key handled: {this.App.LastKeyHandled}. Focus: {s.Focus}. Dialogs: {String.Join(" | ", s.Dialogs)}");
        }
        finally
        {
            await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);
        }
    }

    [Fact]
    public async Task The_command_line_has_the_keyboard_on_the_main_page()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.WaitFor(x => x.Focus == "CommandLine", "the command line to have the keyboard", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_command_line_gets_the_keyboard_back_after_a_dialog_closes()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.RealDialogs();

        await this.App.Key("F7");
        await this.DialogEntry();
        await this.Ui.ClickInDialog("Cancel");
        await this.App.WaitFor(x => x.Focus == "CommandLine", "focus back in the command line", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_command_line_gets_the_keyboard_back_after_the_lister_closes()
    {
        await this.Start(l => l.File("a.txt", "text"));
        await this.App.Cursor("a.txt");

        await this.App.Key("F3");
        await this.App.Key("Escape");
        await this.App.WaitFor(x => x.Page == "MainPage" && x.Focus == "CommandLine", "focus back in the command line", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_real_confirm_dialog_deletes_on_its_button()
    {
        await this.Start(l => l.File("doomed.txt").File("keep.txt"));
        await this.App.Cursor("doomed.txt");
        await this.App.RealDialogs();

        await this.App.Key("F8");
        await this.Ui.ClickInDialog("Delete");
        await this.App.WaitFor(_ => !this.LeftTree.Exists("doomed.txt"), "the delete");
        this.LeftTree.Exists("keep.txt").ShouldBeTrue();
    }

    [Fact]
    public async Task A_real_confirm_dialog_moves_to_trash_on_its_button()
    {
        await this.Start(l => l.File("doomed.txt").File("keep.txt"));
        await this.App.UseTrash();
        await this.App.Cursor("doomed.txt");
        await this.App.RealDialogs();

        await this.App.Key("F8");
        await this.Ui.ClickInDialog("Move to Trash");
        await this.App.WaitFor(_ => !this.LeftTree.Exists("doomed.txt"), "the move to trash");
        this.LeftTree.Exists("keep.txt").ShouldBeTrue();
        Directory.EnumerateFiles(TestConfig.TrashDir, "*-doomed.txt").ShouldNotBeEmpty();
    }

    // DevFlow taps a button directly; these click the way a mouse does - through AppKit's hit-testing - which the
    // dialog layer once failed (the backend ignored CascadeInputTransparent, so clicks fell through to the panels)
    [Theory]
    [InlineData(true, "Move to Trash")]
    [InlineData(false, "Delete")]
    public async Task A_mouse_click_on_the_confirm_button_deletes(bool trash, string button)
    {
        await this.Start(l => l.File("doomed.txt").File("keep.txt"));
        await this.App.UseTrash(trash);
        await this.App.Cursor("doomed.txt");
        await this.App.RealDialogs();
        await this.App.Key("F8");
        var target = await this.Ui.ByText(button, within: "type:DialogView");
        await Task.Delay(400); // let it animate in

        var (x, y) = await this.Ui.Center(target);
        await this.App.ClickAt(x, y);

        await this.App.WaitFor(_ => !this.LeftTree.Exists("doomed.txt"), $"the click on {button} to delete", TimeSpan.FromSeconds(5));
        this.LeftTree.Exists("keep.txt").ShouldBeTrue();
    }

    [Fact]
    public async Task A_mouse_click_on_cancel_closes_the_dialog_and_the_panels_take_clicks_again()
    {
        await this.Start(l => l.File("doomed.txt"));
        await this.App.Cursor("doomed.txt");
        await this.App.RealDialogs();
        await this.App.Key("F8");
        var cancel = await this.Ui.ByText("Cancel", within: "type:DialogView");
        await Task.Delay(400);

        var (x, y) = await this.Ui.Center(cancel);
        await this.App.ClickAt(x, y);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && await this.Ui.Exists(e => e.Type == "DialogView" && e.IsVisible && e.Bounds is { Width: > 0 }))
            await Task.Delay(100);
        (await this.Ui.Exists(e => e.Type == "DialogView" && e.IsVisible && e.Bounds is { Width: > 0 })).ShouldBeFalse("the click on Cancel should close the dialog");
        this.LeftTree.Exists("doomed.txt").ShouldBeTrue();

        // with the dialog gone the layer is click-through again: a click on a row reaches the panel
        await Task.Delay(500);
        var (rx, ry) = await this.Ui.Center(await this.Ui.ByText("doomed", within: "LeftPanel"));
        await this.App.ClickAt(rx, ry);
        this.App.LastResult.GetProperty("hit").GetString()!.ShouldNotContain("DialogLayer");
    }

    [Fact]
    public async Task A_real_conflict_choice_skips()
    {
        await this.Start(l => l.File("a.txt", "new"), r => r.File("a.txt", "old"));
        await this.App.Cursor("a.txt");
        await this.App.RealDialogs();

        await this.App.Key("F5");
        await this.DialogEntry();
        await this.Ui.ClickInDialog("Copy"); // the prompt's OK button carries the verb
        await this.Ui.ClickInDialog("Skip");
        await this.App.WaitFor(x => !x.IsOperationRunning, "the copy to finish");
        this.RightTree.Read("a.txt").ShouldBe("old");
    }

    [Fact]
    public async Task Settings_switch_shows_hidden_files_back_on_the_main_page()
    {
        await this.Start(l => l.File(".secret").File("plain.txt"));

        await this.Ui.Tap("ribbon-tab-tools");
        await this.Ui.Tap("ribbon-Settings");
        await this.App.WaitFor(x => x.Page == "SettingsPage", "the settings page");
        var row = await this.Ui.ByText("Show hidden files");
        var sw = (await this.Ui.All()).First(x => x.Type == "Switch" && x.ParentId != null && x.Bounds!.Y >= row.Bounds!.Y - 20 && x.Bounds.Y <= row.Bounds.Y + 30);
        await this.Ui.Tap(sw);
        await this.Ui.Tap("PageBack");

        var s = await this.App.WaitFor(x => x.Page == "MainPage" && x.Left.Names.Contains(".secret"), "hidden files on the main page");
        s.Left.Names.ShouldContain("plain.txt");
    }

    [Fact]
    public async Task The_lister_shows_the_file_text()
    {
        await this.Start(l => l.File("read-me.txt", "the quick brown fox"));
        await this.App.Cursor("read-me.txt");

        await this.App.Key("F3");
        await this.Ui.ByText("the quick brown fox", timeout: TimeSpan.FromSeconds(5));
    }

    // the form is centred in a scroll view - its document view once took the form's 640 width at x=0, and the controls on
    // the right of a freshly opened editor fell outside it, so the protocol dropdown ignored the first clicks
    [Fact]
    public async Task The_new_connection_form_takes_clicks_on_the_protocol_dropdown()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Command("Connections");
        await this.App.WaitFor(x => x.Page == "ConnectionsPage", "the Connections page");
        await this.Ui.Tap("NewConnection");
        await this.App.WaitFor(x => x.Page == "ConnectionEditPage", "the connection editor");

        foreach (var (id, element) in new[] { ("ConnProtocol", "Picker#ConnProtocol"), ("ConnName", "Entry#ConnName") })
        {
            var (x, y) = await this.Ui.Center(await this.Ui.ById(id));
            await this.App.HitAt(x, y);
            this.App.LastResult.GetProperty("element").GetString().ShouldStartWith(element);
        }
        await this.Ui.Tap("PageBack");
        await this.Ui.Tap("PageBack");
    }

    async Task<ElementInfo> DialogEntry()
    {
        var known = new HashSet<string?> { "CommandLine", "LeftPath", "RightPath", "LeftFilter", "RightFilter" };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var entry = (await this.Ui.All()).FirstOrDefault(x => x.Type == "Entry" && x.IsVisible && !known.Contains(x.AutomationId));
            if (entry != null)
                return entry;
            if (DateTime.UtcNow > deadline)
                throw new ShouldAssertException("No dialog text box appeared");
            await Task.Delay(150);
        }
    }
}
