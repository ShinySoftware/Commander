using Shiny.Net.HttpServer.WebDav;

namespace ShinyCommander.Share;

/// <summary>
/// One share's folder, as both the WebDAV mount and the browser API see it. Every path is relative to the share and
/// '/' separated; nothing resolves outside the folder - not through "..", and not through a link that points out of
/// it. What the share allows is enforced here, so the WebDAV verbs and the JSON API can't disagree.
/// </summary>
sealed class ShareFileSystem(ServedFolder share, TransferMonitor monitor, Func<ClientContext?> client) : IWebDavFileSystem
{
    readonly string root = Canonical(share.RootPath);

    public ServedFolder Share => share;

    // ------------------------------------------------------------------------------------------- paths

    /// <summary>The full path for a share-relative one, or null when it would escape the share or is hidden.</summary>
    public string? Resolve(string? relative)
    {
        var parts = Split(relative);
        if (parts == null)
            return null;

        var full = parts.Length == 0 ? this.root : Path.Combine([this.root, .. parts]);
        if (!IsInside(this.root, Path.GetFullPath(full)))
            return null;

        // a link anywhere along the way must land inside the share as well
        var walked = this.root;
        foreach (var part in parts)
        {
            walked = Path.Combine(walked, part);
            if (!this.ShowHidden && part.StartsWith('.'))
                return null;

            var target = LinkTarget(walked);
            if (target != null && !IsInside(this.root, target))
                return null;
        }
        return full;
    }

    /// <summary>Path segments, or null for anything with "..", "." or an empty / rooted segment.</summary>
    public static string[]? Split(string? relative)
    {
        if (String.IsNullOrEmpty(relative))
            return [];

        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalid = Path.GetInvalidFileNameChars();
        var bad = parts.Any(x => x is "." or ".." || x.IndexOfAny(invalid) >= 0 || x.Contains(':'));
        return bad ? null : parts;
    }

    /// <summary>The share-relative path of a full one under the root.</summary>
    public string Relative(string full) => Path.GetRelativePath(this.root, full).Replace(Path.DirectorySeparatorChar, '/') is var r && r == "." ? "" : r;

    bool ShowHidden => share.Options.ShowHidden;

    bool IsVisible(FileSystemInfo info)
    {
        if (this.ShowHidden)
            return true;

        return !info.Name.StartsWith('.') && !info.Attributes.HasFlag(FileAttributes.Hidden);
    }

    static string Canonical(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var target = LinkTarget(full);
        return target ?? full;
    }

