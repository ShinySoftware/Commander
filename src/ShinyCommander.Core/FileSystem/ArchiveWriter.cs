using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.ZStandard;
using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.FileSystem;

/// <summary>An entry to put into an archive. <see cref="Open"/> is called once, when it's written; null for a folder.</summary>
public sealed record ArchiveAdd(string Key, bool IsDirectory, Func<Stream>? Open, DateTime Modified, UnixFileMode? Mode)
{
    public static ArchiveAdd Folder(string key, DateTime? modified = null) => new(key, true, null, modified ?? DateTime.Now, null);
}

/// <summary>
/// Changes to one archive, applied together by <see cref="ArchiveWriter.Apply"/>. Keys are '/' separated with no
/// trailing '/'. A deleted or renamed key that's a folder takes everything under it along. Deletes and renames are
/// worked out against the archive as it was; an add replaces whatever ends up with its key.
/// </summary>
public sealed class ArchiveChanges
{
    public List<ArchiveAdd> Adds { get; } = [];
    public List<string> Deletes { get; } = [];
    public List<(string From, string To)> Renames { get; } = [];

    public bool IsEmpty => this.Adds.Count == 0 && this.Deletes.Count == 0 && this.Renames.Count == 0;

    /// <summary>Where an existing entry ends up: its new key, or null when it's deleted.</summary>
    internal string? Map(string key)
    {
        // a macOS "._name" companion holds name's extended attributes - it goes wherever name goes
        var slash = key.LastIndexOf('/');
        var leaf = key[(slash + 1)..];
        if (leaf.StartsWith("._", StringComparison.Ordinal) && leaf.Length > 2)
        {
            var partner = key[..(slash + 1)] + leaf[2..];
            var moved = this.MapOwn(partner);
            if (moved == null)
                return null;
            if (moved != partner)
            {
                var cut = moved.LastIndexOf('/');
                return moved[..(cut + 1)] + "._" + moved[(cut + 1)..];
            }
        }
        return this.MapOwn(key);
    }

    string? MapOwn(string key)
    {
        if (this.Deletes.Any(d => Covers(d, key)))
            return null;

        foreach (var (from, to) in this.Renames)
        {
            if (Covers(from, key))
                return to + key[from.Length..];
        }
        return key;
    }

    static bool Covers(string folderOrKey, string key)
        => key == folderOrKey || key.StartsWith(folderOrKey + "/", StringComparison.Ordinal);
}

/// <summary>
/// Changes archives in place. Zip is updated where it is; tar (plain, gz, bz2, zst) and 7z are written again to a
/// temporary file next to the original, which then replaces it - a compressed stream can't be edited in the middle.
/// rar (no free writer exists), tar.xz (SharpCompress only reads xz) and lone .gz/.bz2/.xz files stay read-only.
/// </summary>
public static class ArchiveWriter
{
    const int BufferSize = 1024 * 1024;

    public static void Apply(string archiveFile, ArchiveChanges changes, CancellationToken ct = default)
    {
        if (changes.IsEmpty)
            return;

        var kind = ArchiveFormats.GetKind(archiveFile);
        if (!ArchiveFormats.IsWritable(archiveFile))
            throw new NotSupportedException($"'{Path.GetFileName(archiveFile)}' is a read-only archive format");

        if (kind == ArchiveKind.Zip)
            ApplyZip(archiveFile, changes, ct);
        else
            Rewrite(archiveFile, kind, changes, ct);
    }

    /// <summary>A new archive with nothing in it, in the format its name says.</summary>
    public static void CreateEmpty(string archiveFile)
    {
        var kind = ArchiveFormats.GetKind(archiveFile);
        if (!ArchiveFormats.IsWritable(archiveFile))
            throw new NotSupportedException($"Can't create {ArchiveFormats.Describe(kind)} archives");

        Directory.CreateDirectory(Path.GetDirectoryName(archiveFile)!);
        using var fs = new FileStream(archiveFile, FileMode.CreateNew, FileAccess.ReadWrite);
        if (kind == ArchiveKind.Zip)
        {
            using var _ = new ZipArchive(fs, ZipArchiveMode.Create);
        }
        else if (kind == ArchiveKind.SevenZip)
        {
            using var _ = new SevenZipWriter(fs);
        }
        else
        {
            using var compressed = Compress(fs, kind);
            using var _ = new TarOutput(compressed);
        }
    }

    // ---------------------------------------------------------------------------------------------------- zip

