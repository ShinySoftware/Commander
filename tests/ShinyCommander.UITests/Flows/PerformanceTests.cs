using System.Diagnostics;
using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// How long the UI thread is busy for everyday actions. <b>Known red</b> on the macOS head, so these are explicit
/// (run with <c>run.sh --explicit -t PerformanceTests</c>): opening a page (F1, F3) or coming back takes several
/// seconds, almost all of it AppKit detaching/attaching the main page's ~1000 NSViews while re-solving ~4000
/// Auto Layout constraints - each MAUI view's autoresizing mask translated, because the Shell's split view puts the
/// window's layout engine to work (state.NativeViewTypes shows the numbers). Turning translation off from the app
/// breaks layout (views end up zero-sized), so the fix belongs in the maui-labs AppKit containers.
/// </summary>
public class PerformanceTests(AppFixture fixture) : UiTest(fixture)
{
    async Task<long> Time(Func<Task> action)
    {
        var w = Stopwatch.StartNew();
        await action();
        return w.ElapsedMilliseconds;
    }

    [Fact(Explicit = true)]
    public async Task Opening_and_closing_the_keyboard_reference_is_quick()
    {
        await this.Start(l => { for (var i = 0; i < 40; i++) l.File($"f{i}.txt"); });

        var open = await this.Time(() => this.App.Key("F1"));
        var back = await this.Time(() => this.App.Key("Escape"));
        var st = await this.App.State();
        TestContext.Current.SendDiagnosticMessage($"[perf] F1 {open}ms, back {back}ms; {st.NativeViews} native views, {st.Shortcuts} shortcuts, {st.ManagedMb} MB: {st.NativeViewTypes}");
        open.ShouldBeLessThan(1500, "F1 should open the keyboard reference within 1.5s");
        back.ShouldBeLessThan(1500, "going back to the panels should take under 1.5s");
    }

    [Fact(Explicit = true)]
    public async Task Opening_the_lister_and_coming_back_is_quick()
    {
        await this.Start(l => l.File("a.txt", "text"));
        await this.App.Cursor("a.txt");

        var open = await this.Time(() => this.App.Key("F3"));
        var back = await this.Time(() => this.App.Key("Escape"));
        TestContext.Current.SendDiagnosticMessage($"[perf] F3 {open}ms, back {back}ms");
        open.ShouldBeLessThan(1500);
        back.ShouldBeLessThan(1500);
    }

    [Fact(Explicit = true)]
    public async Task Switching_tabs_is_quick()
    {
        await this.Start(l => l.Dir("sub"));
        await this.App.Cursor("sub");
        await this.App.Key("Primary+T");

        var next = await this.Time(() => this.App.Key("Ctrl+Tab"));
        TestContext.Current.SendDiagnosticMessage($"[perf] Ctrl+Tab {next}ms");
        next.ShouldBeLessThan(500);
    }
}
