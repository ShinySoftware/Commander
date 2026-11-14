using System.Text;
using SharpCompress.Compressors.LZMA;

namespace ShinyCommander.Core.FileSystem;

/// <summary>
/// Writes a .7z archive - SharpCompress reads 7z but has no writer. This is the plain subset of the format: every
/// file LZMA-compressed as its own folder (not solid, so any file can be read without the ones before it), folders
/// and empty files as "empty streams", modification times, and unix modes in the high word of the attributes the
/// way p7zip stores them. No encryption, no header compression.
/// <para>
/// Layout: a 32 byte signature header, the packed streams one after another, then the header describing them. The
/// signature header points at the header and is filled in last, so the output has to be seekable.
/// </para>
/// </summary>
public sealed class SevenZipWriter : IDisposable
{
    static readonly byte[] Signature = [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];
    static readonly byte[] LzmaCoderId = [0x03, 0x01, 0x01];
    const int Dictionary = 1 << 22;

    // header property ids
    const byte End = 0x00;
    const byte Header = 0x01;
    const byte MainStreamsInfo = 0x04;
    const byte FilesInfo = 0x05;
    const byte PackInfo = 0x06;
    const byte UnpackInfo = 0x07;
    const byte SubStreamsInfo = 0x08;
    const byte Size = 0x09;
    const byte Crc = 0x0A;
    const byte Folder = 0x0B;
    const byte CodersUnpackSize = 0x0C;
    const byte EmptyStream = 0x0E;
    const byte EmptyFile = 0x0F;
    const byte Name = 0x11;
    const byte MTime = 0x14;
    const byte WinAttributes = 0x15;

    sealed record Entry(string Name, bool IsDirectory, long Size, uint Crc, long PackedSize, byte[]? Properties, DateTime Modified, uint Attributes)
    {
        public bool HasStream => this.Size > 0;
    }

    readonly Stream output;
    readonly long origin;
    readonly List<Entry> entries = [];
    bool finished;

    public SevenZipWriter(Stream output)
    {
        if (!output.CanSeek)
            throw new ArgumentException("7z needs a seekable output - the start of the file points at the header written last", nameof(output));

        this.output = output;
        this.origin = output.Position;
        output.Write(new byte[32]);
    }

    public void AddDirectory(string name, DateTime modified, UnixFileMode? mode)
        => this.entries.Add(new Entry(name, true, 0, 0, 0, null, modified, Attributes(true, mode)));

    /// <summary>Compresses <paramref name="data"/> (read to its end) into the archive as <paramref name="name"/>.</summary>
    public void AddFile(string name, Stream data, DateTime modified, UnixFileMode? mode)
    {
        var start = this.output.Position;
        var input = new Crc32Stream(data);
        byte[] properties;
        using (var lzma = new LzmaStream(new LzmaEncoderProperties(false, Dictionary), false, new KeepOpenStream(this.output)))
        {
            input.CopyTo(lzma);
            properties = lzma.Properties;
        }

        if (input.Count == 0)
        {
            // an empty file has no stream at all - drop what the encoder wrote for nothing
            this.output.Position = start;
            this.output.SetLength(start);
            this.entries.Add(new Entry(name, false, 0, 0, 0, null, modified, Attributes(false, mode)));
            return;
        }
        this.entries.Add(new Entry(name, false, input.Count, input.Crc, this.output.Position - start, properties, modified, Attributes(false, mode)));
    }

    /// <summary>Writes the header and points the signature header at it. Called by <see cref="Dispose"/> too.</summary>
    public void Finish()
    {
        if (this.finished)
            return;
        this.finished = true;

        var headerOffset = this.output.Position - this.origin - 32;
        // 7-Zip writes an empty archive as a signature header pointing at nothing, which SharpCompress can't read -
        // a header listing no files is just as valid
        var header = this.BuildHeader();
        this.output.Write(header);
        var end = this.output.Position;

        var startHeader = new byte[20];
        BitConverter.TryWriteBytes(startHeader.AsSpan(0), (ulong)headerOffset);
        BitConverter.TryWriteBytes(startHeader.AsSpan(8), (ulong)header.Length);
        BitConverter.TryWriteBytes(startHeader.AsSpan(16), Crc32.Compute(header));

        var signature = new byte[32];
        Signature.CopyTo(signature, 0);
        signature[7] = 4; // format version 0.4
        BitConverter.TryWriteBytes(signature.AsSpan(8), Crc32.Compute(startHeader));
        startHeader.CopyTo(signature, 12);

        this.output.Position = this.origin;
        this.output.Write(signature);
        this.output.Position = end;
    }

    public void Dispose() => this.Finish();

