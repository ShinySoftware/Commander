namespace ShinyCommander.Remote;

/// <summary>
/// An inner path on object storage: "/" is the account (its buckets / containers), "/bucket" one bucket, and
/// "/bucket/a/b" the key "a/b" - a folder is every key under "a/b/".
/// </summary>
readonly record struct ObjectPath(string Bucket, string Key)
{
    public bool IsAccount => this.Bucket.Length == 0;
    public bool IsBucket => this.Bucket.Length > 0 && this.Key.Length == 0;

    /// <summary>The key as a folder prefix: "a/b/", or "" for the bucket itself.</summary>
    public string Prefix => this.Key.Length == 0 ? String.Empty : this.Key + "/";

    public static ObjectPath Parse(string inner)
    {
        var trimmed = inner.Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? new ObjectPath(trimmed, String.Empty) : new ObjectPath(trimmed[..slash], trimmed[(slash + 1)..]);
    }
}
