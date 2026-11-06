using ShinyCommander.Core.Compare;
using ShinyCommander.Core.Search;

namespace ShinyCommander.Core.Tests;

public class CompareAndSearchTests
{
    [Fact]
    public async Task ComparesTrees()
    {
        using var tmp = new TempDir();
        tmp.File("l/same.txt", "x");
        tmp.File("r/same.txt", "x");
        File.SetLastWriteTime(tmp.Path + "/r/same.txt", File.GetLastWriteTime(tmp.Path + "/l/same.txt"));
        tmp.File("l/only-left.txt");
        tmp.File("r/sub/only-right.txt");
        tmp.File("l/sub/newer.txt", "new");
        tmp.File("r/sub/newer.txt", "old");
        File.SetLastWriteTime(tmp.Path + "/r/sub/newer.txt", DateTime.Now.AddDays(-1));

        var result = await DirectoryComparer.CompareAsync(tmp.Path + "/l", tmp.Path + "/r", new CompareOptions());
        result.Single(x => x.RelativePath == "same.txt").State.ShouldBe(CompareState.Equal);
        result.Single(x => x.RelativePath == "only-left.txt").State.ShouldBe(CompareState.LeftOnly);
        result.Single(x => x.RelativePath.EndsWith("only-right.txt")).State.ShouldBe(CompareState.RightOnly);
        var newer = result.Single(x => x.RelativePath.EndsWith("newer.txt"));
        newer.State.ShouldBe(CompareState.LeftNewer);
        newer.DefaultAction.ShouldBe(SyncAction.CopyToRight);
    }

    [Fact]
    public async Task SearchByNameAndContent()
    {
        using var tmp = new TempDir();
        tmp.File("a/one.cs", "class Foo {}");
        tmp.File("a/b/two.cs", "class Bar {}");
        tmp.File("a/b/three.txt", "class Foo");

        var byName = new List<string>();
        await foreach (var e in FileSearcher.SearchAsync(new SearchOptions { StartPath = tmp.Path, NameMask = "*.cs" }))
            byName.Add(e.Name);
        byName.ShouldBe(["one.cs", "two.cs"], ignoreOrder: true);

        var byText = new List<string>();
        await foreach (var e in FileSearcher.SearchAsync(new SearchOptions { StartPath = tmp.Path, ContainingText = "foo" }))
            byText.Add(e.Name);
        byText.ShouldBe(["one.cs", "three.txt"], ignoreOrder: true);
    }
}
