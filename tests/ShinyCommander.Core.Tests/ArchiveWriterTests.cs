using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using ShinyCommander.Core.FileSystem;

namespace ShinyCommander.Core.Tests;

/// <summary>Changing archives in place - every writable format, read back through the same readers the panels use.</summary>
public class ArchiveWriterTests
{
    public static TheoryData<string> Writable => new() { "a.zip", "a.7z", "a.tar", "a.tar.gz", "a.tgz", "a.tar.bz2", "a.tar.zst" };

    static ArchiveAdd Text(string key, string content, UnixFileMode? mode = null)
        => new(key, false, () => new MemoryStream(Encoding.UTF8.GetBytes(content)), new DateTime(2024, 5, 6, 7, 8, 10), mode);

    static Dictionary<string, string?> Read(string archive)
    {
        using var reader = ArchiveReader.Open(archive);
        var result = reader.Items.ToDictionary(x => x.Key + (x.IsDirectory ? "/" : ""), _ => (string?)null);
        reader.ReadSequential(_ => true, (item, s) => result[item.Key] = new StreamReader(s).ReadToEnd(), CancellationToken.None);
        return result;
    }

    [Theory]
    [MemberData(nameof(Writable))]
    public void Add_rename_delete_and_replace(string name)
    {
        using var dir = new TempDir();
        var archive = Path.Combine(dir.Path, name);
        ArchiveWriter.CreateEmpty(archive);
        Read(archive).ShouldBeEmpty();

        var changes = new ArchiveChanges();
        changes.Adds.Add(ArchiveAdd.Folder("docs"));
        changes.Adds.Add(Text("docs/a.txt", "alpha"));
        changes.Adds.Add(Text("docs/deep/b.txt", "beta"));
        changes.Adds.Add(Text("c.txt", "gamma", (UnixFileMode)0b111_101_101));
        changes.Adds.Add(Text("empty.txt", ""));
        ArchiveWriter.Apply(archive, changes);

        var read = Read(archive);
        read["docs/a.txt"].ShouldBe("alpha");
        read["docs/deep/b.txt"].ShouldBe("beta");
        read["c.txt"].ShouldBe("gamma");
        read.ShouldContainKey("empty.txt");
        read.ShouldContainKey("docs/");

        // a folder rename takes what's in it along; a delete; a replacement
        changes = new ArchiveChanges();
        changes.Renames.Add(("docs", "papers"));
        changes.Deletes.Add("c.txt");
        changes.Adds.Add(Text("empty.txt", "not any more"));
        ArchiveWriter.Apply(archive, changes);

        read = Read(archive);
        read.Keys.Where(x => x.StartsWith("docs", StringComparison.Ordinal)).ShouldBeEmpty();
        read["papers/a.txt"].ShouldBe("alpha");
        read["papers/deep/b.txt"].ShouldBe("beta");
        read.ShouldNotContainKey("c.txt");
        read["empty.txt"].ShouldBe("not any more");
    }

    [Theory]
    [MemberData(nameof(Writable))]
    public void Modes_and_times_survive(string name)
    {
        using var dir = new TempDir();
        var archive = Path.Combine(dir.Path, name);
        ArchiveWriter.CreateEmpty(archive);
        var changes = new ArchiveChanges();
        changes.Adds.Add(Text("run.sh", "#!/bin/sh", (UnixFileMode)0b111_101_101));
        ArchiveWriter.Apply(archive, changes);

        // and again through a rewrite of something else
        changes = new ArchiveChanges();
        changes.Adds.Add(Text("other.txt", "x"));
        ArchiveWriter.Apply(archive, changes);

        using var reader = ArchiveReader.Open(archive);
        var item = reader.Items.Single(x => x.Key == "run.sh");
        item.Modified.ShouldBe(new DateTime(2024, 5, 6, 7, 8, 10), TimeSpan.FromSeconds(2));

        // SharpCompress hands back only the low word of a 7z entry's attributes, where the mode isn't - bsdtar shows
        // it's written (Bsdtar_reads_what_the_7z_writer_writes)
        if (!name.EndsWith(".7z", StringComparison.Ordinal))
            item.Mode.ShouldBe((UnixFileMode)0b111_101_101);
    }