    /// <summary>Where a link finally points (full path), or null when <paramref name="path"/> is not a link.</summary>
    static string? LinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget == null)
                return null;

            var final = info.ResolveLinkTarget(true);
            return final == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(final.FullName));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool IsInside(string root, string full)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Equals(root, comparison) || trimmed.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    // ------------------------------------------------------------------------------------------- listing

    /// <summary>The visible entries of a folder, folders first.</summary>
    public IReadOnlyList<FileSystemInfo> List(string full)
    {
        var dir = new DirectoryInfo(full);
        return dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Where(this.IsVisible)
            .Where(this.StaysInside)
            .OrderByDescending(x => x is DirectoryInfo)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    bool StaysInside(FileSystemInfo info)
    {
        if (info.LinkTarget == null)
            return true;

        var target = LinkTarget(info.FullName);
        return target != null && IsInside(this.root, target);
    }

    // ------------------------------------------------------------------------------------------- access rules

    void RequireList()
    {
        if (!share.CanList)
            throw new WebDavException(403);
    }

    void RequireUpload()
    {
        if (!share.CanUpload)
            throw new WebDavException(403);
    }

    void RequireModify()
    {
        if (!share.CanModify)
            throw new WebDavException(403);
    }

    string ResolveOrThrow(string path) => this.Resolve(path) ?? throw new WebDavException(404);

    // ------------------------------------------------------------------------------------------- tracked streams

    /// <summary>Opens a file for download, counted against a transfer.</summary>
    public Stream OpenDownload(string full)
    {
        var info = new FileInfo(full);
        var stream = new FileStream(full, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        var who = client();
        var transfer = monitor.Start(share, who?.Ip ?? "?", this.Relative(full), TransferDirection.Download, info.Length, who?.IsTunneled ?? false);
        return new TrackedStream(stream, transfer, monitor);
    }

    /// <summary>
    /// Writes an upload to a temporary file beside its destination, then moves it into place - a visitor who goes away
    /// half way leaves nothing half written behind.
    /// </summary>
    public async Task SaveUploadAsync(string full, Stream content, long? length, bool overwrite, CancellationToken ct)
    {
        if (share.Options.MaxUploadBytes is { } max && length > max)
            throw new WebDavException(413);

        if (File.Exists(full) && !overwrite)
            throw new WebDavException(412);

        var directory = Path.GetDirectoryName(full)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.part");
        var who = client();
        var transfer = monitor.Start(share, who?.Ip ?? "?", this.Relative(full), TransferDirection.Upload, length, who?.IsTunneled ?? false);
        var tracked = new TrackedStream(File.Create(temp), transfer, monitor);
        var ok = false;
        try
        {
            await content.CopyToAsync(tracked, 81920, ct).ConfigureAwait(false);
            await tracked.FlushAsync(ct).ConfigureAwait(false);
            ok = true;
        }
        catch (OperationCanceledException)
        {
            tracked.End(TransferState.Cancelled, "The upload was stopped");
            throw;
        }
        catch (Exception ex)
        {
            tracked.End(TransferState.Failed, ex.Message);
            throw;
        }
        finally
        {
            await tracked.DisposeAsync().ConfigureAwait(false);
            if (!ok)
                TryDelete(temp);
        }

        File.Move(temp, full, overwrite);
    }

    /// <summary>A file that's already complete (a finished tus upload) moved into the share, counted as an upload.</summary>
    public void Place(string sourceFile, string full, bool overwrite)
    {
        if (File.Exists(full) && !overwrite)
            full = FreeName(full);

        File.Move(sourceFile, full, overwrite);
    }

    /// <summary>"name (2).ext" - the first name not taken.</summary>
    public static string FreeName(string full)
    {
        var directory = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        var candidate = full;
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{name} ({i}){ext}");
        return candidate;
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------------------------------- IWebDavFileSystem

    WebDavEntry? IWebDavFileSystem.GetEntry(string path)
    {
        var full = this.Resolve(path);
        if (full == null)
            return null;

        // a drop box answers for its root (so it can be mounted and written to) and nothing inside it
        if (!share.CanList && path.Length > 0)
            return null;

        if (Directory.Exists(full))
        {
            var dir = new DirectoryInfo(full);
            return new WebDavEntry(path.Length == 0 ? "" : dir.Name, true, 0, dir.CreationTimeUtc, dir.LastWriteTimeUtc)
            {
                DisplayName = path.Length == 0 ? share.Name : null
            };
        }

        if (File.Exists(full))
        {
            var file = new FileInfo(full);
            return new WebDavEntry(file.Name, false, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc);
        }
        return null;
    }

    IEnumerable<WebDavEntry> IWebDavFileSystem.GetChildren(string path)
    {
        if (!share.CanList)
            return [];

        var full = this.ResolveOrThrow(path);
        return this.List(full).Select(x => x is FileInfo f
            ? new WebDavEntry(f.Name, false, f.Length, f.CreationTimeUtc, f.LastWriteTimeUtc)
            : new WebDavEntry(x.Name, true, 0, x.CreationTimeUtc, x.LastWriteTimeUtc)).ToList();
    }

    ValueTask<Stream> IWebDavFileSystem.OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        this.RequireList();
        var full = this.ResolveOrThrow(path);
        if (!File.Exists(full))
            throw new WebDavException(404);

        return ValueTask.FromResult(this.OpenDownload(full));
    }

    async ValueTask IWebDavFileSystem.WriteAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        this.RequireUpload();
        var full = this.ResolveOrThrow(path);
        var exists = File.Exists(full);

        // replacing a file is changing it - only a share that allows changes may
        if (exists && !share.CanModify)
            throw new WebDavException(403);

        var length = client()?.ContentLength;
        await this.SaveUploadAsync(full, content, length, true, cancellationToken).ConfigureAwait(false);
    }

    ValueTask IWebDavFileSystem.CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        this.RequireUpload();
        // a drop box takes files, not a folder tree it can't see
        this.RequireList();
        var full = this.ResolveOrThrow(path);
        Directory.CreateDirectory(full);
        this.Log(ActivityKind.CreateFolder, path);
        return ValueTask.CompletedTask;
    }

    ValueTask IWebDavFileSystem.DeleteAsync(string path, CancellationToken cancellationToken)
    {
        this.RequireModify();
        var full = this.ResolveOrThrow(path);
        if (path.Length == 0)
            throw new WebDavException(403);

        if (Directory.Exists(full))
            Directory.Delete(full, true);
        else
            File.Delete(full);

        this.Log(ActivityKind.Delete, path);
        return ValueTask.CompletedTask;
    }

    ValueTask IWebDavFileSystem.MoveAsync(string source, string destination, CancellationToken cancellationToken)
    {
        this.RequireModify();
        var from = this.ResolveOrThrow(source);
        var to = this.ResolveOrThrow(destination);
        if (source.Length == 0)
            throw new WebDavException(403);

        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);

        this.Log(ActivityKind.Rename, source, destination);
        return ValueTask.CompletedTask;
    }

    ValueTask IWebDavFileSystem.CopyAsync(string source, string destination, bool recursive, CancellationToken cancellationToken)
    {
        this.RequireList();
        this.RequireUpload();
        var from = this.ResolveOrThrow(source);
        var to = this.ResolveOrThrow(destination);

        if (File.Exists(from))
        {
            File.Copy(from, to);
            return ValueTask.CompletedTask;
        }

        Directory.CreateDirectory(to);
        if (recursive)
            CopyTree(from, to);

        return ValueTask.CompletedTask;
    }

    static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    WebDavQuota? IWebDavFileSystem.GetQuota(string path)
    {
        try
        {
            var drive = new DriveInfo(this.root);
            return new WebDavQuota(drive.AvailableFreeSpace, drive.TotalSize - drive.TotalFreeSpace);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Log(ActivityKind kind, string path, string? detail = null)
    {
        var who = client();
        monitor.Log(kind, who?.Ip ?? "?", share, path, detail, who?.IsTunneled ?? false);
    }
}

/// <summary>Who is asking - captured per request by the share middleware, since WebDAV calls carry no context.</summary>
sealed record ClientContext(string Ip, bool IsTunneled, long? ContentLength);
