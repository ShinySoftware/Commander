using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// A WebDAV server in a panel: open it, copy up with F5, and the Connections pages - with screenshots into
/// <c>artifacts/screens/remote-*.png</c>. Needs a server: <c>SC_UITEST_WEBDAV=host:port:user:password</c>
/// (e.g. wsgidav, see docs/testing.md), so it's skipped otherwise.
/// </summary>
public class RemoteTests(AppFixture fixture) : UiTest(fixture)
{
    static string? Server => Environment.GetEnvironmentVariable("SC_UITEST_WEBDAV");

    async Task Shot(string name)
    {
        await Task.Delay(600);
        var current = (await this.App.StateNow()).Page;
        var page = (await this.Ui.All()).LastOrDefault(x => x.Type == current && x.Bounds is { Width: > 0 });
        page.ShouldNotBeNull();
        var png = await this.Fixture.Client.ScreenshotAsync(elementId: page!.Id);
        var dir = Path.Combine(TestConfig.ArtifactDir, "screens");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"remote-{name}.png"), png!);
    }

    [Fact]
    public async Task WebDav_server_opens_in_a_panel_and_takes_an_F5_copy()
    {
        Assert.SkipWhen(Server == null, "set SC_UITEST_WEBDAV=host:port:user:password");
        var parts = Server!.Split(':');
        var folder = "uitest-" + Guid.NewGuid().ToString("N")[..6];

        await this.Start(l => l.File("upload me.txt", "from the left"), r => { });
        await this.App.Connection("WebDav", "Test DAV", parts[0], Int32.Parse(parts[1]), parts[2], parts[3]);
        var root = this.App.LastResult.GetString()!;
        try
        {
            // a folder of our own on the server, made the way F7 makes one
            var s = await this.App.Navigate(root, "right");
            s.Right.Path.ShouldBe(root);
            // the server's root has no ".." (like "/") - activate the right panel from the left
            await this.App.Cursor("upload me.txt", "left");
            await this.App.Command("SwitchPanel");
            await this.App.Answer(folder);
            s = await this.App.Key("F7");
            s.Right.Names.ShouldContain(folder);
            s = await this.App.Navigate(root.TrimEnd('/') + "/" + folder, "right");

            await this.App.Cursor("upload me.txt", "left");
            await this.App.Answer("<default>");
            s = await this.App.Key("F5");

            s.Right.Names.ShouldContain("upload me.txt");
            await this.Ui.Tap("ribbon-tab-go");
            await this.Shot("main");
            await this.Ui.Tap("ribbon-tab-files");

            await this.App.Command("Connections");
            await this.App.WaitFor(x => x.Page == "ConnectionsPage", "the Connections page");
            await Task.Delay(5000); // the Bonjour scan runs for 4s
            await this.Shot("connections");

            await this.Ui.Tap("NewConnection");
            await this.App.WaitFor(x => x.Page == "ConnectionEditPage", "the connection editor");
            await this.Shot("edit");
            await this.Ui.Tap("PageBack");
            await this.Ui.Tap("PageBack");
        }
        finally
        {
            await this.App.RemoveConnection("Test DAV");
        }
    }
}
