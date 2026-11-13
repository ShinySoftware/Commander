using System.IO.Compression;
using System.Runtime.InteropServices;
using Shiny;
using ShinyCommander.Services;

namespace ShinyCommander;

/// <summary>
/// Explorer's icons, from the shell's icon list (SHGetFileInfo). Files share one icon per extension, asked for by name
/// so it needs no file (archive entries get one too); programs, shortcuts, icon files and folders have their own, and
/// are cached by the shell's icon index so a folder of folders still draws one.
/// </summary>
[Singleton]
sealed class WindowsSystemIcons : ISystemIcons
{
    // files whose icon is their own rather than their type's
    static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase) { "exe", "lnk", "ico", "cur", "ani", "url", "scr", "cpl", "msc", "appref-ms" };

    readonly Dictionary<(string Extension, int Size), ImageSource?> byExtension = [];
    readonly Dictionary<(int Index, bool Link, int Size), ImageSource?> byIndex = [];

    public ImageSource? Get(FileEntry entry, int size)
    {
        var sizeFlag = size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON;
        if (entry.IsInArchive || !entry.IsDirectory && !OwnIcon.Contains(entry.Extension))
        {
            var ext = entry.IsDirectory ? "\\dir" : entry.Extension.ToLowerInvariant();
            if (!this.byExtension.TryGetValue((ext, size), out var byType))
            {
                byType = entry.IsDirectory
                    ? Load("folder", FILE_ATTRIBUTE_DIRECTORY, SHGFI_USEFILEATTRIBUTES | sizeFlag)
                    : Load("file." + ext, FILE_ATTRIBUTE_NORMAL, SHGFI_USEFILEATTRIBUTES | sizeFlag);
                this.byExtension[(ext, size)] = byType;
            }
            return byType;
        }

        var info = new SHFILEINFO();
        if (SHGetFileInfo(entry.FullPath, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | sizeFlag) == IntPtr.Zero)
            return null;

        var link = entry.Extension.Equals("lnk", StringComparison.OrdinalIgnoreCase);
        if (!this.byIndex.TryGetValue((info.iIcon, link, size), out var source))
        {
            source = Load(entry.FullPath, 0, sizeFlag | (link ? SHGFI_LINKOVERLAY : 0));
            this.byIndex[(info.iIcon, link, size)] = source;
        }
        return source;
    }

    static ImageSource? Load(string path, uint attributes, uint flags)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var png = ToPng(info.hIcon);
            return png == null ? null : ImageSource.FromStream(() => new MemoryStream(png));
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    // the icon's pixels as a PNG - the image views take a stream, not an HICON
    static byte[]? ToPng(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var iconInfo))
            return null;

        try
        {
            // a monochrome icon has no colour bitmap; the motion icon stands in
            if (iconInfo.hbmColor == IntPtr.Zero)
                return null;

            var bitmap = new BITMAP();
            if (GetObject(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), ref bitmap) == 0)
                return null;

            var (width, height) = (bitmap.bmWidth, bitmap.bmHeight);
            var pixels = ReadBits(iconInfo.hbmColor, width, height);
            if (pixels == null)
                return null;

            // an old icon without alpha takes its transparency from the mask (white = transparent)
            if (!HasAlpha(pixels))
            {
                var mask = ReadBits(iconInfo.hbmMask, width, height);
                for (var i = 0; i < pixels.Length; i += 4)
                    pixels[i + 3] = mask == null || mask[i] == 0 ? (byte)255 : (byte)0;
            }
            return EncodePng(pixels, width, height);
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero)
                DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero)
                DeleteObject(iconInfo.hbmMask);
        }
    }

    static bool HasAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return true;
        }
        return false;
    }

    // top-down 32bpp BGRA
    static byte[]? ReadBits(IntPtr hbm, int width, int height)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            return GetDIBits(dc, hbm, 0, (uint)height, pixels, ref header, DIB_RGB_COLORS) == 0 ? null : pixels;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        // each row: filter type 0, then RGBA
        var raw = new byte[height * (width * 4 + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            for (var x = 0; x < width; x++)
            {
                var s = (y * width + x) * 4;
                var d = row + 1 + x * 4;
                raw[d] = bgra[s + 2];
                raw[d + 1] = bgra[s + 1];
                raw[d + 2] = bgra[s];
                raw[d + 3] = bgra[s + 3];
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);

        var ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var chunk = new byte[data.Length + 12];
        WriteInt(chunk, 0, data.Length);
        for (var i = 0; i < 4; i++)
            chunk[4 + i] = (byte)type[i];
        data.CopyTo(chunk, 8);
        WriteInt(chunk, chunk.Length - 4, (int)Crc32(chunk.AsSpan(4, data.Length + 4)));
        stream.Write(chunk);
    }

    static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_SYSICONINDEX = 0x4000;
    const uint SHGFI_LINKOVERLAY = 0x8000;
    const uint SHGFI_LARGEICON = 0x0;
    const uint SHGFI_SMALLICON = 0x1;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    const uint BI_RGB = 0;
    const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr h, int c, ref BITMAP pv);

    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr ho);
}
