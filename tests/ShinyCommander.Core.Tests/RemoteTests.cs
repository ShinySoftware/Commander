using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Models;
using ShinyCommander.Core.Operations;
using ShinyCommander.Core.Remote;
using ShinyCommander.Core.Util;
using ShinyCommander.Remote;

namespace ShinyCommander.Core.Tests;

public class RemotePathTests
{
    [Theory]
    [InlineData("sftp://me@host/var/www", true)]
    [InlineData("S3://work", true)]
    [InlineData("C:\\stuff", false)]
    [InlineData("/Users/me", false)]
    [InlineData("file:///Users/me", false)]
    [InlineData("", false)]
    public void DetectsRemote(string path, bool expected) => RemotePath.IsRemote(path).ShouldBe(expected);

    [Theory]
    [InlineData("SFTP://me@host", "sftp://me@host/")]
    [InlineData("sftp://me@host//var//www/", "sftp://me@host/var/www")]
    [InlineData("sftp://me@host/var/./www/../log", "sftp://me@host/var/log")]
    [InlineData("s3://work\\bucket\\a", "s3://work/bucket/a")]
    public void Normalizes(string path, string expected) => RemotePath.Normalize(path).ShouldBe(expected);

    [Fact]
    public void WalksUp()
    {
        RemotePath.GetParent("sftp://me@host/var/www").ShouldBe("sftp://me@host/var");
        RemotePath.GetParent("sftp://me@host/var").ShouldBe("sftp://me@host/");
        RemotePath.GetParent("sftp://me@host/").ShouldBeNull();
        RemotePath.GetName("sftp://me@host/var/www").ShouldBe("www");
        RemotePath.GetName("sftp://me@host/").ShouldBe("me@host");
        RemotePath.Combine("sftp://me@host/", "a/b").ShouldBe("sftp://me@host/a/b");
        RemotePath.CombineInner("/", "a").ShouldBe("/a");
    }

    [Fact]
    public void VfsHelpersHandleBothKinds()
    {
        VirtualFileSystem.GetDirectory("dav://host/a/b").ShouldBe("dav://host/a");
        VirtualFileSystem.Combine("dav://host/a", "c").ShouldBe("dav://host/a/c");
        VirtualFileSystem.GetName("dav://host/a/b.txt").ShouldBe("b.txt");
        VirtualFileSystem.IsAbsolute("dav://host/a").ShouldBeTrue();
    }

    [Fact]
    public void ConnectionRoots()
    {
        new RemoteConnection { Name = "Box", Protocol = RemoteProtocol.Sftp, Host = "NAS.local", UserName = "me" }.Root.ShouldBe("sftp://me@nas.local");
        new RemoteConnection { Name = "Box", Protocol = RemoteProtocol.Ftp, Host = "h", Port = 2121 }.Root.ShouldBe("ftp://h:2121");
        new RemoteConnection { Name = "Box", Protocol = RemoteProtocol.WebDavs, Host = "h", Port = 443 }.Root.ShouldBe("davs://h");
        new RemoteConnection { Name = "Work Logs!", Protocol = RemoteProtocol.S3 }.Root.ShouldBe("s3://work-logs");
        new RemoteConnection { Name = "x", Protocol = RemoteProtocol.AzureBlob, Host = "Acct" }.Root.ShouldBe("az://acct");
        new RemoteConnection { Name = "x", Protocol = RemoteProtocol.Sftp, Host = "h", StartPath = "/var/www/" }.StartUri.ShouldBe("sftp://h/var/www");
    }

    [Fact]
    public void ConnectionsRoundTrip()
    {
        var list = new[] { new RemoteConnection { Name = "a", Protocol = RemoteProtocol.S3, Region = "eu-west-1" } };
        var back = RemoteConnection.Deserialize(RemoteConnection.Serialize(list));
        back.Single().ShouldBe(list[0]);
        RemoteConnection.Deserialize("not json").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/", "", "")]
    [InlineData("/bucket", "bucket", "")]
    [InlineData("/bucket/a/b", "bucket", "a/b")]
    public void ObjectPaths(string inner, string bucket, string key)
    {
        var p = ObjectPath.Parse(inner);
        p.Bucket.ShouldBe(bucket);
        p.Key.ShouldBe(key);
    }
}

public class RemoteOperationTests
{
    readonly MemoryRemote server = new();
    readonly RemoteConnection connection = new() { Name = "box", Protocol = RemoteProtocol.Sftp, Host = "box", UserName = "me" };
    readonly VirtualFileSystem vfs;
    const string Root = "sftp://me@box";

    public RemoteOperationTests()
    {
        var sessions = new RemoteSessions(new MemorySecretStore(), [this.server]);
        sessions.Secrets.Set(this.connection.Id, "pw");
        sessions.SetConnections([this.connection]);
        this.vfs = new VirtualFileSystem(sessions);
    }

    FileOperationService Ops => new(this.vfs);

    async Task<FileEntry> Get(string dir, string name) => (await this.vfs.ListAsync(dir, true)).Single(x => x.Name == name);

    [Fact]
    public async Task ListsWithParentLink()
    {
        this.server.AddFile("/docs/a.txt", "A");
        var list = await this.vfs.ListAsync(Root + "/docs", true);
        list[0].IsParentLink.ShouldBeTrue();
        list[0].FullPath.ShouldBe(Root + "/");
        var file = list.Single(x => x.Name == "a.txt");
        file.IsRemote.ShouldBeTrue();
        file.FullPath.ShouldBe(Root + "/docs/a.txt");
        file.Size.ShouldBe(1);
    }

