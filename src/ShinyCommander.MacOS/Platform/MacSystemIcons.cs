using AppKit;
using CoreGraphics;
using Foundation;
using Shiny;
using ShinyCommander.Services;
using UniformTypeIdentifiers;

namespace ShinyCommander;

/// <summary>
/// Finder's icons. Files share one icon per extension (Launch Services' icon for the type, so it needs no file -
/// archive entries get one too); folders share the plain folder icon unless they have one of their own: an app or
/// other bundle, a custom icon, or one of the folders Finder decorates (Desktop, Downloads, Applications...).
/// </summary>
[Singleton]
sealed class MacSystemIcons : ISystemIcons
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // the folders Finder draws with their own symbol
    static readonly HashSet<string> SpecialFolders = new(
        new[] { "Desktop", "Documents", "Downloads", "Library", "Movies", "Music", "Pictures", "Public", "Applications", "Developer", "Sites" }
            .Select(x => Path.Combine(Home, x))
            .Append(Home)
            .Concat(["/Applications", "/Library", "/System", "/Users", "/Users/Shared", "/System/Applications", "/System/Library"]),
        StringComparer.Ordinal);

    readonly Dictionary<(string Key, int Size), ImageSource?> cache = [];

    public ImageSource? Get(FileEntry entry, int size)
    {
        var key = KeyFor(entry);
        if (!this.cache.TryGetValue((key, size), out var source))
        {
            source = Render(IconFor(key), size);
            this.cache[(key, size)] = source;
        }
        return source;
    }

    // "path:..." for a folder with an icon of its own, "dir" for any other, "ext:..." for a file
    static string KeyFor(FileEntry entry)
    {
        if (entry.IsDirectory)
            return !entry.IsInArchive && HasOwnIcon(entry) ? "path:" + entry.FullPath : "dir";

        if (entry.Extension.Length > 0)
            return "ext:" + entry.Extension.ToLowerInvariant();

        return entry.UnixMode is { } mode && (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0
            ? "exec"
            : "data";
    }

    static bool HasOwnIcon(FileEntry entry)
    {
        if (SpecialFolders.Contains(entry.FullPath))
            return true;

        // a custom folder icon lives in a hidden "Icon\r" file
        if (File.Exists(Path.Combine(entry.FullPath, "Icon\r")))
            return true;

        // apps and other bundles; only a folder with an extension can be one
        return Path.GetExtension(entry.Name).Length > 1 && NSWorkspace.SharedWorkspace.IsFilePackage(entry.FullPath);
    }

    static NSImage IconFor(string key)
    {
        var workspace = NSWorkspace.SharedWorkspace;
        if (key.StartsWith("path:", StringComparison.Ordinal))
            return workspace.IconForFile(key[5..]);

        var type = key switch
        {
            "dir" => UTTypes.Folder,
            "exec" => UTTypes.UnixExecutable,
            "data" => UTTypes.Data,
            _ => UTType.CreateFromExtension(key[4..]) ?? UTTypes.Data
        };
        return workspace.GetIcon(type);
    }

    // drawn at the screen's scale into a PNG - the image views take a stream, not an NSImage
    static ImageSource? Render(NSImage icon, int size)
    {
        var scale = NSScreen.MainScreen?.BackingScaleFactor ?? 2;
        var pixels = (int)Math.Ceiling(size * scale);

        using var rep = new NSBitmapImageRep(IntPtr.Zero, pixels, pixels, 8, 4, true, false, NSColorSpace.DeviceRGB, 0, 0);
        using var context = NSGraphicsContext.FromBitmap(rep);
        if (context == null)
            return null;

        NSGraphicsContext.GlobalSaveGraphicsState();
        try
        {
            NSGraphicsContext.CurrentContext = context;
            icon.Draw(new CGRect(0, 0, pixels, pixels), CGRect.Empty, NSCompositingOperation.SourceOver, 1);
            context.FlushGraphics();
        }
        finally
        {
            NSGraphicsContext.GlobalRestoreGraphicsState();
        }

        using var data = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
        if (data == null)
            return null;

        var png = data.ToArray();
        return ImageSource.FromStream(() => new MemoryStream(png));
    }
}
