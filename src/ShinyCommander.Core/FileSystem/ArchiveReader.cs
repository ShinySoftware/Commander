using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;
using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.FileSystem;

/// <summary>
/// One entry inside an archive. <see cref="Key"/> is normalised ('/' separated, no leading "./" or '/',
/// no trailing '/' on folders); <see cref="RawKey"/> is the name exactly as stored (zip edits need it).
/// </summary>
public sealed record ArchiveItem(string Key, string RawKey, bool IsDirectory, long Size, DateTime Modified, UnixFileMode? Mode);

/// <summary>Read access to any supported archive format.</summary>
public interface IArchiveReader : IDisposable
{
    ArchiveKind Kind { get; }
    IReadOnlyList<ArchiveItem> Items { get; }

    /// <summary>Opens one entry. On solid / stream formats this decompresses from the start - prefer <see cref="ReadSequential"/> for many entries.</summary>
    Stream Open(ArchiveItem item);

    /// <summary>One forward pass over the archive, handing each wanted entry's content to <paramref name="handle"/>.</summary>
    void ReadSequential(Func<ArchiveItem, bool> want, Action<ArchiveItem, Stream> handle, CancellationToken ct);
}

public static class ArchiveReader
{
    public static IArchiveReader Open(string archiveFile)
    {
        var kind = ArchiveFormats.GetKind(archiveFile);
        return kind switch
        {
            ArchiveKind.Zip => new ZipReader(archiveFile),
            ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz => new SingleStreamReader(archiveFile, kind),
            ArchiveKind.Tar or ArchiveKind.TarGz or ArchiveKind.TarBz2 or ArchiveKind.TarXz or ArchiveKind.TarZst => new TarFormatReader(archiveFile, kind),
            ArchiveKind.None => throw new NotSupportedException($"'{Path.GetFileName(archiveFile)}' is not a supported archive"),
            _ => new SharpReader(archiveFile, kind)
        };
    }

