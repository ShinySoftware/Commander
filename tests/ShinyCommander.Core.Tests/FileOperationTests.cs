using System.IO.Compression;
using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Models;
using ShinyCommander.Core.Operations;

namespace ShinyCommander.Core.Tests;

public class FileOperationTests
{
    readonly VirtualFileSystem vfs = new();
    FileOperationService Ops => new(this.vfs);

    async Task<FileEntry> Get(string dir, string name)
        => (await this.vfs.ListAsync(dir, true)).Single(x => x.Name == name);

    [Fact]
    public async Task CopiesFolderRecursively()
    {
        using var tmp = new TempDir();
        tmp.File("src/folder/a.txt", "A");
        tmp.File("src/folder/sub/b.txt", "BB");
        var dest = tmp.Dir("dest");

        var progress = new List<OperationProgress>();
        var result = await this.Ops.CopyAsync([await Get(tmp.Path + "/src", "folder")], new OperationTarget(dest), progress: new SyncProgress<OperationProgress>(progress.Add));

        File.ReadAllText(Path.Combine(dest, "folder", "sub", "b.txt")).ShouldBe("BB");
        result.Errors.ShouldBeEmpty();
        progress.Last().BytesDone.ShouldBe(3);
    }

    [Fact]
    public async Task ConflictSkipAllKeepsTarget()
    {
        using var tmp = new TempDir();
        tmp.File("src/a.txt", "new");
        tmp.File("src/b.txt", "new");
        tmp.File("dest/a.txt", "old");
        tmp.File("dest/b.txt", "old");

        var asked = 0;
        var src = (await this.vfs.ListAsync(tmp.Path + "/src", true)).Where(x => !x.IsParentLink).ToList();
        var result = await this.Ops.CopyAsync(src, new OperationTarget(tmp.Path + "/dest"), (_, _) =>
        {
            asked++;
            return Task.FromResult(ConflictAction.SkipAll);
        });

        asked.ShouldBe(1);
        result.Skipped.ShouldBe(2);
        File.ReadAllText(tmp.Path + "/dest/a.txt").ShouldBe("old");
    }

    [Fact]
    public async Task MoveRenamesSingleItem()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt", "A");
        var entry = await Get(tmp.Path, "a.txt");
        var target = this.Ops.ResolveTarget("renamed.txt", tmp.Path, 1);
        target.RenameTo.ShouldBe("renamed.txt");

        await this.Ops.MoveAsync([entry], target);
        File.Exists(tmp.Path + "/a.txt").ShouldBeFalse();
        File.ReadAllText(tmp.Path + "/renamed.txt").ShouldBe("A");
    }

    [Fact]
    public async Task CannotCopyIntoSelf()
    {
        using var tmp = new TempDir();
        tmp.File("folder/a.txt");
        var folder = await Get(tmp.Path, "folder");
        await Should.ThrowAsync<IOException>(() => this.Ops.CopyAsync([folder], new OperationTarget(tmp.Path + "/folder/inner")));
    }

    [Fact]
    public async Task PackBrowseExtractAndDeleteInArchive()
    {
        using var tmp = new TempDir();
        tmp.File("data/readme.md", "# hi");
        tmp.File("data/sub/x.bin", "xyz");
        var zip = Path.Combine(tmp.Path, "out.zip");

        await this.Ops.PackAsync([await Get(tmp.Path, "data")], zip);
        File.Exists(zip).ShouldBeTrue();

        // browse into the archive like a folder
        var root = await this.vfs.ListAsync(zip, true);
        root.ShouldContain(x => x.Name == "data" && x.IsDirectory && x.IsInArchive);
        var inner = await this.vfs.ListAsync(Path.Combine(zip, "data"), true);
        inner.Select(x => x.Name).ShouldBe(["..", "sub", "readme.md"], ignoreOrder: true);

        using (var reader = new StreamReader(this.vfs.OpenRead(Path.Combine(zip, "data", "readme.md"))))
            reader.ReadToEnd().ShouldBe("# hi");

        // copy out of the archive
        var dest = tmp.Dir("extracted");
        await this.Ops.CopyAsync([inner.Single(x => x.Name == "sub")], new OperationTarget(dest));
        File.ReadAllText(Path.Combine(dest, "sub", "x.bin")).ShouldBe("xyz");

        // delete inside the archive
        await this.Ops.DeleteAsync([inner.Single(x => x.Name == "readme.md")], false);
        using var z = ZipFile.OpenRead(zip);
        z.Entries.ShouldNotContain(e => e.FullName.EndsWith("readme.md"));
    }

    [Fact]
    public async Task CopyIntoExistingArchive()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt", "A");
        var zip = Path.Combine(tmp.Path, "z.zip");
        using (ZipFile.Open(zip, ZipArchiveMode.Create)) { }

        await this.Ops.CopyAsync([await Get(tmp.Path, "a.txt")], this.Ops.ResolveTarget(zip, tmp.Path, 1));
        (await this.vfs.ListAsync(zip, true)).ShouldContain(x => x.Name == "a.txt");
    }

    [Fact]
    public async Task RenameAndCreate()
    {
        using var tmp = new TempDir();
        await this.Ops.CreateDirectoryAsync(tmp.Path, "new/nested");
        Directory.Exists(tmp.Path + "/new/nested").ShouldBeTrue();

        await this.Ops.CreateFileAsync(tmp.Path, "f.txt");
        await this.Ops.RenameAsync(await Get(tmp.Path, "f.txt"), "F.txt");
        (await this.vfs.ListAsync(tmp.Path, true)).ShouldContain(x => x.Name == "F.txt");
    }

    sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
