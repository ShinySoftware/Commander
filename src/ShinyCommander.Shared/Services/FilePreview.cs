using System.Text;

namespace ShinyCommander.Services;

public enum PreviewKind
{
    Text,
    Hex,
    Image,
    Markdown
}

public sealed record PreviewContent(PreviewKind Kind, byte[] Bytes, long Length, bool Truncated);

/// <summary>Loads a file (on disk or inside an archive) for the F3 lister and the quick-view panel.</summary>
public static class FilePreview
{
    public static async Task<PreviewContent> LoadAsync(VirtualFileSystem vfs, string path, int maxTextBytes, int maxImageBytes = Int32.MaxValue, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        var isImage = FileIcons.IsImage(ext);
        var (bytes, length) = await Task.Run(() =>
        {
            using var stream = vfs.OpenRead(path);
            var len = stream.CanSeek ? stream.Length : -1;
            var limit = isImage ? maxImageBytes : maxTextBytes;
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while (ms.Length < limit && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                ms.Write(buffer, 0, read);
            }
            return (ms.ToArray(), len);
        }, ct).ConfigureAwait(false);

        var truncated = length > bytes.Length;
        var kind = isImage && !truncated ? PreviewKind.Image
            : FileIcons.IsMarkdown(ext) ? PreviewKind.Markdown
            : LooksBinary(bytes) ? PreviewKind.Hex
            : PreviewKind.Text;

        return new PreviewContent(kind, bytes, length, truncated);
    }

    public static bool LooksBinary(byte[] data)
    {
        var sample = Math.Min(data.Length, 8000);
        for (var i = 0; i < sample; i++)
        {
            if (data[i] == 0)
                return true;
        }
        return false;
    }

    public static string DecodeText(byte[] data)
    {
        // BOM aware, otherwise UTF-8 with replacement
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    public static string ToHex(byte[] data, int max)
    {
        var length = Math.Min(data.Length, max);
        var sb = new StringBuilder(length * 4);
        for (var offset = 0; offset < length; offset += 16)
        {
            sb.Append(offset.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                if (offset + i < length)
                    sb.Append(data[offset + i].ToString("X2")).Append(' ');
                else
                    sb.Append("   ");
                if (i == 7)
                    sb.Append(' ');
            }
            sb.Append(' ');
            for (var i = 0; i < 16 && offset + i < length; i++)
            {
                var b = data[offset + i];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }
            sb.AppendLine();
        }
        if (data.Length > max)
            sb.AppendLine(L.Preview_MoreBytesFormat(SizeFormatter.Exact(data.Length - max)));
        return sb.ToString();
    }
}
