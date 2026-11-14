using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Models;
using ShinyCommander.Core.Remote;

namespace ShinyCommander.Core.Operations;

/// <summary>
/// Copy/move/delete/rename/pack/extract across the local disk and archives with progress, cancellation and
/// conflict resolution. Zip, 7z and tar (plain, gz, bz2, zst) archives can be changed like folders, through
/// <see cref="ArchiveWriter"/>; rar, tar.xz and lone gz/bz2/xz files are read-only. Servers and storage accounts
/// (<see cref="IRemoteFileSystem"/>) are read from and written to like folders.
/// </summary>
public sealed class FileOperationService(VirtualFileSystem vfs, ITrashService? trash = null)
{
    const int BufferSize = 1024 * 1024;

    sealed record PlanItem(
        string SourcePath,
        string RelativePath,
        bool IsDirectory,
        long Size,
        DateTime Modified,
        UnixFileMode? Mode,
        string? ArchiveFile = null,
        string? EntryName = null,
        bool FromRemote = false
    )
    {
        public bool FromArchive => this.ArchiveFile != null;
    }

    sealed class RunState(FileOperationKind kind, IProgress<OperationProgress>? progress, ConflictResolver? resolver)
    {
        public FileOperationKind Kind { get; } = kind;
        public ConflictResolver? Resolver { get; } = resolver;
        public ConflictAction? Sticky { get; set; }
        public long BytesTotal { get; set; }
        public long BytesDone { get; set; }
        public int ItemsTotal { get; set; }
        public int ItemsDone { get; set; }
        public int Skipped { get; set; }
        public HashSet<string> SkippedSources { get; } = new(StringComparer.Ordinal);
        public List<string> Errors { get; } = [];

        public void Report(string item) => progress?.Report(new OperationProgress(this.Kind, item, this.BytesDone, this.BytesTotal, this.ItemsDone, this.ItemsTotal));
    }

    /// <summary>
    /// Interprets the text typed into the copy/move destination box. A path that is an existing directory
    /// (or ends with a separator, or there are multiple sources) is a target folder; otherwise the last
    /// segment is the new name for the single source item.
    /// </summary>
    public OperationTarget ResolveTarget(string input, string currentDirectory, int sourceCount)
    {
        if (String.IsNullOrWhiteSpace(input))
            throw new ArgumentException("No target given");

        var raw = input.Trim();
        var endsWithSep = raw.EndsWith('/') || raw.EndsWith('\\');
        var full = VirtualFileSystem.IsAbsolute(raw)
            ? VirtualFileSystem.Normalize(raw)
            : VirtualFileSystem.Normalize(VirtualFileSystem.Combine(currentDirectory, raw));

        if (endsWithSep || sourceCount > 1 || vfs.Exists(full) && IsContainer(full))
            return new OperationTarget(full);

        var parent = VirtualFileSystem.GetDirectory(full);
        if (parent == null)
            return new OperationTarget(full);

        return new OperationTarget(parent, VirtualFileSystem.GetName(full));
    }

    bool IsContainer(string path)
    {
        if (RemotePath.IsRemote(path))
            return vfs.Exists(path);

        if (Directory.Exists(path))
            return true;

        if (File.Exists(path))
            return ArchiveFormats.IsSupported(path); // copying "into" a zip

        return vfs.Exists(path); // directory inside an archive
    }

    public Task<OperationResult> CopyAsync(
        IReadOnlyList<FileEntry> sources,
        OperationTarget target,
        ConflictResolver? onConflict = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default
    ) => Task.Run(() => this.Transfer(sources, target, false, onConflict, progress, ct), ct);

    public Task<OperationResult> MoveAsync(
        IReadOnlyList<FileEntry> sources,
        OperationTarget target,
        ConflictResolver? onConflict = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default
    ) => Task.Run(() => this.Transfer(sources, target, true, onConflict, progress, ct), ct);

    OperationResult Transfer(
        IReadOnlyList<FileEntry> sources,
        OperationTarget target,
        bool move,
        ConflictResolver? onConflict,
        IProgress<OperationProgress>? progress,
        CancellationToken ct
    )
    {
        var items = sources.Where(x => !x.IsParentLink).ToList();
        if (items.Count == 0)
            return OperationResult.Empty;

        var renameTo = items.Count == 1 ? target.RenameTo : null;
        var dest = vfs.Resolve(EnsureContainer(target.Directory));
        if (dest.IsReadOnlyArchive)
            throw ReadOnly(dest.ArchiveFile!);
        if (move && items.FirstOrDefault(x => x.IsInArchive && vfs.Resolve(Path.GetDirectoryName(x.FullPath)!).IsReadOnlyArchive) is { } ro)
            throw new NotSupportedException($"'{ro.Name}' is inside a read-only archive - copy it out instead of moving");

        var state = new RunState(move ? FileOperationKind.Move : FileOperationKind.Copy, progress, onConflict);

        // a move within one archive is a rename there - nothing needs unpacking
        if (move && dest.IsArchive && items.All(x => x.IsInArchive && this.ArchiveOf(x).File == dest.ArchiveFile))
            return this.MoveWithinArchive(items, dest, renameTo, state, ct);

        // and a move on one server is a rename there - nothing needs downloading
        if (move && dest.IsRemote && items.All(x => x.IsRemote && vfs.Remote.Find(x.FullPath)?.Id == dest.Connection!.Id))
            return this.MoveWithinRemote(items, dest, renameTo, state, ct);

        foreach (var item in items)
        {
            var name = renameTo ?? item.Name;
            var destFull = VirtualFileSystem.Combine(dest.Path, name);
            if (String.Equals(VirtualFileSystem.Normalize(item.FullPath), VirtualFileSystem.Normalize(destFull), PathComparison))
                throw new IOException($"'{item.Name}' cannot be copied onto itself");

            if (item.IsDirectory && !dest.IsArchive && !item.IsInArchive && item.IsRemote == dest.IsRemote && IsSubPath(item.FullPath, destFull))
                throw new IOException($"Cannot copy '{item.Name}' into its own subfolder");
        }

        // fast path: a plain rename on the same volume
        var remaining = items;
        if (move && !dest.IsArchive && !dest.IsRemote)
        {
            remaining = [];
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var destPath = Path.Combine(dest.Path, renameTo ?? item.Name);
                if (!item.IsInArchive && !item.IsRemote && !Path.Exists(destPath) && TryFastMove(item, destPath))
                {
                    state.ItemsDone++;
                    state.Report(item.Name);
                    continue;
                }
                remaining.Add(item);
            }
            if (remaining.Count == 0)
                return new OperationResult(state.ItemsDone, 0, state.Errors);
        }

