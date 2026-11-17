using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace ShinyCommander.Core.Metadata;

/// <summary>
/// The string values of a property list's top-level dictionary - an app bundle's Info.plist, for its version. Reads
/// both the XML form and the binary one (bplist00) most shipped apps use, on any OS.
/// </summary>
public static class PlistReader
{
    public static IReadOnlyDictionary<string, string> ReadStrings(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return bytes.AsSpan().StartsWith("bplist00"u8) ? Binary(bytes) : Xml(bytes);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    static Dictionary<string, string> Xml(byte[] bytes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = new MemoryStream(bytes);
        var doc = XDocument.Load(stream, LoadOptions.None);
        var dict = doc.Root?.Element("dict");
        if (dict == null)
            return result;

        // <key>name</key><string>value</string> pairs; values that aren't strings are skipped
        var children = dict.Elements().ToList();
        for (var i = 0; i + 1 < children.Count; i++)
        {
            if (children[i].Name.LocalName == "key" && children[i + 1].Name.LocalName == "string")
                result[children[i].Value] = children[i + 1].Value;
        }
        return result;
    }

    static Dictionary<string, string> Binary(byte[] b)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var trailer = b.AsSpan(b.Length - 32);
        int offsetSize = trailer[6];
        int refSize = trailer[7];
        var count = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(8, 8));
        var top = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(16, 8));
        var table = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(24, 8));

        int Offset(int obj) => (int)Read(b, table + obj * offsetSize, offsetSize);

        var at = Offset(top);
        if (b[at] >> 4 != 0xD) // not a dictionary
            return result;

        var (pairs, start) = Length(b, at);
        for (var i = 0; i < pairs && i < count; i++)
        {
            var key = Text(b, Offset((int)Read(b, start + i * refSize, refSize)));
            var value = Text(b, Offset((int)Read(b, start + (pairs + i) * refSize, refSize)));
            if (key != null && value != null)
                result[key] = value;
        }
        return result;
    }

    // ASCII (0x5_) and UTF-16 (0x6_) strings; anything else isn't text
    static string? Text(byte[] b, int at)
    {
        var type = b[at] >> 4;
        if (type != 0x5 && type != 0x6)
            return null;

        var (length, start) = Length(b, at);
        return type == 0x5
            ? Encoding.ASCII.GetString(b, start, length)
            : Encoding.BigEndianUnicode.GetString(b, start, length * 2);
    }

    // the low nibble is the length, or 0xF and an int object after the marker holds it
    static (int Length, int Start) Length(byte[] b, int at)
    {
        var low = b[at] & 0xF;
        if (low != 0xF)
            return (low, at + 1);

        var size = 1 << (b[at + 1] & 0xF);
        return ((int)Read(b, at + 2, size), at + 2 + size);
    }

    static long Read(byte[] b, int at, int size)
    {
        long value = 0;
        for (var i = 0; i < size; i++)
            value = value << 8 | b[at + i];
        return value;
    }
}