    [Fact]
    public void UnknownServerIsAnError()
        => Should.Throw<DirectoryNotFoundException>(() => this.vfs.Resolve("sftp://someone@else/x"));

    [Fact]
    public async Task UploadsFolder()
    {
        using var tmp = new TempDir();
        tmp.File("src/folder/a.txt", "A");
        tmp.File("src/folder/sub/b.txt", "BB");

        var result = await this.Ops.CopyAsync([await Get(tmp.Path + "/src", "folder")], new OperationTarget(Root + "/up"));

        result.Errors.ShouldBeEmpty();
        this.server.Read("/up/folder/sub/b.txt").ShouldBe("BB");
        this.server.Read("/up/folder/a.txt").ShouldBe("A");
    }

    [Fact]
    public async Task DownloadsFolder()
    {
        using var tmp = new TempDir();
        this.server.AddFile("/site/index.html", "<h1/>");
        this.server.AddFile("/site/css/app.css", "body{}");

        var result = await this.Ops.CopyAsync([await Get(Root + "/", "site")], new OperationTarget(tmp.Path));

        result.Errors.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(tmp.Path, "site", "css", "app.css")).ShouldBe("body{}");
    }

    [Fact]
    public async Task MoveDownDeletesFromServer()
    {
        using var tmp = new TempDir();
        this.server.AddFile("/site/index.html", "x");

        await this.Ops.MoveAsync([await Get(Root + "/", "site")], new OperationTarget(tmp.Path));

        File.Exists(Path.Combine(tmp.Path, "site", "index.html")).ShouldBeTrue();
        this.server.Has("/site").ShouldBeFalse();
    }

    [Fact]
    public async Task MoveOnServerIsARename()
    {
        this.server.AddFile("/a/x.txt", "x");
        this.server.AddFile("/b/keep.txt", "k");

        await this.Ops.MoveAsync([await Get(Root + "/a", "x.txt")], new OperationTarget(Root + "/b"));

        this.server.Renames.ShouldBe(1);
        this.server.Read("/b/x.txt").ShouldBe("x");
        this.server.Has("/a/x.txt").ShouldBeFalse();
    }

    [Fact]
    public async Task UploadConflictKeepBoth()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt", "new");
        this.server.AddFile("/a.txt", "old");

        await this.Ops.CopyAsync([await Get(tmp.Path, "a.txt")], new OperationTarget(Root + "/"), (_, _) => Task.FromResult(ConflictAction.KeepBoth));

        this.server.Read("/a.txt").ShouldBe("old");
        this.server.Read("/a (2).txt").ShouldBe("new");
    }

    [Fact]
    public async Task CreatesRenamesAndDeletes()
    {
        await this.Ops.CreateDirectoryAsync(Root + "/", "x/y");
        this.server.Has("/x/y").ShouldBeTrue();

        await this.Ops.CreateFileAsync(Root + "/x", "n.txt");
        this.server.Read("/x/n.txt").ShouldBe(String.Empty);

        await this.Ops.RenameAsync(await Get(Root + "/x", "n.txt"), "m.txt");
        this.server.Has("/x/m.txt").ShouldBeTrue();

        await this.Ops.DeleteAsync([await Get(Root + "/", "x")], useTrash: true);
        this.server.Has("/x").ShouldBeFalse();
    }

    [Fact]
    public async Task ResolvesTypedTargetsOnServer()
    {
        this.server.AddFile("/dir/a.txt", "a");
        this.Ops.ResolveTarget("dir", Root + "/", 1).ShouldBe(new OperationTarget(Root + "/dir"));
        this.Ops.ResolveTarget("dir/new.txt", Root + "/", 1).ShouldBe(new OperationTarget(Root + "/dir", "new.txt"));
        this.Ops.ResolveTarget(Root + "/dir", "/tmp", 1).ShouldBe(new OperationTarget(Root + "/dir"));
        (await this.vfs.CalculateRemoteFolderSizeAsync(Root + "/")).ShouldBe(1);
    }

    [Fact]
    public async Task AsksForPasswordAndRetriesWhenRefused()
    {
        var server = new MemoryRemote { ExpectedSecret = "right" };
        var sessions = new RemoteSessions(new MemorySecretStore(), [server]);
        sessions.SetConnections([this.connection]);
        var asked = new List<string?>();
        sessions.SecretRequested = (_, error) =>
        {
            asked.Add(error);
            return Task.FromResult<(string, bool)?>((asked.Count == 1 ? "wrong" : "right", true));
        };

        await new VirtualFileSystem(sessions).ListAsync(Root + "/", true);

        asked.Count.ShouldBe(2);
        asked[1].ShouldBe("bad password");
        sessions.Secrets.Get(this.connection.Id).ShouldBe("right");
    }

    [Fact]
    public void SyncPathNeverPrompts()
    {
        var sessions = new RemoteSessions(new MemorySecretStore(), [new MemoryRemote()]);
        sessions.SetConnections([this.connection]);
        sessions.SecretRequested = (_, _) => throw new InvalidOperationException("must not ask");
        Should.Throw<RemoteAuthenticationException>(() => sessions.Get(Root + "/"));
    }
}

public class RemoteCrumbTests
{
    [Fact]
    public void SplitsUriIntoCrumbs()
    {
        var crumbs = PathCrumbs.Split("sftp://me@host/var/www");
        crumbs.Select(x => x.Name).ShouldBe(["me@host", "var", "www"]);
        crumbs.Select(x => x.Path).ShouldBe(["sftp://me@host/", "sftp://me@host/var", "sftp://me@host/var/www"]);
        crumbs[^1].IsCurrent.ShouldBeTrue();
    }
}
