using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Models;
using ShinyCommander.Core.Operations;
using ShinyCommander.Core.Rename;

namespace ShinyCommander.Core.Tests;

/// <summary>Everything a folder does, inside each writable archive format - through the same service the panels use.</summary>
public class ArchiveEditingTests
{
    public static TheoryData<string> Formats => new() { "box.zip", "box.7z", "box.tar", "box.tar.gz", "box.tar.bz2", "box.tar.zst" };

    readonly VirtualFileSystem vfs = new();
    FileOperationService Ops => new(this.vfs);

    async Task<List<FileEntry>> List(string path) => (await this.vfs.ListAsync(path, true)).Where(x => !x.IsParentLink).ToList();
    async Task<FileEntry> Get(string path, string name) => (await this.List(path)).Single(x => x.Name == name);
    async Task<string[]> Names(string path) => (await this.List(path)).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    string Read(string path)
    {
        using var reader = new StreamReader(this.vfs.OpenRead(path));
        return reader.ReadToEnd();
    }

    // a packed folder, with everything under it
    async Task<string> Packed(TempDir tmp, string name)
    {
        tmp.File("src/project/readme.md", "read me");
        tmp.File("src/project/code/main.cs", "class A {}");
        tmp.File("src/project/code/deep/deeper/x.txt", "deep");
        var archive = Path.Combine(tmp.Path, name);
        var result = await this.Ops.PackAsync([await this.Get(Path.Combine(tmp.Path, "src"), "project")], archive);
        result.Errors.ShouldBeEmpty();
        return archive;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Packing_a_folder_takes_everything_under_it(string name)
    {
        using var tmp = new TempDir();
        var archive = await this.Packed(tmp, name);
        (await this.Names(archive)).ShouldBe(["project"]);
        (await this.Names(Path.Combine(archive, "project"))).ShouldBe(["code", "readme.md"]);
        this.Read(Path.Combine(archive, "project", "code", "deep", "deeper", "x.txt")).ShouldBe("deep");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Copy_in_rename_new_folder_new_file_and_delete(string name)
    {
        using var tmp = new TempDir();
        var archive = await this.Packed(tmp, name);
        var project = Path.Combine(archive, "project");

        tmp.File("incoming/notes.txt", "notes");
        await this.Ops.CopyAsync([await this.Get(Path.Combine(tmp.Path, "incoming"), "notes.txt")], new OperationTarget(project));
        this.Read(Path.Combine(project, "notes.txt")).ShouldBe("notes");

        await this.Ops.RenameAsync(await this.Get(project, "notes.txt"), "renamed.txt");
        await this.Ops.RenameAsync(await this.Get(project, "code"), "src"); // a folder, and all in it
        await this.Ops.CreateDirectoryAsync(project, "empty-folder");
        await this.Ops.CreateFileAsync(project, "new.txt");
        (await this.Names(project)).ShouldBe(["empty-folder", "new.txt", "readme.md", "renamed.txt", "src"]);
        this.Read(Path.Combine(project, "src", "deep", "deeper", "x.txt")).ShouldBe("deep");

        await this.Ops.DeleteAsync([await this.Get(project, "src"), await this.Get(project, "renamed.txt")], useTrash: false);
        (await this.Names(project)).ShouldBe(["empty-folder", "new.txt", "readme.md"]);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Copying_over_a_file_asks_and_can_keep_both(string name)
    {
        using var tmp = new TempDir();
        var archive = await this.Packed(tmp, name);
        var project = Path.Combine(archive, "project");
        tmp.File("incoming/readme.md", "newer");
        var incoming = await this.Get(Path.Combine(tmp.Path, "incoming"), "readme.md");

        await this.Ops.CopyAsync([incoming], new OperationTarget(project), (_, _) => Task.FromResult(ConflictAction.Skip));
        this.Read(Path.Combine(project, "readme.md")).ShouldBe("read me");

        await this.Ops.CopyAsync([incoming], new OperationTarget(project), (_, _) => Task.FromResult(ConflictAction.KeepBoth));
        this.Read(Path.Combine(project, "readme (2).md")).ShouldBe("newer");

        await this.Ops.CopyAsync([incoming], new OperationTarget(project), (_, _) => Task.FromResult(ConflictAction.Overwrite));
        this.Read(Path.Combine(project, "readme.md")).ShouldBe("newer");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Move_out_move_within_and_copy_between_archives(string name)
    {
        using var tmp = new TempDir();
        var archive = await this.Packed(tmp, name);
        var project = Path.Combine(archive, "project");

        // within: a rename into another folder of the same archive
        await this.Ops.MoveAsync([await this.Get(project, "readme.md")], new OperationTarget(Path.Combine(project, "code")));
        (await this.Names(Path.Combine(project, "code"))).ShouldBe(["deep", "main.cs", "readme.md"]);

        // out to disk, gone from the archive
        var outDir = tmp.Dir("out");
        await this.Ops.MoveAsync([await this.Get(Path.Combine(project, "code"), "deep")], new OperationTarget(outDir));
        File.ReadAllText(Path.Combine(outDir, "deep", "deeper", "x.txt")).ShouldBe("deep");
        (await this.Names(Path.Combine(project, "code"))).ShouldBe(["main.cs", "readme.md"]);

        // into a second archive, of another kind
        var other = Path.Combine(tmp.Path, name.EndsWith(".zip", StringComparison.Ordinal) ? "other.7z" : "other.zip");
        ArchiveWriter.CreateEmpty(other);
        await this.Ops.CopyAsync([await this.Get(project, "code")], new OperationTarget(other));
        this.Read(Path.Combine(other, "code", "main.cs")).ShouldBe("class A {}");
        this.Read(Path.Combine(project, "code", "main.cs")).ShouldBe("class A {}"); // a copy leaves it
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Packing_into_an_existing_archive_adds_to_it(string name)
    {
        using var tmp = new TempDir();
        var archive = await this.Packed(tmp, name);
        tmp.File("more/extra.txt", "extra");
        await this.Ops.PackAsync([await this.Get(Path.Combine(tmp.Path, "more"), "extra.txt")], archive);
        (await this.Names(archive)).ShouldBe(["extra.txt", "project"]);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Multi_rename_swaps_names_and_undoes(string name)
    {
        using var tmp = new TempDir();
        var archive = await this.Packed(tmp, name);
        var project = Path.Combine(archive, "project");
        var readme = await this.Get(project, "readme.md");
        var code = await this.Get(project, "code");

        // a swap - each new name is the other's old one
        var undo = await MultiRenameEngine.ExecuteAsync([new RenamePreview(readme, "code", null), new RenamePreview(code, "readme.md", null)]);
        this.Read(Path.Combine(project, "code")).ShouldBe("read me");
        this.Read(Path.Combine(project, "readme.md", "main.cs")).ShouldBe("class A {}");

        await MultiRenameEngine.UndoAsync(undo);
        this.Read(Path.Combine(project, "readme.md")).ShouldBe("read me");
        this.Read(Path.Combine(project, "code", "main.cs")).ShouldBe("class A {}");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Saving_an_opened_copy_writes_it_back(string name)
    {
        using var tmp = new TempDir();
        var archive = await this.Packed(tmp, name);
        var entry = Path.Combine(archive, "project", "readme.md");
        var copy = await this.vfs.MaterializeAsync(entry);

        using var writeBack = new ArchiveWriteBack();
        var saved = new TaskCompletionSource<ArchiveWriteBackEventArgs>();
        writeBack.Saved += (_, e) => saved.TrySetResult(e);
        writeBack.Failed += (_, e) => saved.TrySetException(e.Error!);
        writeBack.Watch(copy, archive, "project/readme.md");

        await Task.Delay(200); // the watcher is up
        await File.WriteAllTextAsync(copy, "edited");
        var e = await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        e.Key.ShouldBe("project/readme.md");
        this.Read(entry).ShouldBe("edited");
    }
}
