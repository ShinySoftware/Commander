using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace ShinyCommander.UITests.Driver;

/// <summary>A throwaway folder a test fills before pointing a panel at it.</summary>
public sealed class TestTree(string root)
{
    public string Path { get; } = Directory.CreateDirectory(root).FullName;

    public string this[string relative] => System.IO.Path.Combine(this.Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public TestTree File(string relative, string content = "x", DateTime? modified = null)
    {
        var full = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        if (modified is { } m)
            System.IO.File.SetLastWriteTime(full, m);
        return this;
    }

    public TestTree Bytes(string relative, byte[] content)
    {
        var full = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return this;
    }

    public TestTree Dir(string relative)
    {
        Directory.CreateDirectory(this[relative]);
        return this;
    }

    public TestTree Zip(string relative, params (string Name, string Content)[] entries)
    {
        var full = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        using var zip = ZipFile.Open(full, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(content));
        }
        return this;
    }

    public TestTree TarGz(string relative, params (string Name, string Content)[] entries)
    {
        var full = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        using var fs = System.IO.File.Create(full);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz, TarEntryFormat.Gnu);
        foreach (var (name, content) in entries)
            tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        return this;
    }

    public bool Exists(string relative) => System.IO.File.Exists(this[relative]) || Directory.Exists(this[relative]);

    public string Read(string relative) => System.IO.File.ReadAllText(this[relative]);

    public List<string> Names() => Directory.EnumerateFileSystemEntries(this.Path).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToList()!;

    /// <summary>A tiny valid PNG (1x1), for image previews and thumbnails.</summary>
    public static byte[] Png1x1 { get; } = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");
}
