using System.Text.Json;

namespace ShinyCommander.UITests.Driver;

/// <summary>
/// Base for every UI test. Each test gets its own empty <see cref="LeftTree"/> / <see cref="RightTree"/> folders;
/// <see cref="Start"/> fills them and resets the app onto them (both panels, details view, nothing marked, no
/// dialogs pending, main page). On failure the state JSON, both panels' screenshots and the visual tree go to
/// <c>artifacts/&lt;test&gt;/</c>.
/// </summary>
[Collection(AppCollection.Name)]
public abstract class UiTest(AppFixture fixture) : IAsyncLifetime
{
    protected AppFixture Fixture { get; } = fixture;
    protected Commander App => this.Fixture.App;
    protected Ui Ui => new(this.Fixture.Client);
    protected TestTree LeftTree { get; private set; } = null!;
    protected TestTree RightTree { get; private set; } = null!;

    public virtual ValueTask InitializeAsync()
    {
        var name = Safe(TestContext.Current.Test?.TestDisplayName ?? this.GetType().Name);
        // the method name is the readable part; never start with '.' (that's a hidden folder)
        var shortName = (name.Length > 80 ? name[^80..] : name).TrimStart('.', '_');
        var dir = Path.Combine(TestConfig.WorkDir, shortName) + "-" + Guid.NewGuid().ToString("N")[..6];
        this.LeftTree = new TestTree(Path.Combine(dir, "left"));
        this.RightTree = new TestTree(Path.Combine(dir, "right"));
        return ValueTask.CompletedTask;
    }

    /// <summary>Fill the folders, then reset the app onto them. The left panel is active with the cursor on "..".</summary>
    protected Task<AppState> Start(Action<TestTree>? left = null, Action<TestTree>? right = null)
    {
        left?.Invoke(this.LeftTree);
        right?.Invoke(this.RightTree);
        return this.App.Reset(this.LeftTree.Path, this.RightTree.Path);
    }

    public async ValueTask DisposeAsync()
    {
        if (TestContext.Current.TestState?.Result is TestResult.Failed)
            await this.CaptureFailure(TestContext.Current.Test?.TestDisplayName ?? "unknown");
        GC.SuppressFinalize(this);
    }

    async Task CaptureFailure(string testName)
    {
        var dir = Path.Combine(TestConfig.ArtifactDir, "failures", Safe(testName));
        try
        {
            Directory.CreateDirectory(dir);
            await Try(async () =>
            {
                var state = await this.App.State();
                await File.WriteAllTextAsync(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions(AppState.Json) { WriteIndented = true }));
            });
            foreach (var id in new[] { "LeftPanel", "RightPanel" })
            {
                await Try(async () =>
                {
                    var element = (await this.Fixture.Client.QueryAsync(automationId: id)).FirstOrDefault();
                    if (element != null && await this.Fixture.Client.ScreenshotAsync(elementId: element.Id) is { } png)
                        await File.WriteAllBytesAsync(Path.Combine(dir, id + ".png"), png);
                });
            }
            await Try(async () =>
            {
                var tree = await this.Fixture.Client.GetTreeAsync();
                await File.WriteAllTextAsync(Path.Combine(dir, "visual-tree.json"), JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true }));
            });
            TestContext.Current.SendDiagnosticMessage($"[uitests] failure artifacts: {dir}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[uitests] could not capture failure artifacts: {ex.Message}");
        }

        static async Task Try(Func<Task> action)
        {
            try { await action(); } catch { }
        }
    }

    static string Safe(string name) => String.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
}
