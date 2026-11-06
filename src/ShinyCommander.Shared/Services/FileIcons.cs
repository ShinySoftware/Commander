namespace ShinyCommander.Services;

public static class FileIcons
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "bmp", "webp", "svg", "ico", "heic", "tif", "tiff" };
    static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { "mp3", "wav", "flac", "aac", "ogg", "m4a" };
    static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase) { "mp4", "mov", "mkv", "avi", "webm", "m4v" };
    static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase) { "cs", "xaml", "csproj", "sln", "slnx", "props", "targets", "js", "ts", "tsx", "jsx", "json", "xml", "html", "css", "scss", "py", "rb", "go", "rs", "java", "kt", "swift", "c", "cpp", "h", "hpp", "m", "sh", "ps1", "yml", "yaml", "toml", "razor", "sql" };
    static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase) { "txt", "log", "ini", "cfg", "conf", "csv", "tsv", "env", "gitignore", "editorconfig" };
    static readonly HashSet<string> Markdown = new(StringComparer.OrdinalIgnoreCase) { "md", "markdown" };
    static readonly HashSet<string> Exec = new(StringComparer.OrdinalIgnoreCase) { "exe", "msi", "app", "dmg", "pkg", "bat", "cmd", "com", "appimage", "deb", "rpm" };

    public static string For(FileEntry e)
    {
        if (e.IsParentLink) return "⤴";
        if (e.IsDirectory) return e.IsSymlink ? "🔗" : "📁";
        if (e.IsArchive) return "🗜";
        var ext = e.Extension;
        if (Images.Contains(ext)) return "🖼";
        if (Audio.Contains(ext)) return "🎵";
        if (Video.Contains(ext)) return "🎬";
        if (Markdown.Contains(ext)) return "📝";
        if (Code.Contains(ext)) return "📜";
        if (Text.Contains(ext)) return "📄";
        if (Exec.Contains(ext)) return "⚙";
        if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return "📕";
        return "📄";
    }

    /// <summary>Motion icon name + colour per file type - animated in the panels and the ribbon.</summary>
    public static (string Icon, string Color) Motion(FileEntry e)
    {
        if (e.IsParentLink) return ("arrow-up", "#64748B");
        if (e.IsDirectory) return e.IsSymlink ? ("link", "#F59E0B") : ("folder", "#F5A524");
        if (e.IsArchive) return ("database", "#A855F7");
        var ext = e.Extension;
        if (Images.Contains(ext)) return ("image", "#EC4899");
        if (Audio.Contains(ext)) return ("music", "#06B6D4");
        if (Video.Contains(ext)) return ("video", "#EF4444");
        if (Markdown.Contains(ext)) return ("book", "#3B82F6");
        if (Code.Contains(ext)) return ("file-text", "#22C55E");
        if (Text.Contains(ext)) return ("file-text", "#94A3B8");
        if (Exec.Contains(ext)) return ("rocket", "#F97316");
        if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return ("book", "#DC2626");
        return ("file", "#94A3B8");
    }

    public static bool IsImage(string ext) => Images.Contains(ext) && !ext.Equals("svg", StringComparison.OrdinalIgnoreCase);
    public static bool IsMarkdown(string ext) => Markdown.Contains(ext);
    public static bool IsLikelyText(string ext) => Code.Contains(ext) || Text.Contains(ext) || Markdown.Contains(ext);
}
