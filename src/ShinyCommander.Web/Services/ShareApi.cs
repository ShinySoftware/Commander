using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShinyCommander.Share.Contracts;

namespace ShinyCommander.Web.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ShareInfo))]
[JsonSerializable(typeof(ShareListing))]
[JsonSerializable(typeof(ShareKeyRequest))]
[JsonSerializable(typeof(ShareFolderRequest))]
[JsonSerializable(typeof(ShareRenameRequest))]
[JsonSerializable(typeof(ShareUploaded))]
[JsonSerializable(typeof(ShareError))]
partial class WebJson : JsonSerializerContext;

/// <summary>The share server's answer when it says no - its message is meant to be shown as is.</summary>
public sealed class ShareApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status => status;
}

/// <summary>The share's browser API (<c>/api/s/{id}</c>) - downloads go straight to the WebDAV address instead.</summary>
public sealed class ShareApi(HttpClient http)
{
    public static string Encode(string path) => String.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    public static string Join(string? folder, string name) => String.IsNullOrEmpty(folder) ? name : $"{folder.TrimEnd('/')}/{name}";

    /// <summary>A file's address - also what a download link points at.</summary>
    public static string FileUrl(string shareId, string path, bool download = false)
        => $"dav/{shareId}/{Encode(path)}";

    public static string ZipUrl(string shareId, string path) => $"api/s/{shareId}/zip?path={Uri.EscapeDataString(path)}";

    public async Task<ShareInfo> Info(string id, string? key, CancellationToken ct = default)
    {
        var url = $"api/s/{Uri.EscapeDataString(id)}" + (String.IsNullOrEmpty(key) ? "" : $"?key={Uri.EscapeDataString(key)}");
        using var response = await http.GetAsync(url, ct);
        await Check(response, ct);
        return (await response.Content.ReadFromJsonAsync(WebJson.Default.ShareInfo, ct))!;
    }

    public async Task Key(string id, string key, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/s/{Uri.EscapeDataString(id)}/key", new ShareKeyRequest(key), WebJson.Default.ShareKeyRequest, ct);
        await Check(response, ct);
    }

    public async Task<ShareListing> List(string id, string path, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/s/{Uri.EscapeDataString(id)}/list?path={Uri.EscapeDataString(path)}", ct);
        await Check(response, ct);
        return (await response.Content.ReadFromJsonAsync(WebJson.Default.ShareListing, ct))!;
    }

    public async Task CreateFolder(string id, string path, string name, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/s/{Uri.EscapeDataString(id)}/folder", new ShareFolderRequest(path, name), WebJson.Default.ShareFolderRequest, ct);
        await Check(response, ct);
    }

    public async Task Rename(string id, string path, string newName, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/s/{Uri.EscapeDataString(id)}/rename", new ShareRenameRequest(path, newName), WebJson.Default.ShareRenameRequest, ct);
        await Check(response, ct);
    }

    public async Task Delete(string id, string path, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/s/{Uri.EscapeDataString(id)}/item?path={Uri.EscapeDataString(path)}", ct);
        await Check(response, ct);
    }

    static async Task Check(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = await Message(response, ct);
        throw new ShareApiException(response.StatusCode, message);
    }

    static async Task<string> Message(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync(WebJson.Default.ShareError, ct);
            if (!String.IsNullOrWhiteSpace(error?.Message))
                return error.Message;
        }
        catch (JsonException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => "This share has ended, or the link is wrong.",
            HttpStatusCode.Forbidden => "This share doesn't allow that.",
            _ => $"Something went wrong ({(int)response.StatusCode})."
        };
    }
}
