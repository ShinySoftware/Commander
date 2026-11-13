using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// The background jobs pane: operations run as jobs, finished ones offer Go To, running ones Stop, Clear Completed
/// keeps what's still going - and closing or quitting while one runs asks first. Also ⌘⌫, the Mac's delete key.
/// </summary>
public class JobTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task A_copy_is_listed_as_a_finished_job()
    {
        await this.Start(l => l.File("a.txt", "hi"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<default>");

        var s = await this.App.Key("F5");

        this.RightTree.Read("a.txt").ShouldBe("hi");
        var job = s.Jobs.ShouldHaveSingleItem();
        job.State.ShouldBe("Completed");
        job.Title.ShouldBe("Copy 'a.txt'");
        job.CanGoTo.ShouldBeTrue();
        job.Destination.ShouldBe(this.RightTree.Path);
        s.IsOperationRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task Go_to_opens_where_the_copy_went_in_the_other_panel_with_the_cursor_on_it()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"), r => r.Dir("elsewhere"));
        await this.App.Cursor("b.txt");
        await this.App.Answer("<default>");
        await this.App.Key("F5");

        // wander off: the right panel leaves the target, the left stays active
        await this.App.Navigate(Path.Combine(this.RightTree.Path, "elsewhere"), side: "right");
        await this.App.Cursor("a.txt", side: "left");

        var s = await this.App.Job("goto");

        s.ActiveSide.ShouldBe("right");
        s.Right.Path.ShouldBe(this.RightTree.Path);
        s.Right.CursorName.ShouldBe("b.txt");
        s.Left.Path.ShouldBe(this.LeftTree.Path); // the source stays open beside it
    }

    [Fact]
    public async Task Go_to_after_a_rename_style_copy_lands_on_the_new_name()
    {
        await this.Start(l => l.File("a.txt", "x"));
        await this.App.Cursor("a.txt");
        await this.App.Answer(Path.Combine(this.RightTree.Path, "renamed.txt"));
        await this.App.Key("F5");

        var s = await this.App.Job("goto");

        s.Right.CursorName.ShouldBe("renamed.txt");
    }

    [Fact]
    public async Task Stop_ends_a_running_job()
    {
        await this.Start();
        var s = await this.App.HoldJob();
        s.Jobs.ShouldHaveSingleItem().State.ShouldBe("Running");

        s = await this.App.Job("stop");

        s.Jobs.ShouldHaveSingleItem().State.ShouldBe("Cancelled");
        s.IsOperationRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task Clear_completed_keeps_the_jobs_still_running()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("<default>");
        await this.App.Key("F5");
        await this.App.HoldJob();

        var s = await this.App.Job("clear");

        s.Jobs.ShouldHaveSingleItem().State.ShouldBe("Running");
        await this.App.Job("stop");
    }

    [Fact]
    public async Task Dismiss_removes_one_finished_job()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            await this.App.Cursor(name);
            await this.App.Answer("<default>");
            await this.App.Key("F5");
        }

        var s = await this.App.Job("dismiss", index: 0); // newest first

        s.Jobs.ShouldHaveSingleItem().Title.ShouldBe("Copy 'a.txt'");
    }

    [Fact]
    public async Task A_failed_job_opens_the_pane_and_says_why()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Answer(this.LeftTree.Path + "/"); // onto itself

        var s = await this.App.Key("F5");

        var job = s.Jobs.ShouldHaveSingleItem();
        job.State.ShouldBe("Failed");
        job.CanGoTo.ShouldBeFalse();
        job.Errors.ShouldHaveSingleItem().ShouldContain("onto itself");
        s.ShowJobs.ShouldBeTrue();
        s.Dialogs.ShouldContain(d => d.Kind == "toast" && d.Message!.Contains("onto itself"));
    }

    [Fact]
    public async Task Primary_J_toggles_the_pane_and_Escape_hides_it()
    {
        await this.Start();

        (await this.App.Key("Primary+J")).ShowJobs.ShouldBeTrue();
        (await this.App.Key("Primary+J")).ShowJobs.ShouldBeFalse();
        await this.App.Key("Primary+J");
        (await this.App.Key("Escape")).ShowJobs.ShouldBeFalse();
    }

    [Fact]
    public async Task Escape_hides_the_pane_but_the_job_keeps_running()
    {
        await this.Start();
        await this.App.HoldJob();
        await this.App.Key("Primary+J");

        var s = await this.App.Key("Escape");

        s.ShowJobs.ShouldBeFalse();
        s.Jobs.ShouldHaveSingleItem().State.ShouldBe("Running");
        await this.App.Job("stop");
    }

    [Fact]
    public async Task Exit_while_a_job_runs_asks_and_no_keeps_it_running()
    {
        await this.Start();
        await this.App.HoldJob();
        await this.App.Answer("no");

        var s = await this.App.Command("Exit");

        s.Dialogs.ShouldContain(d => d.Kind == "confirm" && d.Title == "Jobs still running" && d.Message!.Contains("Test hold"));
        s.Jobs.ShouldHaveSingleItem().State.ShouldBe("Running");
        await this.App.Job("stop");
    }

    [Fact]
    public async Task The_close_button_while_a_job_runs_asks_and_no_keeps_the_window()
    {
        await this.Start();
        await this.App.HoldJob();
        await this.App.Answer("no");

        var s = await this.App.CloseWindow();

        this.App.LastResult.GetProperty("closing").GetBoolean().ShouldBeFalse();
        s.Dialogs.ShouldContain(d => d.Kind == "confirm" && d.Title == "Jobs still running");
        s.Jobs.ShouldHaveSingleItem().State.ShouldBe("Running");
        await this.App.Job("stop");
    }

    [Fact]
    public async Task Primary_Backspace_deletes_when_the_command_line_is_empty()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("yes");

        var s = await this.App.Key("Primary+Backspace");

        this.LeftTree.Names().ShouldBe(["b.txt"]);
        s.Dialogs.ShouldContain(d => d.Kind == "confirm");
    }

    [Fact]
    public async Task Primary_Backspace_stays_with_the_command_line_while_it_has_text()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Type("echo hi");

        var s = await this.App.Key("Primary+Backspace");

        this.LeftTree.Exists("a.txt").ShouldBeTrue();
        s.Dialogs.ShouldNotContain(d => d.Kind == "confirm");
        this.App.LastKeyHandled.ShouldBeFalse();
    }

    [Fact]
    public async Task Primary_Alt_Backspace_deletes_permanently()
    {
        await this.Start(l => l.File("a.txt"));
        await this.App.Cursor("a.txt");
        await this.App.Answer("yes");

        var s = await this.App.Key("Primary+Alt+Backspace");

        this.LeftTree.Exists("a.txt").ShouldBeFalse();
        s.Dialogs.ShouldContain(d => d.Kind == "confirm" && d.Title == "Delete permanently");
    }
}