        var plan = this.BuildPlan(remaining, renameTo, ct);
        state.ItemsTotal = plan.Count + state.ItemsDone;
        state.BytesTotal = plan.Sum(x => x.Size);
        state.Report(String.Empty);

        if (dest.IsArchive)
            this.WriteIntoArchive(plan, dest, state, ct);
        else if (dest.IsRemote)
            this.WriteToRemote(plan, dest, state, ct);
        else
            this.WriteToDisk(plan, dest.Path, state, ct);

        if (move)
            this.DeleteMovedSources(plan, state, ct);

        return new OperationResult(state.ItemsDone, state.Skipped, state.Errors);
    }

    static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    static bool IsSubPath(string parent, string child)
    {
        if (RemotePath.IsRemote(parent) || RemotePath.IsRemote(child))
            return RemotePath.IsRemote(parent) && RemotePath.IsRemote(child) && RemotePath.IsSameOrBelow(parent, child);

        var p = VirtualFileSystem.Normalize(parent) + Path.DirectorySeparatorChar;
        var c = VirtualFileSystem.Normalize(child) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, PathComparison);
    }

    string EnsureContainer(string dir)
    {
        if (vfs.Exists(dir))
            return dir;

        // TC creates missing target folders on copy
        if (RemotePath.IsRemote(dir))
        {
            var (fs, inner) = vfs.Remote.Get(dir);
            CreateRemoteDirectories(fs, inner, CancellationToken.None);
        }
        else
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    static bool TryFastMove(FileEntry item, string destPath)
    {
        try
        {
            if (item.IsDirectory)
                Directory.Move(item.FullPath, destPath);
            else
                File.Move(item.FullPath, destPath);
            return true;
        }
        catch (IOException)
        {
            return false; // cross device etc - fall back to copy + delete
        }
    }

    List<PlanItem> BuildPlan(IReadOnlyList<FileEntry> sources, string? renameTo, CancellationToken ct)
    {
        var plan = new List<PlanItem>();
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            var rootName = renameTo ?? src.Name;

            if (src.IsRemote)
                this.PlanRemote(plan, src, rootName, ct);
            else
                this.PlanLocalOrArchive(plan, src, rootName, ct);
        }
        return plan;
    }

    void PlanLocalOrArchive(List<PlanItem> plan, FileEntry src, string rootName, CancellationToken ct)
    {
        if (src.IsInArchive)
        {
            var loc = vfs.Resolve(Path.GetDirectoryName(src.FullPath)!);
            var inner = loc.InnerPath.Length == 0 ? src.Name : loc.InnerPath + "/" + src.Name;
            using var reader = ArchiveReader.Open(loc.ArchiveFile!);

            if (!src.IsDirectory)
            {
                var e = VirtualFileSystem.FindItem(reader, inner);
                if (e != null)
                    plan.Add(new PlanItem(src.FullPath, rootName, false, e.Size, e.Modified, e.Mode, loc.ArchiveFile, e.RawKey));
                return;
            }

            plan.Add(new PlanItem(src.FullPath, rootName, true, 0, src.Modified, null, loc.ArchiveFile, inner + "/"));
            var prefix = inner + "/";
            var seenDirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in reader.Items.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var rest = e.Key[prefix.Length..];
                // the folder itself plus implied intermediate folders
                var parts = rest.Split('/');
                for (var i = 0; i < parts.Length - (e.IsDirectory ? 0 : 1); i++)
                {
                    var rel = String.Join('/', parts.Take(i + 1));
                    if (seenDirs.Add(rel))
                        plan.Add(new PlanItem(Path.Combine(src.FullPath, rel), JoinRel(rootName, rel), true, 0, e.Modified, null, loc.ArchiveFile, prefix + rel + "/"));
                }
                if (!e.IsDirectory)
                    plan.Add(new PlanItem(Path.Combine(src.FullPath, rest), JoinRel(rootName, rest), false, e.Size, e.Modified, e.Mode, loc.ArchiveFile, e.RawKey));
            }
            return;
        }

        if (!src.IsDirectory)
        {
            plan.Add(new PlanItem(src.FullPath, rootName, false, src.Size, src.Modified, src.UnixMode));
            return;
        }

        plan.Add(new PlanItem(src.FullPath, rootName, true, 0, src.Modified, src.UnixMode));
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(src.FullPath).EnumerateFileSystemInfos("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(src.FullPath, info.FullName).Replace('\\', '/');
            var entry = FileEntry.FromInfo(info);
            plan.Add(new PlanItem(info.FullName, JoinRel(rootName, rel), entry.IsDirectory, entry.Size, entry.Modified, entry.UnixMode));
        }
    }

    static string JoinRel(string a, string b) => a + "/" + b;

    // a file, or a folder walked to the bottom - the server is asked folder by folder
    void PlanRemote(List<PlanItem> plan, FileEntry src, string rootName, CancellationToken ct)
    {
        if (!src.IsDirectory)
        {
            plan.Add(new PlanItem(src.FullPath, rootName, false, src.Size, src.Modified, src.UnixMode, FromRemote: true));
            return;
        }

        var (fs, _) = vfs.Remote.Get(src.FullPath);
        plan.Add(new PlanItem(src.FullPath, rootName, true, 0, src.Modified, src.UnixMode, FromRemote: true));
        var pending = new Stack<(string Path, string Rel)>([(src.FullPath, rootName)]);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel) = pending.Pop();
            foreach (var item in fs.ListAsync(RemotePath.Split(dir).Inner, ct).GetAwaiter().GetResult())
            {
                var path = RemotePath.Combine(dir, item.Name);
                var itemRel = JoinRel(rel, item.Name);
                plan.Add(new PlanItem(path, itemRel, item.IsDirectory, item.IsDirectory ? 0 : item.Size, item.Modified, item.Mode, FromRemote: true));
                if (item.IsDirectory)
                    pending.Push((path, itemRel));
            }
        }
    }

    async Task<ConflictAction> Resolve(RunState state, FileConflict conflict, CancellationToken ct)
    {
        if (state.Sticky is { } sticky)
        {
            if (sticky == ConflictAction.OverwriteAllOlder)
                return conflict.TargetModified < conflict.SourceModified ? ConflictAction.Overwrite : ConflictAction.Skip;
            return sticky;
        }

        if (state.Resolver == null)
            return ConflictAction.Overwrite;

        var action = await state.Resolver(conflict, ct).ConfigureAwait(false);
        switch (action)
        {
            case ConflictAction.OverwriteAll:
                state.Sticky = ConflictAction.Overwrite;
                return ConflictAction.Overwrite;

            case ConflictAction.SkipAll:
                state.Sticky = ConflictAction.Skip;
                return ConflictAction.Skip;

            case ConflictAction.OverwriteAllOlder:
                state.Sticky = ConflictAction.OverwriteAllOlder;
                return conflict.TargetModified < conflict.SourceModified ? ConflictAction.Overwrite : ConflictAction.Skip;

            case ConflictAction.Cancel:
                throw new OperationCanceledException(ct);
        }
        return action;
    }

    void WriteToDisk(List<PlanItem> plan, string destDir, RunState state, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        var fromArchives = new List<PlanItem>();

        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(TargetPath(destDir, item));
                state.ItemsDone++;
                state.Report(item.RelativePath);
            }
            else if (item.FromArchive)
            {
                fromArchives.Add(item); // read in one pass per archive below
            }
            else if (item.FromRemote)
            {
                this.WriteFile(item, destDir, () => vfs.OpenRead(item.SourcePath), buffer, state, ct);
            }
            else
            {
                this.WriteFile(item, destDir, () => new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan), buffer, state, ct);
            }
        }

        // Solid archives (tar.gz, solid 7z/rar) can only be decompressed front to back, so opening entries one
        // at a time would re-read the archive for every file - walk each archive once instead.
        foreach (var group in fromArchives.GroupBy(x => x.ArchiveFile!))
        {
            var wanted = new Dictionary<string, List<PlanItem>>(StringComparer.Ordinal);
            foreach (var item in group)
            {
                if (!wanted.TryGetValue(item.EntryName!, out var list))
                    wanted[item.EntryName!] = list = [];
                list.Add(item);
            }

            var done = new HashSet<PlanItem>();
            using (var reader = ArchiveReader.Open(group.Key))
            {
                reader.ReadSequential(
                    e => wanted.ContainsKey(e.RawKey),
                    (e, stream) =>
                    {
                        var targets = wanted[e.RawKey];
                        if (targets.Count == 1)
                        {
                            this.WriteFile(targets[0], destDir, () => new NonClosingStream(stream), buffer, state, ct);
                            done.Add(targets[0]);
                            return;
                        }

                        // the same entry copied to several names (rare) - buffer it once
                        var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        foreach (var t in targets)
                        {
                            ms.Position = 0;
                            this.WriteFile(t, destDir, () => new NonClosingStream(ms), buffer, state, ct);
                            done.Add(t);
                        }
                    },
                    ct
                );
            }

            foreach (var missing in group.Where(x => !done.Contains(x)))
            {
                state.Errors.Add($"{missing.RelativePath}: not found in archive");
                state.SkippedSources.Add(missing.SourcePath);
                state.ItemsDone++;
            }
        }
    }

    static string TargetPath(string destDir, PlanItem item) => Path.Combine(destDir, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    void WriteFile(PlanItem item, string destDir, Func<Stream> open, byte[] buffer, RunState state, CancellationToken ct)
    {
        var target = TargetPath(destDir, item);
        if (File.Exists(target))
        {
            var ti = new FileInfo(target);
            var action = this.Resolve(state, new FileConflict(item.SourcePath, item.Size, item.Modified, target, ti.Length, ti.LastWriteTime), ct).GetAwaiter().GetResult();
            if (action == ConflictAction.Skip)
            {
                state.Skipped++;
                state.SkippedSources.Add(item.SourcePath);
                state.BytesDone += item.Size;
                state.ItemsDone++;
                state.Report(item.RelativePath);
                return;
            }
            if (action == ConflictAction.KeepBoth)
                target = UniqueName(target);
            else if (ti.IsReadOnly)
                ti.IsReadOnly = false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using (var input = open())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    state.BytesDone += read;
                    state.Report(item.RelativePath);
                }
            }

            if (item.Modified > DateTime.MinValue)
                File.SetLastWriteTime(target, item.Modified);
            if (item.Mode is { } mode && !OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(target, mode); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(target); // don't leave half written files around
            throw;
        }
        catch (Exception ex)
        {
            state.Errors.Add($"{item.RelativePath}: {ex.Message}");
            state.SkippedSources.Add(item.SourcePath);
        }
        state.ItemsDone++;
        state.Report(item.RelativePath);
    }

    void WriteIntoArchive(List<PlanItem> plan, VfsLocation dest, RunState state, CancellationToken ct)
    {
        var prefix = dest.InnerPath.Length == 0 ? String.Empty : dest.InnerPath + "/";

        // what's there already - for the overwrite questions, and for "keep both" names
        Dictionary<string, ArchiveItem> existing;
        using (var reader = ArchiveReader.Open(dest.ArchiveFile!))
            existing = reader.Items.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var taken = existing.Keys.ToHashSet(StringComparer.Ordinal);

        // files from inside another archive (or this one) are unpacked first, one pass per archive
        var fromArchives = plan.Where(x => (x.FromArchive || x.FromRemote) && !x.IsDirectory).ToList();
        var staging = fromArchives.Count > 0 ? Path.Combine(Path.GetTempPath(), "shinycommander-" + Guid.NewGuid().ToString("N")[..8]) : null;
        try
        {
            var staged = staging == null ? [] : this.Stage(fromArchives, staging, state, ct);
            var changes = new ArchiveChanges();
            var written = 0;

            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                var key = prefix + item.RelativePath;
                if (item.IsDirectory)
                {
                    if (!existing.ContainsKey(key))
                        changes.Adds.Add(ArchiveAdd.Folder(key, item.Modified));
                    state.ItemsDone++;
                    state.Report(item.RelativePath);
                    continue;
                }

                var source = item.FromArchive || item.FromRemote ? staged.GetValueOrDefault(item) : item.SourcePath;
                if (source == null)
                    continue; // couldn't be unpacked - already counted as an error

                if (existing.TryGetValue(key, out var there) && !there.IsDirectory)
                {
                    var action = this.Resolve(state, new FileConflict(item.SourcePath, item.Size, item.Modified, Path.Combine(dest.Path, item.RelativePath), there.Size, there.Modified), ct).GetAwaiter().GetResult();
                    if (action == ConflictAction.Skip)
                    {
                        state.Skipped++;
                        state.SkippedSources.Add(item.SourcePath);
                        state.BytesDone += item.Size;
                        state.ItemsDone++;
                        continue;
                    }
                    if (action == ConflictAction.KeepBoth)
                        key = UniqueKey(taken, key);
                }
                taken.Add(key);

                var progressItem = item;
                changes.Adds.Add(new ArchiveAdd(key, false, () => new ProgressStream(File.OpenRead(source), n =>
                {
                    ct.ThrowIfCancellationRequested();
                    state.BytesDone += n;
                    state.Report(progressItem.RelativePath);
                }), item.Modified, item.Mode));
                written++;
            }

            ArchiveWriter.Apply(dest.ArchiveFile!, changes, ct);
            state.ItemsDone += written;
            state.Report(String.Empty);
        }
        finally
        {
            if (staging != null)
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }
    }

    void WriteToRemote(List<PlanItem> plan, VfsLocation dest, RunState state, CancellationToken ct)
    {
        var (fs, destInner) = vfs.Remote.Get(dest.Path);

        // archive entries are unpacked first, one pass per archive - other files stream straight up
        var fromArchives = plan.Where(x => x.FromArchive && !x.IsDirectory).ToList();
        var staging = fromArchives.Count > 0 ? Path.Combine(Path.GetTempPath(), "shinycommander-" + Guid.NewGuid().ToString("N")[..8]) : null;
        try
        {
            var staged = staging == null ? [] : this.Stage(fromArchives, staging, state, ct);
            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                var target = RemotePath.CombineInner(destInner, item.RelativePath);
                if (item.IsDirectory)
                {
                    RemoteStep(item, state, () =>
                    {
                        if (fs.GetItemAsync(target, ct).GetAwaiter().GetResult() is not { IsDirectory: true })
                            fs.CreateDirectoryAsync(target, ct).GetAwaiter().GetResult();
                    });
                    state.ItemsDone++;
                    state.Report(item.RelativePath);
                }
                else if (!item.FromArchive || staged.ContainsKey(item))
                {
                    Func<Stream> open = item.FromArchive
                        ? () => File.OpenRead(staged[item])
                        : item.FromRemote
                            ? () => vfs.OpenRead(item.SourcePath)
                            : () => new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
                    this.UploadFile(fs, item, target, RemotePath.Combine(dest.Path, item.RelativePath), open, state, ct);
                }
            }
        }
        finally
        {
            if (staging != null)
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }
    }

    void UploadFile(IRemoteFileSystem fs, PlanItem item, string target, string targetDisplay, Func<Stream> open, RunState state, CancellationToken ct)
    {
        var there = fs.GetItemAsync(target, ct).GetAwaiter().GetResult();
        if (there is { IsDirectory: false })
        {
            var action = this.Resolve(state, new FileConflict(item.SourcePath, item.Size, item.Modified, targetDisplay, there.Size, there.Modified), ct).GetAwaiter().GetResult();
            if (action == ConflictAction.Skip)
            {
                state.Skipped++;
                state.SkippedSources.Add(item.SourcePath);
                state.BytesDone += item.Size;
                state.ItemsDone++;
                state.Report(item.RelativePath);
                return;
            }
            if (action == ConflictAction.KeepBoth)
                target = UniqueRemoteName(fs, target, ct);
        }

        RemoteStep(item, state, () =>
        {
            using var input = new ProgressStream(open(), n =>
            {
                ct.ThrowIfCancellationRequested();
                state.BytesDone += n;
                state.Report(item.RelativePath);
            });
            fs.WriteAsync(target, input, item.Size, item.Modified > DateTime.MinValue ? item.Modified : null, ct).GetAwaiter().GetResult();
        });
        state.ItemsDone++;
        state.Report(item.RelativePath);
    }

    // one server call - a failure is that item's error, a cancel stops everything
    static void RemoteStep(PlanItem item, RunState state, Action step)
    {
        try
        {
            step();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.Errors.Add($"{item.RelativePath}: {ex.Message}");
            state.SkippedSources.Add(item.SourcePath);
        }
    }

    static string UniqueRemoteName(IRemoteFileSystem fs, string inner, CancellationToken ct)
    {
        var slash = inner.LastIndexOf('/');
        var dir = inner[..(slash + 1)];
        var file = inner[(slash + 1)..];
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        for (var i = 2; ; i++)
        {
            var candidate = $"{dir}{name} ({i}){ext}";
            if (fs.GetItemAsync(candidate, ct).GetAwaiter().GetResult() == null)
                return candidate;
        }
    }

    static void CreateRemoteDirectories(IRemoteFileSystem fs, string inner, CancellationToken ct)
    {
        var path = "/";
        foreach (var part in inner.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = RemotePath.CombineInner(path, part);
            if (fs.GetItemAsync(path, ct).GetAwaiter().GetResult() is not { IsDirectory: true })
                fs.CreateDirectoryAsync(path, ct).GetAwaiter().GetResult();
        }
    }

    OperationResult MoveWithinRemote(List<FileEntry> items, VfsLocation dest, string? renameTo, RunState state, CancellationToken ct)
    {
        var (fs, destInner) = vfs.Remote.Get(dest.Path);
        state.ItemsTotal = items.Count;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var from = RemotePath.Split(item.FullPath).Inner;
            var to = RemotePath.CombineInner(destInner, renameTo ?? item.Name);
            if (from != to && to.StartsWith(from + "/", StringComparison.Ordinal))
                throw new IOException($"Cannot move '{item.Name}' into its own subfolder");

            var skip = from == to;
            if (!skip && fs.GetItemAsync(to, ct).GetAwaiter().GetResult() is { } there)
            {
                var action = this.Resolve(state, new FileConflict(item.FullPath, item.Size, item.Modified, RemotePath.Combine(dest.Path, renameTo ?? item.Name), there.Size, there.Modified), ct).GetAwaiter().GetResult();
                if (action == ConflictAction.Skip)
                {
                    state.Skipped++;
                    skip = true;
                }
                else if (action == ConflictAction.KeepBoth)
                {
                    to = UniqueRemoteName(fs, to, ct);
                }
                else if (there.IsDirectory)
                {
                    fs.DeleteDirectoryAsync(to, ct).GetAwaiter().GetResult();
                }
                else
                {
                    fs.DeleteFileAsync(to, ct).GetAwaiter().GetResult();
                }
            }

            if (!skip)
            {
                try
                {
                    fs.RenameAsync(from, to, item.IsDirectory, ct).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    state.Errors.Add($"{item.Name}: {ex.Message}");
                }
            }
            state.ItemsDone++;
            state.Report(item.Name);
        }
        return new OperationResult(state.ItemsDone, state.Skipped, state.Errors);
    }

    // unpacks archive files to a temporary folder - a pass of their own, so they don't count as copied yet
    Dictionary<PlanItem, string> Stage(List<PlanItem> items, string folder, RunState state, CancellationToken ct)
    {
        var quiet = new RunState(FileOperationKind.Copy, null, null);
        this.WriteToDisk(items, folder, quiet, ct);
        state.Errors.AddRange(quiet.Errors);
        return items
            .Where(x => !quiet.SkippedSources.Contains(x.SourcePath) && File.Exists(TargetPath(folder, x)))
            .ToDictionary(x => x, x => TargetPath(folder, x));
    }

    OperationResult MoveWithinArchive(List<FileEntry> items, VfsLocation dest, string? renameTo, RunState state, CancellationToken ct)
    {
        var prefix = dest.InnerPath.Length == 0 ? String.Empty : dest.InnerPath + "/";
        HashSet<string> keys;
        using (var reader = ArchiveReader.Open(dest.ArchiveFile!))
            keys = reader.Items.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

        state.ItemsTotal = items.Count;
        var changes = new ArchiveChanges();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var from = this.ArchiveOf(item).Key;
            var to = prefix + (renameTo ?? item.Name);
            if (to.StartsWith(from + "/", StringComparison.Ordinal))
                throw new IOException($"Cannot move '{item.Name}' into its own subfolder");

            if (from != to && (keys.Contains(to) || keys.Any(k => k.StartsWith(to + "/", StringComparison.Ordinal))))
            {
                var action = this.Resolve(state, new FileConflict(item.FullPath, item.Size, item.Modified, Path.Combine(dest.Path, renameTo ?? item.Name), 0, DateTime.MinValue), ct).GetAwaiter().GetResult();
                if (action == ConflictAction.Skip)
                {
                    state.Skipped++;
                    state.ItemsDone++;
                    continue;
                }
                if (action == ConflictAction.KeepBoth)
                    to = UniqueKey(keys, to);
                else
                    changes.Deletes.Add(to);
            }
            if (from != to)
            {
                keys.Add(to);
                changes.Renames.Add((from, to));
            }
            state.ItemsDone++;
            state.Report(item.Name);
        }

        ArchiveWriter.Apply(dest.ArchiveFile!, changes, ct);
        return new OperationResult(state.ItemsDone, state.Skipped, state.Errors);
    }

    /// <summary>The archive an entry inside one lives in, and its key there.</summary>
    (string File, string Key) ArchiveOf(FileEntry entry)
    {
        var loc = vfs.Resolve(Path.GetDirectoryName(entry.FullPath)!);
        return (loc.ArchiveFile!, loc.InnerPath.Length == 0 ? entry.Name : loc.InnerPath + "/" + entry.Name);
    }

    void DeleteMovedSources(List<PlanItem> plan, RunState state, CancellationToken ct)
    {
        // files first, then folders deepest-first (only if empty - skipped files keep their folders alive)
        foreach (var group in plan.Where(x => x.FromArchive).GroupBy(x => x.ArchiveFile!))
        {
            var changes = new ArchiveChanges();
            var gone = group
                .Where(x => !x.IsDirectory && !state.SkippedSources.Contains(x.SourcePath))
                .Select(x => ArchiveReader.NormalizeKey(x.EntryName!))
                .ToHashSet(StringComparer.Ordinal);
            changes.Deletes.AddRange(gone);

            List<ArchiveItem> items;
            using (var reader = ArchiveReader.Open(group.Key))
                items = reader.Items.ToList();
            foreach (var dir in group.Where(x => x.IsDirectory))
            {
                var key = ArchiveReader.NormalizeKey(dir.EntryName!);
                if (!items.Any(e => !e.IsDirectory && !gone.Contains(e.Key) && e.Key.StartsWith(key + "/", StringComparison.Ordinal)))
                    changes.Deletes.Add(key);
            }
            ArchiveWriter.Apply(group.Key, changes, ct);
        }

        this.DeleteMovedRemoteSources(plan, state, ct);

        var local = plan.Where(x => !x.FromArchive && !x.FromRemote).ToList();
        foreach (var item in local.Where(x => !x.IsDirectory && !state.SkippedSources.Contains(x.SourcePath)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ClearReadOnly(item.SourcePath);
                File.Delete(item.SourcePath);
            }
            catch (Exception ex)
            {
                state.Errors.Add($"{item.SourcePath}: {ex.Message}");
            }
        }
        foreach (var dir in local.Where(x => x.IsDirectory).OrderByDescending(x => x.SourcePath.Length))
        {
            try
            {
                if (Directory.Exists(dir.SourcePath) && !Directory.EnumerateFileSystemEntries(dir.SourcePath).Any())
                    Directory.Delete(dir.SourcePath);
            }
            catch (Exception ex)
            {
                state.Errors.Add($"{dir.SourcePath}: {ex.Message}");
            }
        }
    }

    void DeleteMovedRemoteSources(List<PlanItem> plan, RunState state, CancellationToken ct)
    {
        var remote = plan.Where(x => x.FromRemote).ToList();
        foreach (var item in remote.Where(x => !x.IsDirectory && !state.SkippedSources.Contains(x.SourcePath)))
        {
            ct.ThrowIfCancellationRequested();
            RemoteStep(item, state, () =>
            {
                var (fs, inner) = vfs.Remote.Get(item.SourcePath);
                fs.DeleteFileAsync(inner, ct).GetAwaiter().GetResult();
            });
        }
        foreach (var dir in remote.Where(x => x.IsDirectory).OrderByDescending(x => x.SourcePath.Length))
        {
            RemoteStep(dir, state, () =>
            {
                var (fs, inner) = vfs.Remote.Get(dir.SourcePath);
                if (fs.ListAsync(inner, ct).GetAwaiter().GetResult().Count == 0)
                    fs.DeleteDirectoryAsync(inner, ct).GetAwaiter().GetResult();
            });
        }
    }

    public Task<OperationResult> DeleteAsync(
        IReadOnlyList<FileEntry> items,
        bool useTrash,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default
    ) => Task.Run(() =>
    {
        var list = items.Where(x => !x.IsParentLink).ToList();
        var state = new RunState(FileOperationKind.Delete, progress, null) { ItemsTotal = list.Count };

        foreach (var archiveGroup in list.Where(x => x.IsInArchive).GroupBy(x => this.ArchiveOf(x).File))
        {
            if (!ArchiveFormats.IsWritable(archiveGroup.Key))
                throw ReadOnly(archiveGroup.Key);

            var changes = new ArchiveChanges();
            changes.Deletes.AddRange(archiveGroup.Select(x => this.ArchiveOf(x).Key));
            ArchiveWriter.Apply(archiveGroup.Key, changes, ct);
            foreach (var item in archiveGroup)
            {
                state.ItemsDone++;
                state.Report(item.Name);
            }
        }

        foreach (var item in list.Where(x => !x.IsInArchive))
        {
            ct.ThrowIfCancellationRequested();
            state.Report(item.Name);
            try
            {
                if (item.IsRemote)
                {
                    // servers have no trash - the UI says so before asking
                    var (fs, inner) = vfs.Remote.Get(item.FullPath);
                    if (item.IsDirectory)
                        fs.DeleteDirectoryAsync(inner, ct).GetAwaiter().GetResult();
                    else
                        fs.DeleteFileAsync(inner, ct).GetAwaiter().GetResult();
                }
                else if (useTrash && trash != null && trash.TryMoveToTrash(item.FullPath))
                {
                    // done
                }
                else if (item.IsDirectory && !item.IsSymlink)
                {
                    DeleteDirectory(item.FullPath, ct);
                }
                else
                {
                    ClearReadOnly(item.FullPath);
                    File.Delete(item.FullPath);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                state.Errors.Add($"{item.Name}: {ex.Message}");
            }
            state.ItemsDone++;
            state.Report(item.Name);
        }
        return new OperationResult(state.ItemsDone, 0, state.Errors);
    }, ct);

    static void DeleteDirectory(string path, CancellationToken ct)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // read-only files block recursive deletes on Windows
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                ClearReadOnly(f);
            }
            Directory.Delete(path, true);
        }
    }

    static void ClearReadOnly(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if (attr.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    public Task CreateDirectoryAsync(string parentPath, string name) => Task.Run(() =>
    {
        ValidateName(name, allowSubPaths: true);
        var loc = vfs.Resolve(parentPath);
        if (loc.IsRemote)
        {
            var (fs, inner) = vfs.Remote.Get(loc.Path);
            CreateRemoteDirectories(fs, RemotePath.CombineInner(inner, name), CancellationToken.None);
            return;
        }
        if (!loc.IsArchive)
        {
            Directory.CreateDirectory(Path.Combine(loc.Path, name));
            return;
        }
        if (loc.IsReadOnlyArchive)
            throw ReadOnly(loc.ArchiveFile!);
        var prefix = loc.InnerPath.Length == 0 ? String.Empty : loc.InnerPath + "/";
        var changes = new ArchiveChanges();
        changes.Adds.Add(ArchiveAdd.Folder(prefix + name.Replace('\\', '/').Trim('/')));
        ArchiveWriter.Apply(loc.ArchiveFile!, changes);
    });

    public Task CreateFileAsync(string parentPath, string name) => Task.Run(() =>
    {
        ValidateName(name, allowSubPaths: false);
        var loc = vfs.Resolve(parentPath);
        if (loc.IsRemote)
        {
            var (fs, inner) = vfs.Remote.Get(loc.Path);
            var target = RemotePath.CombineInner(inner, name);
            if (fs.GetItemAsync(target).GetAwaiter().GetResult() == null)
                fs.WriteAsync(target, new MemoryStream(), 0, DateTime.Now).GetAwaiter().GetResult();
            return;
        }
        if (loc.IsArchive)
        {
            if (loc.IsReadOnlyArchive)
                throw ReadOnly(loc.ArchiveFile!);
            var key = (loc.InnerPath.Length == 0 ? String.Empty : loc.InnerPath + "/") + name;
            using (var reader = ArchiveReader.Open(loc.ArchiveFile!))
            {
                if (reader.Items.Any(x => x.Key == key))
                    return;
            }
            var changes = new ArchiveChanges();
            changes.Adds.Add(new ArchiveAdd(key, false, () => new MemoryStream(), DateTime.Now, null));
            ArchiveWriter.Apply(loc.ArchiveFile!, changes);
            return;
        }

        var path = Path.Combine(loc.Path, name);
        if (!File.Exists(path))
            File.Create(path).Dispose();
    });

    public Task RenameAsync(FileEntry entry, string newName) => Task.Run(() =>
    {
        ValidateName(newName, allowSubPaths: false);
        if (entry.IsParentLink || entry.Name == newName)
            return;

        if (entry.IsRemote)
        {
            var (fs, oldInner) = vfs.Remote.Get(entry.FullPath);
            var newInner = RemotePath.CombineInner(RemotePath.Split(RemotePath.GetParent(entry.FullPath)!).Inner, newName);
            var caseOnlyRemote = String.Equals(entry.Name, newName, StringComparison.OrdinalIgnoreCase);
            if (!caseOnlyRemote && fs.GetItemAsync(newInner).GetAwaiter().GetResult() != null)
                throw new IOException($"'{newName}' already exists");
            fs.RenameAsync(oldInner, newInner, entry.IsDirectory).GetAwaiter().GetResult();
            return;
        }

        if (entry.IsInArchive)
        {
            var (archive, oldKey) = this.ArchiveOf(entry);
            if (!ArchiveFormats.IsWritable(archive))
                throw ReadOnly(archive);

            var newKey = oldKey[..(oldKey.Length - entry.Name.Length)] + newName;
            using (var reader = ArchiveReader.Open(archive))
            {
                // archives are case sensitive - a case-only rename is a rename like any other
                if (reader.Items.Any(x => x.Key == newKey || x.Key.StartsWith(newKey + "/", StringComparison.Ordinal)))
                    throw new IOException($"'{newName}' already exists");
            }
            var changes = new ArchiveChanges();
            changes.Renames.Add((oldKey, newKey));
            ArchiveWriter.Apply(archive, changes);
            return;
        }

        var target = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, newName);
        var caseOnly = String.Equals(entry.Name, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Path.Exists(target))
            throw new IOException($"'{newName}' already exists");

        if (entry.IsDirectory)
        {
            if (caseOnly)
            {
                // case-insensitive file systems need a two step rename
                var temp = target + ".~tmp" + Guid.NewGuid().ToString("N")[..4];
                Directory.Move(entry.FullPath, temp);
                Directory.Move(temp, target);
            }
            else
            {
                Directory.Move(entry.FullPath, target);
            }
        }
        else
        {
            File.Move(entry.FullPath, target, caseOnly);
        }
    });

    /// <summary>
    /// Alt+F5 - pack the sources (folders with everything under them) into an archive: zip, 7z, tar, tar.gz,
    /// tar.bz2 or tar.zst. A new archive is created; an existing one has the sources added, like a copy into it.
    /// A name without a known archive extension gets ".zip".
    /// </summary>
    public async Task<OperationResult> PackAsync(
        IReadOnlyList<FileEntry> sources,
        string archivePath,
        ConflictResolver? onConflict = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        archivePath = VirtualFileSystem.Normalize(archivePath);
        if (RemotePath.IsRemote(archivePath))
            throw new NotSupportedException("Archives are packed on this computer - pack here, then copy the archive to the server");
        if (!ArchiveFormats.CanPack(archivePath))
        {
            if (ArchiveFormats.IsSupported(archivePath))
                throw new NotSupportedException($"Can't create {ArchiveFormats.Describe(ArchiveFormats.GetKind(archivePath))} archives - pack as {String.Join(", ", ArchiveFormats.PackExtensions)}");
            archivePath += ".zip";
        }

        var created = !File.Exists(archivePath);
        if (created)
            ArchiveWriter.CreateEmpty(archivePath);
        try
        {
            return await this.CopyAsync(sources, new OperationTarget(archivePath), onConflict, progress, ct).ConfigureAwait(false);
        }
        catch
        {
            if (created)
                TryDelete(archivePath); // no half made archive left behind
            throw;
        }
    }

    /// <summary>Alt+F9 - unpack whole archives into the destination (optionally one folder per archive).</summary>
    public async Task<OperationResult> ExtractAsync(
        IReadOnlyList<FileEntry> archives,
        string destDir,
        bool folderPerArchive,
        ConflictResolver? onConflict = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var processed = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var archive in archives.Where(x => x.IsArchive && !x.IsInArchive && !x.IsRemote))
        {
            var root = await vfs.ListAsync(archive.FullPath, true, ct).ConfigureAwait(false);
            var target = folderPerArchive ? Path.Combine(destDir, ArchiveFormats.StripExtension(archive.Name)) : destDir;
            var result = await this.CopyAsync(root.Where(x => !x.IsParentLink).ToList(), new OperationTarget(target), onConflict, progress, ct).ConfigureAwait(false);
            processed += result.Processed;
            skipped += result.Skipped;
            errors.AddRange(result.Errors);
        }
        return new OperationResult(processed, skipped, errors);
    }

    /// <summary>Space on a folder - TC calculates and shows the folder's size in the size column.</summary>
    public static Task<long> CalculateSizeAsync(string directory, CancellationToken ct = default) => Task.Run(() =>
    {
        long total = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var f in new DirectoryInfo(directory).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            try { total += f.Length; } catch { }
        }
        return total;
    }, ct);

    static void ValidateName(string name, bool allowSubPaths)
    {
        if (String.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty");

        var invalid = Path.GetInvalidFileNameChars().Where(c => !allowSubPaths || (c != '/' && c != '\\'));
        if (name.IndexOfAny(invalid.ToArray()) >= 0 || name is "." or "..")
            throw new ArgumentException($"'{name}' is not a valid name");
    }

    static string UniqueName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Path.Exists(candidate))
                return candidate;
        }
    }

    static string UniqueKey(ISet<string> taken, string key)
    {
        var dir = key.Contains('/') ? key[..(key.LastIndexOf('/') + 1)] : String.Empty;
        var file = key[dir.Length..];
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        for (var i = 2; ; i++)
        {
            var candidate = $"{dir}{name} ({i}){ext}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    static NotSupportedException ReadOnly(string archiveFile)
        => new($"{ArchiveFormats.Describe(ArchiveFormats.GetKind(archiveFile))} archives are read-only - extract, change, then pack again as zip, 7z or tar");

    /// <summary>Lets a reader hand out an entry stream without the consumer closing the archive under it.</summary>
    sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Reports bytes as an archive writer pulls a file through it.</summary>
    sealed class ProgressStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            if (n > 0)
                onRead(n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            if (n > 0)
                onRead(n);
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
