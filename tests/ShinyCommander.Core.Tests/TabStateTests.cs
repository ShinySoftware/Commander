using ShinyCommander.Core.Util;

namespace ShinyCommander.Core.Tests;

public class TabStateTests
{
    [Fact]
    public void PlainPath()
    {
        var tab = TabState.Parse("/Users/me/docs");
        tab.ShouldBe(new TabState("/Users/me/docs"));
        tab.Format().ShouldBe("/Users/me/docs");
    }

    [Fact]
    public void LegacyLockedLine() => TabState.Parse("locked|/Users/me").ShouldBe(new TabState("/Users/me", Locked: true));

    [Fact]
    public void RoundTrip()
    {
        var tab = new TabState("/Users/me/Projects", true, "Work stuff", "Blue", "star");
        TabState.Parse(tab.Format()).ShouldBe(tab);
    }

    [Fact]
    public void CustomSortKeepsItsColumn()
    {
        var tab = new TabState("/music", Sort: SortColumn.Custom, Descending: true, SortKey: "duration");
        tab.Format().ShouldBe("/music\tsort=Custom\tsortkey=duration\tdesc");
        TabState.Parse(tab.Format()).ShouldBe(tab);
    }

    [Fact]
    public void SortRoundTrip()
    {
        var tab = new TabState("/x", Sort: SortColumn.Size, Descending: true);
        tab.Format().ShouldBe("/x\tsort=Size\tdesc");
        TabState.Parse(tab.Format()).ShouldBe(tab);
    }

    [Fact]
    public void UnknownSortFallsBackToName() => TabState.Parse("/x\tsort=Bogus").Sort.ShouldBe(SortColumn.Name);

    [Fact]
    public void NameOnly()
    {
        var tab = new TabState("/x", Alias: "Inbox");
        tab.Format().ShouldBe("/x\talias=Inbox");
        TabState.Parse(tab.Format()).ShouldBe(tab);
    }

    [Fact]
    public void SeparatorsInANameAreDropped()
        => TabState.Parse(new TabState("/x", Alias: "a\tb\nc").Format()).Alias.ShouldBe("a b c");

    [Fact]
    public void BlankNameAndColourAreNotSaved() => new TabState("/x", Alias: "  ", Color: "").Format().ShouldBe("/x");
}
