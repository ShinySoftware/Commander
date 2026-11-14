namespace ShinyCommander.Remote;

static class RemoteUtil
{
    /// <summary>A scratch file that deletes itself on close - downloads that must not hold a connection open.</summary>
    public static FileStream CreateTempStream() => new(
        Path.Combine(Path.GetTempPath(), "shinycommander-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        81920,
        FileOptions.DeleteOnClose | FileOptions.Asynchronous
    );

    /// <summary>"/a/b/c" -> "c".</summary>
    public static string NameOf(string inner) => inner[(inner.TrimEnd('/').LastIndexOf('/') + 1)..].TrimEnd('/');

    /// <summary>"/a/b/c" -> "/a/b"; "/a" -> "/".</summary>
    public static string ParentOf(string inner)
    {
        var slash = inner.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : inner[..slash];
    }
}
