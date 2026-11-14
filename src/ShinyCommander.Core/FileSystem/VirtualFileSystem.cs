using ShinyCommander.Core.Models;
using ShinyCommander.Core.Remote;

namespace ShinyCommander.Core.FileSystem;

/// <summary>
/// Resolves panel paths to the local disk, an archive (zip, tar, 7z, rar, gz…) or a server / storage account
/// (sftp://, ftp://, dav://, s3://, az://) and lists/opens their contents.
/// </summary>
public sealed class VirtualFileSystem(RemoteSessions remote)
{
    public VirtualFileSystem() : this(new RemoteSessions()) { }

    public static char Sep => Path.DirectorySeparatorChar;

    /// <summary>The saved connections and their live sessions.</summary>
    public RemoteSessions Remote => remote;

    public VfsLocation Resolve(string path)
    {
        path = Normalize(path);
        if (RemotePath.IsRemote(path))
            return new VfsLocation(VfsKind.Remote, path, null, RemotePath.Split(path).Inner, remote.FindRequired(path));

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
            if (loc.IsRemote)
            {
                if (loc.InnerPath == "/")
                    return true;

                var (fs, inner) = remote.Get(loc.Path);
                return fs.GetItemAsync(inner).GetAwaiter().GetResult() is { IsDirectory: true };
            }
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
        if (RemotePath.IsRemote(path))
            return RemotePath.Normalize(path);

        if (path == "~" || path.StartsWith("~/"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];

        var full = Path.GetFullPath(path);
        // keep roots ("/", "C:\") intact, trim trailing separators elsewhere
        var root = Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0))
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return full;
    }

    /// <summary>Returns the parent of a panel path, or null at a drive/file-system root (or a connection's root).</summary>
    public string? GetParent(string path) => GetDirectory(Normalize(path));

    /// <summary>Path.GetDirectoryName that leaves URIs intact; null at a root.</summary>
    public static string? GetDirectory(string path)
    {
        if (RemotePath.IsRemote(path))
            return RemotePath.GetParent(path);

        var parent = Path.GetDirectoryName(path);
        return String.IsNullOrEmpty(parent) ? null : parent;
    }

    /// <summary>Path.GetFileName that understands URIs.</summary>
    public static string GetName(string path) => RemotePath.IsRemote(path) ? RemotePath.GetName(path) : Path.GetFileName(path);

    public static string Combine(string dir, string name) => RemotePath.IsRemote(dir) ? RemotePath.Combine(dir, name) : Path.Combine(dir, name);

    /// <summary>Whether a typed path is absolute - rooted on disk, "~", or a URI.</summary>
    public static bool IsAbsolute(string path) => RemotePath.IsRemote(path) || Path.IsPathRooted(path) || path.StartsWith('~');

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string path, bool showHidden, CancellationToken ct = default)
    {
        var location = this.Resolve(path);
        var list = location.IsRemote
            ? await this.ListRemoteAsync(location, ct).ConfigureAwait(false)
            : await Task.Run(() => location.IsArchive
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

    async Task<List<FileEntry>> ListRemoteAsync(VfsLocation location, CancellationToken ct)
    {
        var (fs, inner) = await remote.GetAsync(location.Path, ct).ConfigureAwait(false);
        var items = await fs.ListAsync(inner, ct).ConfigureAwait(false);
        return items.Select(x => FromRemote(location.Path, x)).ToList();
    }

    internal static FileEntry FromRemote(string dir, RemoteItem item) => new()
    {
        Name = item.Name,
        FullPath = RemotePath.Combine(dir, item.Name),
        IsDirectory = item.IsDirectory,
        Size = item.IsDirectory ? 0 : item.Size,
        Modified = item.Modified,
        Attributes = item.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
        UnixMode = item.Mode,
        IsRemote = true
    };

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
        if (RemotePath.IsRemote(path))
        {
            var (fs, inner) = remote.Get(path);
            return fs.OpenReadAsync(inner).GetAwaiter().GetResult();
        }
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

    /// <summary>Like <see cref="OpenRead"/>, but connects to a server asynchronously (asking for a password if needed).</summary>
    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        if (!RemotePath.IsRemote(path))
            return await Task.Run(() => this.OpenRead(path), ct).ConfigureAwait(false);

        var (fs, inner) = await remote.GetAsync(path, ct).ConfigureAwait(false);
        return await fs.OpenReadAsync(inner, ct).ConfigureAwait(false);
    }

    /// <summary>A folder on a server: the total size of the files under it, walking every folder.</summary>
    public async Task<long> CalculateRemoteFolderSizeAsync(string path, CancellationToken ct = default)
    {
        var (fs, inner) = await remote.GetAsync(path, ct).ConfigureAwait(false);
        long total = 0;
        var pending = new Stack<string>([inner]);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            foreach (var item in await fs.ListAsync(dir, ct).ConfigureAwait(false))
            {
                if (item.IsDirectory)
                    pending.Push(RemotePath.CombineInner(dir, item.Name));
                else
                    total += item.Size;
            }
        }
        return total;
    }

    internal static ArchiveItem? FindItem(IArchiveReader reader, string innerPath)
        => reader.Items.FirstOrDefault(e => !e.IsDirectory && e.Key.Equals(innerPath, StringComparison.Ordinal));

    /// <summary>A folder inside an archive: the total size of the files under it, from the archive's listing.</summary>
    public Task<long> CalculateArchiveFolderSizeAsync(string path) => Task.Run(() =>
    {
        var location = this.Resolve(path);
        if (!location.IsArchive)
            throw new ArgumentException($"'{path}' is not inside an archive", nameof(path));

        using var reader = ArchiveReader.Open(location.ArchiveFile!);
        var prefix = location.InnerPath + "/";
        return reader.Items.Where(x => !x.IsDirectory && x.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(x => x.Size);
    });

    /// <summary>
    /// Archive files can't be handed to other processes, so extract to a temp folder (for "open with", the
    /// viewer, the editor). Local paths come back untouched.
    /// </summary>
    public async Task<string> MaterializeAsync(string path, CancellationToken ct = default)
    {
        if (!RemotePath.IsRemote(path) && File.Exists(path))
            return path;

        var temp = Path.Combine(Path.GetTempPath(), "ShinyCommander", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var target = Path.Combine(temp, GetName(path));
        await using var src = await this.OpenReadAsync(path, ct).ConfigureAwait(false);
        await using var dst = File.Create(target);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return target;
    }
}
