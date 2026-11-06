namespace ShinyCommander.Core.Models;

public enum ArchiveKind
{
    None,
    Zip,
    Tar,
    TarGz,
    TarBz2,
    TarXz,
    TarZst,
    SevenZip,
    Rar,
    GZip,
    BZip2,
    Xz
}

/// <summary>
/// Which files the panels can enter like a folder. Zip (and the zip-container family) is read/write through
/// System.IO.Compression; tar (plain or gz/bz2/xz/zstd compressed), 7z and rar are read-only through
/// SharpCompress, and a lone .gz/.bz2/.xz shows up as an archive holding the one decompressed file.
/// </summary>
public static class ArchiveFormats
{
    // plain zip containers
    static readonly HashSet<string> ZipFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".jar", ".nupkg", ".snupkg", ".apk", ".aab", ".epub", ".vsix", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".ipa", ".whl"
    };

    // compound suffixes first so ".tar.gz" wins over ".gz"
    static readonly (string Suffix, ArchiveKind Kind)[] Suffixes =
    [
        (".tar.gz", ArchiveKind.TarGz),
        (".tgz", ArchiveKind.TarGz),
        (".tar.bz2", ArchiveKind.TarBz2),
        (".tbz2", ArchiveKind.TarBz2),
        (".tbz", ArchiveKind.TarBz2),
        (".tar.xz", ArchiveKind.TarXz),
        (".txz", ArchiveKind.TarXz),
        (".tar.zst", ArchiveKind.TarZst),
        (".tzst", ArchiveKind.TarZst),
        (".tar", ArchiveKind.Tar),
        (".7z", ArchiveKind.SevenZip),
        (".rar", ArchiveKind.Rar),
        (".gz", ArchiveKind.GZip),
        (".bz2", ArchiveKind.BZip2),
        (".xz", ArchiveKind.Xz)
    ];

    /// <summary>Formats Pack (Alt+F5) can create, as typed in the target name.</summary>
    public static IReadOnlyList<string> PackExtensions { get; } = [".zip", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2"];

    public static ArchiveKind GetKind(string fileName)
    {
        if (ZipFamily.Contains(Path.GetExtension(fileName)))
            return ArchiveKind.Zip;

        foreach (var (suffix, kind) in Suffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && fileName.Length > suffix.Length)
                return kind;
        }
        return ArchiveKind.None;
    }

    public static bool IsSupported(string fileName) => GetKind(fileName) != ArchiveKind.None;

    /// <summary>Only zip archives can be changed in place (copy into, delete, rename, new folder).</summary>
    public static bool IsWritable(string fileName) => GetKind(fileName) == ArchiveKind.Zip;

    /// <summary>A lone compressed file rather than a container.</summary>
    public static bool IsSingleStream(ArchiveKind kind) => kind is ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz;

    public static bool CanPack(string fileName) => GetKind(fileName) is ArchiveKind.Zip or ArchiveKind.Tar or ArchiveKind.TarGz or ArchiveKind.TarBz2;

    /// <summary>"site.tar.gz" → "site", "notes.txt.gz" → "notes.txt", "a.zip" → "a".</summary>
    public static string StripExtension(string fileName)
    {
        var kind = GetKind(fileName);
        if (kind == ArchiveKind.None)
            return fileName;

        if (kind == ArchiveKind.Zip)
            return Path.GetFileNameWithoutExtension(fileName);

        foreach (var (suffix, k) in Suffixes)
        {
            if (k == kind && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName[..^suffix.Length];
        }
        return Path.GetFileNameWithoutExtension(fileName);
    }

    public static string Describe(ArchiveKind kind) => kind switch
    {
        ArchiveKind.Zip => "zip",
        ArchiveKind.Tar => "tar",
        ArchiveKind.TarGz => "tar.gz",
        ArchiveKind.TarBz2 => "tar.bz2",
        ArchiveKind.TarXz => "tar.xz",
        ArchiveKind.TarZst => "tar.zst",
        ArchiveKind.SevenZip => "7z",
        ArchiveKind.Rar => "rar",
        ArchiveKind.GZip => "gzip",
        ArchiveKind.BZip2 => "bzip2",
        ArchiveKind.Xz => "xz",
        _ => String.Empty
    };
}