    [Fact]
    public void A_tar_rewrite_keeps_links()
    {
        using var dir = new TempDir();
        var archive = Path.Combine(dir.Path, "links.tar.gz");
        using (var fs = File.Create(archive))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "target.txt") { DataStream = new MemoryStream("t"u8.ToArray()) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link.txt") { LinkName = "target.txt" });
        }

        var changes = new ArchiveChanges();
        changes.Renames.Add(("target.txt", "renamed.txt"));
        ArchiveWriter.Apply(archive, changes);

        using var input = File.OpenRead(archive);
        using var unzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new TarReader(unzip);
        var entries = new List<(string, TarEntryType, string)>();
        while (reader.GetNextEntry() is { } e)
            entries.Add((e.Name, e.EntryType, e.LinkName));
        entries.ShouldContain(("renamed.txt", TarEntryType.RegularFile, ""));
        entries.ShouldContain(("link.txt", TarEntryType.SymbolicLink, "target.txt"));
    }

    [Fact]
    public void A_big_file_round_trips_through_7z()
    {
        using var dir = new TempDir();
        var archive = Path.Combine(dir.Path, "big.7z");
        var data = new byte[3 * 1024 * 1024 + 17];
        new Random(42).NextBytes(data.AsSpan(0, data.Length / 2)); // half noise, half zeros
        ArchiveWriter.CreateEmpty(archive);
        var changes = new ArchiveChanges();
        changes.Adds.Add(new ArchiveAdd("bin/data.bin", false, () => new MemoryStream(data), DateTime.Now, null));
        ArchiveWriter.Apply(archive, changes);

        using var reader = ArchiveReader.Open(archive);
        using var s = reader.Open(reader.Items.Single(x => x.Key == "bin/data.bin"));
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.ToArray().ShouldBe(data);
    }

    // libarchive shares no code with SharpCompress: if bsdtar lists and extracts it, it's a real 7z
    [Fact]
    public void Bsdtar_reads_what_the_7z_writer_writes()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/bsdtar"), "needs bsdtar (libarchive)");
        using var dir = new TempDir();
        var archive = Path.Combine(dir.Path, "check.7z");
        ArchiveWriter.CreateEmpty(archive);
        var changes = new ArchiveChanges();
        changes.Adds.Add(ArchiveAdd.Folder("folder"));
        changes.Adds.Add(Text("folder/note.txt", "hello from the writer"));
        changes.Adds.Add(Text("empty.txt", ""));
        changes.Adds.Add(Text("ünïcode name.txt", "names are utf-16"));
        changes.Adds.Add(Text("run.sh", "#!/bin/sh", (UnixFileMode)0b111_101_101));
        ArchiveWriter.Apply(archive, changes);

        Run("/usr/bin/bsdtar", "-tvf", archive).ShouldContain("-rwxr-xr-x");
        var listing = Run("/usr/bin/bsdtar", "-tf", archive);
        listing.ShouldContain("folder/note.txt");
        listing.ShouldContain("empty.txt");
        listing.ShouldContain("ünïcode name.txt");

        var outDir = dir.Dir("out");
        Run("/usr/bin/bsdtar", "-xf", archive, "-C", outDir);
        File.ReadAllText(Path.Combine(outDir, "folder", "note.txt")).ShouldBe("hello from the writer");
        File.ReadAllText(Path.Combine(outDir, "ünïcode name.txt")).ShouldBe("names are utf-16");
        new FileInfo(Path.Combine(outDir, "empty.txt")).Length.ShouldBe(0);
        File.GetLastWriteTime(Path.Combine(outDir, "folder", "note.txt")).ShouldBe(new DateTime(2024, 5, 6, 7, 8, 10), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Read_only_formats_say_so()
    {
        using var dir = new TempDir();
        var archive = dir.File("a.rar", "not really");
        var changes = new ArchiveChanges();
        changes.Deletes.Add("x");
        Should.Throw<NotSupportedException>(() => ArchiveWriter.Apply(archive, changes));
    }

    static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.ShouldBe(0, error);
        return output.Normalize(NormalizationForm.FormC); // macOS tools print names decomposed
    }
}
