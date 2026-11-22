using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.Ssh;
using Shiny.Net.HttpServer.StaticFiles;
using Shiny.Net.HttpServer.Tus;
using Shiny.Net.HttpServer.WebDav;

namespace ShinyCommander.Share;

/// <summary>Server-wide settings - the management window's Settings section.</summary>
public sealed record ShareServerSettings
{
    /// <summary>The port to listen on; when it's taken the next free one is used.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>Take the client address and scheme from X-Forwarded-* - only behind a reverse proxy you run.</summary>
    public bool TrustForwardedHeaders { get; init; }

    /// <summary>A pinggy.io access token: without one a tunnel lasts an hour and gets a new address each time.</summary>
    public string? TunnelToken { get; init; }
}

public enum TunnelStatus
{
    Off,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>
/// Serves shared folders: a browser page per share (the Blazor app embedded in this assembly), the same folder over
/// WebDAV (Finder, Explorer, another ShinyCommander), resumable uploads (tus) and folder downloads as zip - on every
/// interface of this machine, and optionally through a pinggy tunnel. Nothing is saved: when the app closes, the
/// shares are gone.
/// </summary>
public sealed partial class ShareServer : IAsyncDisposable
{
    /// <summary>Files from this size up are uploaded resumably by the browser page.</summary>
    public const long ResumableThreshold = 16L * 1024 * 1024;

    const string FrontendResource = "ShinyCommander.Share.wwwroot.zip";

    readonly ILoggerFactory loggerFactory;
    readonly ILogger logger;
    readonly IMdnsManager? mdns;
    readonly TimeProvider time;
    readonly ShareGate gate = new();
    readonly AsyncLocal<ClientContext?> currentClient = new();
    readonly Lock sharesLock = new();
    readonly List<Mounted> shares = [];
    readonly SemaphoreSlim lifecycle = new(1, 1);
    readonly string uploadRoot = Path.Combine(Path.GetTempPath(), "ShinyCommander-uploads", Environment.ProcessId.ToString());
    readonly Timer sweeper;

    HttpServer? server;
    QuickTunnel? tunnel;

    sealed class Mounted(ServedFolder share, ShareFileSystem files)
    {
        public ServedFolder Share => share;
        public ShareFileSystem Files => files;
        public List<Shiny.Net.HttpServer.Routing.RouteEndpoint> Routes { get; } = [];
        public TrackedTusStore? Tus { get; set; }
        public IHttpServerAdvertiser? Advertiser { get; set; }
    }

