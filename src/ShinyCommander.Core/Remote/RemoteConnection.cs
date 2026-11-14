using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShinyCommander.Core.Remote;

[JsonConverter(typeof(JsonStringEnumConverter<RemoteProtocol>))]
public enum RemoteProtocol
{
    Sftp,
    Ftp,
    /// <summary>FTP over explicit TLS.</summary>
    Ftps,
    WebDav,
    /// <summary>WebDAV over https.</summary>
    WebDavs,
    S3,
    AzureBlob
}

/// <summary>
/// A saved server or storage account. What it takes to reach it lives here (and in settings); the password, key
/// passphrase or secret key does not - that goes to the OS keychain (<see cref="ISecretStore"/>) under <see cref="Id"/>.
/// </summary>
public sealed record RemoteConnection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public RemoteProtocol Protocol { get; init; }

    /// <summary>Server name or address; for Azure the storage account name.</summary>
    public string Host { get; init; } = String.Empty;

    /// <summary>0 for the protocol's usual port.</summary>
    public int Port { get; init; }

    /// <summary>User name; for S3 the access key id. Empty for anonymous FTP / public WebDAV.</summary>
    public string UserName { get; init; } = String.Empty;

    /// <summary>SFTP only: a private key file used instead of a password (the secret is then its passphrase).</summary>
    public string? KeyFile { get; init; }

    /// <summary>Where the connection opens: a folder on the server, or "/bucket/prefix" on S3 and Azure.</summary>
    public string StartPath { get; init; } = "/";

    /// <summary>S3 region ("us-east-1"); empty for the SDK's default.</summary>
    public string? Region { get; init; }

    /// <summary>
    /// S3: a compatible service's URL (MinIO, Cloudflare R2, Wasabi...) - path-style addressing is used then.
    /// Azure: the blob endpoint, for Azurite or a sovereign cloud. Empty for the public cloud.
    /// </summary>
    public string? Endpoint { get; init; }

    public bool IsObjectStorage => this.Protocol is RemoteProtocol.S3 or RemoteProtocol.AzureBlob;

    public string Scheme => this.Protocol switch
    {
        RemoteProtocol.Sftp => "sftp",
        RemoteProtocol.Ftp => "ftp",
        RemoteProtocol.Ftps => "ftps",
        RemoteProtocol.WebDav => "dav",
        RemoteProtocol.WebDavs => "davs",
        RemoteProtocol.S3 => "s3",
        _ => "az"
    };

    public int DefaultPort => this.Protocol switch
    {
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.Ftp or RemoteProtocol.Ftps => 21,
        RemoteProtocol.WebDav => 80,
        RemoteProtocol.WebDavs => 443,
        _ => 0
    };

    /// <summary>
    /// The panel path of the connection's root - <c>sftp://me@host</c>, <c>ftp://host:2121</c>. S3 has no host of its
    /// own (many accounts share one endpoint) so it is named by the connection: <c>s3://work</c>; Azure by the account.
    /// </summary>
    [JsonIgnore]
    public string Root
    {
        get
        {
            string authority;
            if (this.Protocol == RemoteProtocol.S3)
            {
                authority = Slug(this.Name);
            }
            else if (this.Protocol == RemoteProtocol.AzureBlob)
            {
                authority = this.Host.ToLowerInvariant();
            }
            else
            {
                var user = this.UserName.Length == 0 ? String.Empty : Uri.EscapeDataString(this.UserName) + "@";
                var port = this.Port == 0 || this.Port == this.DefaultPort ? String.Empty : ":" + this.Port;
                authority = user + this.Host.ToLowerInvariant() + port;
            }
            return this.Scheme + "://" + authority;
        }
    }

    /// <summary>Where a click on the connection goes: the root plus <see cref="StartPath"/>.</summary>
    [JsonIgnore]
    public string StartUri => RemotePath.Normalize(this.Root + "/" + this.StartPath.TrimStart('/'));

    public int EffectivePort => this.Port == 0 ? this.DefaultPort : this.Port;

    /// <summary>Lower case letters, digits and dashes - the rest of a name becomes a dash.</summary>
    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => Char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = String.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "storage" : slug;
    }

    public static IReadOnlyList<RemoteConnection> Deserialize(string? json)
    {
        if (String.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize(json, RemoteJsonContext.Default.ListRemoteConnection) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<RemoteConnection> connections)
        => JsonSerializer.Serialize(connections.ToList(), RemoteJsonContext.Default.ListRemoteConnection);
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<RemoteConnection>))]
partial class RemoteJsonContext : JsonSerializerContext;
