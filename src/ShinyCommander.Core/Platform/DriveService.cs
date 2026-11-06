using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Platform;

/// <summary>
/// The drive button bar. Windows lists drive letters; macOS lists "/" + /Volumes; Linux lists "/" + the
/// usual removable/user mount points. Home is always offered first on unix since there are no letters.
/// </summary>
public static class DriveService
{
    public static IReadOnlyList<DriveEntry> GetDrives()
    {
        var list = new List<DriveEntry>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!OperatingSystem.IsWindows())
            list.Add(FromPath("~", home, "Home"));

        foreach (var d in SafeDrives())
        {
            try
            {
                if (!d.IsReady)
                    continue;

                var root = d.RootDirectory.FullName;
                if (!OperatingSystem.IsWindows() && !IsInterestingUnixMount(root, d))
                    continue;

                var name = OperatingSystem.IsWindows()
                    ? root.TrimEnd('\\')
                    : root == "/" ? "/" : Path.GetFileName(root.TrimEnd('/'));

                list.Add(new DriveEntry(name, root, SafeLabel(d), d.TotalSize, d.AvailableFreeSpace, d.DriveType));
            }
            catch
            {
                // not ready / permission denied
            }
        }
        return list;
    }

    static IEnumerable<DriveInfo> SafeDrives()
    {
        try { return DriveInfo.GetDrives(); } catch { return []; }
    }

    static string SafeLabel(DriveInfo d)
    {
        try { return String.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : d.VolumeLabel; } catch { return d.Name; }
    }

    static bool IsInterestingUnixMount(string root, DriveInfo d)
    {
        if (root == "/")
            return true;

        if (d.DriveType is DriveType.Ram or DriveType.Unknown && !root.StartsWith("/Volumes/"))
            return false;

        if (OperatingSystem.IsMacOS())
            return root.StartsWith("/Volumes/", StringComparison.Ordinal) && !root.Contains("/.timemachine", StringComparison.Ordinal);

        return root.StartsWith("/media/", StringComparison.Ordinal)
            || root.StartsWith("/mnt/", StringComparison.Ordinal)
            || root.StartsWith("/run/media/", StringComparison.Ordinal)
            || root == "/home";
    }

    public static DriveEntry FromPath(string name, string path, string label)
    {
        try
        {
            var d = new DriveInfo(path);
            return new DriveEntry(name, path, label, d.TotalSize, d.AvailableFreeSpace, d.DriveType);
        }
        catch
        {
            return new DriveEntry(name, path, label, 0, 0, DriveType.Unknown);
        }
    }

    /// <summary>Finds the drive that holds the given path (longest matching root).</summary>
    public static DriveEntry? FindForPath(IReadOnlyList<DriveEntry> drives, string path)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return drives
            .Where(x => x.Name != "~" && path.StartsWith(x.RootPath, comparison))
            .OrderByDescending(x => x.RootPath.Length)
            .FirstOrDefault();
    }
}
