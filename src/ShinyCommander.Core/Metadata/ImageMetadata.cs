using System.Globalization;
using System.Text;
using ExifLibrary;

namespace ShinyCommander.Core.Metadata;

/// <summary>The EXIF fields the metadata editor offers. Empty text (or null) removes the tag.</summary>
public sealed class ImageMetadataValues
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Artist { get; set; }
    public string? Copyright { get; set; }
    public string? Keywords { get; set; }
    public string? Comment { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? Software { get; set; }
    public DateTime? DateTaken { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

/// <summary>One tag as it is stored, for the read-only list under the editable fields.</summary>
public sealed record ImageMetadataTag(string Group, string Name, string Value);

public sealed record ImageMetadataSnapshot(ImageMetadataValues Values, IReadOnlyList<ImageMetadataTag> Tags);

/// <summary>
/// Reads and writes EXIF in JPEG files without re-encoding the picture - only the metadata segment is rewritten,
/// so the pixels stay byte for byte what they were.
/// </summary>
public static class ImageMetadata
{
    static readonly HashSet<string> Editable = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "jpe", "jfif" };

    // a JPEG starts with an SOI marker, whatever its extension says
    public static bool CanEdit(string path)
    {
        if (!Editable.Contains(Path.GetExtension(path).TrimStart('.')))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[3];
            return stream.Read(header) == 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static ImageMetadataSnapshot Read(string path)
    {
        var file = ImageFile.FromFile(path);
        var p = file.Properties;
        var values = new ImageMetadataValues
        {
            Title = Text(p, ExifTag.WindowsTitle),
            Description = Text(p, ExifTag.ImageDescription),
            Artist = Text(p, ExifTag.Artist) ?? Text(p, ExifTag.WindowsAuthor),
            Copyright = Text(p, ExifTag.Copyright),
            Keywords = Text(p, ExifTag.WindowsKeywords),
            Comment = Text(p, ExifTag.UserComment) ?? Text(p, ExifTag.WindowsComment),
            Make = Text(p, ExifTag.Make),
            Model = Text(p, ExifTag.Model),
            Software = Text(p, ExifTag.Software),
            DateTaken = p.Get(ExifTag.DateTimeOriginal) is ExifDateTime dt ? dt.Value : null,
            Latitude = Coordinate(p, ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef),
            Longitude = Coordinate(p, ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef)
        };

        var tags = p
            .Where(x => x.IFD != IFD.JFIF && x.Tag is not (ExifTag.EXIFIFDPointer or ExifTag.GPSIFDPointer or ExifTag.InteroperabilityIFDPointer))
            .Select(x => new ImageMetadataTag(GroupName(x.IFD), TagName(x.Tag), Describe(x)))
            .ToList();

        return new ImageMetadataSnapshot(values, tags);
    }

    public static void Write(string path, ImageMetadataValues values)
    {
        var file = ImageFile.FromFile(path);
        var p = file.Properties;

        SetText(p, ExifTag.WindowsTitle, values.Title);
        SetText(p, ExifTag.ImageDescription, values.Description);
        SetText(p, ExifTag.Artist, values.Artist);
        SetText(p, ExifTag.WindowsAuthor, values.Artist);
        SetText(p, ExifTag.Copyright, values.Copyright);
        SetText(p, ExifTag.WindowsKeywords, values.Keywords);
        SetText(p, ExifTag.UserComment, values.Comment);
        SetText(p, ExifTag.WindowsComment, values.Comment);
        SetText(p, ExifTag.Make, values.Make);
        SetText(p, ExifTag.Model, values.Model);
        SetText(p, ExifTag.Software, values.Software);

        if (values.DateTaken is { } taken)
        {
            p.Set(ExifTag.DateTimeOriginal, taken);
            p.Set(ExifTag.DateTimeDigitized, taken);
        }
        else
        {
            p.Remove(ExifTag.DateTimeOriginal);
            p.Remove(ExifTag.DateTimeDigitized);
        }

        if (values.Latitude is { } lat && values.Longitude is { } lon)
        {
            SetCoordinate(p, ExifTag.GPSLatitude, lat);
            p.Set(ExifTag.GPSLatitudeRef, lat < 0 ? GPSLatitudeRef.South : GPSLatitudeRef.North);
            SetCoordinate(p, ExifTag.GPSLongitude, lon);
            p.Set(ExifTag.GPSLongitudeRef, lon < 0 ? GPSLongitudeRef.West : GPSLongitudeRef.East);
        }
        else
        {
            RemoveLocation(p);
        }

        Save(file, path);
    }

    /// <summary>Takes the GPS block out - where the picture was taken - and leaves the rest.</summary>
    public static void RemoveLocation(string path)
    {
        var file = ImageFile.FromFile(path);
        RemoveLocation(file.Properties);
        Save(file, path);
    }

    /// <summary>Removes every EXIF tag (camera, dates, location, thumbnail); the JFIF header the decoder needs stays.</summary>
    public static void RemoveAll(string path)
    {
        var file = ImageFile.FromFile(path);
        var exif = file.Properties.Where(x => x.IFD != IFD.JFIF).ToList();
        file.Properties.Remove(exif);
        file.Thumbnail = null;
        Save(file, path);
    }

    /// <summary>
    /// Gives <paramref name="target"/> - a re-encoded copy of the same photo, as the image editor saves it - the EXIF
    /// of <paramref name="source"/>, which an encoder drops. What no longer describes the new pixels goes: the
    /// orientation (the editor saves the picture upright), the stored pixel size and the embedded thumbnail.
    /// </summary>
    public static void CopyMetadata(string source, string target)
    {
        var from = ImageFile.FromFile(source);
        var to = ImageFile.FromFile(target);

        to.Properties.Remove(to.Properties.Where(x => x.IFD != IFD.JFIF).ToList());
        foreach (var property in from.Properties.Where(x => x.IFD is not (IFD.JFIF or IFD.First or IFD.MakerNote)))
            to.Properties.Add(property);

        to.Properties.Remove(ExifTag.PixelXDimension);
        to.Properties.Remove(ExifTag.PixelYDimension);
        to.Properties.Remove(ExifTag.Orientation);
        to.Properties.Remove(ExifTag.MakerNote);
        to.Thumbnail = null;
        Save(to, target);
    }

    static void RemoveLocation(ExifPropertyCollection<ExifProperty> p)
    {
        p.Remove(IFD.GPS);
        p.Remove(ExifTag.GPSIFDPointer);
    }

    static void Save(ImageFile file, string path)
    {
        // ExifLibNet writes each IFD in insertion order, but TIFF wants ascending tag numbers - readers like macOS's
        // ImageIO skip an IFD that isn't sorted, which made new tags look like they were never saved
        var sorted = file.Properties
            .OrderBy(x => x.IFD)
            .ThenBy(x => (int)x.Tag)
            .Select(x => x is ExifAscii ascii and not Utf8Ascii ? new Utf8Ascii(ascii.Tag, ascii.Value) : x)
            .ToList();
        file.Properties.Clear();
        foreach (var property in sorted)
            file.Properties.Add(property);

        // a temporary file first, so a failed write can't leave a half-written picture behind
        var temp = path + ".shinytmp";
        try
        {
            file.Save(temp);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    static string? Text(ExifPropertyCollection<ExifProperty> p, ExifTag tag)
    {
        var value = p.Get(tag) switch
        {
            null => null,
            ExifEncodedString s => s.Value,
            WindowsByteString s => s.Value,
            ExifAscii s => s.Value,
            var x => x.ToString()
        };
        value = value?.TrimEnd('\0').Trim();
        return String.IsNullOrEmpty(value) ? null : value;
    }

    static void SetText(ExifPropertyCollection<ExifProperty> p, ExifTag tag, string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
            p.Remove(tag);
        else
            // the two-argument Set adds a second copy of the tag instead of replacing it
            p.Set(tag, value.Trim(), Encoding.UTF8);
    }

    /// <summary>
    /// ExifLibNet's ASCII tag counts characters, not bytes, so text with an "é" in it was written with a count one
    /// short and readers cut it off (or rejected the IFD). Same tag, with the count of the bytes actually written.
    /// </summary>
    sealed class Utf8Ascii(ExifTag tag, string value) : ExifAscii(tag, value, Encoding.UTF8)
    {
        public override ExifInterOperability Interoperability
        {
            get
            {
                var bytes = Encoding.UTF8.GetBytes(this.Value + "\0");
                return new ExifInterOperability(ExifTagFactory.GetTagID(this.Tag), InterOpType.ASCII, (uint)bytes.Length, bytes);
            }
        }
    }

    static double? Coordinate(ExifPropertyCollection<ExifProperty> p, ExifTag tag, ExifTag refTag)
    {
        if (p.Get(tag) is not ExifURationalArray { Value.Length: 3 } dms)
            return null;

        var value = (double)dms.Value[0] + (double)dms.Value[1] / 60.0 + (double)dms.Value[2] / 3600.0;
        var negative = p.Get(refTag) switch
        {
            ExifEnumProperty<GPSLatitudeRef> r => r.Value == GPSLatitudeRef.South,
            ExifEnumProperty<GPSLongitudeRef> r => r.Value == GPSLongitudeRef.West,
            _ => false
        };
        return Math.Round(negative ? -value : value, 6);
    }

    static void SetCoordinate(ExifPropertyCollection<ExifProperty> p, ExifTag tag, double value)
    {
        var abs = Math.Abs(value);
        var degrees = Math.Floor(abs);
        var minutes = Math.Floor((abs - degrees) * 60);
        var seconds = (abs - degrees - minutes / 60) * 3600;
        p.Set(tag, (float)degrees, (float)minutes, (float)Math.Round(seconds, 4));
    }

    static string GroupName(IFD ifd) => ifd switch
    {
        IFD.Zeroth => "Image",
        IFD.EXIF => "EXIF",
        IFD.GPS => "GPS",
        IFD.Interop => "Interop",
        IFD.First => "Thumbnail",
        _ => ifd.ToString()
    };

    // "DateTimeOriginal" -> "Date Time Original"
    static string TagName(ExifTag tag)
    {
        var name = tag.ToString();
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && Char.IsUpper(name[i]) && !Char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    static string Describe(ExifProperty property)
    {
        var text = property switch
        {
            ExifDateTime d => d.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ExifUndefined u => $"{u.Value.Length} bytes",
            ExifByteArray b => $"{b.Value.Length} bytes",
            ExifURationalArray a => String.Join(" ", a.Value.Select(x => ((double)x).ToString("0.####", CultureInfo.InvariantCulture))),
            _ => property.ToString()
        };
        text = (text ?? String.Empty).TrimEnd('\0').Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }
}
