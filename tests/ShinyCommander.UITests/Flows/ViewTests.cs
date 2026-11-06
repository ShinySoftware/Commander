using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>Thumbnails, columns, quick view, stacked layout, per-panel trees, hidden files, quick filter and sorting.</summary>
public class ViewTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task Primary_Shift_F1_toggles_thumbnails_for_the_active_panel_only()
    {
        await this.Start(l => l.File("a.txt"));

        var s = await this.App.Key("Primary+Shift+F1");
        s.Left.ViewMode.ShouldBe("Thumbnails");
        s.Right.ViewMode.ShouldBe("Details");

        (await this.App.Key("Primary+Shift+F1")).Left.ViewMode.ShouldBe("Details");
    }

    [Fact]
    public async Task Primary_F2_returns_to_details()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Key("Primary+Shift+F1");

        (await this.App.Key("Primary+F2")).Left.ViewMode.ShouldBe("Details");
    }

    [Fact]
    public async Task In_thumbnails_Right_steps_one_tile_and_Down_a_whole_row()
    {
        await this.Start(l =>
        {
            for (var i = 1; i <= 12; i++)
                l.File($"f{i:00}.txt");
        });
        var s = await this.App.Key("Primary+Shift+F1");
        var span = s.Left.ThumbnailSpan;
        span.ShouldBeGreaterThan(1);

        (await this.App.Key("Right")).Left.Cursor.ShouldBe(1);
        (await this.App.Key("Down")).Left.Cursor.ShouldBe(1 + span);
        (await this.App.Key("Left")).Left.Cursor.ShouldBe(span);
    }

    [Fact]
    public async Task Left_and_Right_are_left_to_the_command_line_in_details_view()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));

        var s = await this.App.Key("Right");
        this.App.LastKeyHandled.ShouldBeFalse();
        s.Left.Cursor.ShouldBe(0);
    }

    [Fact]
    public async Task Ctrl_Q_previews_the_cursor_file_in_the_other_panel_and_follows_the_cursor()
    {
        await this.Start(l => l.File("one.txt", "first file").File("two.md", "# second"));
        await this.App.Cursor("one.txt");

        var s = await this.App.Key("Ctrl+Q");
        s.Right.IsQuickView.ShouldBeTrue();
        s.Left.IsQuickView.ShouldBeFalse();
        s = await this.App.WaitFor(x => x.Right.QuickView.Text.Contains("first file"), "the text preview");
        s.Right.QuickView.Title.ShouldBe("one.txt");
        s.Right.QuickView.Kind.ShouldBe("Text");

        s = await this.App.Key("Down");
        s = await this.App.WaitFor(x => x.Right.QuickView.Title == "two.md" && x.Right.QuickView.Kind == "Markdown", "the preview to follow the cursor");
        s.Right.QuickView.Text.ShouldContain("# second");
    }

    [Fact]
    public async Task Quick_view_previews_images()
    {
        await this.Start(l => l.Bytes("dot.png", TestTree.Png1x1));
        await this.App.Cursor("dot.png");

        await this.App.Key("Ctrl+Q");
        var s = await this.App.WaitFor(x => x.Right.QuickView.HasImage, "the image preview");
        s.Right.QuickView.Kind.ShouldBe("Image");
    }

    [Fact]
    public async Task Quick_view_summarizes_folders()
    {
        await this.Start(l => l.Dir("box").File("box/a.txt", "12345").File("box/b.txt", "1"));
        await this.App.Cursor("box");

        await this.App.Key("Ctrl+Q");
        var s = await this.App.WaitFor(x => x.Right.QuickView.Info.Contains("2 file(s)"), "the folder summary");
        s.Right.QuickView.Kind.ShouldBeNull();
    }

    [Fact]
    public async Task Ctrl_Q_again_or_tabbing_into_the_preview_ends_quick_view()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");

        await this.App.Key("Ctrl+Q");
        (await this.App.Key("Ctrl+Q")).Right.IsQuickView.ShouldBeFalse();

        await this.App.Key("Ctrl+Q");
        var s = await this.App.Key("Tab");
        s.Right.IsQuickView.ShouldBeFalse();
        s.ActiveSide.ShouldBe("right");
    }

    [Fact]
    public async Task Primary_Shift_L_stacks_the_panels_and_back()
    {
        await this.Start();
        (await this.App.Key("Primary+Shift+L")).StackPanels.ShouldBeTrue();
        (await this.App.Key("Primary+Shift+L")).StackPanels.ShouldBeFalse();
    }

    [Fact]
    public async Task Stacked_panels_are_laid_out_top_and_bottom()
    {
        await this.Start();
        await this.App.Key("Primary+Shift+L");

        var left = (await this.Fixture.Client.QueryAsync(automationId: "LeftPanel")).Single();
        var right = (await this.Fixture.Client.QueryAsync(automationId: "RightPanel")).Single();
        right.Bounds!.Y.ShouldBeGreaterThan(left.Bounds!.Y + left.Bounds.Height - 1);
        right.Bounds.X.ShouldBe(left.Bounds.X, 1);
    }

    [Fact]
    public async Task Primary_F8_toggles_the_tree_of_the_active_panel_only()
    {
        await this.Start();

        var s = await this.App.Key("Primary+F8");
        s.Left.ShowTree.ShouldBeTrue();
        s.Right.ShowTree.ShouldBeFalse();

        await this.App.Key("Tab");
        s = await this.App.Key("Primary+F8");
        s.Right.ShowTree.ShouldBeTrue();
        s.Left.ShowTree.ShouldBeTrue();
    }

    [Fact]
    public async Task Choosing_columns_hides_and_shows_them()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Answer("Attributes", "Date created", "Done");

        var s = await this.App.Key("Primary+Shift+K");
        s.Columns.ShouldNotContain("Attributes");
        s.Columns.ShouldContain("Created");
        s.Dialogs.Count(d => d.Title == "Columns").ShouldBe(3);
    }

    [Fact]
    public async Task Primary_H_shows_and_hides_dot_files()
    {
        await this.Start(l => l.File(".hidden").File("shown.txt"));
        (await this.App.State()).Left.Names.ShouldBe(["shown.txt"]);

        (await this.App.Key("Primary+H")).Left.Names.ShouldBe([".hidden", "shown.txt"]);
        (await this.App.Key("Primary+H")).Left.Names.ShouldBe(["shown.txt"]);
    }

    [Fact]
    public async Task Quick_filter_narrows_the_list_and_escape_clears_it()
    {
        await this.Start(l => l.File("a.cs").File("b.cs").File("c.md"));

        var s = await this.App.Key("Primary+F");
        s.Left.ShowQuickFilter.ShouldBeTrue();

        var filter = (await this.Fixture.Client.QueryAsync(type: "Entry")).First(x => x.IsVisible && x.Text == "" && x.Id != "CommandLine" && x.AutomationId != "CommandLine");
        await this.Fixture.Client.FillAsync(filter.Id, "*.cs");
        s = await this.App.WaitFor(x => x.Left.Names.Count == 2, "the filter to apply");
        s.Left.Names.ShouldBe(["a.cs", "b.cs"]);

        s = await this.App.Key("Escape");
        s.Left.ShowQuickFilter.ShouldBeFalse();
        s.Left.Names.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Clicking_a_column_header_sorts_and_again_reverses()
    {
        await this.Start(l => l.File("small.txt", "1").File("big.txt", "1234567890").File("mid.txt", "12345"));

        var s = await this.App.Command("panel.Sort", "Size");
        s.Left.Sort.ShouldBe("Size desc");
        s.Left.Names.ShouldBe(["big.txt", "mid.txt", "small.txt"]);

        s = await this.App.Command("panel.Sort", "Size");
        s.Left.Names.ShouldBe(["small.txt", "mid.txt", "big.txt"]);
    }

    [Fact]
    public async Task Branch_view_flattens_every_file_below()
    {
        await this.Start(l => l.File("top.txt").File("a/one.txt").File("a/b/two.txt"));

        var s = await this.App.Key("Primary+B");
        s.Left.IsBranchView.ShouldBeTrue();
        s.Left.Names.Order().ShouldBe(["one.txt", "top.txt", "two.txt"]);
    }
}