    static void ApplyZip(string archiveFile, ArchiveChanges changes, CancellationToken ct)
    {
        using var fs = new FileStream(archiveFile, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Update);

        foreach (var entry in zip.Entries.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var key = ArchiveReader.NormalizeKey(entry.FullName);
            var target = changes.Map(key);
            if (target == null)
            {
                entry.Delete();
            }
            else if (target != key)
            {
                // zip entries can't be renamed - copy under the new name
                var isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var copy = zip.CreateEntry(target + (isDir ? "/" : String.Empty), CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                copy.ExternalAttributes = entry.ExternalAttributes;
                if (!isDir)
                {
                    using var src = entry.Open();
                    using var dst = copy.Open();
                    src.CopyTo(dst);
                }
                entry.Delete();
            }
        }

        foreach (var add in changes.Adds)
        {
            ct.ThrowIfCancellationRequested();
            var existing = zip.Entries.Where(e => ArchiveReader.NormalizeKey(e.FullName) == add.Key).ToList();
            if (add.IsDirectory)
            {
                if (existing.Count == 0)
                    zip.CreateEntry(add.Key + "/");
                continue;
            }

            foreach (var e in existing)
                e.Delete();

            var entry = zip.CreateEntry(add.Key, CompressionLevel.Optimal);
            if (add.Modified > DateTime.MinValue)
                entry.LastWriteTime = add.Modified;
            if (add.Mode is { } mode)
                entry.ExternalAttributes = ((int)mode | 0x8000) << 16; // regular file + permissions

            using var input = add.Open!();
            using var output = entry.Open();
            input.CopyTo(output, BufferSize);
        }
    }

    // ----------------------------------------------------------------------------------------- tar, 7z: rewrite

    static void Rewrite(string archiveFile, ArchiveKind kind, ArchiveChanges changes, CancellationToken ct)
    {
        var temp = archiveFile + ".~" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            if (kind == ArchiveKind.SevenZip)
            {
                WriteTemp(temp, fs =>
                {
                    using var writer = new SevenZipWriter(fs);
                    CopyItems(archiveFile, changes, new SevenZipTarget(writer), ct);
                    return true;
                });
            }
            else
            {
                var copied = WriteTemp(temp, fs =>
                {
                    using var compressed = Compress(fs, kind);
                    using var tar = new TarOutput(compressed);
                    return CopyTarRecords(archiveFile, kind, changes, tar, ct);
                });
                if (!copied)
                {
                    // System.Formats.Tar refused the headers (bsdtar's xattr records) - start again entry by entry
                    // through SharpCompress, which keeps files and folders but not links
                    File.Delete(temp);
                    WriteTemp(temp, fs =>
                    {
                        using var compressed = Compress(fs, kind);
                        using var tar = new TarOutput(compressed);
                        CopyItems(archiveFile, changes, new TarTarget(tar), ct);
                        return true;
                    });
                }
            }
            File.Move(temp, archiveFile, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    static bool WriteTemp(string temp, Func<FileStream, bool> write)
    {
        using var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize);
        return write(fs);
    }

    static Stream Compress(Stream output, ArchiveKind kind) => kind switch
    {
        ArchiveKind.TarGz => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
        ArchiveKind.TarBz2 => new BZip2Stream(new KeepOpen(output), SharpCompress.Compressors.CompressionMode.Compress, false),
        ArchiveKind.TarZst => new CompressionStream(output, level: 9, leaveOpen: true),
        _ => new KeepOpen(output)
    };

    /// <summary>
    /// Tar, record by record: whatever isn't deleted or replaced is written back as it was read - links, devices,
    /// pax attributes and all - renamed where a rename covers it. False when System.Formats.Tar can't read the archive.
    /// </summary>
    static bool CopyTarRecords(string archiveFile, ArchiveKind kind, ArchiveChanges changes, TarOutput tar, CancellationToken ct)
    {
        var addedFiles = changes.Adds.Where(x => !x.IsDirectory).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var input = ArchiveReader.OpenDecompressed(archiveFile, kind);
            using var reader = new TarReader(input);
            while (reader.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                var key = ArchiveReader.NormalizeKey(entry.Name);
                var isDir = entry.EntryType == TarEntryType.Directory;
                var target = key.Length == 0 ? key : changes.Map(key);
                if (target == null || !isDir && addedFiles.Contains(target))
                    continue;

                // the data first: a compressed tar only reads forward, and the reader keeps its place by this stream
                var original = entry.DataStream;
                var data = Seekable(original);

                var copy = target == key ? entry : Renamed(entry, target + (isDir ? "/" : String.Empty));
                if (data != null && (copy != entry || !ReferenceEquals(data, original)))
                    copy.DataStream = data; // folders and links refuse any data stream, even null
                if (isDir)
                    folders.Add(target);

                tar.Write(copy);
                if (!ReferenceEquals(data, original))
                    data?.Dispose();
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        WriteAdds(changes, new TarTarget(tar), folders, ct);
        return true;
    }

    // the same record under a new name, as pax so a longer name still fits. Built by hand: PaxTarEntry's converting
    // constructor takes over the original's data stream, and a forward-only reader loses its place
    static PaxTarEntry Renamed(TarEntry entry, string name)
    {
        var attributes = entry is PaxTarEntry pax
            ? pax.ExtendedAttributes.Where(x => x.Key is not ("path" or "size")).ToDictionary(x => x.Key, x => x.Value)
            : new Dictionary<string, string>();
        var type = entry.EntryType switch
        {
            TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => TarEntryType.RegularFile,
            var t => t
        };

        var renamed = new PaxTarEntry(type, name, attributes)
        {
            Mode = entry.Mode,
            ModificationTime = entry.ModificationTime,
            Uid = entry.Uid,
            Gid = entry.Gid
        };
        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            renamed.LinkName = entry.LinkName;
        if (entry is PosixTarEntry posix)
        {
            renamed.UserName = posix.UserName;
            renamed.GroupName = posix.GroupName;
        }
        return renamed;
    }

    /// <summary>Any readable archive into a target, through the archive's item list (folders first, then files).</summary>
    static void CopyItems(string archiveFile, ArchiveChanges changes, IArchiveTarget target, CancellationToken ct)
    {
        var addedFiles = changes.Adds.Where(x => !x.IsDirectory).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);

        using (var reader = ArchiveReader.Open(archiveFile))
        {
            foreach (var item in reader.Items.Where(x => x.IsDirectory))
            {
                if (changes.Map(item.Key) is { } key)
                {
                    target.AddDirectory(key, item.Modified, item.Mode);
                    folders.Add(key);
                }
            }

            reader.ReadSequential(
                item => changes.Map(item.Key) is { } key && !addedFiles.Contains(key),
                (item, stream) => target.AddFile(changes.Map(item.Key)!, stream, item.Size, item.Modified, item.Mode),
                ct
            );
        }

        WriteAdds(changes, target, folders, ct);
    }

    static void WriteAdds(ArchiveChanges changes, IArchiveTarget target, HashSet<string> folders, CancellationToken ct)
    {
        foreach (var add in changes.Adds.Where(x => x.IsDirectory && folders.Add(x.Key)))
            target.AddDirectory(add.Key, add.Modified, add.Mode);

        foreach (var add in changes.Adds.Where(x => !x.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            using var input = add.Open!();
            target.AddFile(add.Key, input, input.CanSeek ? input.Length : -1, add.Modified, add.Mode);
        }
    }

    // tar wants each entry's length up front; a stream from inside a compressed archive can't say - so copy it to
    // something that can (memory when small, a temporary file when not)
    static Stream? Seekable(Stream? data)
    {
        if (data == null || data.CanSeek)
            return data;

        Stream copy = new MemoryStream();
        var buffer = new byte[BufferSize];
        int read;
        while ((read = data.Read(buffer)) > 0)
        {
            if (copy is MemoryStream { Length: > 64 * 1024 * 1024 } ms)
            {
                var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.DeleteOnClose);
                ms.Position = 0;
                ms.CopyTo(file);
                copy = file;
            }
            copy.Write(buffer, 0, read);
        }
        copy.Position = 0;
        return copy;
    }

    interface IArchiveTarget
    {
        void AddDirectory(string key, DateTime modified, UnixFileMode? mode);
        void AddFile(string key, Stream data, long size, DateTime modified, UnixFileMode? mode);
    }

    sealed class TarTarget(TarOutput tar) : IArchiveTarget
    {
        public void AddDirectory(string key, DateTime modified, UnixFileMode? mode)
            => tar.Write(new PaxTarEntry(TarEntryType.Directory, key + "/")
            {
                Mode = mode ?? (UnixFileMode)0b111_101_101,
                ModificationTime = Time(modified)
            });

        public void AddFile(string key, Stream data, long size, DateTime modified, UnixFileMode? mode)
        {
            var seekable = Seekable(data);
            tar.Write(new PaxTarEntry(TarEntryType.RegularFile, key)
            {
                Mode = mode ?? (UnixFileMode)0b110_100_100,
                ModificationTime = Time(modified),
                DataStream = seekable
            });
            if (!ReferenceEquals(seekable, data))
                seekable?.Dispose();
        }

        static DateTimeOffset Time(DateTime modified) => modified > DateTime.MinValue ? new DateTimeOffset(modified) : DateTimeOffset.Now;
    }

    // a TarWriter that wrote nothing writes nothing at all - not even the end of archive blocks - and that isn't a
    // tar anyone can read; an empty archive is those two zero blocks
    sealed class TarOutput(Stream compressed) : IDisposable
    {
        readonly TarWriter writer = new(compressed, TarEntryFormat.Pax, leaveOpen: true);
        bool wrote;

        public void Write(TarEntry entry)
        {
            this.writer.WriteEntry(entry);
            this.wrote = true;
        }

        public void Dispose()
        {
            this.writer.Dispose();
            if (!this.wrote)
                compressed.Write(new byte[1024]);
        }
    }

    sealed class SevenZipTarget(SevenZipWriter writer) : IArchiveTarget
    {
        public void AddDirectory(string key, DateTime modified, UnixFileMode? mode) => writer.AddDirectory(key, modified, mode);
        public void AddFile(string key, Stream data, long size, DateTime modified, UnixFileMode? mode) => writer.AddFile(key, data, modified, mode);
    }

    // compressors close what they write to; the temporary file still needs moving into place
    sealed class KeepOpen(Stream inner) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
