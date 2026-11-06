namespace ShinyCommander.Core.Models;

/// <summary>
/// A single row in a file panel - a real file/folder, an entry inside an archive, or the ".." parent link.
/// </summary>
public sealed record FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public FileAttributes Attributes { get; init; }
    public UnixFileMode? UnixMode { get; init; }
    public bool IsParentLink { get; init; }
    public bool IsInArchive { get; init; }
    public bool IsSymlink { get; init; }

    public string Extension => this.IsDirectory || this.IsParentLink
        ? String.Empty
        : Path.GetExtension(this.Name).TrimStart('.');

    public string NameWithoutExtension => this.IsDirectory || this.IsParentLink || this.Name.StartsWith('.') && this.Name.IndexOf('.', 1) < 0
        ? this.Name
        : Path.GetFileNameWithoutExtension(this.Name);

    public bool IsHidden => !this.IsParentLink && (this.Name.StartsWith('.') || this.Attributes.HasFlag(FileAttributes.Hidden));

    public bool IsArchive => !this.IsDirectory && ArchiveFormats.IsSupported(this.Name);

    /// <summary>TC shows "rahs" on Windows; we show the unix mode string where available.</summary>
    public string AttributeText
    {
        get
        {
            if (this.IsParentLink)
                return String.Empty;

            if (this.UnixMode is { } mode)
                return FormatUnixMode(mode, this.IsDirectory, this.IsSymlink);

            Span<char> c = stackalloc char[4];
            c[0] = this.Attributes.HasFlag(FileAttributes.ReadOnly) ? 'r' : '-';
            c[1] = this.Attributes.HasFlag(FileAttributes.Archive) ? 'a' : '-';
            c[2] = this.Attributes.HasFlag(FileAttributes.Hidden) ? 'h' : '-';
            c[3] = this.Attributes.HasFlag(FileAttributes.System) ? 's' : '-';
            return new string(c);
        }
    }

    internal static string FormatUnixMode(UnixFileMode m, bool dir, bool link)
    {
        Span<char> c = stackalloc char[10];
        c[0] = link ? 'l' : dir ? 'd' : '-';
        c[1] = m.HasFlag(UnixFileMode.UserRead) ? 'r' : '-';
        c[2] = m.HasFlag(UnixFileMode.UserWrite) ? 'w' : '-';
        c[3] = m.HasFlag(UnixFileMode.UserExecute) ? 'x' : '-';
        c[4] = m.HasFlag(UnixFileMode.GroupRead) ? 'r' : '-';
        c[5] = m.HasFlag(UnixFileMode.GroupWrite) ? 'w' : '-';
        c[6] = m.HasFlag(UnixFileMode.GroupExecute) ? 'x' : '-';
        c[7] = m.HasFlag(UnixFileMode.OtherRead) ? 'r' : '-';
        c[8] = m.HasFlag(UnixFileMode.OtherWrite) ? 'w' : '-';
        c[9] = m.HasFlag(UnixFileMode.OtherExecute) ? 'x' : '-';
        return new string(c);
    }

    public static FileEntry ParentLink(string parentPath) => new()
    {
        Name = "..",
        FullPath = parentPath,
        IsDirectory = true,
        IsParentLink = true
    };

    public static FileEntry FromInfo(FileSystemInfo info)
    {
        var isDir = info is DirectoryInfo;
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows())
        {
            try { mode = info.UnixFileMode; } catch { /* broken link etc */ }
        }

        long size = 0;
        if (info is FileInfo fi)
        {
            try { size = fi.Length; } catch { }
        }

        DateTime modified;
        try { modified = info.LastWriteTime; } catch { modified = DateTime.MinValue; }

        return new FileEntry
        {
            Name = info.Name,
            FullPath = info.FullName,
            IsDirectory = isDir,
            Size = size,
            Modified = modified,
            Attributes = SafeAttributes(info),
            UnixMode = mode,
            IsSymlink = info.LinkTarget != null
        };
    }

    static FileAttributes SafeAttributes(FileSystemInfo info)
    {
        try { return info.Attributes; } catch { return FileAttributes.Normal; }
    }
}