    byte[] BuildHeader()
    {
        var w = new HeaderWriter();
        w.Byte(Header);

        var streams = this.entries.Where(x => x.HasStream).ToList();
        if (streams.Count > 0)
        {
            w.Byte(MainStreamsInfo);

            w.Byte(PackInfo);
            w.Number(0); // the packed streams start right after the signature header
            w.Number((ulong)streams.Count);
            w.Byte(Size);
            foreach (var s in streams)
                w.Number((ulong)s.PackedSize);
            w.Byte(End);

            // one folder per file: a single LZMA coder, one stream in, one out
            w.Byte(UnpackInfo);
            w.Byte(Folder);
            w.Number((ulong)streams.Count);
            w.Byte(0); // not external
            foreach (var s in streams)
            {
                w.Number(1);
                w.Byte((byte)(LzmaCoderId.Length | 0x20)); // id size, has properties
                w.Bytes(LzmaCoderId);
                w.Number((ulong)s.Properties!.Length);
                w.Bytes(s.Properties);
            }
            w.Byte(CodersUnpackSize);
            foreach (var s in streams)
                w.Number((ulong)s.Size);
            w.Byte(Crc);
            w.Byte(1); // all defined
            foreach (var s in streams)
                w.UInt32(s.Crc);
            w.Byte(End);

            // one file per folder (the default when it's empty), each checked by its folder's CRC. The spec lets the
            // whole block go, but SharpCompress needs it there
            w.Byte(SubStreamsInfo);
            w.Byte(End);
            w.Byte(End);
        }

        w.Byte(FilesInfo);
        w.Number((ulong)this.entries.Count);
        if (this.entries.Count == 0)
        {
            w.Byte(End);
            w.Byte(End);
            return w.ToArray();
        }

        var empties = this.entries.Where(x => !x.HasStream).ToList();
        if (empties.Count > 0)
        {
            w.Property(EmptyStream, BitField(this.entries.Select(x => !x.HasStream)));
            if (empties.Any(x => !x.IsDirectory))
                w.Property(EmptyFile, BitField(empties.Select(x => !x.IsDirectory)));
        }

        var names = new MemoryStream();
        names.WriteByte(0); // not external
        foreach (var e in this.entries)
        {
            names.Write(Encoding.Unicode.GetBytes(e.Name));
            names.Write([0, 0]);
        }
        w.Property(Name, names.ToArray());

        var times = new byte[2 + 8 * this.entries.Count];
        times[0] = 1; // all defined, not external
        for (var i = 0; i < this.entries.Count; i++)
            BitConverter.TryWriteBytes(times.AsSpan(2 + 8 * i), FileTime(this.entries[i].Modified));
        w.Property(MTime, times);

        var attributes = new byte[2 + 4 * this.entries.Count];
        attributes[0] = 1;
        for (var i = 0; i < this.entries.Count; i++)
            BitConverter.TryWriteBytes(attributes.AsSpan(2 + 4 * i), this.entries[i].Attributes);
        w.Property(WinAttributes, attributes);

        w.Byte(End);
        w.Byte(End);
        return w.ToArray();
    }

    // FILE_ATTRIBUTE_DIRECTORY / ARCHIVE, plus p7zip's unix extension: 0x8000 and st_mode in the high 16 bits
    static uint Attributes(bool directory, UnixFileMode? mode)
    {
        uint attributes = directory ? 0x10u : 0x20u;
        if (mode is { } m)
            attributes |= 0x8000u | (((directory ? 0x4000u : 0x8000u) | ((uint)m & 0xFFF)) << 16);
        return attributes;
    }

    static ulong FileTime(DateTime modified)
    {
        try
        {
            // ToFileTime: a time that doesn't say is local, as file times read off disk and out of archives are
            // (ToFileTimeUtc would take it as UTC already, and shift it by the time zone)
            return modified > DateTime.MinValue ? (ulong)modified.ToFileTime() : (ulong)DateTime.UtcNow.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return (ulong)DateTime.UtcNow.ToFileTimeUtc(); // before 1601
        }
    }

    // most significant bit first
    static byte[] BitField(IEnumerable<bool> bits)
    {
        var list = bits.ToList();
        var bytes = new byte[(list.Count + 7) / 8];
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i])
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        return bytes;
    }

    sealed class HeaderWriter
    {
        readonly MemoryStream ms = new();

        public void Byte(byte value) => this.ms.WriteByte(value);
        public void Bytes(byte[] value) => this.ms.Write(value);

        public void UInt32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BitConverter.TryWriteBytes(b, value);
            this.ms.Write(b);
        }

        public void Property(byte id, byte[] data)
        {
            this.Byte(id);
            this.Number((ulong)data.Length);
            this.Bytes(data);
        }

        // 7z's variable length number: the count of leading 1 bits in the first byte is the count of extra bytes
        public void Number(ulong value)
        {
            byte first = 0;
            byte mask = 0x80;
            var extra = 0;
            for (; extra < 8; extra++)
            {
                if (value < 1UL << (7 * (extra + 1)))
                {
                    first |= (byte)(value >> (8 * extra));
                    break;
                }
                first |= mask;
                mask >>= 1;
            }
            this.ms.WriteByte(first);
            for (var i = 0; i < extra; i++)
            {
                this.ms.WriteByte((byte)value);
                value >>= 8;
            }
        }

        public byte[] ToArray() => this.ms.ToArray();
    }

    // counts and checksums what's read through it
    sealed class Crc32Stream(Stream inner) : Stream
    {
        uint crc = 0xFFFFFFFF;

        public long Count { get; private set; }
        public uint Crc => ~this.crc;

        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            this.crc = Crc32.Update(this.crc, buffer[..read]);
            this.Count += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => this.Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // the LZMA encoder closes its output when disposed - the archive has more to come
    sealed class KeepOpenStream(Stream inner) : Stream
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

/// <summary>CRC-32 (IEEE 802.3), as 7z checksums its streams and headers.</summary>
static class Crc32
{
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);
}
