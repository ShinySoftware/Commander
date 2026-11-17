using System.IO.Compression;
using System.Text;

namespace ShinyCommander.Core.Metadata;

/// <summary>
/// A PDF's page count, author and title, from its bytes - no PDF library. The page tree's root says how many pages
/// there are (<c>/Type /Pages … /Count n</c>, the largest count); since PDF 1.5 those dictionaries often sit in
/// compressed object streams, which are inflated and searched the same way.
/// </summary>
static class PdfInfo
{
    // past this a PDF is read from its two ends - where the page tree and the document info usually are
    const int MaxWhole = 64 * 1024 * 1024;
    const int EndChunk = 8 * 1024 * 1024;
    const int MaxInflated = 16 * 1024 * 1024;
    const int Window = 400;

    public static FileMetadata Read(string path, long maxBytes, CancellationToken ct)
    {
        var bytes = Load(path, maxBytes);
        if (bytes.Length < 8 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return FileMetadata.Empty;

        var parts = new List<byte[]> { bytes };
        parts.AddRange(ObjectStreams(bytes, ct));

        var pages = parts.Select(x => PageCount(x)).Max();
        var info = parts.Select(x => DocumentInfo(x)).FirstOrDefault(x => x.Author != null || x.Title != null);
        return new FileMetadata
        {
            Pages = pages > 0 ? pages : null,
            Author = info.Author,
            Title = info.Title
        };
    }

    static byte[] Load(string path, long maxBytes)
    {
        using var stream = File.OpenRead(path);
        var length = Math.Min(stream.Length, maxBytes);
        if (length <= MaxWhole)
        {
            var all = new byte[length];
            stream.ReadExactly(all);
            return all;
        }

        var joined = new byte[EndChunk * 2];
        stream.ReadExactly(joined, 0, EndChunk);
        stream.Seek(-EndChunk, SeekOrigin.End);
        stream.ReadExactly(joined, EndChunk, EndChunk);
        return joined;
    }

    static IEnumerable<byte[]> ObjectStreams(byte[] bytes, CancellationToken ct)
    {
        var at = 0;
        var total = 0;
        while (total < MaxInflated && (at = IndexOf(bytes, "/ObjStm"u8, at)) >= 0)
        {
            ct.ThrowIfCancellationRequested();
            at += 7;
            var start = IndexOf(bytes, "stream"u8, at);
            var inflated = start < 0 || start - at > 2048 ? null : Inflate(bytes, start + 6, MaxInflated - total);
            if (inflated != null)
            {
                total += inflated.Length;
                yield return inflated;
            }
        }
    }

    static byte[]? Inflate(byte[] bytes, int at, int max)
    {
        // the data starts after the end of line that follows the keyword
        if (at < bytes.Length && bytes[at] == '\r')
            at++;
        if (at < bytes.Length && bytes[at] == '\n')
            at++;

        try
        {
            using var zlib = new ZLibStream(new MemoryStream(bytes, at, bytes.Length - at, false), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while (output.Length < max && (read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
            return output.ToArray();
        }
        catch
        {
            // not flate, or cut short - whatever came out before it stopped is all there is
            return null;
        }
    }

    // the root of the page tree counts every page; its kids count fewer
    static int PageCount(byte[] bytes)
    {
        var best = 0;
        var at = 0;
        while ((at = IndexOf(bytes, "/Type"u8, at)) >= 0)
        {
            at += 5;
            var name = SkipSpace(bytes, at);
            if (Token(bytes, name, "/Pages"u8))
                best = Math.Max(best, CountNear(bytes, at));
        }
        return best;
    }

    static int CountNear(byte[] bytes, int at)
    {
        var from = Math.Max(0, at - Window);
        var to = Math.Min(bytes.Length, at + Window);
        var best = 0;
        var i = from;
        while ((i = IndexOf(bytes, "/Count"u8, i, to)) >= 0)
        {
            i = SkipSpace(bytes, i + 6);
            var n = 0;
            while (i < bytes.Length && bytes[i] is >= (byte)'0' and <= (byte)'9' && n < 10_000_000)
                n = n * 10 + (bytes[i++] - '0');
            best = Math.Max(best, n);
        }
        return best;
    }

    // /Title also names bookmarks - only the one beside /Author or /Producer belongs to the document info
    static (string? Author, string? Title) DocumentInfo(byte[] bytes)
    {
        var anchor = IndexOf(bytes, "/Author"u8, 0);
        if (anchor < 0)
            anchor = IndexOf(bytes, "/Producer"u8, 0);
        if (anchor < 0)
            return (null, null);

        var from = Math.Max(0, anchor - Window);
        var to = Math.Min(bytes.Length, anchor + Window);
        return (StringAfter(bytes, "/Author"u8, from, to), StringAfter(bytes, "/Title"u8, from, to));
    }

    static string? StringAfter(byte[] bytes, ReadOnlySpan<byte> key, int from, int to)
    {
        var at = IndexOf(bytes, key, from, to);
        if (at < 0)
            return null;

        at = SkipSpace(bytes, at + key.Length);
        var raw = at < bytes.Length && bytes[at] == '(' ? Literal(bytes, at)
            : at < bytes.Length && bytes[at] == '<' ? Hex(bytes, at)
            : null;
        if (raw == null)
            return null;

        var text = raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2)
            : Encoding.Latin1.GetString(raw);
        text = text.Trim('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    // ( ... ) with nested parentheses and backslash escapes
    static byte[] Literal(byte[] bytes, int at)
    {
        var output = new List<byte>();
        var depth = 0;
        for (var i = at; i < bytes.Length && output.Count < 4096; i++)
        {
            var b = bytes[i];
            if (b == '\\' && i + 1 < bytes.Length)
            {
                i = Escape(bytes, i + 1, output);
            }
            else if (b == '(')
            {
                if (depth++ > 0)
                    output.Add(b);
            }
            else if (b == ')')
            {
                if (--depth == 0)
                    break;
                output.Add(b);
            }
            else
            {
                output.Add(b);
            }
        }
        return output.ToArray();
    }

    // returns the index of the escape's last byte
    static int Escape(byte[] bytes, int i, List<byte> output)
    {
        var c = bytes[i];
        if (c is >= (byte)'0' and <= (byte)'7')
        {
            var value = 0;
            var end = Math.Min(i + 3, bytes.Length);
            var j = i;
            while (j < end && bytes[j] is >= (byte)'0' and <= (byte)'7')
                value = value * 8 + (bytes[j++] - '0');
            output.Add((byte)value);
            return j - 1;
        }

        switch (c)
        {
            case (byte)'n': output.Add((byte)'\n'); break;
            case (byte)'r': output.Add((byte)'\r'); break;
            case (byte)'t': output.Add((byte)'\t'); break;
            case (byte)'b': output.Add((byte)'\b'); break;
            case (byte)'f': output.Add((byte)'\f'); break;
            case (byte)'\r': return i + 1 < bytes.Length && bytes[i + 1] == '\n' ? i + 1 : i; // a line continuation
            case (byte)'\n': break;
            default: output.Add(c); break;
        }
        return i;
    }

    static byte[] Hex(byte[] bytes, int at)
    {
        var digits = new List<int>();
        for (var i = at + 1; i < bytes.Length && bytes[i] != '>' && digits.Count < 8192; i++)
        {
            var d = HexDigit(bytes[i]);
            if (d >= 0)
                digits.Add(d);
        }
        if (digits.Count % 2 == 1)
            digits.Add(0);
        return Enumerable.Range(0, digits.Count / 2).Select(x => (byte)(digits[x * 2] << 4 | digits[x * 2 + 1])).ToArray();
    }

    static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1
    };

    static int SkipSpace(byte[] bytes, int at)
    {
        while (at < bytes.Length && bytes[at] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0 or (byte)'\f')
            at++;
        return at;
    }

    // the name and nothing more: /Pages, not /PagesX
    static bool Token(byte[] bytes, int at, ReadOnlySpan<byte> name)
    {
        if (at + name.Length > bytes.Length || !bytes.AsSpan(at, name.Length).SequenceEqual(name))
            return false;
        var end = at + name.Length;
        return end == bytes.Length || !Char.IsAsciiLetterOrDigit((char)bytes[end]);
    }

    static int IndexOf(byte[] bytes, ReadOnlySpan<byte> value, int from, int to = -1)
    {
        var end = to < 0 ? bytes.Length : to;
        if (from >= end)
            return -1;
        var found = bytes.AsSpan(from, end - from).IndexOf(value);
        return found < 0 ? -1 : from + found;
    }
}
