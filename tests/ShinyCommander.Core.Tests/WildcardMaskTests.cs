using ShinyCommander.Core.Search;

namespace ShinyCommander.Core.Tests;

public class WildcardMaskTests
{
    [Theory]
    [InlineData("*.txt", "a.txt", true)]
    [InlineData("*.txt", "a.TXT", true)]
    [InlineData("*.txt", "a.md", false)]
    [InlineData("*.cs;*.xaml", "Page.xaml", true)]
    [InlineData("*.cs *.xaml", "Page.cs", true)]
    [InlineData("a?c.*", "abc.txt", true)]
    [InlineData("*.*", "Makefile", true)]
    [InlineData("readme", "my-README.md", true)] // plain text == contains
    [InlineData("*.cs | *Test*.cs", "FooTests.cs", false)]
    [InlineData("*.cs | *Test*.cs", "Foo.cs", true)]
    public void Matches(string mask, string name, bool expected)
        => new WildcardMask(mask).IsMatch(name).ShouldBe(expected);

    [Fact]
    public void DirectoryExcludes()
    {
        var mask = new WildcardMask("* | bin/ obj\\");
        mask.IsDirectoryExcluded("bin").ShouldBeTrue();
        mask.IsDirectoryExcluded("obj").ShouldBeTrue();
        mask.IsDirectoryExcluded("src").ShouldBeFalse();
        mask.IsMatch("bin").ShouldBeTrue(); // folder excludes don't apply to file names
    }
}
