using Microsoft.Extensions.Logging;
using Shiny.Net.Discovery;
using ShinyCommander.Share;

namespace ShinyCommander.Services;

/// <summary>When sharing counts as work that keeps the computer awake (KeepAwakeService decides whether it does, and on battery).</summary>
public enum KeepAwakeMode
{
    Off,

    /// <summary>While something is being downloaded or uploaded.</summary>
    Transfers,

    /// <summary>For as long as any folder is shared.</summary>
    Serving
}

/// <summary>
/// The app's folder server (<see cref="ShareServer"/>) and everything around it: settings, the pinggy token in the
/// keychain, whether sharing should keep the computer awake (<see cref="WantsKeepAwake"/>), telling the user about finished uploads, and shutting it all down when the app
/// closes. Nothing here is restored on the next launch - a share lasts as long as the app is open.
/// <see cref="Changed"/> is raised on the UI thread.
/// </summary>
[Singleton]
public partial class SharingService : ObservableObject, IAsyncDisposable
{
    public const string TunnelTokenKey = "sharing:pinggy";

    readonly AppSettings settings;
    readonly ISecretStore secrets;
    readonly IAppDialogs dialogs;
    bool shutDown;

    public SharingService(AppSettings settings, ISecretStore secrets, IAppDialogs dialogs, IMdnsManager mdns, ILoggerFactory loggerFactory)
    {
        this.settings = settings;
        this.secrets = secrets;
        this.dialogs = dialogs;
        this.Server = new ShareServer(loggerFactory, mdns);
        this.Server.Settings = this.ReadSettings();
        this.Server.StateChanged += this.OnServerChanged;
        this.Server.ShareEnded += this.OnShareEnded;
        this.Server.Monitor.Changed += this.OnMonitorChanged;
        this.Server.Monitor.UploadCompleted += this.OnUploadCompleted;
    }

    public ShareServer Server { get; }
    public TransferMonitor Monitor => this.Server.Monitor;

    /// <summary>Something about the server, its shares or its transfers changed - raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The status bar chip: "2 shared" / "3 transfers".</summary>
    public string Summary => this.Monitor.RunningCount > 0
        ? L.Sharing_ChipTransfersFormat(this.Monitor.RunningCount)
        : L.Sharing_ChipSharedFormat(this.Server.Shares.Count);

    public bool IsSharing => this.Server.Shares.Count > 0;
    public int ActiveTransfers => this.Monitor.RunningCount;
    public bool HasTransfers => this.Monitor.RunningCount > 0;

    public KeepAwakeMode KeepAwake => Enum.TryParse<KeepAwakeMode>(this.settings.ShareKeepAwake, out var mode) ? mode : KeepAwakeMode.Transfers;
    /// <summary>Sharing wants the computer kept awake right now, per <see cref="KeepAwake"/>.</summary>
    public bool WantsKeepAwake => this.KeepAwake switch
    {
        KeepAwakeMode.Serving => this.IsSharing,
        KeepAwakeMode.Transfers => this.Monitor.RunningCount > 0,
        _ => false
    };

    public bool NotifyUploads => this.settings.ShareNotifyUploads;

    public string? TunnelToken => this.secrets.Get(TunnelTokenKey);

    ShareServerSettings ReadSettings() => new()
    {
        Port = this.settings.SharePort > 0 ? this.settings.SharePort : 8080,
        TrustForwardedHeaders = this.settings.ShareTrustForwardedHeaders,
        TunnelToken = this.secrets.Get(TunnelTokenKey)
    };

    // ------------------------------------------------------------------------------------------- shares

    /// <summary>Shares a folder (starting the server if needed) and remembers the answers for next time.</summary>
    public async Task<ServedFolder> ServeAsync(ServeOptions options)
    {
        this.settings.ShareLastAccess = options.Access.ToString();
        this.settings.ShareLastAllowModify = options.AllowModify;
        this.settings.ShareLastUseKey = options.Key != null;
        this.settings.ShareLastAdvertise = options.Advertise;
        return await this.Server.AddAsync(options);
    }

    public Task UpdateAsync(ServedFolder share, ServeOptions options) => this.Server.UpdateAsync(share, options);

    public Task StopAsync(ServedFolder share) => this.Server.RemoveAsync(share);

    /// <summary>Stops every share and the server.</summary>
    public async Task StopAllAsync()
    {
        foreach (var share in this.Server.Shares)
            await this.Server.RemoveAsync(share);
        await this.Server.StopAsync();
    }

    // ------------------------------------------------------------------------------------------- settings

    /// <summary>Saves the Sharing window's settings and applies them (a changed port restarts the server).</summary>
    public async Task SaveSettingsAsync(int port, bool trustForwarded, string? tunnelToken, KeepAwakeMode keepAwake, bool notifyUploads)
    {
        this.settings.SharePort = port;
        this.settings.ShareTrustForwardedHeaders = trustForwarded;
        this.settings.ShareKeepAwake = keepAwake.ToString();
        this.settings.ShareNotifyUploads = notifyUploads;
        if (String.IsNullOrWhiteSpace(tunnelToken))
            this.secrets.Remove(TunnelTokenKey);
        else
            this.secrets.Set(TunnelTokenKey, tunnelToken.Trim());

        await this.Server.ApplySettingsAsync(this.ReadSettings());
        this.Raise();
    }

    // ------------------------------------------------------------------------------------------- events

    void OnServerChanged(object? sender, EventArgs e) => this.Raise();

    void OnMonitorChanged(object? sender, EventArgs e) => this.Raise();

    void OnShareEnded(object? sender, ServedFolder share)
        => MainThread.BeginInvokeOnMainThread(() => _ = this.dialogs.Toast(L.Sharing_EndedFormat(share.Name), ToastKind.Info));

    void OnUploadCompleted(object? sender, Transfer transfer)
    {
        if (!this.NotifyUploads)
            return;

        MainThread.BeginInvokeOnMainThread(() => _ = this.dialogs.Toast(L.Sharing_UploadedFormat(transfer.Name, transfer.Share.Name), ToastKind.Success));
    }

    void Raise()
    {
        if (this.shutDown)
            return;

        if (MainThread.IsMainThread)
            this.RaiseNow();
        else
            MainThread.BeginInvokeOnMainThread(this.RaiseNow);
    }

    void RaiseNow()
    {
        this.OnPropertyChanged(nameof(this.Summary));
        this.OnPropertyChanged(nameof(this.IsSharing));
        this.OnPropertyChanged(nameof(this.ActiveTransfers));
        this.OnPropertyChanged(nameof(this.HasTransfers));
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The app is closing: every share ends, the tunnel closes and the server stops. Nothing is kept.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this.shutDown)
            return;

        this.shutDown = true;
        try
        {
            await this.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
        }
    }
}
