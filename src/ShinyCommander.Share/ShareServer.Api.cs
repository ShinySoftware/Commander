using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Tus;
using Shiny.Net.HttpServer.WebDav;
using ShinyCommander.Share.Contracts;

namespace ShinyCommander.Share;

public sealed partial class ShareServer
{
    // ------------------------------------------------------------------------------------------- the guard

    /// <summary>
    /// In front of everything: blocked addresses, then - for a share's API and WebDAV paths - whether the share exists,
    /// whether it's reachable this way (no keyless share through the tunnel) and whether the visitor has its key.
    /// </summary>
    async ValueTask GuardAsync(HttpContext ctx, RequestDelegate next)
    {
        var ip = ShareGate.ClientIp(ctx);
        var tunneled = ctx.Connection.IsTunneled;
        this.Monitor.Seen(ip, tunneled);

        if (this.gate.IsBlocked(ip))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        var (id, isDav) = ShareIdOf(ctx.Request.Path);
        if (id == null)
        {
            await next(ctx).ConfigureAwait(false);
            return;
        }

        var mounted = this.Find(id);
        var now = this.time.GetUtcNow();
        if (mounted == null || mounted.Share.EndedReason(now) != null)
        {
            await Error(ctx, 404, "This share has ended, or the link is wrong.").ConfigureAwait(false);
            return;
        }

        var share = mounted.Share;
        if (tunneled && !share.HasKey)
        {
            this.Monitor.Log(ActivityKind.Refused, ip, share, null, "no key - not reachable through the public address", true);
            await Error(ctx, 403, "This share is only available on the local network.").ConfigureAwait(false);
            return;
        }

        if (this.gate.IsLockedOut(ip, now))
        {
            await Error(ctx, 429, "Too many wrong keys. Try again in a few minutes.").ConfigureAwait(false);
            return;
        }

        var hasAccess = this.gate.HasAccess(ctx, share);

        // a key in the link signs the browser in
        var queryKey = ctx.Request.Query.GetFirst("key");
        if (!hasAccess && share.HasKey && queryKey != null)
        {
            hasAccess = this.gate.TryKey(ip, share, queryKey, now);
            this.Monitor.Log(hasAccess ? ActivityKind.KeyAccepted : ActivityKind.KeyRefused, ip, share, null, null, tunneled);
            if (hasAccess)
                this.gate.GrantCookie(ctx, share);
        }

        // the share's description and the key page answer without the key - they're how a visitor gets one in
        var open = !isDav && ctx.Request.Path.TrimEnd('/') == $"/api/s/{id}" || ctx.Request.Path == $"/api/s/{id}/key";
        if (!hasAccess && !open)
        {
            if (isDav)
            {
                // Finder and Explorer ask for a password when they see this; any user name with the key as the password
                ctx.Response.Headers.Set("WWW-Authenticate", $"Basic realm=\"{share.Name.Replace("\"", "")}\", charset=\"UTF-8\"");
                ctx.Response.StatusCode = 401;
                return;
            }
            await Error(ctx, 401, "This share needs its key.").ConfigureAwait(false);
            return;
        }

        this.currentClient.Value = new ClientContext(ip, tunneled, ctx.Request.ContentLength);
        ctx.Items["share"] = mounted;
        ctx.Items["access"] = hasAccess;
        await next(ctx).ConfigureAwait(false);
    }

    /// <summary>The share id in "/api/s/{id}/..." or "/dav/{id}/...".</summary>
    static (string? Id, bool IsDav) ShareIdOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0] == "api" && parts[1] == "s")
            return (parts[2], false);

        if (parts.Length >= 2 && parts[0] == "dav")
            return (parts[1], true);

        return (null, false);
    }

    // ------------------------------------------------------------------------------------------- the browser API

    void MapApi(HttpServer app)
    {
        app.MapRoute("GET", "/api/s/{id}", this.InfoAsync);
        app.MapRoute("POST", "/api/s/{id}/key", this.KeyAsync);
        app.MapRoute("GET", "/api/s/{id}/list", this.ListAsync);
        app.MapRoute("GET", "/api/s/{id}/zip", this.ZipAsync);
        app.MapRoute("PUT", "/api/s/{id}/upload", this.UploadAsync);
        app.MapRoute("POST", "/api/s/{id}/folder", this.CreateFolderAsync);
        app.MapRoute("POST", "/api/s/{id}/rename", this.RenameAsync);
        app.MapRoute("DELETE", "/api/s/{id}/item", this.DeleteAsync);
        // the page for a share without the web app built in (a -p:SkipFrontendBuild=true build): WebDAV's own file manager
        if (!this.HasWebApp)
            app.MapRoute("GET", "/s/{id}", this.FallbackPageAsync);
    }

    static Mounted Share(HttpContext ctx) => (Mounted)ctx.Items["share"]!;

    ValueTask InfoAsync(HttpContext ctx)
    {
        var mounted = Share(ctx);
        var share = mounted.Share;
        var access = (bool)ctx.Items["access"]!;
        if (access)
            mounted.Files.Log(ActivityKind.Visit, "");

        var info = new ShareInfo(
            share.Id,
            share.Name,
            share.Options.Access,
            share.CanModify,
            !access,
            share.ExpiresUtc,
            share.Options.MaxUploadBytes,
            ResumableThreshold,
            Environment.MachineName
        );
        return Json(ctx, info, ShareJson.Default.ShareInfo);
    }

    async ValueTask KeyAsync(HttpContext ctx)
    {
        var mounted = Share(ctx);
        var ip = ShareGate.ClientIp(ctx);
        var body = await ctx.Request.ReadJsonAsync(ShareJson.Default.ShareKeyRequest, ctx.RequestAborted).ConfigureAwait(false);
        if (!mounted.Share.HasKey)
        {
            ctx.Response.StatusCode = 204;
            return;
        }

        var ok = this.gate.TryKey(ip, mounted.Share, body?.Key, this.time.GetUtcNow());
        this.Monitor.Log(ok ? ActivityKind.KeyAccepted : ActivityKind.KeyRefused, ip, mounted.Share, null, null, ctx.Connection.IsTunneled);
        if (!ok)
        {
            await Error(ctx, 401, "That isn't the key.").ConfigureAwait(false);
            return;
        }

        this.gate.GrantCookie(ctx, mounted.Share);
        ctx.Response.StatusCode = 204;
    }

    async ValueTask ListAsync(HttpContext ctx)
    {
        var mounted = Share(ctx);
        if (!mounted.Share.CanList)
        {
            await Error(ctx, 403, "This is a drop box - you can add files, not see them.").ConfigureAwait(false);
            return;
        }

        var path = ctx.Request.Query.GetFirst("path") ?? "";
        var full = mounted.Files.Resolve(path);
        if (full == null || !Directory.Exists(full))
        {
            await Error(ctx, 404, "That folder isn't there.").ConfigureAwait(false);
            return;
        }

        var entries = mounted.Files.List(full)
            .Select(x => new ShareEntry(x.Name, x is DirectoryInfo, x is FileInfo f ? f.Length : 0, x.LastWriteTimeUtc))
            .ToList();
        await Json(ctx, new ShareListing(mounted.Files.Relative(full), entries), ShareJson.Default.ShareListing).ConfigureAwait(false);
    }

    async ValueTask UploadAsync(HttpContext ctx)
    {
        var mounted = Share(ctx);
        var share = mounted.Share;
        if (!share.CanUpload)
        {
            await Error(ctx, 403, "This share doesn't take uploads.").ConfigureAwait(false);
            return;
        }

        var folder = mounted.Files.Resolve(ctx.Request.Query.GetFirst("path"));
        var name = ctx.Request.Query.GetFirst("name");
        var parts = ShareFileSystem.Split(name);
        if (folder == null || !Directory.Exists(folder) || parts is not { Length: 1 })
        {
            await Error(ctx, 400, "That isn't somewhere a file can go.").ConfigureAwait(false);
            return;
        }

        // the upload route takes any size - the share's own limit is checked against Content-Length
        if (!ctx.Request.IsMaxBodySizeReadOnly)
            ctx.Request.MaxBodySize = share.Options.MaxUploadBytes;

        var overwrite = ctx.Request.Query.GetFirst("overwrite") == "true" && share.CanModify;
        var target = Path.Combine(folder, parts[0]);
        if (!overwrite && (File.Exists(target) || Directory.Exists(target)))
            target = ShareFileSystem.FreeName(target);

        try
        {
            await mounted.Files.SaveUploadAsync(target, ctx.Request.Body, ctx.Request.ContentLength, overwrite, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (WebDavException ex) when (ex.StatusCode == 413)
        {
            await Error(ctx, 413, "That file is larger than this share accepts.").ConfigureAwait(false);
            return;
        }

        await Json(ctx, new ShareUploaded(mounted.Files.Relative(target), Path.GetFileName(target)), ShareJson.Default.ShareUploaded).ConfigureAwait(false);
    }

    async ValueTask CreateFolderAsync(HttpContext ctx)
    {
        var mounted = Share(ctx);
        var body = await ctx.Request.ReadJsonAsync(ShareJson.Default.ShareFolderRequest, ctx.RequestAborted).ConfigureAwait(false);
        var parent = mounted.Files.Resolve(body?.Path);
        var parts = ShareFileSystem.Split(body?.Name);
        if (body == null || parent == null || parts is not { Length: 1 } || !Directory.Exists(parent))
        {
            await Error(ctx, 400, "That isn't a folder name that can be used here.").ConfigureAwait(false);
            return;
        }

        var target = Path.Combine(parent, parts[0]);
        if (Directory.Exists(target) || File.Exists(target))
        {
            await Error(ctx, 409, "Something with that name is already there.").ConfigureAwait(false);
            return;
        }

        await this.Dav(ctx, mounted, fs => fs.CreateDirectoryAsync(mounted.Files.Relative(target), ctx.RequestAborted)).ConfigureAwait(false);
    }

    async ValueTask RenameAsync(HttpContext ctx)
    {
        var mounted = Share(ctx);
        var body = await ctx.Request.ReadJsonAsync(ShareJson.Default.ShareRenameRequest, ctx.RequestAborted).ConfigureAwait(false);
        var source = mounted.Files.Resolve(body?.Path);
        var parts = ShareFileSystem.Split(body?.NewName);
        if (body == null || source == null || parts is not { Length: 1 } || String.IsNullOrEmpty(body.Path))
        {
            await Error(ctx, 400, "That isn't a name that can be used here.").ConfigureAwait(false);
            return;
        }

        var target = Path.Combine(Path.GetDirectoryName(source)!, parts[0]);
        if (Directory.Exists(target) || File.Exists(target))
        {
            await Error(ctx, 409, "Something with that name is already there.").ConfigureAwait(false);
            return;
        }

        await this.Dav(ctx, mounted, fs => fs.MoveAsync(mounted.Files.Relative(source), mounted.Files.Relative(target), ctx.RequestAborted)).ConfigureAwait(false);
    }

    async ValueTask DeleteAsync(HttpContext ctx)
    {
        var mounted = Share(ctx);
        var path = ctx.Request.Query.GetFirst("path");
        var full = mounted.Files.Resolve(path);
        if (full == null || String.IsNullOrEmpty(path) || (!File.Exists(full) && !Directory.Exists(full)))
        {
            await Error(ctx, 404, "That isn't there any more.").ConfigureAwait(false);
            return;
        }

        await this.Dav(ctx, mounted, fs => fs.DeleteAsync(mounted.Files.Relative(full), ctx.RequestAborted)).ConfigureAwait(false);
    }

    /// <summary>Runs a change through the share's file system, so the browser and WebDAV obey the same rules.</summary>
    async ValueTask Dav(HttpContext ctx, Mounted mounted, Func<IWebDavFileSystem, ValueTask> change)
    {
        try
        {
            await change(mounted.Files).ConfigureAwait(false);
            ctx.Response.StatusCode = 204;
        }
        catch (WebDavException ex)
        {
            await Error(ctx, ex.StatusCode, ex.StatusCode == 403 ? "This share doesn't allow that." : "That didn't work.").ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await Error(ctx, 409, ex.Message).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            await Error(ctx, 403, "The computer sharing this folder doesn't allow that.").ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------- zip

    /// <summary>A folder (or the whole share) as one zip, streamed as it's made - nothing is written to disk.</summary>
    async ValueTask ZipAsync(HttpContext ctx)
    {
        var mounted = Share(ctx);
        if (!mounted.Share.CanList)
        {
            await Error(ctx, 403, "This is a drop box - you can add files, not see them.").ConfigureAwait(false);
            return;
        }

        var full = mounted.Files.Resolve(ctx.Request.Query.GetFirst("path"));
        if (full == null || !Directory.Exists(full))
        {
            await Error(ctx, 404, "That folder isn't there.").ConfigureAwait(false);
            return;
        }

        var files = this.Walk(mounted.Files, full).ToList();
        var name = (full == mounted.Files.Resolve("") ? mounted.Share.Name : Path.GetFileName(full)) + ".zip";
        var who = this.currentClient.Value;
        var transfer = this.Monitor.Start(mounted.Share, who?.Ip ?? "?", mounted.Files.Relative(full) is { Length: > 0 } r ? r + ".zip" : name, TransferDirection.Download, files.Sum(x => x.Length), who?.IsTunneled ?? false);

        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.Set("Content-Disposition", $"attachment; filename*=UTF-8''{Uri.EscapeDataString(name)}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, transfer.Token);
        try
        {
            await using (var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, true))
            {
                var buffer = new byte[81920];
                foreach (var file in files)
                {
                    var entry = zip.CreateEntry(Path.GetRelativePath(full, file.FullName).Replace(Path.DirectorySeparatorChar, '/'), CompressionFor(file.Name));
                    entry.LastWriteTime = file.LastWriteTime;
                    await using var output = await entry.OpenAsync(linked.Token).ConfigureAwait(false);
                    await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                    int read;
                    while ((read = await input.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                    {
                        await mounted.Share.Limiter.WaitAsync(read, linked.Token).ConfigureAwait(false);
                        await output.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                        transfer.Add(read);
                    }
                }
            }
            this.Monitor.End(transfer, TransferState.Completed);
        }
        catch (OperationCanceledException)
        {
            this.Monitor.End(transfer, TransferState.Cancelled, transfer.Token.IsCancellationRequested ? "Stopped from ShinyCommander" : "The visitor stopped the download");
            // the response is part way through - all that's left is to drop the connection
            throw;
        }
        catch (IOException ex)
        {
            this.Monitor.End(transfer, TransferState.Failed, ex.Message);
            throw;
        }
    }

    IEnumerable<FileInfo> Walk(ShareFileSystem files, string folder)
    {
        foreach (var item in files.List(folder))
        {
            if (item is FileInfo file)
            {
                yield return file;
            }
            else
            {
                foreach (var nested in this.Walk(files, item.FullName))
                    yield return nested;
            }
        }
    }

    static readonly HashSet<string> Compressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".mp4", ".mov",
        ".mkv", ".avi", ".webm", ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".docx", ".xlsx", ".pptx", ".pdf", ".apk", ".dmg"
    };

    static CompressionLevel CompressionFor(string name) => Compressed.Contains(Path.GetExtension(name)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;

    // ------------------------------------------------------------------------------------------- tus

    ValueTask OnTusCreate(Mounted mounted, TusCreateContext c)
    {
        var share = mounted.Share;
        var name = TusMeta.Get(c.Metadata, "filename");
        var folder = mounted.Files.Resolve(TusMeta.Get(c.Metadata, "path"));
        if (!share.CanUpload)
            c.Reject(403, "This share doesn't take uploads.");
        else if (ShareFileSystem.Split(name) is not { Length: 1 } || folder == null || !Directory.Exists(folder))
            c.Reject(400, "That isn't somewhere a file can go.");
        else if (share.Options.MaxUploadBytes is { } max && c.UploadLength > max)
            c.Reject(413, "That file is larger than this share accepts.");

        return ValueTask.CompletedTask;
    }

    async ValueTask OnTusComplete(Mounted mounted, TusCompleteContext c)
    {
        var store = mounted.Tus!;
        var name = TusMeta.Get(c.Upload.Metadata, "filename")!;
        var folder = mounted.Files.Resolve(TusMeta.Get(c.Upload.Metadata, "path"))!;
        var overwrite = TusMeta.Get(c.Upload.Metadata, "overwrite") == "true" && mounted.Share.CanModify;
        var target = Path.Combine(folder, name);
        if (!overwrite && (File.Exists(target) || Directory.Exists(target)))
            target = ShareFileSystem.FreeName(target);

        try
        {
            mounted.Files.Place(store.Inner.GetFilePath(c.Upload.Id), target, overwrite);
            store.End(c.Upload.Id, TransferState.Completed);
            // where it landed, for the page to show (a taken name gets " (2)")
            c.HttpContext.Response.Headers.Set("Upload-Saved-As", Uri.EscapeDataString(mounted.Files.Relative(target)));
        }
        catch (IOException ex)
        {
            store.End(c.Upload.Id, TransferState.Failed, ex.Message);
            throw;
        }
        await store.Inner.DeleteAsync(c.Upload.Id, CancellationToken.None).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------- fallback page

    ValueTask FallbackPageAsync(HttpContext ctx)
    {
        var id = ctx.Request.RouteValues["id"]?.ToString() ?? "";
        ctx.Response.Redirect($"/dav/{Uri.EscapeDataString(id)}/" + ctx.Request.QueryString, false, false);
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------- helpers

    static async ValueTask Json<T>(HttpContext ctx, T value, JsonTypeInfo<T> type)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        ctx.Response.Headers.Set("Cache-Control", "no-store");
        await ctx.Response.WriteBytesAsync(bytes, "application/json; charset=utf-8", ctx.RequestAborted).ConfigureAwait(false);
    }

    static ValueTask Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return Json(ctx, new ShareError(message), ShareJson.Default.ShareError);
    }
}
