using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// Doing something and undoing it must leave the window as it was. Page changes slowed from ~2s on a fresh launch to
/// 10s+ after an hour of use, so each test repeats one round trip and checks the native view count comes back.
/// </summary>
public class LeakTests(AppFixture fixture) : UiTest(fixture)
{
    async Task RoundTripLeavesNoViewsBehind(string what, Func<Task> roundTrip, int rounds = 4)
    {
        await this.Start(l => { for (var i = 0; i < 20; i++) l.File($"f{i:00}.txt", "x"); l.Dir("sub"); });
        await roundTrip(); // first time may build caches
        var baseline = (await this.App.State()).NativeViews;

        for (var i = 0; i < rounds; i++)
            await roundTrip();

        var after = await this.App.State();
        TestContext.Current.SendDiagnosticMessage($"[leak] {what}: {baseline} → {after.NativeViews} native views after {rounds} more rounds; {after.NativeViewTypes}");
        after.NativeViews.ShouldBeLessThanOrEqualTo(baseline + 10, $"{what} leaves native views behind ({baseline} → {after.NativeViews} after {rounds} rounds)");
    }

    [Fact]
    public Task Lister_open_and_close() => this.RoundTripLeavesNoViewsBehind("F3 / Escape", async () =>
    {
        await this.App.Cursor("f01.txt");
        await this.App.Key("F3");
        await this.App.Key("Escape");
    });

    [Fact]
    public Task Help_open_and_close() => this.RoundTripLeavesNoViewsBehind("F1 / Escape", async () =>
    {
        await this.App.Key("F1");
        await this.App.Key("Escape");
    });

    [Fact]
    public Task Tab_open_and_close() => this.RoundTripLeavesNoViewsBehind("Primary+T / Primary+W", async () =>
    {
        await this.App.Cursor("sub");
        await this.App.Key("Primary+T");
        await this.App.Key("Primary+W");
    });

    [Fact]
    public Task Navigate_in_and_out() => this.RoundTripLeavesNoViewsBehind("Enter / Backspace", async () =>
    {
        await this.App.Cursor("sub");
        await this.App.Key("Enter");
        await this.App.Key("Backspace");
    });

    [Fact]
    public Task Thumbnails_on_and_off() => this.RoundTripLeavesNoViewsBehind("thumbnails / details", async () =>
    {
        await this.App.Key("Primary+Shift+F1");
        await this.App.Key("Primary+F2");
    });

    [Fact]
    public Task Quick_view_on_and_off() => this.RoundTripLeavesNoViewsBehind("Ctrl+Q twice", async () =>
    {
        await this.App.Cursor("f01.txt");
        await this.App.Key("Ctrl+Q");
        await this.App.Key("Ctrl+Q");
    });

    [Fact]
    public Task Real_dialog_open_and_cancel() => this.RoundTripLeavesNoViewsBehind("real prompt / Cancel", async () =>
    {
        await this.App.RealDialogs();
        await this.App.Cursor("f01.txt");
        await this.App.Key("F2");
        await this.Ui.ClickInDialog("Cancel");
    });

    [Fact]
    public Task Reset_between_tests() => this.RoundTripLeavesNoViewsBehind("reset", async () =>
    {
        await this.App.Reset(this.LeftTree.Path, this.RightTree.Path);
    });
}
