using System.Diagnostics;
using System.Text.Json;
using Microsoft.JSInterop;
using ShinyCommander.Share.Contracts;

namespace ShinyCommander.Web.Services;

public enum UploadState
{
    Waiting,
    Sending,
    Done,
    Failed,
    Cancelled
}

/// <summary>One file on its way up - what the uploads panel shows.</summary>
public sealed class UploadItem
{
    internal UploadItem(string claim, int index, string name, long size, string folder)
    {
        this.Claim = claim;
        this.Index = index;
        this.Name = name;
        this.Size = size;
        this.Folder = folder;
    }

    internal string Claim { get; }
    internal int Index { get; }
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal CancellationTokenSource Cancel { get; } = new();

    public string Name { get; }
    public long Size { get; }

    /// <summary>The share folder it goes into ('/' separated).</summary>
    public string Folder { get; }
    public long Sent { get; internal set; }
    public UploadState State { get; internal set; }
    public string? Status { get; internal set; }
    public string? SavedAs { get; internal set; }
    public double BytesPerSecond { get; internal set; }

    public double Fraction => this.Size > 0 ? Math.Clamp((double)this.Sent / this.Size, 0, 1) : (this.State == UploadState.Done ? 1 : 0);
    public bool IsFinished => this.State is UploadState.Done or UploadState.Failed or UploadState.Cancelled;

    public TimeSpan? Remaining => this.State == UploadState.Sending && this.BytesPerSecond > 1
        ? TimeSpan.FromSeconds(Math.Max(0, this.Size - this.Sent) / this.BytesPerSecond)
        : null;

    long sampleBytes;
    long sampleTicks = Stopwatch.GetTimestamp();

    internal void Report(long sent)
    {
        this.Sent = sent;
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(this.sampleTicks, now).TotalSeconds;
        if (seconds < 0.4)
            return;

        var instant = (sent - this.sampleBytes) / seconds;
        this.BytesPerSecond = this.BytesPerSecond == 0 ? instant : this.BytesPerSecond * 0.6 + instant * 0.4;
        this.sampleBytes = sent;
        this.sampleTicks = now;
    }
}

/// <summary>
/// The upload queue: two files at a time, small ones in one PUT and big ones resumably (tus). Folders that were dropped
/// are created first, so a dropped tree arrives as a tree.
/// </summary>
public sealed class Uploads(IJSRuntime js, ShareApi api) : IAsyncDisposable
{
    const int Parallel = 2;
    const long ChunkSize = 8L * 1024 * 1024;

    readonly List<UploadItem> items = [];
    IJSObjectReference? module;
    ShareInfo? share;
    int sending;

    public IReadOnlyList<UploadItem> Items => this.items;
    public bool HasItems => this.items.Count > 0;
    public int Pending => this.items.Count(x => !x.IsFinished);

    /// <summary>Something changed - the page re-renders.</summary>
    public event Action? Changed;

    /// <summary>A file landed in this folder - the page refreshes if it's showing it.</summary>
    public event Action<string>? Landed;

    public async Task<IJSObjectReference> Module() => this.module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/share.js");

    public void Use(ShareInfo info) => this.share = info;

