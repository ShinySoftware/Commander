using System.Security.Cryptography;
using ShinyCommander.Share.Contracts;

namespace ShinyCommander.Share;

/// <summary>What to share and how - the answers to the Serve Folder form.</summary>
public sealed record ServeOptions
{
    public required string RootPath { get; init; }
    public ShareAccess Access { get; init; } = ShareAccess.ReadOnly;

    /// <summary>Rename and delete as well (on top of <see cref="Access"/>; meaningless for read-only).</summary>
    public bool AllowModify { get; init; }

    /// <summary>The key a visitor must give, or null for none.</summary>
    public string? Key { get; init; }

    /// <summary>Dotfiles and files the platform marks hidden are listed and served.</summary>
    public bool ShowHidden { get; init; }

    /// <summary>The share stops by itself after this long.</summary>
    public TimeSpan? ExpiresAfter { get; init; }

    /// <summary>The share stops by itself once this many files have been downloaded.</summary>
    public int? MaxDownloads { get; init; }

    /// <summary>Bytes per second across every transfer of this share, both ways; 0 for no limit.</summary>
    public long BandwidthLimit { get; init; }

    /// <summary>Published over Bonjour/mDNS and listed at <c>/dav/</c>, so other machines find it without the link.</summary>
    public bool Advertise { get; init; }

    /// <summary>The largest file accepted, or null for no limit.</summary>
    public long? MaxUploadBytes { get; init; }
}

/// <summary>A folder being served - a share. Mutable counters are updated from request threads.</summary>
public sealed class ServedFolder
{
    internal ServedFolder(string id, ServeOptions options, DateTimeOffset now)
    {
        this.Id = id;
        this.Options = options;
        this.CreatedUtc = now;
        this.ExpiresUtc = options.ExpiresAfter is { } after ? now + after : null;
        this.Limiter = new BandwidthLimiter(options.BandwidthLimit);
    }

    public string Id { get; }
    public ServeOptions Options { get; private set; }
    public string RootPath => this.Options.RootPath;
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(this.RootPath)) is { Length: > 0 } n ? n : this.RootPath;
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset? ExpiresUtc { get; private set; }

    internal BandwidthLimiter Limiter { get; }

    long bytesDown, bytesUp;
    int filesDown, filesUp;

    public long BytesDown => Interlocked.Read(ref this.bytesDown);
    public long BytesUp => Interlocked.Read(ref this.bytesUp);
    public int FilesDown => Volatile.Read(ref this.filesDown);
    public int FilesUp => Volatile.Read(ref this.filesUp);

    public bool HasKey => !String.IsNullOrEmpty(this.Options.Key);
    public bool CanList => this.Options.Access != ShareAccess.UploadOnly;
    public bool CanUpload => this.Options.Access != ShareAccess.ReadOnly;
    public bool CanModify => this.Options.AllowModify && this.Options.Access == ShareAccess.ReadWrite;

    /// <summary>Why the share has stopped answering by itself, or null while it's live.</summary>
    public string? EndedReason(DateTimeOffset now)
    {
        if (this.ExpiresUtc is { } until && now >= until)
            return "expired";

        if (this.Options.MaxDownloads is { } max && this.FilesDown >= max)
            return "download limit reached";

        return null;
    }

    internal void AddDown(long bytes) => Interlocked.Add(ref this.bytesDown, bytes);
    internal void AddUp(long bytes) => Interlocked.Add(ref this.bytesUp, bytes);
    internal void FileDownloaded() => Interlocked.Increment(ref this.filesDown);
    internal void FileUploaded() => Interlocked.Increment(ref this.filesUp);

    /// <summary>Changes what the share allows. The address (id) stays the same; a changed expiry starts from now.</summary>
    internal void Update(ServeOptions options, DateTimeOffset now)
    {
        if (options.ExpiresAfter != this.Options.ExpiresAfter)
            this.ExpiresUtc = options.ExpiresAfter is { } after ? now + after : null;

        this.Options = options with { RootPath = this.RootPath };
        this.Limiter.BytesPerSecond = options.BandwidthLimit;
    }

    // ------------------------------------------------------------------------------------------- ids and keys

    const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";   // no l/1/o/0 - read aloud or typed from a phone

    /// <summary>
    /// A share id: 10 characters (50 bits), random, so the address itself is hard to guess - shares are never listed
    /// unless <see cref="ServeOptions.Advertise"/> says so.
    /// </summary>
    public static string NewId() => Random(10);

    /// <summary>A key that's easy to read out: three groups of four ("k7mp-2xqa-9fhd").</summary>
    public static string NewKey() => $"{Random(4)}-{Random(4)}-{Random(4)}";

    static string Random(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}
