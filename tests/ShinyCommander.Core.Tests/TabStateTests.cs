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
