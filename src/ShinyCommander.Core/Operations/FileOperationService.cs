using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Compressors.BZip2;
using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Operations;

/// <summary>
/// Copy/move/delete/pack/extract across the local disk and archives with progress, cancellation and
/// conflict resolution. Zip archives are read/write; tar, 7z, rar and gz/bz2/xz are read-only.
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
        string? EntryName = null
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
        var full = Path.IsPathRooted(raw) || raw.StartsWith('~')
            ? VirtualFileSystem.Normalize(raw)
            : VirtualFileSystem.Normalize(Path.Combine(currentDirectory, raw));

        if (endsWithSep || sourceCount > 1 || vfs.Exists(full) && IsContainer(full))
            return new OperationTarget(full);

        var parent = Path.GetDirectoryName(full);
        if (parent == null)
            return new OperationTarget(full);

        return new OperationTarget(parent, Path.GetFileName(full));
    }

    bool IsContainer(string path)
    {
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

        foreach (var item in items)
        {
            var name = renameTo ?? item.Name;
            var destFull = Path.Combine(dest.Path, name);
            if (String.Equals(VirtualFileSystem.Normalize(item.FullPath), VirtualFileSystem.Normalize(destFull), PathComparison))
                throw new IOException($"'{item.Name}' cannot be copied onto itself");

            if (item.IsDirectory && !dest.IsArchive && !item.IsInArchive && IsSubPath(item.FullPath, destFull))
                throw new IOException($"Cannot copy '{item.Name}' into its own subfolder");
        }

        // fast path: a plain rename on the same volume
        var remaining = items;
        if (move && !dest.IsArchive)
        {
            remaining = [];
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var destPath = Path.Combine(dest.Path, renameTo ?? item.Name);
                if (!item.IsInArchive && !Path.Exists(destPath) && TryFastMove(item, destPath))
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

        if (plan.Any(x => x.FromArchive) && dest.IsArchive)
            throw new NotSupportedException("Copying directly between archives is not supported - extract first");

        if (dest.IsArchive)
            this.WriteIntoArchive(plan, dest, state, ct);
        else
            this.WriteToDisk(plan, dest.Path, state, ct);

        if (move)
            this.DeleteMovedSources(plan, state, ct);

        return new OperationResult(state.ItemsDone, state.Skipped, state.Errors);
    }

    static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    static bool IsSubPath(string parent, string child)
    {
        var p = VirtualFileSystem.Normalize(parent) + Path.DirectorySeparatorChar;
        var c = VirtualFileSystem.Normalize(child) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, PathComparison);
    }

    string EnsureContainer(string dir)
    {
        if (vfs.Exists(dir))
            return dir;

        // TC creates missing target folders on copy
        Directory.CreateDirectory(dir);
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
                    continue;
                }

                plan.Add(new PlanItem(src.FullPath, rootName, true, 0, src.Modified, null, loc.ArchiveFile, inner + "/"));
                var prefix = inner + "/";
                var seenDirs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in reader.Items)
                {
                    if (!e.Key.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

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
                continue;
            }

            if (!src.IsDirectory)
            {
                plan.Add(new PlanItem(src.FullPath, rootName, false, src.Size, src.Modified, src.UnixMode));
                continue;
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
        return plan;
    }

    static string JoinRel(string a, string b) => a + "/" + b;

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
        var buffer = new byte[BufferSize];
        var prefix = dest.InnerPath.Length == 0 ? String.Empty : dest.InnerPath + "/";

        using var fs = new FileStream(dest.ArchiveFile!, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Update);

        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();
            var entryName = prefix + item.RelativePath + (item.IsDirectory ? "/" : String.Empty);
            var existing = zip.GetEntry(entryName);

            if (item.IsDirectory)
            {
                if (existing == null)
                    zip.CreateEntry(entryName);
                state.ItemsDone++;
                state.Report(item.RelativePath);
                continue;
            }

            if (existing != null)
            {
                var action = this.Resolve(state, new FileConflict(item.SourcePath, item.Size, item.Modified, Path.Combine(dest.Path, item.RelativePath), existing.Length, existing.LastWriteTime.LocalDateTime), ct).GetAwaiter().GetResult();
                if (action == ConflictAction.Skip)
                {
                    state.Skipped++;
                    state.SkippedSources.Add(item.SourcePath);
                    state.BytesDone += item.Size;
                    state.ItemsDone++;
                    continue;
                }
                if (action == ConflictAction.KeepBoth)
                    entryName = UniqueEntryName(zip, entryName);
                else
                    existing.Delete();
            }

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = item.Modified;
            if (item.Mode is { } mode)
                entry.ExternalAttributes = ((int)mode | 0x8000) << 16; // regular file + permissions

            using (var input = File.OpenRead(item.SourcePath))
            using (var output = entry.Open())
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
            state.ItemsDone++;
            state.Report(item.RelativePath);
        }
    }

    void DeleteMovedSources(List<PlanItem> plan, RunState state, CancellationToken ct)
    {
        // files first, then folders deepest-first (only if empty - skipped files keep their folders alive)
        var byArchive = plan.Where(x => x.FromArchive).GroupBy(x => x.ArchiveFile!);
        foreach (var group in byArchive)
        {
            using var fs = new FileStream(group.Key, FileMode.Open, FileAccess.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Update);
            foreach (var item in group.Where(x => !x.IsDirectory && !state.SkippedSources.Contains(x.SourcePath)))
                zip.GetEntry(item.EntryName!)?.Delete();

            foreach (var item in group.Where(x => x.IsDirectory))
            {
                var prefix = item.EntryName!;
                if (!zip.Entries.Any(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.FullName != prefix))
                    zip.GetEntry(prefix)?.Delete();
            }
        }

        var local = plan.Where(x => !x.FromArchive).ToList();
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

    public Task<OperationResult> DeleteAsync(
        IReadOnlyList<FileEntry> items,
        bool useTrash,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default
    ) => Task.Run(() =>
    {
        var list = items.Where(x => !x.IsParentLink).ToList();
        var state = new RunState(FileOperationKind.Delete, progress, null) { ItemsTotal = list.Count };

        foreach (var archiveGroup in list.Where(x => x.IsInArchive).GroupBy(x => vfs.Resolve(Path.GetDirectoryName(x.FullPath)!)))
        {
            var loc = archiveGroup.Key;
            if (loc.IsReadOnlyArchive)
                throw ReadOnly(loc.ArchiveFile!);
            using var fs = new FileStream(loc.ArchiveFile!, FileMode.Open, FileAccess.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Update);
            foreach (var item in archiveGroup)
            {
                ct.ThrowIfCancellationRequested();
                var inner = loc.InnerPath.Length == 0 ? item.Name : loc.InnerPath + "/" + item.Name;
                var doomed = item.IsDirectory
                    ? zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith(inner + "/", StringComparison.Ordinal)).ToList()
                    : zip.Entries.Where(e => e.FullName.Replace('\\', '/') == inner).ToList();
                foreach (var e in doomed)
                    e.Delete();
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
                if (useTrash && trash != null && trash.TryMoveToTrash(item.FullPath))
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
        if (!loc.IsArchive)
        {
            Directory.CreateDirectory(Path.Combine(loc.Path, name));
            return;
        }
        if (loc.IsReadOnlyArchive)
            throw ReadOnly(loc.ArchiveFile!);
        using var fs = new FileStream(loc.ArchiveFile!, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Update);
        var prefix = loc.InnerPath.Length == 0 ? String.Empty : loc.InnerPath + "/";
        zip.CreateEntry(prefix + name.Replace('\\', '/').Trim('/') + "/");
    });

    public Task CreateFileAsync(string parentPath, string name) => Task.Run(() =>
    {
        ValidateName(name, allowSubPaths: false);
        var loc = vfs.Resolve(parentPath);
        if (loc.IsArchive)
            throw new NotSupportedException("Create the file on disk and copy it into the archive");

        var path = Path.Combine(loc.Path, name);
        if (!File.Exists(path))
            File.Create(path).Dispose();
    });

    public Task RenameAsync(FileEntry entry, string newName) => Task.Run(() =>
    {
        ValidateName(newName, allowSubPaths: false);
        if (entry.IsParentLink || entry.Name == newName)
            return;

        if (entry.IsInArchive)
        {
            var loc = vfs.Resolve(Path.GetDirectoryName(entry.FullPath)!);
            if (loc.IsReadOnlyArchive)
                throw ReadOnly(loc.ArchiveFile!);
            var prefix = loc.InnerPath.Length == 0 ? String.Empty : loc.InnerPath + "/";
            using var fs = new FileStream(loc.ArchiveFile!, FileMode.Open, FileAccess.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Update);
            var oldName = prefix + entry.Name;
            var entries = entry.IsDirectory
                ? zip.Entries.Where(e => e.FullName.StartsWith(oldName + "/", StringComparison.Ordinal)).ToList()
                : zip.Entries.Where(e => e.FullName == oldName).ToList();

            foreach (var e in entries)
            {
                var renamed = prefix + newName + e.FullName[oldName.Length..];
                var copy = zip.CreateEntry(renamed);
                copy.LastWriteTime = e.LastWriteTime;
                copy.ExternalAttributes = e.ExternalAttributes;
                if (!renamed.EndsWith('/'))
                {
                    using var src = e.Open();
                    using var dst = copy.Open();
                    src.CopyTo(dst);
                }
                e.Delete();
            }
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
    /// Alt+F5 - pack the sources into an archive. Zip can be new or existing (files are added); tar, tar.gz and
    /// tar.bz2 are always written fresh. A name without a known archive extension gets ".zip".
    /// </summary>
    public async Task<OperationResult> PackAsync(
        IReadOnlyList<FileEntry> sources,
        string archivePath,
        ConflictResolver? onConflict = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (sources.Any(x => x.IsInArchive))
            throw new NotSupportedException("Packing files that are already inside an archive is not supported");

        archivePath = VirtualFileSystem.Normalize(archivePath);
        if (!ArchiveFormats.CanPack(archivePath))
        {
            if (ArchiveFormats.IsSupported(archivePath))
                throw new NotSupportedException($"Can't create {ArchiveFormats.Describe(ArchiveFormats.GetKind(archivePath))} archives - pack as {String.Join(", ", ArchiveFormats.PackExtensions)}");
            archivePath += ".zip";
        }

        var kind = ArchiveFormats.GetKind(archivePath);
        if (kind != ArchiveKind.Zip)
            return await Task.Run(() => this.PackTar(sources, archivePath, kind, onConflict, progress, ct), ct).ConfigureAwait(false);

        if (!File.Exists(archivePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            await using var fs = File.Create(archivePath);
            using var _ = new ZipArchive(fs, ZipArchiveMode.Create);
        }

        return await this.CopyAsync(sources, new OperationTarget(archivePath), onConflict, progress, ct).ConfigureAwait(false);
    }

    OperationResult PackTar(IReadOnlyList<FileEntry> sources, string archivePath, ArchiveKind kind, ConflictResolver? onConflict, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var items = sources.Where(x => !x.IsParentLink).ToList();
        var state = new RunState(FileOperationKind.Copy, progress, onConflict);

        if (File.Exists(archivePath))
        {
            // tar streams can't be appended to once compressed - the only choice is replace or stop
            var fi = new FileInfo(archivePath);
            var action = this.Resolve(state, new FileConflict(items.FirstOrDefault()?.FullPath ?? archivePath, 0, DateTime.Now, archivePath, fi.Length, fi.LastWriteTime), ct).GetAwaiter().GetResult();
            if (action is ConflictAction.Skip or ConflictAction.KeepBoth)
            {
                if (action == ConflictAction.Skip)
                    return new OperationResult(0, items.Count, []);
                archivePath = UniqueName(archivePath);
            }
        }

        var plan = this.BuildPlan(items, null, ct);
        state.ItemsTotal = plan.Count;
        state.BytesTotal = plan.Sum(x => x.Size);
        state.Report(String.Empty);

        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        var temp = archivePath + ".~" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            using (var compressed = kind switch
            {
                ArchiveKind.TarGz => new GZipStream(fs, CompressionLevel.Optimal, true),
                ArchiveKind.TarBz2 => new BZip2Stream(fs, SharpCompress.Compressors.CompressionMode.Compress, false),
                _ => (Stream)new NonClosingStream(fs)
            })
            using (var tar = new TarWriter(compressed, TarEntryFormat.Gnu, leaveOpen: true))
            {
                foreach (var item in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    var mode = item.Mode ?? (item.IsDirectory ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100);
                    var entry = new GnuTarEntry(item.IsDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, item.RelativePath + (item.IsDirectory ? "/" : String.Empty))
                    {
                        Mode = mode,
                        ModificationTime = item.Modified > DateTime.MinValue ? new DateTimeOffset(item.Modified) : DateTimeOffset.Now
                    };

                    if (item.IsDirectory)
                    {
                        tar.WriteEntry(entry);
                    }
                    else
                    {
                        try
                        {
                            using var input = new ProgressStream(File.OpenRead(item.SourcePath), n =>
                            {
                                ct.ThrowIfCancellationRequested();
                                state.BytesDone += n;
                                state.Report(item.RelativePath);
                            });
                            entry.DataStream = input;
                            tar.WriteEntry(entry);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            state.Errors.Add($"{item.RelativePath}: {ex.Message}");
                        }
                    }
                    state.ItemsDone++;
                    state.Report(item.RelativePath);
                }
            }
            File.Move(temp, archivePath, true);
        }
        finally
        {
            TryDelete(temp);
        }
        return new OperationResult(state.ItemsDone, state.Skipped, state.Errors);
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

        foreach (var archive in archives.Where(x => x.IsArchive && !x.IsInArchive))
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

    static string UniqueEntryName(ZipArchive zip, string entryName)
    {
        var dir = entryName.Contains('/') ? entryName[..(entryName.LastIndexOf('/') + 1)] : String.Empty;
        var file = entryName[dir.Length..];
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        for (var i = 2; ; i++)
        {
            var candidate = $"{dir}{name} ({i}){ext}";
            if (zip.GetEntry(candidate) == null)
                return candidate;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    static NotSupportedException ReadOnly(string archiveFile)
        => new($"{ArchiveFormats.Describe(ArchiveFormats.GetKind(archiveFile))} archives are read-only - extract, change, then pack again");

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

    /// <summary>Reports bytes as TarWriter pulls a file through it.</summary>
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
