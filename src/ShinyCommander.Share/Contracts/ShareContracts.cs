// Shared with the browser client (ShinyCommander.Web links this file) - plain records only, nothing server side.
namespace ShinyCommander.Share.Contracts;

/// <summary>What a share lets the person holding its link do.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ShareAccess>))]
public enum ShareAccess
{
    /// <summary>Browse and download.</summary>
    ReadOnly,

    /// <summary>A drop box: upload only - nothing in the folder is listed or downloadable.</summary>
    UploadOnly,

    /// <summary>Browse, download, upload and create folders.</summary>
    ReadWrite
}

/// <summary>A share as the browser sees it - <c>GET /api/s/{id}</c>.</summary>
/// <param name="Id">The share's id - part of its address.</param>
/// <param name="Name">The folder's name.</param>
/// <param name="Access">What is allowed.</param>
/// <param name="CanModify">Rename and delete are allowed too.</param>
/// <param name="NeedsKey">The share has a key and this browser hasn't given it yet - nothing else works until it does.</param>
/// <param name="ExpiresUtc">When the share stops by itself, if it does.</param>
/// <param name="MaxUploadBytes">The largest file accepted, or null for no limit.</param>
/// <param name="ResumableThreshold">Files from this size up go over tus (<c>/api/s/{id}/tus</c>); smaller ones are one PUT.</param>
/// <param name="SharedBy">The machine sharing it.</param>
public sealed record ShareInfo(
    string Id,
    string Name,
    ShareAccess Access,
    bool CanModify,
    bool NeedsKey,
    DateTimeOffset? ExpiresUtc,
    long? MaxUploadBytes,
    long ResumableThreshold,
    string SharedBy
)
{
    public bool CanList => this.Access != ShareAccess.UploadOnly;
    public bool CanUpload => this.Access != ShareAccess.ReadOnly;
}

/// <summary>One file or folder in a listing.</summary>
public sealed record ShareEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);

/// <summary><c>GET /api/s/{id}/list?path=</c> - the folder at a path relative to the share, '/' separated.</summary>
public sealed record ShareListing(string Path, IReadOnlyList<ShareEntry> Entries);

/// <summary><c>POST /api/s/{id}/key</c> - the key typed on the key page.</summary>
public sealed record ShareKeyRequest(string Key);

/// <summary><c>POST /api/s/{id}/folder</c> - a new folder called <paramref name="Name"/> inside <paramref name="Path"/>.</summary>
public sealed record ShareFolderRequest(string Path, string Name);

/// <summary><c>POST /api/s/{id}/rename</c> - the item at <paramref name="Path"/> renamed in place.</summary>
public sealed record ShareRenameRequest(string Path, string NewName);

/// <summary>Where an upload landed - another name when the one asked for was taken.</summary>
public sealed record ShareUploaded(string Path, string Name);

/// <summary>An error the page shows as is.</summary>
public sealed record ShareError(string Message);
