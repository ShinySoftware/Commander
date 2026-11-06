namespace ShinyCommander.Core.FileSystem;

public enum VfsKind
{
    Local,
    Archive
}

/// <summary>
/// A resolved panel path. Archive paths look like a normal path that walks "through" the archive file,
/// e.g. <c>/Users/me/stuff.zip/docs/readme.md</c> - the same convention Total Commander uses.
/// </summary>
public sealed record VfsLocation(VfsKind Kind, string Path, string? ArchiveFile = null, string InnerPath = "")
{
    public bool IsArchive => this.Kind == VfsKind.Archive;
    public bool IsArchiveRoot => this.IsArchive && this.InnerPath.Length == 0;

    /// <summary>Only zip archives can be changed in place; tar/7z/rar/gz are browse + extract only.</summary>
    public bool IsReadOnlyArchive => this.IsArchive && !Models.ArchiveFormats.IsWritable(this.ArchiveFile!);
}
