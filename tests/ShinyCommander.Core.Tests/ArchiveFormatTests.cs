using System.Formats.Tar;
using System.IO.Compression;
using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Models;
using ShinyCommander.Core.Operations;

namespace ShinyCommander.Core.Tests;

public class ArchiveFormatTests
{
    // a.txt ("hello 7z\n") + docs/b.md ("nested\n"), written by bsdtar --format 7zip
    const string SevenZipBase64 = "N3q8ryccAAPjAtnFpAAAAAAAAAAiAAAAAAAAAP6GQ3wANBlJ7o3pBhLsfVusa5FHy/vMJaf//49UAAAAAIEzB64Pz7XvEA/r6p4BDWIDjdNMQj8OdlN5R7yHOmVRyYN9fDH2++gnRoHQn8CfDoo7S1mXfGT1S5ZgIpcN2cTHOkDXlABMR50herEjoXAr/JPG7UvKnm71+eiyS92e4QgWplQi7ADPT2fGoMAMYELxObqBa6pYDf8yrzS45jucZhY//WgwABcGGwEJgIkABwsBAAEjAwEBBV0AAIAADIDDCgGSCWJNAAA=";

    // same files, tar -czf by macOS bsdtar: pax xattr records + "._" AppleDouble companions
    const string MacTarGzBase64 = "H4sIAHU+7moAA+2Y3U7bMBiGXSSE6I7hDMlXYPwbtwccFLSJaiAYIESPKpMapVL/lmZQcSU73x3sona83QFO1KlboJSiOkjkeyTLsRXbnxO9/l6ZtA1JJgnyCaU0kBKntQ5UVjv+1pRyKTBTXAspgkApTJngnCFMvUY15ds4MbELxcTdJIy6dt57d5G1vWfm+X9TePWBemJ9ewOtIXRsQnxyjq/wlLQPbbrCXfnqStr+8bIpGxcXZ9PHdMR3Vz7kXqnM+rfCYZ+Y0ahnySge3tqBGYQWVdYQ+2N+7lR//1rBJoF5nJrJoTUdG+/6OwcW6p/RnP6llhrhiZdocpRc/4LiftLt2z2m66Jer3EliHAnshSspqtK46PmfuPs4LB5+ZFMTJLE5Cm57jW+NBvHN5+icUQ/105aVVnH527QUeu5Qf9ovPrW36Gs+M/+i/VPGcvpn2utIP8XQWR7vSHW9yDAckLanWE49rtGqnullvL/jAWg/0IA/19qZv7f3zmwUP+P/b/zC+D/i2CO/1epZ+Pg/989qep3Pa+xSP9pI+//ldO/8hxXRsn1n/1/0r4m/Y63NV5x/6uFAP9XCOD/Sk2m/5kJ9HIOLH//q2TAwP8VwTz/x2uBVOD/3j2Z/r1m/xfc/1Kdz/+KScj/RTCw48R2QH4AAABl4wE3Lz1rACYAAA==";

    readonly VirtualFileSystem vfs = new();
    FileOperationService Ops => new(this.vfs);

    [Theory]
    [InlineData("a.zip", ArchiveKind.Zip)]
    [InlineData("a.docx", ArchiveKind.Zip)]
    [InlineData("a.tar", ArchiveKind.Tar)]
    [InlineData("a.tar.gz", ArchiveKind.TarGz)]
    [InlineData("a.TGZ", ArchiveKind.TarGz)]
    [InlineData("a.tar.bz2", ArchiveKind.TarBz2)]
    [InlineData("a.tar.xz", ArchiveKind.TarXz)]
    [InlineData("a.7z", ArchiveKind.SevenZip)]
    [InlineData("a.rar", ArchiveKind.Rar)]
    [InlineData("notes.txt.gz", ArchiveKind.GZip)]
    [InlineData("a.bz2", ArchiveKind.BZip2)]
    [InlineData("a.txt", ArchiveKind.None)]
    [InlineData(".gz", ArchiveKind.None)]
    public void DetectsKind(string name, ArchiveKind kind) => ArchiveFormats.GetKind(name).ShouldBe(kind);

