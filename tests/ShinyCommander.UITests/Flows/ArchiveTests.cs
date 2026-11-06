using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>Browsing archives like folders, copying in and out, Pack (Alt+F5) and Unpack (Alt+F9), and read-only formats.</summary>
public class ArchiveTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task Enter_browses_a_zip_like_a_folder()
    {
        await this.Start(l => l.Zip("pack.zip", ("readme.md", "# hi"), ("docs/a.txt", "A")));
        await this.App.Cursor("pack.zip");

        var s = await this.App.Key("Enter");
        s.Left.Path.ShouldBe(this.LeftTree["pack.zip"]);
        s.Left.IsArchive.ShouldBeTrue();
        s.Left.IsReadOnlyArchive.ShouldBeFalse();
        s.Left.ArchiveBadge.ShouldBe("zip");
        s.Left.Names.ShouldBe(["docs", "readme.md"]);

        await this.App.Cursor("docs");
        (await this.App.Key("Enter")).Left.Names.ShouldBe(["a.txt"]);
        var up = await this.App.Keys("Backspace", "Backspace");
        up.Left.Path.ShouldBe(this.LeftTree.Path);
        up.Left.CursorName.ShouldBe("pack.zip");
    }

    [Fact]
    public async Task A_tar_gz_opens_read_only()
    {
        await this.Start(l => l.TarGz("site.tar.gz", ("index.html", "<h1/>"), ("css/site.css", "body{}")));
        await this.App.Cursor("site.tar.gz");

        var s = await this.App.Key("Enter");
        s.Left.IsReadOnlyArchive.ShouldBeTrue();
        s.Left.ArchiveBadge.ShouldBe("tar.gz • read-only");
        s.Left.Names.ShouldBe(["css", "index.html"]);
    }

    [Fact]
    public async Task F5_copies_out_of_a_tar_gz()
    {
        await this.Start(l => l.TarGz("site.tar.gz", ("index.html", "<h1/>"), ("css/site.css", "body{}")));
        await this.App.Navigate(this.LeftTree["site.tar.gz"]);
        await this.App.Mark("css", "index.html");
        await this.App.Answer("<default>");

        await this.App.Key("F5");
        this.RightTree.Read("index.html").ShouldBe("<h1/>");
        this.RightTree.Read("css/site.css").ShouldBe("body{}");
    }

    [Fact]
    public async Task F8_inside_a_read_only_archive_explains_instead_of_deleting()
    {
        await this.Start(l => l.TarGz("site.tar.gz", ("index.html", "<h1/>")));
        await this.App.Navigate(this.LeftTree["site.tar.gz"]);
        await this.App.Cursor("index.html");
        await this.App.Answer("yes");

        var s = await this.App.Key("F8");
        s.Left.Names.ShouldBe(["index.html"]);
        s.Dialogs.ShouldContain(d => (d.Kind == "alert" || d.Kind == "toast") && (d.Message ?? d.Title).Contains("read-only"));
    }

    [Fact]
    public async Task F5_copies_into_a_zip_open_in_the_other_panel()
    {
        await this.Start(l => l.File("add.txt", "added"), r => r.Zip("box.zip", ("old.txt", "O")));
        await this.App.Navigate(this.RightTree["box.zip"], "right");
        await this.App.Cursor("add.txt", "left");
        await this.App.Answer("<default>");

        var s = await this.App.Key("F5");
        s.Right.Names.ShouldBe(["add.txt", "old.txt"]);
    }

    [Fact]
    public async Task Alt_F5_packs_to_tar_gz_and_the_result_browses()
    {
        await this.Start(l => l.File("a.txt", "A").Dir("d").File("d/b.txt", "B"));
        await this.App.Mark("a.txt", "d");
        await this.App.Answer(this.RightTree["bundle.tar.gz"]);

        var s = await this.App.Key("Alt+F5");
        this.RightTree.Exists("bundle.tar.gz").ShouldBeTrue();
        s.Right.Names.ShouldContain("bundle.tar.gz");

        await this.App.Navigate(this.RightTree["bundle.tar.gz"], "right");
        var inside = await this.App.State();
        inside.Right.Names.ShouldBe(["d", "a.txt"]);
    }

    [Fact]
    public async Task Alt_F5_suggests_a_zip_in_the_other_panel()
    {
        await this.Start(l => l.File("report.txt"));
        await this.App.Cursor("report.txt");
        await this.App.Answer("<cancel>");

        var s = await this.App.Key("Alt+F5");
        s.Dialogs.First(d => d.Kind == "prompt").Initial.ShouldBe(this.RightTree["report.zip"]);
    }

    [Fact]
    public async Task Alt_F9_unpacks_each_archive_into_its_own_folder()
    {
        await this.Start(l => l.Zip("one.zip", ("x.txt", "X")).TarGz("two.tar.gz", ("y.txt", "Y")));
        await this.App.Mark("one.zip", "two.tar.gz");
        await this.App.Answer("<default>");

        await this.App.Key("Alt+F9");
        this.RightTree.Read("one/x.txt").ShouldBe("X");
        this.RightTree.Read("two/y.txt").ShouldBe("Y");
    }

    [Fact]
    public async Task Alt_F9_with_no_archive_selected_says_so()
    {
        await this.Start(l => l.File("plain.txt"));
        await this.App.Cursor("plain.txt");

        var s = await this.App.Key("Alt+F9");
        s.Dialogs.ShouldContain(d => d.Kind == "toast" && d.Message!.Contains("archives to unpack"));
    }
}
