namespace ShinyCommander.Services;

/// <summary>
/// The operating system's own icon for a file - Finder's on macOS, Explorer's on Windows, the icon theme's on Linux.
/// Optional - a head registers one, and without it (or with Settings > Animated icons on) the motion icons show.
/// </summary>
public interface ISystemIcons
{
    /// <summary>The icon for <paramref name="size"/> points, or null when there's none to show. Called on the UI thread.</summary>
    ImageSource? Get(FileEntry entry, int size);
}

public static class SystemIcons
{
    static ISystemIcons? provider;
    static AppSettings? settings;
    static bool resolved;

    /// <summary>The system icon to show for a file, or null to show its motion icon.</summary>
    public static ImageSource? Get(FileEntry entry, int size)
    {
        if (!resolved && IPlatformApplication.Current?.Services is { } services)
        {
            provider = services.GetService<ISystemIcons>();
            settings = services.GetService<AppSettings>();
            resolved = true;
        }

        // the ".." row keeps its arrow - no system has an icon for "up"
        if (provider == null || entry.IsParentLink || settings?.MotionFileIcons == true)
            return null;

        try
        {
            return provider.Get(entry, size);
        }
        catch
        {
            // a file the system can't describe keeps its motion icon
            return null;
        }
    }
}
