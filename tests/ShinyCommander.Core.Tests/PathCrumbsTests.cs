using ShinyCommander.Core.Util;

namespace ShinyCommander.Core.Tests;

public class PathCrumbsTests
{
    [Fact]
    public void Empty() => PathCrumbs.Split("").ShouldBeEmpty();

    [Fact]
    public void Unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix paths");
        var crumbs = PathCrumbs.Split("/Users/me/docs");
        crumbs.Select(x => x.Name).ShouldBe(["/", "Users", "me", "docs"]);
        crumbs.Select(x => x.Path).ShouldBe(["/", "/Users", "/Users/me", "/Users/me/docs"]);
        crumbs.Select(x => x.IsCurrent).ShouldBe([false, false, false, true]);
    }

    [Fact]
    public void UnixRoot()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix paths");
        var crumb = PathCrumbs.Split("/").ShouldHaveSingleItem();
        crumb.Name.ShouldBe("/");
        crumb.IsCurrent.ShouldBeTrue();
    }

    [Fact]
    public void TrailingSeparatorAndArchive()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix paths");
        var crumbs = PathCrumbs.Split("/x/stuff.zip/docs/");
        crumbs.Select(x => x.Name).ShouldBe(["/", "x", "stuff.zip", "docs"]);
        crumbs[2].Path.ShouldBe("/x/stuff.zip");
    }

    [Fact]
    public void Windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "windows paths");
        var crumbs = PathCrumbs.Split(@"C:\stuff\more");
        crumbs.Select(x => x.Name).ShouldBe(["C:", "stuff", "more"]);
        crumbs.Select(x => x.Path).ShouldBe([@"C:\", @"C:\stuff", @"C:\stuff\more"]);
    }

    [Fact]
    public void WindowsUnc()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "windows paths");
        var crumbs = PathCrumbs.Split(@"\\server\share\dir");
        crumbs.Select(x => x.Name).ShouldBe([@"\\server\share", "dir"]);
    }
}