    public ShareServer(ILoggerFactory? loggerFactory = null, IMdnsManager? mdns = null, TimeProvider? time = null)
    {
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.logger = this.loggerFactory.CreateLogger<ShareServer>();
        this.mdns = mdns;
        this.time = time ?? TimeProvider.System;
        this.sweeper = new Timer(this.OnSweep, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public TransferMonitor Monitor { get; } = new();
    public ShareServerSettings Settings { get; set; } = new();

    /// <summary>Raised (on any thread) when the server starts or stops, a share is added, changed or removed, or the tunnel changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when a share stops by itself (expired, or its download limit reached).</summary>
    public event EventHandler<ServedFolder>? ShareEnded;

    public bool IsRunning => this.server?.IsRunning ?? false;
    public int Port { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The embedded browser app was built into this assembly (not a <c>-p:SkipFrontendBuild=true</c> build).</summary>
    public bool HasWebApp { get; } = FrontendSource() != null;

    public IReadOnlyList<ServedFolder> Shares
    {
        get
        {
            lock (this.sharesLock)
                return this.shares.Select(x => x.Share).ToList();
        }
    }

    // ------------------------------------------------------------------------------------------- addresses

    /// <summary>"http://192.168.1.20:8080" for each address on this machine others can reach, most likely first.</summary>
    public IReadOnlyList<string> LanUrls => this.IsRunning ? LocalAddresses().Select(x => $"http://{Format(x)}:{this.Port}").ToList() : [];

    /// <summary>The share's page at a base address (a LAN url or the tunnel's).</summary>
    public static string PageUrl(ServedFolder share, string baseUrl, bool withKey = false)
        => $"{baseUrl.TrimEnd('/')}/s/{share.Id}" + (withKey && share.HasKey ? "?key=" + Uri.EscapeDataString(share.Options.Key!) : "");

    /// <summary>The share's WebDAV address - connect to it from Finder, Explorer or another ShinyCommander.</summary>
    public static string DavUrl(ServedFolder share, string baseUrl) => $"{baseUrl.TrimEnd('/')}/dav/{share.Id}/";

    static string Format(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();

    /// <summary>The machine's addresses on networks that are up - IPv4 first, then the rest, no loopback.</summary>
    static IEnumerable<IPAddress> LocalAddresses()
    {
        IEnumerable<NetworkInterface> nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        return nics
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Select(x => x.Address)
            .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))
            // 169.254 (no DHCP answer) last
            .OrderBy(x => x.GetAddressBytes()[0] == 169 ? 1 : 0)
            .Distinct()
            .ToList();
    }

    // ------------------------------------------------------------------------------------------- lifecycle

    /// <summary>Starts listening (if it isn't already). Shares added before start are mounted now.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await this.lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.server is { IsRunning: true })
                return;

            this.LastError = null;
            Exception? last = null;
            // the port asked for, or the next free one
            for (var port = this.Settings.Port; port < this.Settings.Port + 20; port++)
            {
                var candidate = this.Build(port);
                try
                {
                    await candidate.StartAsync(ct).ConfigureAwait(false);
                    this.server = candidate;
                    this.Port = new Uri(candidate.ListenUrl!).Port;
                    break;
                }
                catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
                {
                    last = ex;
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (this.server == null)
            {
                this.LastError = last?.Message ?? "The server couldn't start.";
                throw new InvalidOperationException(this.LastError, last);
            }

            List<Mounted> mounted;
            lock (this.sharesLock)
                mounted = this.shares.ToList();
            foreach (var m in mounted)
                await this.MountAsync(m).ConfigureAwait(false);
        }
        finally
        {
            this.lifecycle.Release();
            this.Raise();
        }
    }

    /// <summary>Closes the tunnel, cuts off every transfer and stops listening. The shares stay listed (restartable).</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await this.lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await this.StopTunnelCoreAsync().ConfigureAwait(false);
            this.Monitor.CancelAll();

            List<Mounted> mounted;
            lock (this.sharesLock)
                mounted = this.shares.ToList();
            foreach (var m in mounted)
                await this.UnmountAsync(m).ConfigureAwait(false);

            if (this.server is { } s)
            {
                this.server = null;
                try
                {
                    await s.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    this.logger.LogWarning("The share server took too long to drain; disposing it");
                }
                await s.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            this.lifecycle.Release();
            this.Raise();
        }
    }

    /// <summary>Stops everything and forgets every share - the app is closing.</summary>
    public async ValueTask DisposeAsync()
    {
        await this.sweeper.DisposeAsync().ConfigureAwait(false);
        await this.StopAsync().ConfigureAwait(false);
        lock (this.sharesLock)
            this.shares.Clear();

        try
        {
            if (Directory.Exists(this.uploadRoot))
                Directory.Delete(this.uploadRoot, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Restarts on the new settings (port, forwarded headers) when running; the tunnel is reopened.</summary>
    public async Task ApplySettingsAsync(ShareServerSettings settings, CancellationToken ct = default)
    {
        var wasTunnel = this.TunnelStatus is TunnelStatus.Connected or TunnelStatus.Connecting or TunnelStatus.Reconnecting;
        var restart = this.IsRunning && (settings.Port != this.Settings.Port || settings.TrustForwardedHeaders != this.Settings.TrustForwardedHeaders);
        var tunnelChanged = settings.TunnelToken != this.Settings.TunnelToken;
        this.Settings = settings;

        if (restart)
        {
            await this.StopAsync(ct).ConfigureAwait(false);
            await this.StartAsync(ct).ConfigureAwait(false);
        }
        if (wasTunnel && (restart || tunnelChanged))
        {
            await this.StopTunnelAsync().ConfigureAwait(false);
            await this.StartTunnelAsync(ct).ConfigureAwait(false);
        }
        this.Raise();
    }

    // ------------------------------------------------------------------------------------------- shares

    /// <summary>Shares a folder and starts the server if it isn't running.</summary>
    public async Task<ServedFolder> AddAsync(ServeOptions options, CancellationToken ct = default)
    {
        if (!Directory.Exists(options.RootPath))
            throw new DirectoryNotFoundException(options.RootPath);

        var share = new ServedFolder(ServedFolder.NewId(), options with { RootPath = Path.GetFullPath(options.RootPath) }, this.time.GetUtcNow());
        var mounted = new Mounted(share, new ShareFileSystem(share, this.Monitor, this.CurrentClient));
        lock (this.sharesLock)
            this.shares.Add(mounted);

        if (this.IsRunning)
            await this.MountAsync(mounted).ConfigureAwait(false);
        else
            await this.StartAsync(ct).ConfigureAwait(false);

        this.Raise();
        return share;
    }

    /// <summary>Changes what a share allows; its address stays the same. Visitors who gave an old key must give the new one.</summary>
    public async Task UpdateAsync(ServedFolder share, ServeOptions options)
    {
        var mounted = this.Find(share);
        if (mounted == null)
            return;

        var wasAdvertised = share.Options.Advertise;
        share.Update(options, this.time.GetUtcNow());

        // the WebDAV mount's write/delete switches are fixed when it's mapped - map it again on the new terms
        if (this.IsRunning)
        {
            await this.UnmountAsync(mounted).ConfigureAwait(false);
            await this.MountAsync(mounted).ConfigureAwait(false);
        }
        else if (wasAdvertised && !options.Advertise && mounted.Advertiser is { } ad)
        {
            await ad.DisposeAsync().ConfigureAwait(false);
            mounted.Advertiser = null;
        }
        this.Raise();
    }

    /// <summary>Stops sharing a folder: its transfers are cut off and its address stops answering.</summary>
    public async Task RemoveAsync(ServedFolder share)
    {
        var mounted = this.Find(share);
        if (mounted == null)
            return;

        lock (this.sharesLock)
            this.shares.Remove(mounted);

        this.Monitor.CancelAll(share);
        await this.UnmountAsync(mounted).ConfigureAwait(false);

        // the last share gone: nothing left to serve
        if (this.Shares.Count == 0)
            await this.StopAsync().ConfigureAwait(false);

        this.Raise();
    }

    public void Block(string ip)
    {
        this.gate.Block(ip);
        foreach (var transfer in this.Monitor.Running.Where(x => x.ClientIp == ip))
            transfer.Cancel();
        this.Raise();
    }

    public void Unblock(string ip)
    {
        this.gate.Unblock(ip);
        this.Raise();
    }

    public IReadOnlyCollection<string> BlockedAddresses => this.gate.Blocked;

    ClientContext? CurrentClient() => this.currentClient.Value;

    Mounted? Find(ServedFolder share)
    {
        lock (this.sharesLock)
            return this.shares.FirstOrDefault(x => x.Share == share);
    }

    Mounted? Find(string? id)
    {
        if (id == null)
            return null;

        lock (this.sharesLock)
            return this.shares.FirstOrDefault(x => x.Share.Id == id);
    }

    async Task MountAsync(Mounted mounted)
    {
        var s = this.server;
        if (s == null)
            return;

        var share = mounted.Share;
        var dav = s.MapWebDav($"/dav/{share.Id}", o =>
        {
            o.FileSystem = mounted.Files;
            o.AllowWrite = share.CanUpload;
            o.AllowDelete = share.CanModify;
            o.DisplayName = share.Name;
            o.ServeHiddenFiles = share.Options.ShowHidden;
            o.DirectoryBrowsing = true;
            if (share.Options.MaxUploadBytes is { } max)
                o.MaxUploadBytes = max;
        });
        mounted.Routes.AddRange(dav.Routes.Select(x => x.Endpoint));

        if (share.CanUpload)
        {
            var folder = Path.Combine(this.uploadRoot, share.Id);
            Directory.CreateDirectory(folder);
            var store = mounted.Tus ??= new TrackedTusStore(new DiskTusStore(folder), mounted.Files, this.Monitor, this.CurrentClient);
            var tus = s.MapTus($"/api/s/{share.Id}/tus", o =>
            {
                o.Store = store;
                o.MaxSize = share.Options.MaxUploadBytes;
                o.Expiration = TimeSpan.FromHours(6);
                o.OnBeforeCreateAsync = c => this.OnTusCreate(mounted, c);
                o.OnUploadCompleteAsync = c => this.OnTusComplete(mounted, c);
            });
            // tus chunks are 16 MB; the server-wide body limit is 30 MB, so they pass as they are
            mounted.Routes.AddRange(tus.Routes.Select(x => x.Endpoint));
        }

        if (share.Options.Advertise && this.mdns != null && mounted.Advertiser == null)
        {
            try
            {
                mounted.Advertiser = await s.AdvertiseAsync(this.mdns, o =>
                {
                    o.ServiceType = "_webdav._tcp";
                    o.InstanceName = $"{share.Name} ({Environment.MachineName})";
                    o.Path = $"/dav/{share.Id}/";
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Couldn't advertise {Share} on the local network", share.Name);
            }
        }
    }

    async Task UnmountAsync(Mounted mounted)
    {
        if (this.server is { } s)
        {
            foreach (var route in mounted.Routes)
                s.Unmap(route);
        }
        mounted.Routes.Clear();

        if (mounted.Advertiser is { } ad)
        {
            mounted.Advertiser = null;
            try
            {
                await ad.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Couldn't withdraw the advertisement for {Share}", mounted.Share.Name);
            }
        }
    }

    void OnSweep(object? state) => _ = this.SweepAsync();

    /// <summary>Ends the shares that have expired or reached their download limit.</summary>
    internal Task SweepAsync()
    {
        var now = this.time.GetUtcNow();
        return Task.WhenAll(this.Shares.Where(x => x.EndedReason(now) != null).Select(this.EndShareAsync));
    }

    async Task EndShareAsync(ServedFolder share)
    {
        await this.RemoveAsync(share).ConfigureAwait(false);
        this.ShareEnded?.Invoke(this, share);
    }

    // ------------------------------------------------------------------------------------------- tunnel

    public TunnelStatus TunnelStatus { get; private set; }
    public string? TunnelUrl { get; private set; }
    public string? TunnelError { get; private set; }

    /// <summary>
    /// Opens a public HTTPS address through pinggy.io. Shares without a key aren't reachable through it - only from
    /// this network.
    /// </summary>
    public async Task<string?> StartTunnelAsync(CancellationToken ct = default)
    {
        if (!this.IsRunning)
            await this.StartAsync(ct).ConfigureAwait(false);

        await this.StopTunnelCoreAsync().ConfigureAwait(false);
        var t = QuickTunnel.For(this.server!, QuickTunnelHost.Pinggy, String.IsNullOrWhiteSpace(this.Settings.TunnelToken) ? null! : this.Settings.TunnelToken.Trim(), null!, this.loggerFactory);
        t.PropertyChanged += this.OnTunnelChanged;
        this.tunnel = t;
        this.TunnelStatus = TunnelStatus.Connecting;
        this.TunnelError = null;
        this.Raise();

        try
        {
            var url = await t.StartAsync(ct).ConfigureAwait(false);
            if (url == null)
            {
                this.TunnelStatus = TunnelStatus.Failed;
                this.TunnelError = t.LastError ?? "The tunnel connected but didn't report an address.";
            }
            else
            {
                this.TunnelUrl = url;
                this.TunnelStatus = TunnelStatus.Connected;
            }
            return url;
        }
        catch (OperationCanceledException)
        {
            await this.StopTunnelCoreAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            this.TunnelStatus = TunnelStatus.Failed;
            this.TunnelError = ex.Message;
            return null;
        }
        finally
        {
            this.Raise();
        }
    }

    public async Task StopTunnelAsync()
    {
        await this.StopTunnelCoreAsync().ConfigureAwait(false);
        this.Raise();
    }

    async Task StopTunnelCoreAsync()
    {
        var t = this.tunnel;
        this.tunnel = null;
        this.TunnelStatus = TunnelStatus.Off;
        this.TunnelUrl = null;
        if (t == null)
            return;

        t.PropertyChanged -= this.OnTunnelChanged;
        // a visitor coming in through the tunnel is cut off with it
        foreach (var transfer in this.Monitor.Running.Where(x => x.IsTunneled))
            transfer.Cancel();

        try
        {
            await t.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await t.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Closing the tunnel failed");
        }
    }

    void OnTunnelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not QuickTunnel t || t != this.tunnel)
            return;

        this.TunnelUrl = t.PublicUrl;
        this.TunnelError = t.LastError;
        this.TunnelStatus = t.State switch
        {
            QuickTunnelState.Connected => TunnelStatus.Connected,
            QuickTunnelState.Reconnecting => TunnelStatus.Reconnecting,
            QuickTunnelState.Failed => TunnelStatus.Failed,
            _ => this.TunnelStatus
        };
        this.Raise();
    }

    void Raise() => this.StateChanged?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------------------------------- the server

    static ZipFileSource? FrontendSource()
    {
        try
        {
            var source = new ZipFileSource(typeof(ShareServer).Assembly, FrontendResource)
            {
                PrecompressedEncodings = ["br", "gzip"]
            };
            return source.Paths.Any(x => x.Equals("index.html", StringComparison.OrdinalIgnoreCase)) ? source : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    HttpServer Build(int port)
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Address = IPAddress.Any;
        builder.Options.Port = port;
        builder.Options.UseForwardedHeaders = this.Settings.TrustForwardedHeaders;
        builder.Options.RebindOnNetworkChange = true;
        builder.Options.ServerHeader = "ShinyCommander";
        builder.Services.AddSingleton(this.loggerFactory);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        var app = builder.Build();

        app.Use(this.GuardAsync);
        this.MapApi(app);

        var frontend = FrontendSource();
        if (frontend != null)
            app.UseBlazorWebAssembly(frontend);

        return app;
    }
}