    [Theory]
    [InlineData("site.tar.gz", "site")]
    [InlineData("notes.txt.gz", "notes.txt")]
    [InlineData("a.zip", "a")]
    [InlineData("pkg.7z", "pkg")]
    public void StripsArchiveExtension(string name, string expected) => ArchiveFormats.StripExtension(name).ShouldBe(expected);

    static void WriteTarGz(string path, params (string Name, string Content)[] files)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax);
        foreach (var (name, content) in files)
        {
            var e = new PaxTarEntry(TarEntryType.RegularFile, "./" + name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) };
            tar.WriteEntry(e);
        }
    }

    [Fact]
    public async Task BrowsesTarGzWithImpliedFolders()
    {
        using var tmp = new TempDir();
        var archive = Path.Combine(tmp.Path, "site.tar.gz");
        WriteTarGz(archive, ("index.html", "<h1/>"), ("css/site.css", "body{}"), ("css/print/p.css", "x"));

        var root = await this.vfs.ListAsync(archive, true);
        root.Where(x => !x.IsParentLink).Select(x => (x.Name, x.IsDirectory)).OrderBy(x => x.Name)
            .ShouldBe([("css", true), ("index.html", false)]);

        var css = await this.vfs.ListAsync(Path.Combine(archive, "css"), true);
        css.Select(x => x.Name).ShouldContain("print");
        css.Single(x => x.Name == "site.css").Size.ShouldBe(6);
        this.vfs.Exists(Path.Combine(archive, "css", "print")).ShouldBeTrue();

        using var reader = new StreamReader(this.vfs.OpenRead(Path.Combine(archive, "css", "site.css")));
        reader.ReadToEnd().ShouldBe("body{}");
    }

    [Fact]
    public async Task CopiesFolderOutOfTarGzInOnePass()
    {
        using var tmp = new TempDir();
        var archive = Path.Combine(tmp.Path, "site.tar.gz");
        WriteTarGz(archive, ("index.html", "<h1/>"), ("css/site.css", "body{}"), ("css/print/p.css", "x"));
        var dest = tmp.Dir("out");

        var css = (await this.vfs.ListAsync(archive, true)).Single(x => x.Name == "css");
        var result = await this.Ops.CopyAsync([css], new OperationTarget(dest));

        result.Errors.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(dest, "css", "site.css")).ShouldBe("body{}");
        File.ReadAllText(Path.Combine(dest, "css", "print", "p.css")).ShouldBe("x");
    }

    [Fact]
    public async Task ReadsSevenZip()
    {
        using var tmp = new TempDir();
        var archive = Path.Combine(tmp.Path, "sample.7z");
        File.WriteAllBytes(archive, Convert.FromBase64String(SevenZipBase64));

        var root = await this.vfs.ListAsync(archive, true);
        root.Where(x => !x.IsParentLink).Select(x => x.Name).OrderBy(x => x).ShouldBe(["a.txt", "docs"]);

        var dest = tmp.Dir("out");
        var result = await this.Ops.ExtractAsync([new FileEntry { Name = "sample.7z", FullPath = archive }], dest, folderPerArchive: true);
        result.Errors.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(dest, "sample", "a.txt")).ShouldBe("hello 7z\n");
        File.ReadAllText(Path.Combine(dest, "sample", "docs", "b.md")).ShouldBe("nested\n");
    }

    [Fact]
    public async Task SingleGzipShowsTheDecompressedFile()
    {
        using var tmp = new TempDir();
        var archive = Path.Combine(tmp.Path, "notes.txt.gz");
        using (var gz = new GZipStream(File.Create(archive), CompressionLevel.Optimal))
            gz.Write("some notes"u8);

        var list = (await this.vfs.ListAsync(archive, true)).Where(x => !x.IsParentLink).ToList();
        list.Count.ShouldBe(1);
        list[0].Name.ShouldBe("notes.txt");
        list[0].Size.ShouldBe(10);

        using var reader = new StreamReader(this.vfs.OpenRead(Path.Combine(archive, "notes.txt")));
        reader.ReadToEnd().ShouldBe("some notes");
    }

    [Theory]
    [InlineData("out.tar")]
    [InlineData("out.tar.gz")]
    [InlineData("out.tar.bz2")]
    public async Task PacksTarFamilyRoundTrip(string name)
    {
        using var tmp = new TempDir();
        tmp.File("src/folder/a.txt", "A");
        tmp.File("src/folder/sub/b.txt", "BB");
        tmp.File("src/top.txt", "TOP");
        var sources = (await this.vfs.ListAsync(tmp.Path + "/src", true)).Where(x => !x.IsParentLink).ToList();
        var archive = Path.Combine(tmp.Path, name);

        var result = await this.Ops.PackAsync(sources, archive);
        result.Errors.ShouldBeEmpty();

        var sub = await this.vfs.ListAsync(Path.Combine(archive, "folder", "sub"), true);
        sub.Single(x => x.Name == "b.txt").Size.ShouldBe(2);
        using var reader = new StreamReader(this.vfs.OpenRead(Path.Combine(archive, "top.txt")));
        reader.ReadToEnd().ShouldBe("TOP");
    }

    [Fact]
    public async Task PackRejectsFormatsItCannotWrite()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt", "A");
        var a = (await this.vfs.ListAsync(tmp.Path, true)).Single(x => x.Name == "a.txt");
        await Should.ThrowAsync<NotSupportedException>(() => this.Ops.PackAsync([a], Path.Combine(tmp.Path, "x.7z")));
    }

    [Fact]
    public async Task ReadOnlyArchivesRefuseChanges()
    {
        using var tmp = new TempDir();
        var archive = Path.Combine(tmp.Path, "site.tar.gz");
        WriteTarGz(archive, ("index.html", "<h1/>"));
        tmp.File("new.txt", "N");
        var inside = (await this.vfs.ListAsync(archive, true)).Single(x => x.Name == "index.html");
        var local = (await this.vfs.ListAsync(tmp.Path, true)).Single(x => x.Name == "new.txt");

        await Should.ThrowAsync<NotSupportedException>(() => this.Ops.CopyAsync([local], new OperationTarget(archive)));
        await Should.ThrowAsync<NotSupportedException>(() => this.Ops.DeleteAsync([inside], false));
        await Should.ThrowAsync<NotSupportedException>(() => this.Ops.RenameAsync(inside, "x.html"));
        await Should.ThrowAsync<NotSupportedException>(() => this.Ops.MoveAsync([inside], new OperationTarget(tmp.Path)));
        await Should.ThrowAsync<NotSupportedException>(() => this.Ops.CreateDirectoryAsync(archive, "d"));
    }

    [Fact]
    public async Task ReadsMacBsdTarWithoutMetadataNoise()
    {
        using var tmp = new TempDir();
        var archive = Path.Combine(tmp.Path, "mac.tar.gz");
        File.WriteAllBytes(archive, Convert.FromBase64String(MacTarGzBase64));

        var root = (await this.vfs.ListAsync(archive, true)).Where(x => !x.IsParentLink).Select(x => x.Name).OrderBy(x => x);
        root.ShouldBe(["a.txt", "docs"]);
        var docs = (await this.vfs.ListAsync(Path.Combine(archive, "docs"), true)).Where(x => !x.IsParentLink).Select(x => x.Name);
        docs.ShouldBe(["b.md"]);

        var dest = tmp.Dir("out");
        var result = await this.Ops.ExtractAsync([new FileEntry { Name = "mac.tar.gz", FullPath = archive }], dest, folderPerArchive: true);
        result.Errors.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(dest, "mac", "docs", "b.md")).ShouldBe("nested\n");
    }
}
