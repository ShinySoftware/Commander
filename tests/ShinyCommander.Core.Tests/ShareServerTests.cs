using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ShinyCommander.Share;
using ShinyCommander.Share.Contracts;

namespace ShinyCommander.Core.Tests;

/// <summary>The folder server over a real loopback socket: access rules, keys, paths, uploads and the monitor.</summary>
public sealed class ShareServerTests : IAsyncLifetime
{
    readonly TempDir tmp = new();
    readonly ShareServer server = new();
    HttpClient client = null!;
    CookieContainer cookies = null!;

    public ValueTask InitializeAsync()
    {
        // port 0: the OS picks a free one, so test runs never collide
        this.server.Settings = new ShareServerSettings { Port = 0 };
        this.NewClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        this.client.Dispose();
        await this.server.DisposeAsync();
        this.tmp.Dispose();
    }

    void NewClient()
    {
        this.cookies = new CookieContainer();
        this.client = new HttpClient(new HttpClientHandler { CookieContainer = this.cookies, AllowAutoRedirect = false });
    }

    async Task<ServedFolder> Share(ServeOptions? options = null)
    {
        var share = await this.server.AddAsync(options ?? new ServeOptions { RootPath = this.tmp.Path });
        this.client.BaseAddress ??= new Uri($"http://127.0.0.1:{this.server.Port}/");
        return share;
    }

    static string Q(string value) => Uri.EscapeDataString(value);

    async Task<ShareListing?> List(ServedFolder share, string path = "")
        => await this.client.GetFromJsonAsync<ShareListing>($"api/s/{share.Id}/list?path={Q(path)}");

    [Fact]
    public async Task A_share_lists_its_folder_and_serves_its_files()
    {
        this.tmp.File("readme.txt", "hello world");
        this.tmp.File("docs/a.md", "a");
        var share = await this.Share();

        var info = await this.client.GetFromJsonAsync<ShareInfo>($"api/s/{share.Id}");
        info!.Name.ShouldBe(Path.GetFileName(this.tmp.Path));
        info.NeedsKey.ShouldBeFalse();
        info.Access.ShouldBe(ShareAccess.ReadOnly);

        var listing = await this.List(share);
        listing!.Entries.Select(x => x.Name).ShouldBe(["docs", "readme.txt"]);
        listing.Entries[0].IsDirectory.ShouldBeTrue();

        (await this.client.GetStringAsync($"dav/{share.Id}/readme.txt")).ShouldBe("hello world");
    }

    [Fact]
    public async Task A_download_is_counted_and_shown_as_finished()
    {
        this.tmp.File("big.bin", new string('x', 200_000));
        var share = await this.Share();

        var bytes = await this.client.GetByteArrayAsync($"dav/{share.Id}/big.bin");
        bytes.Length.ShouldBe(200_000);

        await WaitFor(() => this.server.Monitor.Finished.Count == 1);
        var done = this.server.Monitor.Finished[0];
        done.State.ShouldBe(TransferState.Completed);
        done.Direction.ShouldBe(TransferDirection.Download);
        done.Bytes.ShouldBe(200_000);
        done.Name.ShouldBe("big.bin");
        share.FilesDown.ShouldBe(1);
        this.server.Monitor.Activity.ShouldContain(x => x.Kind == ActivityKind.Download && x.Path == "big.bin");
    }

