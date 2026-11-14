namespace ShinyCommander.Core.Util;

/// <summary>One step of a breadcrumb trail: what to show, and the path a click on it goes to.</summary>
public sealed record PathCrumb(string Name, string Path, bool IsCurrent);

/// <summary>A folder in a breadcrumb's dropdown; <paramref name="IsOnPath"/> when it's the way to the folder you're in.</summary>
public sealed record PathCrumbFolder(string Name, string Path, bool IsOnPath);

public static class PathCrumbs
{
    static readonly char[] Separators = [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar];

    /// <summary>
    /// "/Users/me/docs" -> "/", "Users", "me", "docs"; "C:\stuff" -> "C:", "stuff". Each crumb's path is a prefix of
    /// the original text, so archive paths (<c>/x/stuff.zip/docs</c>) and the user's own spelling come through as-is.
    /// </summary>
    public static IReadOnlyList<PathCrumb> Split(string path)
    {
        if (String.IsNullOrEmpty(path))
            return [];

        var parts = new List<(string Name, string Path)>();
        var root = System.IO.Path.GetPathRoot(path) ?? String.Empty;
        if (root.Length > 0)
        {
            var rootName = root.TrimEnd(Separators);
            parts.Add((rootName.Length == 0 ? root : rootName, root));
        }

        var start = root.Length;
        while (start < path.Length)
        {
            var end = path.IndexOfAny(Separators, start);
            if (end < 0)
                end = path.Length;
            if (end > start)
                parts.Add((path[start..end], path[..end]));
            start = end + 1;
        }

        return parts.Select((x, i) => new PathCrumb(x.Name, x.Path, i == parts.Count - 1)).ToList();
    }
}
