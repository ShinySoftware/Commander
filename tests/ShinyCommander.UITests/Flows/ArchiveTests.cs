using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>Archives as folders - browsing, copying in and out, deleting, Pack (Alt+F5), Unpack (Alt+F9) - and the formats that stay read-only.</summary>
public class ArchiveTests(AppFixture fixture) : UiTest(fixture)
{
    // index.html ("<h1/>") as tar.xz - a format that stays read-only (SharpCompress can't write xz)
    const string SiteTarXz = "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4Cf/AGldADSbiLBXymXIPZmcoovNvTmnF+OaKURw2zuHsp48HTwz77oR5S24baw4Z6gRlsnnD9QavZ+z3ZtT2+E6dKEje8MfFn2l33gLo81P5xEAJeWrCPFH+LVUsTEwxB+Rfh0xRlluvAPUOmzYAAAAAAATCMssO1bJ5wABhQGAUAAAjeCtMbHEZ/sCAAAAAARZWg==";

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
    public async Task Enter_opens_a_workbook_in_its_app_and_Primary_PageDown_goes_inside()
    {
        await this.Start(l => l.Zip("budget.xlsx", ("xl/workbook.xml", "<workbook/>"), ("[Content_Types].xml", "<Types/>")));
        await this.App.Cursor("budget.xlsx");

        // a zip, but a document first - handed to Excel / Numbers (recorded in test mode), not browsed
        var s = await this.App.Key("Enter");
        s.Left.Path.ShouldBe(this.LeftTree.Path);
        s.Opened.ShouldBe([this.LeftTree["budget.xlsx"]]);

        s = await this.App.Key("Primary+PageDown");
        s.Left.Path.ShouldBe(this.LeftTree["budget.xlsx"]);
        s.Left.IsArchive.ShouldBeTrue();
        s.Left.Names.ShouldBe(["xl", "[Content_Types].xml"]);
    }

    [Fact]
    public async Task A_tar_gz_opens_like_a_folder_that_can_be_changed()
    {
        await this.Start(l => l.TarGz("site.tar.gz", ("index.html", "<h1/>"), ("css/site.css", "body{}")));
        await this.App.Cursor("site.tar.gz");

        var s = await this.App.Key("Enter");
        s.Left.IsReadOnlyArchive.ShouldBeFalse();
        s.Left.ArchiveBadge.ShouldBe("tar.gz");
        s.Left.Names.ShouldBe(["css", "index.html"]);
    }

    [Fact]
    public async Task A_tar_xz_opens_read_only()
    {
        await this.Start(l => l.Bytes("site.tar.xz", Convert.FromBase64String(SiteTarXz)));
        await this.App.Cursor("site.tar.xz");

        var s = await this.App.Key("Enter");
        s.Left.IsReadOnlyArchive.ShouldBeTrue();
        s.Left.ArchiveBadge.ShouldBe("tar.xz • read-only");
        s.Left.Names.ShouldBe(["index.html"]);
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
        await this.Start(l => l.Bytes("site.tar.xz", Convert.FromBase64String(SiteTarXz)));
        await this.App.Navigate(this.LeftTree["site.tar.xz"]);
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
    public async Task F8_deletes_inside_a_tar_gz()
    {
        await this.Start(l => l.TarGz("site.tar.gz", ("index.html", "<h1/>"), ("css/site.css", "body{}")));
        await this.App.Navigate(this.LeftTree["site.tar.gz"]);
        await this.App.Cursor("css");
        await this.App.Answer("yes");

        var s = await this.App.Key("F8");
        s.Left.Names.ShouldBe(["index.html"]);
    }

    [Fact]
    public async Task Alt_F5_packs_a_folder_to_7z_and_files_copy_in_and_out_of_it()
    {
        await this.Start(l => l.Dir("project").File("project/readme.md", "R").File("project/src/deep/main.cs", "M").File("extra.txt", "E"));
        await this.App.Cursor("project");
        await this.App.Answer(this.RightTree["project.7z"]);
        await this.App.Key("Alt+F5");
        this.RightTree.Exists("project.7z").ShouldBeTrue();

        // the folder went in with everything under it
        await this.App.Navigate(Path.Combine(this.RightTree["project.7z"], "project", "src", "deep"), "right");
        (await this.App.State()).Right.Names.ShouldBe(["main.cs"]);

        // copy a file in, from the other panel
        await this.App.Navigate(this.RightTree["project.7z"], "right");
        await this.App.Cursor("extra.txt", "left");
        await this.App.Answer("<default>");
        var s = await this.App.Key("F5");
        s.Right.Names.ShouldBe(["project", "extra.txt"]);
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
