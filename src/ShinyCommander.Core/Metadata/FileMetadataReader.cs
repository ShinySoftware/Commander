using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Metadata;

/// <summary>
/// Reads what the custom columns show from a local file: duration, dimensions and tags of audio, video and pictures
/// (TagLib#), PDF and Office page counts, text line counts, archive file counts and app versions. Never throws - a file
/// it can't make sense of gives <see cref="FileMetadata.Empty"/>.
/// </summary>
public static class FileMetadataReader
{
    // a text file past this isn't counted - the column is for source and notes, not logs gigabytes long
    const long MaxTextBytes = 32L * 1024 * 1024;
    const long MaxPdfBytes = 256L * 1024 * 1024;
    const ushort LensModelTag = 0xA434;

    public static FileMetadata Read(FileEntry entry, CancellationToken ct = default)
    {
        try
        {
            return FileKinds.Of(entry) switch
            {
                FileKind.Image => ReadImage(entry.FullPath),
                FileKind.Video or FileKind.Audio => ReadMedia(entry.FullPath),
                FileKind.Document => ReadDocument(entry.FullPath, ct),
                FileKind.Text => ReadText(entry.FullPath, ct),
                FileKind.Archive => ReadArchive(entry.FullPath),
                FileKind.App => ReadApp(entry),
                _ => FileMetadata.Empty
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FileMetadata.Empty;
        }
    }

    static FileMetadata ReadMedia(string path)
    {
        using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
        var p = file.Properties;
        var tag = file.Tag;
        var video = p.Codecs.OfType<TagLib.IVideoCodec>().FirstOrDefault(x => x.VideoWidth > 0);
        var audio = p.Codecs.OfType<TagLib.IAudioCodec>().FirstOrDefault();

        var duration = p.Duration;
        var bitrate = p.AudioBitrate;
        if (audio is TagLib.Mpeg.AudioHeader mpeg && mpeg.AudioSampleRate > 0 && MpegFrames(path, file.InvariantStartPosition, mpeg) is { } frames)
        {
            duration = TimeSpan.FromSeconds((double)frames.Count * SamplesPerFrame(mpeg) / mpeg.AudioSampleRate);
            if (frames.Bytes > 0 && duration > TimeSpan.Zero)
            {
                // the average of a variable bitrate file; a constant one ("Info") is its nominal rate - the Info frame and
                // padding would make 64 kbit/s read 65
                var average = frames.Bytes * 8 / duration.TotalSeconds / 1000;
                bitrate = frames.Constant ? StandardBitrates.MinBy(x => Math.Abs(x - average)) : (int)Math.Round(average);
            }
        }

        return new FileMetadata
        {
            Duration = duration > TimeSpan.Zero ? duration : null,
            Width = p.VideoWidth > 0 ? p.VideoWidth : null,
            Height = p.VideoHeight > 0 ? p.VideoHeight : null,
            VideoCodec = Clean(video?.Description),
            AudioCodec = Clean(audio?.Description),
            Bitrate = bitrate > 0 ? bitrate : null,
            SampleRate = p.AudioSampleRate > 0 ? p.AudioSampleRate : null,
            Channels = p.AudioChannels > 0 ? p.AudioChannels : null,
            Title = Clean(tag.Title),
            Artist = Clean(tag.FirstPerformer ?? tag.FirstAlbumArtist),
            Album = Clean(tag.Album),
            Genre = Clean(tag.FirstGenre),
            Track = tag.Track > 0 ? (int)tag.Track : null,
            Year = tag.Year > 0 ? (int)tag.Year : null
        };
    }

    /// <summary>
    /// The frame count (and byte count) in an MP3's Xing / Info or VBRI header. TagLib# doesn't see lame's "Info" header
    /// of a constant bitrate file and times it by its first frame's bitrate - which lame writes bigger than the rest of a
    /// low-bitrate file, so a 32 kbit/s file came out half its length. The count is exact.
    /// </summary>
    static readonly int[] StandardBitrates = [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 192, 224, 256, 320];

    static (uint Count, uint Bytes, bool Constant)? MpegFrames(string path, long audioStart, TagLib.Mpeg.AudioHeader header)
    {
        var head = new byte[256];
        int length;
        using (var stream = File.OpenRead(path))
        {
            stream.Position = Math.Max(0, audioStart);
            length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        // the header sits in the first frame, after the side information
        var span = head.AsSpan(0, length);
        var at = span.IndexOf("Xing"u8);
        var constant = at < 0;
        if (constant)
            at = span.IndexOf("Info"u8);
        if (at >= 0 && at + 16 <= length)
        {
            var flags = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(at + 4, 4));
            var frames = (flags & 1) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(span.Slice(at + 8, 4)) : 0;
            var bytes = (flags & 3) == 3 ? BinaryPrimitives.ReadUInt32BigEndian(span.Slice(at + 12, 4)) : 0;
            if (frames > 0)
                return (frames, bytes, constant);
        }

        return header.VBRIHeader.Present && header.VBRIHeader.TotalFrames > 0
            ? (header.VBRIHeader.TotalFrames, header.VBRIHeader.TotalSize, false)
            : null;
    }

    static int SamplesPerFrame(TagLib.Mpeg.AudioHeader header) => header.AudioLayer switch
    {
        1 => 384,
        3 when header.Version != TagLib.Mpeg.Version.Version1 => 576,
        _ => 1152
    };

    static FileMetadata ReadImage(string path)
    {
        // TagLib# reads JPEG, PNG, GIF, TIFF, BMP and camera RAW - not WebP or HEIF, whose size is in a header we read
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext is "webp" or "heic" or "heif" or "avif")
            return ImageHeaders.Read(path) is { } size ? new FileMetadata { Width = size.Width, Height = size.Height } : FileMetadata.Empty;

        using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
        var p = file.Properties;
        var image = file as TagLib.Image.File;
        var tag = image?.ImageTag;
        var ifd = file.GetTag(TagLib.TagTypes.TiffIFD) as TagLib.IFD.IFDTag;
        var orientation = (int?)tag?.Orientation;

        return new FileMetadata
        {
            Width = p?.PhotoWidth > 0 ? p.PhotoWidth : null,
            Height = p?.PhotoHeight > 0 ? p.PhotoHeight : null,
            DateTaken = tag?.DateTime,
            Camera = Camera(tag?.Make, tag?.Model),
            Lens = Clean(ifd?.ExifIFD?.GetStringValue(0, LensModelTag)),
            FNumber = tag?.FNumber is > 0 ? tag.FNumber : null,
            ExposureTime = tag?.ExposureTime is > 0 ? tag.ExposureTime : null,
            Iso = tag?.ISOSpeedRatings is > 0 ? (int)tag.ISOSpeedRatings : null,
            FocalLength = tag?.FocalLength is > 0 ? tag.FocalLength : null,
            Orientation = orientation is >= 1 and <= 8 ? orientation : null,
            HasLocation = tag == null ? null : tag.Latitude != null && tag.Longitude != null,
            Title = Clean(tag?.Title)
        };
    }