    /// <summary>Queues a claim's files (from the picker or a drop) for <paramref name="folder"/>.</summary>
    public async Task Add(JsonElement claim, string folder)
    {
        var key = claim.GetProperty("key").GetString()!;
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in claim.GetProperty("files").EnumerateArray())
        {
            var dir = file.GetProperty("dir").GetString() ?? "";
            var target = String.IsNullOrEmpty(dir) ? folder : ShareApi.Join(folder, dir);
            // a dropped tree: each folder made once, parents first (one that's already there is fine)
            if (dir.Length > 0)
                await this.EnsureFolders(folder, dir, created);

            this.items.Add(new UploadItem(key, file.GetProperty("index").GetInt32(), file.GetProperty("name").GetString()!, file.GetProperty("size").GetInt64(), target));
        }
        this.Changed?.Invoke();
        this.Pump();
    }

    async Task EnsureFolders(string root, string dir, HashSet<string> created)
    {
        var walked = "";
        foreach (var part in dir.Split('/'))
        {
            var parent = ShareApi.Join(root, walked);
            walked = ShareApi.Join(walked, part);
            if (created.Add(walked))
            {
                try
                {
                    await api.CreateFolder(this.share!.Id, parent, part);
                }
                catch (ShareApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
                {
                }
            }
        }
    }

    public async Task CancelItem(UploadItem item)
    {
        if (item.IsFinished)
            return;

        if (item.State == UploadState.Waiting)
        {
            item.State = UploadState.Cancelled;
            this.Changed?.Invoke();
            return;
        }
        await (await this.Module()).InvokeVoidAsync("abort", item.Id);
    }

    public void ClearFinished()
    {
        this.items.RemoveAll(x => x.IsFinished);
        this.Changed?.Invoke();
    }

    void Pump()
    {
        while (this.sending < Parallel && this.items.FirstOrDefault(x => x.State == UploadState.Waiting) is { } next)
        {
            next.State = UploadState.Sending;
            this.sending++;
            _ = this.Send(next);
        }
    }

    async Task Send(UploadItem item)
    {
        try
        {
            var module = await this.Module();
            using var callback = DotNetObjectReference.Create(new Callback(this, item));
            var share = this.share!;
            JsonElement result;
            if (item.Size >= share.ResumableThreshold)
            {
                var metadata = new Dictionary<string, string> { ["filename"] = item.Name, ["path"] = item.Folder };
                result = await module.InvokeAsync<JsonElement>("tus", item.Id, item.Claim, item.Index, $"api/s/{share.Id}/tus", metadata, ChunkSize, callback);
            }
            else
            {
                var url = $"api/s/{share.Id}/upload?path={Uri.EscapeDataString(item.Folder)}&name={Uri.EscapeDataString(item.Name)}";
                result = await module.InvokeAsync<JsonElement>("put", item.Id, item.Claim, item.Index, url, callback);
            }

            var status = result.GetProperty("status").GetInt32();
            var body = result.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            if (status is >= 200 and < 300)
            {
                item.State = UploadState.Done;
                item.Sent = item.Size;
                item.Status = null;
                item.SavedAs = SavedName(result, body);
                this.Landed?.Invoke(item.Folder);
            }
            else if (status == -1)
            {
                item.State = UploadState.Cancelled;
                item.Status = null;
            }
            else
            {
                item.State = UploadState.Failed;
                item.Status = status == 0 && body.Length == 0 ? "The connection dropped." : Message(body, status);
            }
        }
        catch (JSException ex)
        {
            item.State = UploadState.Failed;
            item.Status = ex.Message;
        }
        finally
        {
            this.sending--;
            this.Changed?.Invoke();
            this.Pump();
        }
    }

    static string? SavedName(JsonElement result, string body)
    {
        if (result.TryGetProperty("savedAs", out var saved) && saved.ValueKind == JsonValueKind.String)
            return saved.GetString()!.Split('/').Last();

        if (body.Length == 0)
            return null;

        try
        {
            return JsonSerializer.Deserialize(body, WebJson.Default.ShareUploaded)?.Name;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Message(string body, int status)
    {
        if (body.StartsWith('{'))
        {
            try
            {
                if (JsonSerializer.Deserialize(body, WebJson.Default.ShareError)?.Message is { Length: > 0 } m)
                    return m;
            }
            catch (JsonException)
            {
            }
        }
        return body.Length > 0 && body.Length < 200 ? body : $"The upload failed ({status}).";
    }

    public async ValueTask DisposeAsync()
    {
        if (this.module != null)
        {
            try
            {
                await this.module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    sealed class Callback(Uploads owner, UploadItem item)
    {
        [JSInvokable]
        public void OnProgress(long loaded, long total)
        {
            item.Report(loaded);
            owner.Changed?.Invoke();
        }

        [JSInvokable]
        public void OnStatus(string? text)
        {
            item.Status = text;
            owner.Changed?.Invoke();
        }
    }
}
