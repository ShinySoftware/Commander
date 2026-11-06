using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.FileSystem;

/// <summary>
/// Resolves panel paths to either the local disk or an archive (zip, tar, 7z, rar, gz…) and lists/opens their contents.
/// </summary>
public sealed class VirtualFileSystem
{
    public static char Sep => Path.DirectorySeparatorChar;

    public VfsLocation Resolve(string path)
    {
        path = Normalize(path);
        if (Directory.Exists(path))
            return new VfsLocation(VfsKind.Local, path);

        // walk up until we find an archive file
        var current = path;
        while (!String.IsNullOrEmpty(current))
        {
            if (File.Exists(current) && ArchiveFormats.IsSupported(current))
            {
                var inner = path.Length > current.Length
                    ? path[(current.Length + 1)..].Replace('\\', '/').Trim('/')
                    : String.Empty;
                return new VfsLocation(VfsKind.Archive, path, current, inner);
            }
            current = Path.GetDirectoryName(current);
        }
        throw new DirectoryNotFoundException($"Path not found: {path}");
    }

    public bool Exists(string path)
    {
        try
        {
            var loc = this.Resolve(path);
            if (!loc.IsArchive || loc.IsArchiveRoot)
                return true;

            using var reader = ArchiveReader.Open(loc.ArchiveFile!);
            var prefix = loc.InnerPath + "/";
            return reader.Items.Any(e => e.IsDirectory && e.Key == loc.InnerPath || e.Key.StartsWith(prefix, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    public static string Normalize(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        path = path.Trim();
        if (path == "~" || path.StartsWith("~/"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];

        var full = Path.GetFullPath(path);
        // keep roots ("/", "C:\") intact, trim trailing separators elsewhere
        var root = Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0))
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return full;
    }

    /// <summary>Returns the parent of a panel path, or null at a drive/file-system root.</summary>
    public string? GetParent(string path)
    {
        var normalized = Normalize(path);
        var parent = Path.GetDirectoryName(normalized);
        return String.IsNullOrEmpty(parent) ? null : parent;
    }

    public static string Combine(string dir, string name) => Path.Combine(dir, name);

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string path, bool showHidden, CancellationToken ct = default)
    {
        var location = this.Resolve(path);
        var list = await Task.Run(() => location.IsArchive
            ? ListArchive(location)
            : ListLocal(location.Path), ct).ConfigureAwait(false);

        if (!showHidden)
            list = list.Where(x => !x.IsHidden).ToList();

        var parent = this.GetParent(location.Path);
        if (parent != null)
            list.Insert(0, FileEntry.ParentLink(parent));

        return list;
    }

    static List<FileEntry> ListLocal(string path)
    {
        var dir = new DirectoryInfo(path);
        var list = new List<FileEntry>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
            list.Add(FileEntry.FromInfo(info));

        return list;
    }

    static List<FileEntry> ListArchive(VfsLocation location)
    {
        using var reader = ArchiveReader.Open(location.ArchiveFile!);
        var prefix = location.InnerPath.Length == 0 ? String.Empty : location.InnerPath + "/";
        var files = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        var dirs = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var entry in reader.Items)
        {
            if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rest = entry.Key[prefix.Length..];
            if (rest.Length == 0)
                continue;

            var slash = rest.IndexOf('/');
            if (slash >= 0 || entry.IsDirectory)
            {
                // a folder entry, or a folder implied by a deeper path
                var dirName = slash >= 0 ? rest[..slash] : rest;
                var stamp = entry.Modified;
                if (!dirs.TryGetValue(dirName, out var existing) || existing < stamp)
                    dirs[dirName] = stamp;
            }
            else
            {
                files[rest] = new FileEntry
                {
                    Name = rest,
                    FullPath = Path.Combine(location.Path, rest),
                    Size = entry.Size,
                    Modified = entry.Modified,
                    Attributes = FileAttributes.Normal,
                    UnixMode = entry.Mode,
                    IsInArchive = true
                };
            }
        }

        var list = new List<FileEntry>(files.Count + dirs.Count);
        foreach (var (name, stamp) in dirs)
        {
            list.Add(new FileEntry
            {
                Name = name,
                FullPath = Path.Combine(location.Path, name),
                IsDirectory = true,
                Modified = stamp,
                Attributes = FileAttributes.Directory,
                IsInArchive = true
            });
        }
        list.AddRange(files.Values);
        return list;
    }

    /// <summary>Opens a file for reading regardless of whether it lives on disk or inside an archive.</summary>
    public Stream OpenRead(string path)
    {
        if (File.Exists(path))
            return File.OpenRead(path);

        var location = this.Resolve(path);
        if (!location.IsArchive)
            throw new FileNotFoundException(path);

        using var reader = ArchiveReader.Open(location.ArchiveFile!);
        var entry = FindItem(reader, location.InnerPath) ?? throw new FileNotFoundException(path);

        // copy so the caller can seek + so we can dispose the archive
        var ms = new MemoryStream();
        using (var s = reader.Open(entry))
            s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    internal static ArchiveItem? FindItem(IArchiveReader reader, string innerPath)
        => reader.Items.FirstOrDefault(e => !e.IsDirectory && e.Key.Equals(innerPath, StringComparison.Ordinal));

    /// <summary>
    /// Archive files can't be handed to other processes, so extract to a temp folder (for "open with", the
    /// viewer, the editor). Local paths come back untouched.
    /// </summary>
    public async Task<string> MaterializeAsync(string path, CancellationToken ct = default)
    {
        if (File.Exists(path))
            return path;

        var temp = Path.Combine(Path.GetTempPath(), "ShinyCommander", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var target = Path.Combine(temp, Path.GetFileName(path));
        await using var src = this.OpenRead(path);
        await using var dst = File.Create(target);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return target;
    }
}