    /// <summary>The file, unwrapped from whatever compression its name says it has.</summary>
    internal static Stream OpenDecompressed(string file, ArchiveKind kind)
    {
        var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        try
        {
            Stream? decoder = kind switch
            {
                ArchiveKind.GZip or ArchiveKind.TarGz => new GZipStream(fs, CompressionMode.Decompress),
                ArchiveKind.BZip2 or ArchiveKind.TarBz2 => new BZip2Stream(fs, SharpCompress.Compressors.CompressionMode.Decompress, true),
                ArchiveKind.Xz or ArchiveKind.TarXz => new XZStream(fs),
                ArchiveKind.TarZst => new DecompressionStream(fs, leaveOpen: false),
                _ => null
            };
            return decoder == null ? fs : new OwningStream(decoder, fs);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    internal static string NormalizeKey(string raw)
    {
        var key = raw.Replace('\\', '/');
        while (key.StartsWith("./", StringComparison.Ordinal))
            key = key[2..];
        return key.Trim('/');
    }

    sealed class ZipReader : IArchiveReader
    {
        readonly ZipArchive zip;
        readonly Dictionary<ArchiveItem, ZipArchiveEntry> map = new(ReferenceEqualityComparer.Instance);

        public ZipReader(string file)
        {
            this.zip = ZipFile.OpenRead(file);
            var items = new List<ArchiveItem>(this.zip.Entries.Count);
            foreach (var e in this.zip.Entries)
            {
                var raw = e.FullName;
                var isDir = raw.EndsWith('/') || raw.EndsWith('\\');
                var key = NormalizeKey(raw);
                if (key.Length == 0)
                    continue;

                var mode = (e.ExternalAttributes >> 16) & 0x1FF;
                var item = new ArchiveItem(key, raw, isDir, isDir ? 0 : e.Length, e.LastWriteTime.LocalDateTime, mode == 0 ? null : (UnixFileMode)mode);
                items.Add(item);
                this.map[item] = e;
            }
            this.Items = items;
        }

        public ArchiveKind Kind => ArchiveKind.Zip;
        public IReadOnlyList<ArchiveItem> Items { get; }
        public Stream Open(ArchiveItem item) => this.map[item].Open();

        public void ReadSequential(Func<ArchiveItem, bool> want, Action<ArchiveItem, Stream> handle, CancellationToken ct)
        {
            foreach (var item in this.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.IsDirectory || !want(item))
                    continue;
                using var s = this.Open(item);
                handle(item, s);
            }
        }

        public void Dispose() => this.zip.Dispose();
    }

    sealed class SharpReader : IArchiveReader
    {
        readonly IArchive archive;
        readonly Dictionary<ArchiveItem, IArchiveEntry> map = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<string, ArchiveItem> byRaw = new(StringComparer.Ordinal);

        public SharpReader(string file, ArchiveKind kind)
        {
            this.Kind = kind;
            this.archive = ArchiveFactory.Open(file, new SharpCompress.Readers.ReaderOptions());
            var items = new List<ArchiveItem>();
            foreach (var e in this.archive.Entries)
            {
                if (e.Key is not { } raw)
                    continue;

                var key = NormalizeKey(raw);
                if (key.Length == 0)
                    continue;

                UnixFileMode? mode = null;
                try
                {
                    // rar stores unix modes in Attrib when packed on unix; 7z keeps them in the high word
                    if (e.Attrib is { } attr)
                    {
                        var unix = kind == ArchiveKind.SevenZip && (attr & 0x8000) != 0 ? (attr >> 16) & 0x1FF : 0;
                        if (unix != 0)
                            mode = (UnixFileMode)unix;
                    }
                }
                catch (NotImplementedException) { }

                var item = new ArchiveItem(key, raw, e.IsDirectory, e.IsDirectory ? 0 : e.Size, e.LastModifiedTime ?? DateTime.MinValue, mode);
                items.Add(item);
                this.map[item] = e;
                this.byRaw.TryAdd(raw, item);
            }
            this.Items = items;
        }

        public ArchiveKind Kind { get; }
        public IReadOnlyList<ArchiveItem> Items { get; }
        public Stream Open(ArchiveItem item) => this.map[item].OpenEntryStream();

        public void ReadSequential(Func<ArchiveItem, bool> want, Action<ArchiveItem, Stream> handle, CancellationToken ct)
        {
            if (!this.archive.IsSolid && this.Kind is ArchiveKind.SevenZip or ArchiveKind.Rar)
            {
                // random access is cheap here
                foreach (var item in this.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.IsDirectory || !want(item))
                        continue;
                    using var s = this.Open(item);
                    handle(item, s);
                }
                return;
            }

            using var reader = this.archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is not { } raw || !this.byRaw.TryGetValue(raw, out var item) || !want(item))
                    continue;

                using var s = reader.OpenEntryStream();
                handle(item, s);
            }
        }

        public void Dispose() => this.archive.Dispose();
    }

    /// <summary>
    /// tar, plain or compressed. System.Formats.Tar reads GNU/ustar/pax properly but rejects the xattr records
    /// macOS bsdtar writes; SharpCompress copes with those but surfaces pax headers as fake "PaxHeaders.N" entries.
    /// So: System.Formats.Tar first, SharpCompress's streaming reader when that refuses the headers. Compressed
    /// tars only decompress front to back - the listing is one pass, every read another, and
    /// <see cref="ReadSequential"/> keeps bulk copies to a single pass.
    /// </summary>
    sealed class TarFormatReader : IArchiveReader
    {
        readonly record struct TarRecord(string Name, bool IsDirectory, bool IsFile, long Size, DateTime Modified, UnixFileMode Mode);

        readonly string file;
        readonly bool useSharp;
        readonly Dictionary<string, ArchiveItem> byRaw = new(StringComparer.Ordinal);

        public TarFormatReader(string file, ArchiveKind kind)
        {
            this.file = file;
            this.Kind = kind;
            var items = new List<ArchiveItem>();
            bool Collect(TarRecord r, Func<Stream> _)
            {
                if (!r.IsDirectory && !r.IsFile)
                    return true; // links, devices

                var key = NormalizeKey(r.Name);
                if (key.Length == 0 || this.byRaw.ContainsKey(r.Name))
                    return true;

                var mode = r.Mode & (UnixFileMode)0x1FF;
                var item = new ArchiveItem(key, r.Name, r.IsDirectory, r.IsDirectory ? 0 : r.Size, r.Modified, mode == 0 ? null : mode);
                items.Add(item);
                this.byRaw[r.Name] = item;
                return true;
            }

            try
            {
                this.Walk(Collect);
            }
            catch (InvalidDataException)
            {
                items.Clear();
                this.byRaw.Clear();
                this.useSharp = true;
                this.Walk(Collect);
            }

            // macOS tars carry "._name" AppleDouble files holding name's xattrs - metadata, not content
            var keys = items.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            items.RemoveAll(x => IsAppleDouble(x.Key, keys));
            this.Items = items;
        }

        public ArchiveKind Kind { get; }
        public IReadOnlyList<ArchiveItem> Items { get; }

        void Walk(Func<TarRecord, Func<Stream>, bool> visit)
        {
            if (this.useSharp)
            {
                using var fs = new FileStream(this.file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                using var reader = SharpCompress.Readers.ReaderFactory.Open(fs, new SharpCompress.Readers.ReaderOptions());
                while (reader.MoveToNextEntry())
                {
                    var e = reader.Entry;
                    if (e.Key is not { } raw || IsPaxArtifact(raw))
                        continue;

                    var mode = e is SharpCompress.Common.Tar.TarEntry te ? (UnixFileMode)(te.Mode & 0x1FF) : 0;
                    var isLink = !String.IsNullOrEmpty(e.LinkTarget);
                    var record = new TarRecord(raw, e.IsDirectory, !e.IsDirectory && !isLink, e.Size, e.LastModifiedTime ?? DateTime.MinValue, mode);
                    if (!visit(record, () => reader.OpenEntryStream()))
                        return;
                }
                return;
            }

            using var stream = OpenDecompressed(this.file, this.Kind);
            using var tar = new System.Formats.Tar.TarReader(stream);
            while (tar.GetNextEntry() is { } entry)
            {
                var isFile = entry.EntryType is System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile or System.Formats.Tar.TarEntryType.ContiguousFile;
                var record = new TarRecord(entry.Name, entry.EntryType == System.Formats.Tar.TarEntryType.Directory, isFile, entry.Length, entry.ModificationTime.LocalDateTime, entry.Mode);
                if (!visit(record, () => entry.DataStream ?? Stream.Null))
                    return;
            }
        }

        static bool IsAppleDouble(string key, HashSet<string> keys)
        {
            var slash = key.LastIndexOf('/');
            var name = key[(slash + 1)..];
            return name.StartsWith("._", StringComparison.Ordinal) && name.Length > 2 && keys.Contains(key[..(slash + 1)] + name[2..]);
        }

        // bsdtar/GNU metadata that a reader without pax support shows as files
        static bool IsPaxArtifact(string raw)
        {
            var key = NormalizeKey(raw);
            if (key == "././@LongLink" || key.Contains("@PaxHeader", StringComparison.Ordinal))
                return true;

            foreach (var segment in key.Split('/'))
            {
                if (segment == "PaxHeader" || segment.StartsWith("PaxHeaders.", StringComparison.Ordinal) || segment.StartsWith("GlobalHead.", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public Stream Open(ArchiveItem item)
        {
            MemoryStream? result = null;
            this.Walk((r, open) =>
            {
                if (!r.IsFile || r.Name != item.RawKey)
                    return true;

                result = new MemoryStream();
                using (var s = open())
                    s.CopyTo(result);
                result.Position = 0;
                return false;
            });
            return result ?? throw new FileNotFoundException(item.Key);
        }

        public void ReadSequential(Func<ArchiveItem, bool> want, Action<ArchiveItem, Stream> handle, CancellationToken ct)
        {
            this.Walk((r, open) =>
            {
                ct.ThrowIfCancellationRequested();
                if (r.IsFile && this.byRaw.TryGetValue(r.Name, out var item) && want(item))
                {
                    using var s = open();
                    handle(item, s);
                }
                return true;
            });
        }

        public void Dispose() { }
    }

    /// <summary>A lone .gz/.bz2/.xz file, presented as an archive holding the one decompressed file.</summary>
    sealed class SingleStreamReader : IArchiveReader
    {
        // counting the decompressed size means decompressing everything; past this, show the packed size
        const long CountLimit = 64L * 1024 * 1024;
        readonly string file;
        readonly ArchiveItem item;

        public SingleStreamReader(string file, ArchiveKind kind)
        {
            this.file = file;
            this.Kind = kind;
            var info = new FileInfo(file);
            var name = ArchiveFormats.StripExtension(info.Name);
            this.item = new ArchiveItem(name, name, false, this.MeasureSize(info), info.LastWriteTime, null);
            this.Items = [this.item];
        }

        public ArchiveKind Kind { get; }
        public IReadOnlyList<ArchiveItem> Items { get; }

        long MeasureSize(FileInfo info)
        {
            try
            {
                if (this.Kind == ArchiveKind.GZip && info.Length >= 18)
                {
                    // ISIZE trailer - the uncompressed length mod 2^32
                    using var fs = info.OpenRead();
                    fs.Seek(-4, SeekOrigin.End);
                    Span<byte> b = stackalloc byte[4];
                    fs.ReadExactly(b);
                    return BitConverter.ToUInt32(b);
                }

                if (info.Length > CountLimit)
                    return info.Length;

                using var s = this.OpenStream();
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
                    total += read;
                return total;
            }
            catch
            {
                return info.Length;
            }
        }

        Stream OpenStream() => OpenDecompressed(this.file, this.Kind);

        public Stream Open(ArchiveItem item) => this.OpenStream();

        public void ReadSequential(Func<ArchiveItem, bool> want, Action<ArchiveItem, Stream> handle, CancellationToken ct)
        {
            if (!want(this.item))
                return;
            using var s = this.OpenStream();
            handle(this.item, s);
        }

        public void Dispose() { }
    }

    /// <summary>Read-only pass-through that also disposes the file under a decoder.</summary>
    sealed class OwningStream(Stream inner, Stream owned) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owned.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
