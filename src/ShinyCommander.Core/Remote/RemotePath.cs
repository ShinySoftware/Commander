namespace ShinyCommander.Core.Remote;

/// <summary>
/// Remote panel paths are URIs - <c>sftp://me@host/var/www</c>, <c>s3://work/bucket/logs</c>,
/// <c>az://account/container/dir</c>, <c>davs://host/remote.php/dav</c>. The root is the scheme and authority
/// ("sftp://me@host"), which picks the connection; the rest is a "/"-rooted path on it. Separators are always "/",
/// whatever the platform, so none of System.IO.Path's helpers apply.
/// </summary>
public static class RemotePath
{
    const string SchemeMark = "://";

    /// <summary>A URI with a scheme of two letters or more - "C:\x" (one letter) and file:// stay local.</summary>
    public static bool IsRemote(string? path)
    {
        if (String.IsNullOrEmpty(path))
            return false;

        var mark = path.IndexOf(SchemeMark, StringComparison.Ordinal);
        return mark > 1
            && path[..mark].All(c => Char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')
            && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"sftp://me@host/a/b" -> ("sftp://me@host", "/a/b"); the root alone -> ("sftp://me@host", "/").</summary>
    public static (string Root, string Inner) Split(string path)
    {
        var mark = path.IndexOf(SchemeMark, StringComparison.Ordinal);
        if (mark <= 1)
            throw new ArgumentException($"'{path}' is not a remote path", nameof(path));

        var authorityStart = mark + SchemeMark.Length;
        var slash = path.IndexOf('/', authorityStart);
        var root = path[..mark].ToLowerInvariant() + SchemeMark + (slash < 0 ? path[authorityStart..] : path[authorityStart..slash]);
        var inner = slash < 0 ? "/" : NormalizeInner(path[slash..]);
        return (root, inner);
    }

    /// <summary>One spelling per location: lower-case scheme, no doubled or trailing slashes, "/" for the root.</summary>
    public static string Normalize(string path)
    {
        var (root, inner) = Split(path.Trim().Replace('\\', '/'));
        return root + inner;
    }

    static string NormalizeInner(string inner)
    {
        var parts = inner.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => x != ".").ToList();
        var stack = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(part);
            }
        }
        return "/" + String.Join('/', stack);
    }

    /// <summary>The folder above, or null at the connection's root.</summary>
    public static string? GetParent(string path)
    {
        var (root, inner) = Split(path);
        if (inner == "/")
            return null;

        var slash = inner.LastIndexOf('/');
        return root + (slash <= 0 ? "/" : inner[..slash]);
    }

    /// <summary>The last segment; the root's authority ("me@host") for the root itself.</summary>
    public static string GetName(string path)
    {
        var (root, inner) = Split(path);
        if (inner == "/")
            return root[(root.IndexOf(SchemeMark, StringComparison.Ordinal) + SchemeMark.Length)..];

        return inner[(inner.LastIndexOf('/') + 1)..];
    }

    public static string Combine(string dir, string relative)
    {
        var rel = relative.Replace('\\', '/').Trim('/');
        return rel.Length == 0 ? dir : dir.TrimEnd('/') + "/" + rel;
    }

    /// <summary>The inner path (no root) of <paramref name="path"/> joined with a relative one.</summary>
    public static string CombineInner(string inner, string relative)
    {
        var rel = relative.Replace('\\', '/').Trim('/');
        return rel.Length == 0 ? inner : (inner == "/" ? "/" : inner.TrimEnd('/') + "/") + rel;
    }

    /// <summary>Whether <paramref name="child"/> is <paramref name="parent"/> or somewhere below it.</summary>
    public static bool IsSameOrBelow(string parent, string child)
    {
        var p = Normalize(parent);
        var c = Normalize(child);
        return c.Equals(p, StringComparison.Ordinal) || c.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal);
    }
}
