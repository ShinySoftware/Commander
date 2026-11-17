using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Metadata;

/// <summary>What a file is for the custom columns - picked by name, so a folder can be sorted into kinds without opening a file.</summary>
public enum FileKind
{
    Other,
    Image,
    Video,
    Audio,
    Document,
    Text,
    Archive,
    App
}

public static class FileKinds
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif",
        "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "pef", "raf", "srw"
    };
    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "mpg", "mpeg", "3gp", "mts", "m2ts" };
    static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { "mp3", "m4a", "m4b", "aac", "flac", "ogg", "oga", "opus", "wav", "aif", "aiff", "wma", "ape", "wv", "alac", "dsf" };
    static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase) { "pdf", "docx", "docm", "xlsx", "xlsm", "pptx", "pptm" };
    static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "log", "md", "markdown", "csv", "tsv", "ini", "cfg", "conf", "json", "xml", "yml", "yaml", "toml", "html", "htm", "css", "scss",
        "cs", "xaml", "csproj", "props", "targets", "razor", "js", "ts", "tsx", "jsx", "py", "rb", "go", "rs", "java", "kt", "swift",
        "c", "cpp", "h", "hpp", "m", "mm", "sh", "ps1", "sql"
    };
    static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase) { "exe", "dll" };
    // archives that list their files without unpacking them all - a tar.gz would decompress to count
    static readonly HashSet<ArchiveKind> IndexedArchives = [ArchiveKind.Zip, ArchiveKind.SevenZip, ArchiveKind.Rar];

    public static FileKind Of(FileEntry entry)
    {
        if (entry.IsParentLink)
            return FileKind.Other;

        if (entry.IsDirectory)
            return IsAppBundle(entry.Name) ? FileKind.App : FileKind.Other;

        var ext = entry.Extension;
        if (Images.Contains(ext))
            return FileKind.Image;
        if (Videos.Contains(ext))
            return FileKind.Video;
        if (Audio.Contains(ext))
            return FileKind.Audio;
        if (Documents.Contains(ext))
            return FileKind.Document;
        if (Text.Contains(ext))
            return FileKind.Text;
        if (Executables.Contains(ext))
            return FileKind.App;
        if (IndexedArchives.Contains(ArchiveFormats.GetKind(entry.Name)))
            return FileKind.Archive;
        return FileKind.Other;
    }

    /// <summary>A macOS application - a folder the Finder shows as one file.</summary>
    public static bool IsAppBundle(string name) => name.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
}
