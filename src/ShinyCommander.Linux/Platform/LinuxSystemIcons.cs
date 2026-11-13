using Shiny;
using ShinyCommander.Services;

namespace ShinyCommander;

/// <summary>
/// The icon theme's icons, found the way GIO-based file managers find them: a file's content type (guessed from its
/// name, so archive entries get one too) names its icon, a folder asks GIO for its own (Documents, Music... have
/// theirs), and a .desktop launcher shows the app's icon.
/// </summary>
[Singleton]
sealed class LinuxSystemIcons : ISystemIcons
{
    readonly Dictionary<(string Key, int Size), ImageSource?> byKey = [];
    readonly Dictionary<(string File, int Size), ImageSource> byFile = [];

    public ImageSource? Get(FileEntry entry, int size)
    {
        // a folder's or launcher's icon depends on the item itself, so isn't cached by its key
        if (entry.IsDirectory && !entry.IsInArchive)
            return this.Load(QueryIcon(entry.FullPath), size);

        if (!entry.IsInArchive && entry.Extension.Equals("desktop", StringComparison.OrdinalIgnoreCase))
            return this.Load(LauncherIcon(entry.FullPath), size);

        var type = entry.IsDirectory ? "inode/directory" : Gio.Functions.ContentTypeGuess(entry.Name, null, out _);
        if (!this.byKey.TryGetValue((type, size), out var source))
        {
            source = this.Load(Gio.Functions.ContentTypeGetIcon(type), size);
            this.byKey[(type, size)] = source;
        }
        return source;
    }

    static Gio.Icon? QueryIcon(string path)
    {
        var info = Gio.FileHelper.NewForPath(path).QueryInfo("standard::icon", Gio.FileQueryInfoFlags.None, null);
        return info.GetIcon();
    }

    // the launcher's Icon= key: a theme icon name, or a path to an image
    static Gio.Icon? LauncherIcon(string path)
    {
        var name = File.ReadLines(path)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("Icon=", StringComparison.Ordinal))?[5..];

        if (String.IsNullOrWhiteSpace(name))
            return null;

        return Path.IsPathRooted(name)
            ? Gio.FileIcon.New(Gio.FileHelper.NewForPath(name))
            : Gio.ThemedIcon.New(name);
    }

    ImageSource? Load(Gio.Icon? icon, int size)
    {
        if (icon == null || Gdk.Display.GetDefault() is not { } display)
            return null;

        var theme = Gtk.IconTheme.GetForDisplay(display);
        var paintable = theme.LookupByGicon(icon, size, 2, Gtk.TextDirection.None, Gtk.IconLookupFlags.None);
        // icons compiled into a resource have no file - the motion icon stands in
        if (paintable.GetFile()?.GetPath() is not { } file)
            return null;

        if (!this.byFile.TryGetValue((file, size), out var source))
        {
            source = ImageSource.FromFile(file);
            this.byFile[(file, size)] = source;
        }
        return source;
    }
}
