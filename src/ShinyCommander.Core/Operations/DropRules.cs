using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Remote;

namespace ShinyCommander.Core.Operations;

/// <summary>What a drag offers, and what a drop does.</summary>
[Flags]
public enum DragOperations
{
    None = 0,
    Copy = 1,
    Move = 2
}

/// <summary>
/// How a drop of files onto a folder behaves - Finder's rules: a move within a volume, a copy onto another one; a
/// source that only offers one of them (or a modifier key that narrowed it down: ⌥ copy, ⌘ move) gets that one.
/// </summary>
public static class DropRules
{
    /// <summary>
    /// The operation for dropping <paramref name="sources"/> into <paramref name="targetDir"/>, or None when it can't go
    /// there. <paramref name="targetOnDisk"/> is false for a folder inside an archive or on a server - another volume.
    /// </summary>
    public static DragOperations Choose(IReadOnlyList<string> sources, string targetDir, DragOperations allowed, bool targetOnDisk = true)
    {
        if (sources.Count == 0 || allowed == DragOperations.None || !CanDropInto(sources, targetDir))
            return DragOperations.None;

        if (allowed != (DragOperations.Copy | DragOperations.Move))
            return allowed;

        return targetOnDisk && sources.All(x => SameVolume(x, targetDir)) ? DragOperations.Move : DragOperations.Copy;
    }

    /// <summary>
    /// False when the drop would do nothing or can't be done: every item is already in that folder, or a folder would
    /// go inside itself.
    /// </summary>
    public static bool CanDropInto(IReadOnlyList<string> sources, string targetDir)
    {
        var target = Normalize(targetDir);
        var alreadyThere = sources.All(x => PathEquals(ParentOf(Normalize(x)), target));
        var intoItself = sources.Any(x => IsSameOrBelow(Normalize(x), target));
        return !alreadyThere && !intoItself;
    }

    /// <summary>Both on the same local volume - the one whose mount point is the longest prefix of the path.</summary>
    public static bool SameVolume(string a, string b)
    {
        if (RemotePath.IsRemote(a) || RemotePath.IsRemote(b))
            return false;

        if (OperatingSystem.IsWindows())
            return String.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

        var roots = MountPoints();
        return PathEquals(VolumeOf(a, roots), VolumeOf(b, roots));
    }

    /// <summary>The mount point a path lives on, from <paramref name="mountPoints"/> ("/" when none is longer).</summary>
    public static string VolumeOf(string path, IEnumerable<string> mountPoints)
    {
        var p = Normalize(path) + Path.DirectorySeparatorChar;
        return mountPoints
            .Select(Normalize)
            .Where(x => p.StartsWith(x.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison))
            .OrderByDescending(x => x.Length)
            .FirstOrDefault() ?? "/";
    }

    static IReadOnlyList<string> MountPoints()
    {
        try
        {
            return DriveInfo.GetDrives().Select(x => x.RootDirectory.FullName).ToList();
        }
        catch
        {
            return ["/"];
        }
    }

    static bool IsSameOrBelow(string parent, string child)
        => PathEquals(parent, child) || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);

    static bool PathEquals(string a, string b) => String.Equals(a, b, Comparison);

    // "" for a root, which has none - never Normalize("") (that's the home folder)
    static string ParentOf(string path)
    {
        if (RemotePath.IsRemote(path))
        {
            var slash = path.LastIndexOf('/');
            return slash > 0 ? path[..slash] : String.Empty;
        }
        var parent = Path.GetDirectoryName(path);
        return String.IsNullOrEmpty(parent) ? String.Empty : Normalize(parent);
    }

    static string Normalize(string path) => VirtualFileSystem.Normalize(path);

    static StringComparison Comparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