    /// <summary>"Canon Canon EOS R5" reads as "Canon EOS R5" - most makers repeat their name in the model.</summary>
    internal static string? Camera(string? make, string? model)
    {
        make = Clean(make);
        model = Clean(model);
        if (model == null)
            return make;
        if (make == null)
            return model;

        var brand = make.Split(' ')[0];
        return model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    static FileMetadata ReadDocument(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext == "pdf" ? PdfInfo.Read(path, MaxPdfBytes, ct) : ReadOffice(path);
    }

    // docx / xlsx / pptx: zip packages whose docProps say who wrote them and, as last saved, how long they are
    static FileMetadata ReadOffice(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var app = LoadXml(zip, "docProps/app.xml");
        var core = LoadXml(zip, "docProps/core.xml");

        var pages = Number(app, "Pages") ?? Number(app, "Slides");
        return new FileMetadata
        {
            Pages = pages,
            Words = Number(app, "Words"),
            Author = Clean(Text(core, "creator")),
            Title = Clean(Text(core, "title"))
        };
    }

    static XDocument? LoadXml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry == null)
            return null;

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    static string? Text(XDocument? doc, string localName)
        => doc?.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == localName)?.Value;

    static int? Number(XDocument? doc, string localName)
        => Int32.TryParse(Text(doc, localName), out var n) && n > 0 ? n : null;

    static FileMetadata ReadText(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxTextBytes)
            return FileMetadata.Empty;
        if (info.Length == 0)
            return new FileMetadata { Lines = 0 };

        var lines = 0;
        var last = (byte)0;
        var buffer = new byte[64 * 1024];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.SequentialScan);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var span = buffer.AsSpan(0, read);
            // a NUL is a binary file in a text file's name - no line count for it
            if (span.IndexOf((byte)0) >= 0)
                return FileMetadata.Empty;
            lines += span.Count((byte)'\n');
            last = span[^1];
        }
        // the last line counts whether or not it ends in a newline
        return new FileMetadata { Lines = last == '\n' ? lines : lines + 1 };
    }

    static FileMetadata ReadArchive(string path)
    {
        using var reader = ArchiveReader.Open(path);
        var files = reader.Items.Where(x => !x.IsDirectory).ToList();
        return new FileMetadata { FileCount = files.Count, UnpackedSize = files.Sum(x => x.Size) };
    }

    static FileMetadata ReadApp(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            var plist = PlistReader.ReadStrings(Path.Combine(entry.FullPath, "Contents", "Info.plist"));
            return new FileMetadata
            {
                Version = Clean(plist.GetValueOrDefault("CFBundleShortVersionString") ?? plist.GetValueOrDefault("CFBundleVersion")),
                Company = Clean(plist.GetValueOrDefault("NSHumanReadableCopyright"))
            };
        }

        // the PE version resource - Windows reads it from any exe or dll, elsewhere only from .NET assemblies
        var info = FileVersionInfo.GetVersionInfo(entry.FullPath);
        return new FileMetadata
        {
            Version = Clean(info.ProductVersion ?? info.FileVersion),
            Company = Clean(info.CompanyName)
        };
    }

    static string? Clean(string? value)
    {
        var trimmed = value?.Trim('\0', ' ', '\t', '\r', '\n');
        return String.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Picture sizes from the header of formats TagLib# doesn't read: WebP (RIFF) and HEIF / AVIF (ISO boxes).</summary>
static class ImageHeaders
{
    public static (int Width, int Height)? Read(string path)
    {
        var head = new byte[256 * 1024];
        int length;
        using (var stream = File.OpenRead(path))
            length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        var span = head.AsSpan(0, length);
        return span.Length > 30 && span[..4].SequenceEqual("RIFF"u8) && span.Slice(8, 4).SequenceEqual("WEBP"u8)
            ? WebP(span)
            : Heif(span);
    }

    static (int, int)? WebP(ReadOnlySpan<byte> s)
    {
        var chunk = s.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
            return (1 + (s[24] | s[25] << 8 | s[26] << 16), 1 + (s[27] | s[28] << 8 | s[29] << 16));
        if (chunk.SequenceEqual("VP8L"u8))
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(21, 4));
            return ((int)(bits & 0x3FFF) + 1, (int)(bits >> 14 & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8 "u8))
            return (BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(26, 2)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(28, 2)) & 0x3FFF);
        return null;
    }

    // every image item has an 'ispe' (image spatial extents) property; the primary picture is the biggest -
    // the rest are thumbnails and the tiles of a grid
    static (int, int)? Heif(ReadOnlySpan<byte> s)
    {
        (int W, int H)? best = null;
        var at = s.IndexOf("ispe"u8);
        while (at >= 4 && at + 16 <= s.Length)
        {
            var w = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(at + 8, 4));
            var h = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(at + 12, 4));
            if (w > 0 && h > 0 && (best == null || (long)w * h > (long)best.Value.W * best.Value.H))
                best = (w, h);

            var next = s[(at + 4)..].IndexOf("ispe"u8);
            at = next < 0 ? -1 : at + 4 + next;
        }
        return best;
    }
}