    [Fact]
    public async Task An_unknown_share_is_not_found()
    {
        await this.Share();
        var response = await this.client.GetAsync("api/s/nosuchshare/list");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Paths_never_leave_the_share()
    {
        var outside = new TempDir();
        outside.File("secret.txt", "secret");
        this.tmp.File("ok.txt");
        var share = await this.Share();

        (await this.client.GetAsync($"api/s/{share.Id}/list?path={Q("..")}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await this.client.GetAsync($"api/s/{share.Id}/list?path={Q("../" + Path.GetFileName(outside.Path))}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await this.client.GetAsync($"api/s/{share.Id}/zip?path={Q("..")}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        outside.Dispose();
    }

    [Fact]
    public async Task A_link_out_of_the_share_is_neither_listed_nor_followed()
    {
        var outside = new TempDir();
        outside.File("secret.txt", "secret");
        this.tmp.File("ok.txt");
        Directory.CreateSymbolicLink(Path.Combine(this.tmp.Path, "escape"), outside.Path);
        var share = await this.Share();

        var listing = await this.List(share);
        listing!.Entries.Select(x => x.Name).ShouldBe(["ok.txt"]);
        (await this.client.GetAsync($"api/s/{share.Id}/list?path=escape")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await this.client.GetAsync($"dav/{share.Id}/escape/secret.txt")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        outside.Dispose();
    }

    [Fact]
    public async Task Hidden_files_are_left_out_unless_the_share_shows_them()
    {
        this.tmp.File(".env", "x");
        this.tmp.File("seen.txt");
        var hidden = await this.Share();
        (await this.List(hidden))!.Entries.Select(x => x.Name).ShouldBe(["seen.txt"]);
        (await this.client.GetAsync($"dav/{hidden.Id}/.env")).IsSuccessStatusCode.ShouldBeFalse();

        var shown = await this.Share(new ServeOptions { RootPath = this.tmp.Path, ShowHidden = true });
        (await this.List(shown))!.Entries.Select(x => x.Name).ShouldBe([".env", "seen.txt"]);
    }

    // ------------------------------------------------------------------------------------------- keys

    [Fact]
    public async Task A_keyed_share_answers_nothing_but_its_description_until_the_key_is_given()
    {
        this.tmp.File("a.txt");
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Key = "open-sesame" });

        var info = await this.client.GetFromJsonAsync<ShareInfo>($"api/s/{share.Id}");
        info!.NeedsKey.ShouldBeTrue();
        (await this.client.GetAsync($"api/s/{share.Id}/list")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var dav = await this.client.GetAsync($"dav/{share.Id}/a.txt");
        dav.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        dav.Headers.WwwAuthenticate.ShouldContain(x => x.Scheme == "Basic");

        (await this.client.PostAsJsonAsync($"api/s/{share.Id}/key", new ShareKeyRequest("wrong"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await this.client.PostAsJsonAsync($"api/s/{share.Id}/key", new ShareKeyRequest("open-sesame"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // the cookie carries it from now on
        (await this.client.GetAsync($"api/s/{share.Id}/list")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await this.client.GetFromJsonAsync<ShareInfo>($"api/s/{share.Id}"))!.NeedsKey.ShouldBeFalse();
        this.server.Monitor.Activity.ShouldContain(x => x.Kind == ActivityKind.KeyRefused);
        this.server.Monitor.Activity.ShouldContain(x => x.Kind == ActivityKind.KeyAccepted);
    }

    [Fact]
    public async Task A_key_in_the_link_signs_the_browser_in()
    {
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Key = "k1" });
        (await this.client.GetAsync($"api/s/{share.Id}?key=k1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await this.client.GetAsync($"api/s/{share.Id}/list")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_WebDAV_client_gives_the_key_as_its_password()
    {
        this.tmp.File("a.txt", "via dav");
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Key = "k2" });

        var request = new HttpRequestMessage(HttpMethod.Get, $"dav/{share.Id}/a.txt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("anyone:k2")));
        var response = await this.client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("via dav");
    }

    [Fact]
    public async Task Changing_the_key_signs_everyone_out()
    {
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Key = "old" });
        await this.client.PostAsJsonAsync($"api/s/{share.Id}/key", new ShareKeyRequest("old"));
        (await this.client.GetAsync($"api/s/{share.Id}/list")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await this.server.UpdateAsync(share, share.Options with { Key = "new" });
        (await this.client.GetAsync($"api/s/{share.Id}/list")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ten_wrong_keys_lock_the_address_out()
    {
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Key = "right" });
        for (var i = 0; i < 10; i++)
            await this.client.PostAsJsonAsync($"api/s/{share.Id}/key", new ShareKeyRequest("wrong" + i));

        (await this.client.PostAsJsonAsync($"api/s/{share.Id}/key", new ShareKeyRequest("right"))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_blocked_address_gets_nothing()
    {
        var share = await this.Share();
        this.server.Block("127.0.0.1");
        (await this.client.GetAsync($"api/s/{share.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        this.server.Unblock("127.0.0.1");
        (await this.client.GetAsync($"api/s/{share.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------------------------------- uploads and changes

    Task<HttpResponseMessage> Upload(ServedFolder share, string name, string content, string path = "", bool overwrite = false)
        => this.client.PutAsync($"api/s/{share.Id}/upload?path={Q(path)}&name={Q(name)}&overwrite={(overwrite ? "true" : "false")}", new StringContent(content));

    [Fact]
    public async Task A_read_only_share_refuses_uploads_and_changes()
    {
        this.tmp.File("a.txt");
        var share = await this.Share();
        (await this.Upload(share, "new.txt", "x")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await this.client.PostAsJsonAsync($"api/s/{share.Id}/folder", new ShareFolderRequest("", "f"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await this.client.DeleteAsync($"api/s/{share.Id}/item?path=a.txt")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await this.client.PutAsync($"dav/{share.Id}/dav.txt", new StringContent("x"))).IsSuccessStatusCode.ShouldBeFalse();
        File.Exists(Path.Combine(this.tmp.Path, "a.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(this.tmp.Path, "new.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task An_upload_with_a_taken_name_keeps_both()
    {
        this.tmp.File("a.txt", "original");
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Access = ShareAccess.ReadWrite });

        var response = await this.Upload(share, "a.txt", "second");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ShareUploaded>())!.Name.ShouldBe("a (2).txt");
        File.ReadAllText(Path.Combine(this.tmp.Path, "a.txt")).ShouldBe("original");
        File.ReadAllText(Path.Combine(this.tmp.Path, "a (2).txt")).ShouldBe("second");

        await WaitFor(() => this.server.Monitor.FilesUp == 1);
        this.server.Monitor.Finished[0].Direction.ShouldBe(TransferDirection.Upload);
        // nothing half written left beside it
        Directory.GetFiles(this.tmp.Path, "*.part", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Overwrite_rename_and_delete_need_a_share_that_allows_changes()
    {
        this.tmp.File("a.txt", "original");
        var plain = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Access = ShareAccess.ReadWrite });
        (await this.Upload(plain, "a.txt", "replaced", overwrite: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(this.tmp.Path, "a.txt")).ShouldBe("original");
        (await this.client.PostAsJsonAsync($"api/s/{plain.Id}/rename", new ShareRenameRequest("a.txt", "b.txt"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var changes = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Access = ShareAccess.ReadWrite, AllowModify = true });
        (await this.Upload(changes, "a.txt", "replaced", overwrite: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(this.tmp.Path, "a.txt")).ShouldBe("replaced");

        (await this.client.PostAsJsonAsync($"api/s/{changes.Id}/rename", new ShareRenameRequest("a.txt", "b.txt"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(this.tmp.Path, "b.txt")).ShouldBeTrue();

        (await this.client.DeleteAsync($"api/s/{changes.Id}/item?path=b.txt")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(this.tmp.Path, "b.txt")).ShouldBeFalse();

        (await this.client.PostAsJsonAsync($"api/s/{changes.Id}/folder", new ShareFolderRequest("", "made"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Directory.Exists(Path.Combine(this.tmp.Path, "made")).ShouldBeTrue();
        this.server.Monitor.Activity.ShouldContain(x => x.Kind == ActivityKind.Rename);
        this.server.Monitor.Activity.ShouldContain(x => x.Kind == ActivityKind.Delete);
    }

    [Fact]
    public async Task A_drop_box_takes_files_but_shows_nothing()
    {
        this.tmp.File("private.txt");
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Access = ShareAccess.UploadOnly });

        (await this.client.GetAsync($"api/s/{share.Id}/list")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await this.client.GetAsync($"dav/{share.Id}/private.txt")).IsSuccessStatusCode.ShouldBeFalse();
        (await this.client.GetAsync($"api/s/{share.Id}/zip")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await this.Upload(share, "dropped.txt", "here")).StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(this.tmp.Path, "dropped.txt")).ShouldBe("here");
    }

    [Fact]
    public async Task An_upload_over_the_limit_is_refused()
    {
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Access = ShareAccess.ReadWrite, MaxUploadBytes = 10 });
        (await this.Upload(share, "big.txt", new string('x', 100))).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        File.Exists(Path.Combine(this.tmp.Path, "big.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_resumable_upload_lands_in_the_share()
    {
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, Access = ShareAccess.ReadWrite });
        this.tmp.Dir("in");
        var content = Encoding.UTF8.GetBytes(new string('z', 5000));

        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        var create = new HttpRequestMessage(HttpMethod.Post, $"api/s/{share.Id}/tus");
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", content.Length.ToString());
        create.Headers.Add("Upload-Metadata", $"filename {B64("big.bin")},path {B64("in")}");
        var created = await this.client.SendAsync(create);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var location = created.Headers.Location!;

        // two chunks, as a browser sends them
        long offset = 0;
        foreach (var size in new[] { 3000, 2000 })
        {
            var patch = new HttpRequestMessage(HttpMethod.Patch, location);
            patch.Headers.Add("Tus-Resumable", "1.0.0");
            patch.Headers.Add("Upload-Offset", offset.ToString());
            patch.Content = new ByteArrayContent(content, (int)offset, size);
            patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
            var sent = await this.client.SendAsync(patch);
            sent.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            offset += size;
        }

        File.ReadAllBytes(Path.Combine(this.tmp.Path, "in", "big.bin")).ShouldBe(content);
        await WaitFor(() => this.server.Monitor.FilesUp == 1);
        this.server.Monitor.Finished[0].Bytes.ShouldBe(5000);
    }

    // ------------------------------------------------------------------------------------------- zip, limits, ends

    [Fact]
    public async Task A_folder_downloads_as_one_zip()
    {
        this.tmp.File("pics/a.jpg", "jpeg");
        this.tmp.File("pics/sub/b.txt", "text");
        var share = await this.Share();

        var bytes = await this.client.GetByteArrayAsync($"api/s/{share.Id}/zip?path=pics");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        zip.Entries.Select(x => x.FullName).OrderBy(x => x).ShouldBe(["a.jpg", "sub/b.txt"]);
        using var reader = new StreamReader(zip.GetEntry("sub/b.txt")!.Open());
        reader.ReadToEnd().ShouldBe("text");
    }

    [Fact]
    public async Task A_share_ends_once_its_download_limit_is_reached()
    {
        this.tmp.File("once.txt", "x");
        this.tmp.File("other.txt", "y");
        var keep = await this.Share();
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, MaxDownloads = 1 });
        ServedFolder? ended = null;
        this.server.ShareEnded += (_, s) => ended = s;

        await this.client.GetStringAsync($"dav/{share.Id}/once.txt");
        await WaitFor(() => share.FilesDown == 1);
        await this.server.SweepAsync();

        ended.ShouldBe(share);
        this.server.Shares.ShouldBe([keep]);
        (await this.client.GetAsync($"api/s/{share.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Removing_the_last_share_stops_the_server()
    {
        var share = await this.Share();
        this.server.IsRunning.ShouldBeTrue();
        await this.server.RemoveAsync(share);
        this.server.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task A_bandwidth_limit_slows_a_download_down()
    {
        this.tmp.File("slow.bin", new string('x', 60_000));
        var share = await this.Share(new ServeOptions { RootPath = this.tmp.Path, BandwidthLimit = 100_000 });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await this.client.GetByteArrayAsync($"dav/{share.Id}/slow.bin");
        // 60 KB at 100 KB/s with a 25 KB burst: at least ~0.35s
        watch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task The_share_page_is_the_browser_app()
    {
        var share = await this.Share();
        this.server.HasWebApp.ShouldBeTrue("build without -p:SkipFrontendBuild=true");

        var page = await this.client.GetAsync($"s/{share.Id}");
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("blazor.webassembly.js");
        (await this.client.GetAsync("_framework/blazor.webassembly.js")).StatusCode.ShouldBe(HttpStatusCode.OK);
        // the SPA fallback leaves the API alone
        (await this.client.GetAsync("api/s/nosuchshare/list")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void A_QR_code_has_a_square_matrix()
    {
        var qr = QrMatrix.Create("http://192.168.1.20:8080/s/abcdefghij?key=k7mp-2xqa-9fhd");
        qr.Size.ShouldBeGreaterThan(20);
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        condition().ShouldBeTrue();
    }
}
