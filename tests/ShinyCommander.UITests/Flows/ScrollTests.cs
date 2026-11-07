using System.Diagnostics;
using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>Scrolling a big folder: the list must stay virtualized and each scroll step must be quick.</summary>
/// <remarks>Uses the native view diagnostics (each test turns them on; the next reset turns them off).</remarks>
public class ScrollTests(AppFixture fixture) : UiTest(fixture)
{
    static void Files(TestTree t, int count)
    {
        for (var i = 0; i < count; i++)
            t.File($"file{i:0000}.txt", "x");
    }

    [Fact]
    public async Task A_big_folder_only_realizes_the_visible_rows()
    {
        await this.Start(l => Files(l, 10));
        var small = await this.App.Diagnostics();
        var baseline = small.NativeViews;

        this.LeftTree.Dir("big");
        Files(new TestTree(this.LeftTree["big"]), 2000);
        var s = await this.App.Navigate(this.LeftTree["big"]);
        TestContext.Current.SendDiagnosticMessage($"[scroll] 10 files: {baseline} views, 2000 files: {s.NativeViews} views");
        s.NativeViews.ShouldBeLessThan(baseline + 600, "rows outside the viewport are being realized");
    }

    [Fact]
    public async Task Scrolling_a_big_folder_is_quick()
    {
        await this.Start(l => Files(l, 2000));
        var list = await this.Ui.ById("LeftFiles");

        var steps = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            var w = Stopwatch.StartNew();
            (await this.Fixture.Client.ScrollAsync(list.Id, deltaY: 600, animated: false)).ShouldBeTrue();
            await this.App.State(); // round trip through the UI thread: done once it has caught up
            steps.Add(w.ElapsedMilliseconds);
        }
        var s = await this.App.State();
        TestContext.Current.SendDiagnosticMessage($"[scroll] 10 x 600pt steps: {String.Join(", ", steps)} ms; {s.NativeViews} views");
        steps.Skip(1).Average().ShouldBeLessThan(150, "each scroll step keeps the UI thread busy too long");
    }

    [Fact]
    public async Task Moving_the_cursor_through_a_big_folder_is_quick()
    {
        await this.Start(l => Files(l, 2000));

        // what one key call costs with nothing to do (the hook's own settle time) - subtracted below
        var w = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
            await this.App.Key("F12");
        var overhead = w.ElapsedMilliseconds / 5;

        w.Restart();
        for (var i = 0; i < 10; i++)
            await this.App.Key("Down");
        var down = w.ElapsedMilliseconds / 10 - overhead;

        w.Restart();
        for (var i = 0; i < 10; i++)
            await this.App.Key("PageDown");
        var page = w.ElapsedMilliseconds / 10 - overhead;

        var s = await this.App.State();
        TestContext.Current.SendDiagnosticMessage($"[scroll] Down {down} ms, PageDown {page} ms (after {overhead} ms call overhead); cursor {s.Left.Cursor}");
        s.Left.Cursor.ShouldBe(210);
        down.ShouldBeLessThan(80, "moving the cursor one row keeps the UI thread busy too long");
        page.ShouldBeLessThan(250, "paging the cursor keeps the UI thread busy too long");
    }
}
