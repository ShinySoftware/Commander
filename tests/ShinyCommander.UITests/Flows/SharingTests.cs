using System.Net;
using System.Text.Json;
using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// Serve Folder end to end in the real app: the command opens the Sharing window with the form filled in, saving it
/// serves the folder, the browser page and WebDAV answer, and the window shows the transfer while it runs. Screenshots
/// of the window go to <c>artifacts/sharing/</c>.
/// </summary>
public class SharingTests(AppFixture fixture) : UiTest(fixture)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    async Task Shot(string name)
    {
        await Task.Delay(700); // motion icons and the half-second refresh
        var page = (await this.Ui.All()).LastOrDefault(x => x.Type == "SharingPage" && x.Bounds is { Width: > 0 });
        page.ShouldNotBeNull("the Sharing window isn't in the visual tree");
        // DevFlow comes back empty for a page whose figures are moving - AppKit draws it instead
        var png = await this.Fixture.Client.ScreenshotAsync(elementId: page!.Id) ?? await this.App.SharingPng();
        png.ShouldNotBeNull("screenshot of the Sharing window failed");
        var dir = Path.Combine(TestConfig.ArtifactDir, "sharing");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, name + ".png"), png!);
    }

    async Task<JsonElement> Shares(int count)
    {
        for (var i = 0; i < 50; i++)
        {
            var result = await this.App.Sharing();
            if (result.GetProperty("shares").GetArrayLength() == count)
                return result;
            await Task.Delay(100);
        }
        throw new ShouldAssertException($"expected {count} share(s)");
    }

    [Fact]
    public async Task Serve_Folder_shares_the_folder_under_the_cursor()
    {
        await this.Start(t => t
            .File("Photos/beach.txt", "sand")
            .Bytes("Photos/video.bin", new byte[40_000_000])
            .Dir("Photos/Album"));

        try
        {
            await this.App.Cursor("Photos");
            await this.App.Command("ServeFolder");

            // the form opens filled in with the folder under the cursor
            var path = await this.Ui.ById("SharingFormPath");
            path.Text.ShouldBe(this.LeftTree["Photos"]);
            await this.Shot("form");

            await this.Ui.Tap("SharingFormSave");
            var state = await this.Shares(1);
            state.GetProperty("running").GetBoolean().ShouldBeTrue();
            var share = state.GetProperty("shares")[0];
            share.GetProperty("name").GetString().ShouldBe("Photos");

            // the page and WebDAV both answer
            var page = await Http.GetAsync(share.GetProperty("link").GetString());
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await Http.GetStringAsync(share.GetProperty("dav").GetString() + "beach.txt")).ShouldBe("sand");
            await this.Shot("served");

            // the main window: the Share group on the Files tab, and the chip by the command line
            var main = (await this.Ui.All()).LastOrDefault(x => x.Type == "MainPage" && x.Bounds is { Width: > 0 });
            if (main != null && await this.Fixture.Client.ScreenshotAsync(elementId: main.Id) is { } mainPng)
                await File.WriteAllBytesAsync(Path.Combine(TestConfig.ArtifactDir, "sharing", "main.png"), mainPng);
            (await this.Ui.ById("SharingChip")).ShouldNotBeNull();

            // a download read slowly, so it's still moving when the window is captured
            using var response = await Http.GetAsync(share.GetProperty("dav").GetString() + "video.bin", HttpCompletionOption.ResponseHeadersRead);
            await using var body = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[256 * 1024];
            for (var i = 0; i < 12; i++)
            {
                await body.ReadAsync(buffer);
                await Task.Delay(100);
            }
            await this.Shot("downloading");
        }
        finally
        {
            await this.App.Sharing(close: true);
        }

        var after = await this.App.Sharing();
        after.GetProperty("running").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Serve_Folder_refuses_a_folder_inside_an_archive()
    {
        await this.Start(t => t.Zip("bundle.zip", ("docs/a.txt", "A")));
        await this.App.Cursor("bundle.zip");
        await this.App.Key("Enter");
        await this.App.Command("ServeFolder");

        // nothing shared (a toast says only folders on this computer can be served)
        (await this.App.Sharing()).GetProperty("shares").GetArrayLength().ShouldBe(0);
        (await this.App.Sharing()).GetProperty("running").GetBoolean().ShouldBeFalse();
    }
}
