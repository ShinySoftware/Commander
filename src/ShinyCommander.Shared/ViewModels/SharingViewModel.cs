using ShinyCommander.Share;
using ShinyCommander.Share.Contracts;

namespace ShinyCommander.ViewModels;

/// <summary>
/// The Sharing window: the server and its addresses, every shared folder (with its link, key and QR code), what is
/// moving right now with speed and time left, who has been connecting, the activity log, the Serve Folder form and the
/// server's settings. Figures are re-read twice a second while the window is open.
/// </summary>
public partial class SharingViewModel : ObservableObject
{
    static readonly TimeSpan?[] Expiries = [null, TimeSpan.FromHours(1), TimeSpan.FromHours(4), TimeSpan.FromDays(1), TimeSpan.FromDays(7)];
    static readonly long[] Bandwidths = [0, 256 * 1024, 1024 * 1024, 5 * 1024 * 1024, 10 * 1024 * 1024, 50 * 1024 * 1024];
    const int FinishedShown = 8;
    const int ActivityShown = 60;

    readonly SharingService sharing;
    readonly AppSettings settings;
    ShareItemViewModel? editing;

    public SharingViewModel(SharingService sharing, AppSettings settings)
    {
        this.sharing = sharing;
        this.settings = settings;
        this.LoadSettings();
        this.Sync();
    }

    ShareServer Server => this.sharing.Server;

    public ObservableCollection<ShareItemViewModel> Shares { get; } = [];
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = [];
    public ObservableCollection<ClientItem> Clients { get; } = [];
    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<string> LanUrls { get; } = [];

    // ------------------------------------------------------------------------------------------- choices

    public static string AccessName(ShareAccess access, bool modify) => access switch
    {
        ShareAccess.ReadOnly => L.Sharing_AccessReadOnly,
        ShareAccess.UploadOnly => L.Sharing_AccessDropBox,
        _ => modify ? L.Sharing_AccessFull : L.Sharing_AccessReadWrite
    };

    public IReadOnlyList<string> AccessChoices { get; } = [L.Sharing_AccessReadOnly, L.Sharing_AccessDropBox, L.Sharing_AccessReadWrite];
    public IReadOnlyList<string> ExpiryChoices { get; } = [L.Sharing_ExpiryNever, L.Sharing_Expiry1h, L.Sharing_Expiry4h, L.Sharing_Expiry1d, L.Sharing_Expiry7d];
    public IReadOnlyList<string> BandwidthChoices { get; } = [L.Sharing_BandwidthUnlimited, "256 KB/s", "1 MB/s", "5 MB/s", "10 MB/s", "50 MB/s"];
    public IReadOnlyList<string> KeepAwakeChoices { get; } = [L.Sharing_KeepAwakeOff, L.Sharing_KeepAwakeTransfers, L.Sharing_KeepAwakeServing];

    // ------------------------------------------------------------------------------------------- server state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string TotalsText { get; private set; } = "";

    [ObservableProperty]
    public partial string SpeedText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoShares))]
    public partial bool HasShares { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoTransfers))]
    public partial bool HasTransfers { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoClients))]
    public partial bool HasClients { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoActivity))]
    public partial bool HasActivity { get; private set; }

    [ObservableProperty]
    public partial bool HasWebApp { get; private set; }

    /// <summary>The first time the server runs: why other machines may not get through (the firewall asks first).</summary>
    [ObservableProperty]
    public partial bool ShowFirewallHint { get; private set; }

    public string FirewallHint => OperatingSystem.IsWindows() ? L.Sharing_FirewallWindows
        : OperatingSystem.IsMacOS() ? L.Sharing_FirewallMac
        : L.Sharing_FirewallLinux;

    public bool IsStopped => !this.IsRunning;
    public bool HasError => !String.IsNullOrEmpty(this.Error);
    public bool NoShares => !this.HasShares;
    public bool NoTransfers => !this.HasTransfers;
    public bool NoClients => !this.HasClients;
    public bool NoActivity => !this.HasActivity;
    public bool HasCopied => this.Copied != null;
    public bool HasFormError => this.FormError != null;
    public bool HasSettingsError => this.SettingsError != null;
    public bool HasTunnelUrl => this.TunnelUrl != null;
    public bool HasTunnelError => !String.IsNullOrEmpty(this.TunnelError);

    // ------------------------------------------------------------------------------------------- tunnel

    [ObservableProperty]
    public partial bool TunnelOpen { get; private set; }

    [ObservableProperty]
    public partial bool TunnelBusy { get; private set; }

    [ObservableProperty]
    public partial string TunnelText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTunnelUrl))]
    public partial string? TunnelUrl { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTunnelError))]
    public partial string? TunnelError { get; private set; }

    [ObservableProperty]
    public partial bool TunnelHasKeylessShares { get; private set; }

    public string TunnelButtonText => this.TunnelOpen || this.TunnelBusy ? L.Sharing_TunnelClose : L.Sharing_TunnelOpen;

    partial void OnTunnelOpenChanged(bool value) => this.OnPropertyChanged(nameof(this.TunnelButtonText));
    partial void OnTunnelBusyChanged(bool value) => this.OnPropertyChanged(nameof(this.TunnelButtonText));

    CancellationTokenSource? tunnelCts;

    [RelayCommand]
    async Task ToggleTunnel()
    {
        if (this.TunnelOpen || this.TunnelBusy)
        {
            this.tunnelCts?.Cancel();
            await this.Server.StopTunnelAsync();
            return;
        }

        this.TunnelBusy = true;
        this.tunnelCts = new CancellationTokenSource();
        try
        {
            await this.Server.StartTunnelAsync(this.tunnelCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
        }
        finally
        {
            this.TunnelBusy = false;
            this.Sync();
        }
    }

    // ------------------------------------------------------------------------------------------- refresh

    /// <summary>Re-reads everything: the shares, the transfers and their figures, clients and the log.</summary>
    public void Sync()
    {
        var server = this.Server;
        this.IsRunning = server.IsRunning;
        this.HasWebApp = server.HasWebApp;
        this.Error = server.LastError;
        this.StatusText = server.IsRunning ? L.Sharing_RunningFormat(server.Port) : L.Sharing_Stopped;
        this.ShowFirewallHint = server.IsRunning && !this.settings.ShareFirewallHintSeen;

        Replace(this.LanUrls, server.LanUrls);

        // shares: kept in place (their QR toggles survive), new ones added, ended ones dropped
        var live = server.Shares;
        foreach (var gone in this.Shares.Where(x => !live.Contains(x.Share)).ToList())
            this.Shares.Remove(gone);
        foreach (var share in live.Where(x => this.Shares.All(s => s.Share != x)))
            this.Shares.Add(new ShareItemViewModel(share, server));
        foreach (var item in this.Shares)
            item.Refresh();
        this.HasShares = this.Shares.Count > 0;

        // the tunnel
        this.TunnelOpen = server.TunnelStatus is TunnelStatus.Connected or TunnelStatus.Reconnecting;
        this.TunnelUrl = server.TunnelStatus == TunnelStatus.Connected ? server.TunnelUrl : null;
        this.TunnelError = server.TunnelStatus == TunnelStatus.Failed ? server.TunnelError : null;
        this.TunnelText = server.TunnelStatus switch
        {
            TunnelStatus.Connecting => L.Sharing_TunnelConnecting,
            TunnelStatus.Connected => L.Sharing_TunnelConnected,
            TunnelStatus.Reconnecting => L.Sharing_TunnelReconnecting,
            TunnelStatus.Failed => L.Sharing_TunnelFailed,
            _ => String.IsNullOrWhiteSpace(this.sharing.TunnelToken) ? L.Sharing_TunnelOffAnonymous : L.Sharing_TunnelOff
        };
        this.TunnelHasKeylessShares = this.TunnelOpen && live.Any(x => !x.HasKey);

        this.SyncTransfers();
        this.SyncClients();
        this.SyncActivity();
    }

    /// <summary>The fast half of <see cref="Sync"/> - just the moving figures (the window's timer).</summary>
    public void Tick()
    {
        this.SyncTransfers();
        foreach (var item in this.Shares)
            item.Refresh();
    }

    void SyncTransfers()
    {
        var monitor = this.sharing.Monitor;
        var wanted = monitor.Running.Concat(monitor.Finished.Take(FinishedShown)).ToList();

        foreach (var gone in this.Transfers.Where(x => !wanted.Contains(x.Transfer)).ToList())
            this.Transfers.Remove(gone);

        // running first (oldest at the top), then finished (newest first) - inserted where they belong
        for (var i = 0; i < wanted.Count; i++)
        {
            var existing = this.Transfers.FirstOrDefault(x => x.Transfer == wanted[i]);
            if (existing == null)
            {
                this.Transfers.Insert(Math.Min(i, this.Transfers.Count), new TransferItemViewModel(wanted[i]));
            }
            else
            {
                var at = this.Transfers.IndexOf(existing);
                if (at != i && i < this.Transfers.Count)
                    this.Transfers.Move(at, i);
                existing.Refresh();
            }
        }
        this.HasTransfers = this.Transfers.Count > 0;

        var speed = monitor.BytesPerSecond;
        this.SpeedText = monitor.RunningCount > 0 ? L.Sharing_SpeedFormat(monitor.RunningCount, FormatBytes.Speed(speed)) : L.Sharing_Idle;
        this.TotalsText = L.Sharing_TotalsFormat(
            FormatBytes.Size(monitor.BytesDown + monitor.Running.Where(x => x.Direction == TransferDirection.Download).Sum(x => x.Bytes)),
            monitor.FilesDown,
            FormatBytes.Size(monitor.BytesUp + monitor.Running.Where(x => x.Direction == TransferDirection.Upload).Sum(x => x.Bytes)),
            monitor.FilesUp
        );
    }

    void SyncClients()
    {
        var blocked = this.Server.BlockedAddresses;
        var now = DateTimeOffset.UtcNow;
        var seen = this.sharing.Monitor.Clients
            .Select(x => new ClientItem(x.Ip, L.Sharing_ClientDetailFormat(Ago(now - x.LastSeenUtc), x.Requests), blocked.Contains(x.Ip), x.IsTunneled))
            .ToList();
        // blocked addresses stay listed (to unblock) after they've gone quiet
        seen.AddRange(blocked.Where(x => seen.All(c => c.Ip != x)).Select(x => new ClientItem(x, L.Sharing_Blocked, true, false)));

        if (!seen.SequenceEqual(this.Clients))
            Replace(this.Clients, seen);
        this.HasClients = this.Clients.Count > 0;
    }

    void SyncActivity()
    {
        var items = this.sharing.Monitor.Activity.Take(ActivityShown).Select(ToItem).ToList();
        if (!items.SequenceEqual(this.Activity))
            Replace(this.Activity, items);
        this.HasActivity = this.Activity.Count > 0;
    }

    static ActivityItem ToItem(ActivityEntry e)
    {
        var what = e.Kind switch
        {
            ActivityKind.Visit => L.Sharing_ActVisitFormat(e.ShareName ?? ""),
            ActivityKind.KeyAccepted => L.Sharing_ActKeyFormat(e.ShareName ?? ""),
            ActivityKind.KeyRefused => L.Sharing_ActWrongKeyFormat(e.ShareName ?? ""),
            ActivityKind.Download => L.Sharing_ActDownloadFormat(e.Path ?? ""),
            ActivityKind.Upload => L.Sharing_ActUploadFormat(e.Path ?? ""),
            ActivityKind.CreateFolder => L.Sharing_ActFolderFormat(e.Path ?? ""),
            ActivityKind.Rename => L.Sharing_ActRenameFormat(e.Path ?? "", e.Detail ?? ""),
            ActivityKind.Delete => L.Sharing_ActDeleteFormat(e.Path ?? ""),
            ActivityKind.Blocked => L.Sharing_Blocked,
            _ => L.Sharing_ActRefusedFormat(e.ShareName ?? "", e.Detail ?? "")
        };
        var who = e.IsTunneled ? $"{e.ClientIp} · {L.Sharing_ViaTunnel}" : e.ClientIp;
        return new ActivityItem(e.AtUtc.ToLocalTime().ToString("T"), who, what, e.Kind is ActivityKind.KeyRefused or ActivityKind.Refused);
    }

    static string Ago(TimeSpan span) => span.TotalSeconds < 10 ? L.Sharing_JustNow : L.Sharing_AgoFormat(FormatBytes.Duration(span));

    static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items))
            return;

        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    // ------------------------------------------------------------------------------------------- server commands

    [RelayCommand]
    async Task StartServer()
    {
        try
        {
            await this.Server.StartAsync();
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
        }
        this.Sync();
    }

    [RelayCommand]
    async Task StopAll()
    {
        await this.sharing.StopAllAsync();
        this.Sync();
    }

    [RelayCommand]
    void DismissFirewallHint()
    {
        this.settings.ShareFirewallHintSeen = true;
        this.ShowFirewallHint = false;
    }

    [RelayCommand]
    async Task Copy(string? text)
    {
        if (String.IsNullOrEmpty(text))
            return;

        await Clipboard.Default.SetTextAsync(text);
        this.Copied = L.Sharing_Copied;
        await Task.Delay(1500);
        this.Copied = null;
    }

    /// <summary>"Copied" flashes in the window's header after a copy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopied))]
    public partial string? Copied { get; private set; }

    [RelayCommand]
    async Task Open(string? url)
    {
        if (!String.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            await Launcher.Default.OpenAsync(uri);
    }

    // ------------------------------------------------------------------------------------------- share commands

    [RelayCommand]
    void ToggleQr(ShareItemViewModel item) => item.ShowQr = !item.ShowQr;

    [RelayCommand]
    Task CopyLink(ShareItemViewModel item) => this.Copy(item.HasKey ? item.LinkWithKey : item.PageUrl);

    [RelayCommand]
    Task CopyDav(ShareItemViewModel item) => this.Copy(item.DavUrl);

    [RelayCommand]
    Task CopyKey(ShareItemViewModel item) => this.Copy(item.Key);

    [RelayCommand]
    Task OpenShare(ShareItemViewModel item) => this.Open(item.HasKey ? item.LinkWithKey : item.PageUrl);

    [RelayCommand]
    async Task StopShare(ShareItemViewModel item)
    {
        await this.sharing.StopAsync(item.Share);
        if (this.editing == item)
            this.CancelForm();
        this.Sync();
    }

    [RelayCommand]
    void EditShare(ShareItemViewModel item)
    {
        var o = item.Share.Options;
        this.editing = item;
        this.FormPath = o.RootPath;
        this.FormAccess = (int)o.Access;
        this.FormAllowModify = o.AllowModify;
        this.FormUseKey = o.Key != null;
        this.FormKey = o.Key ?? ServedFolder.NewKey();
        this.FormExpiry = Math.Max(0, Array.IndexOf(Expiries, o.ExpiresAfter));
        this.FormMaxDownloads = o.MaxDownloads?.ToString() ?? "";
        this.FormBandwidth = Math.Max(0, Array.IndexOf(Bandwidths, o.BandwidthLimit));
        this.FormAdvertise = o.Advertise;
        this.FormShowHidden = o.ShowHidden;
        this.FormError = null;
        this.IsFormEditing = true;
        this.ShowForm = true;
    }

    // ------------------------------------------------------------------------------------------- transfers + clients

    [RelayCommand]
    void StopTransfer(TransferItemViewModel item)
    {
        item.Transfer.Cancel();
        this.Tick();
    }

    [RelayCommand]
    void Block(string? ip)
    {
        if (ip == null)
            return;

        this.Server.Block(ip);
        this.Sync();
    }

    [RelayCommand]
    void Unblock(string? ip)
    {
        if (ip == null)
            return;

        this.Server.Unblock(ip);
        this.Sync();
    }

    [RelayCommand]
    void ClearHistory()
    {
        this.sharing.Monitor.ClearHistory();
        this.Sync();
    }

    // ------------------------------------------------------------------------------------------- the Serve Folder form

    [ObservableProperty]
    public partial bool ShowForm { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle), nameof(FormSaveText))]
    public partial bool IsFormEditing { get; private set; }

    public string FormTitle => this.IsFormEditing ? L.Sharing_FormEditTitle : L.Sharing_FormNewTitle;
    public string FormSaveText => this.IsFormEditing ? L.Common_Save : L.Sharing_FormStart;

    [ObservableProperty]
    public partial string FormPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormCanModify))]
    public partial int FormAccess { get; set; }

    public bool FormCanModify => this.FormAccess == (int)ShareAccess.ReadWrite;

    [ObservableProperty]
    public partial bool FormAllowModify { get; set; }

    [ObservableProperty]
    public partial bool FormUseKey { get; set; }

    [ObservableProperty]
    public partial string FormKey { get; set; } = "";

    [ObservableProperty]
    public partial int FormExpiry { get; set; }

    [ObservableProperty]
    public partial string FormMaxDownloads { get; set; } = "";

    [ObservableProperty]
    public partial int FormBandwidth { get; set; }

    [ObservableProperty]
    public partial bool FormAdvertise { get; set; }

    [ObservableProperty]
    public partial bool FormShowHidden { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    public partial string? FormError { get; private set; }

    /// <summary>Opens the form for a new share of <paramref name="path"/>, starting from the answers given last time.</summary>
    public void NewShare(string path)
    {
        this.editing = null;
        this.FormPath = path;
        this.FormAccess = (int)(Enum.TryParse<ShareAccess>(this.settings.ShareLastAccess, out var access) ? access : ShareAccess.ReadOnly);
        this.FormAllowModify = this.settings.ShareLastAllowModify;
        this.FormUseKey = this.settings.ShareLastUseKey;
        this.FormKey = ServedFolder.NewKey();
        this.FormExpiry = 0;
        this.FormMaxDownloads = "";
        this.FormBandwidth = 0;
        this.FormAdvertise = this.settings.ShareLastAdvertise;
        this.FormShowHidden = false;
        this.FormError = null;
        this.IsFormEditing = false;
        this.ShowForm = true;
    }

    /// <summary>The folder the main window's active panel shows - where "Share a folder" starts.</summary>
    public Func<string?>? CurrentFolder { get; set; }

    [RelayCommand]
    void NewShareHere()
    {
        var folder = this.CurrentFolder?.Invoke();
        this.NewShare(folder is { Length: > 0 } && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [RelayCommand]
    void NewKey() => this.FormKey = ServedFolder.NewKey();

    [RelayCommand]
    void CancelForm()
    {
        this.ShowForm = false;
        this.editing = null;
    }

    [RelayCommand]
    async Task SaveForm()
    {
        this.FormError = null;
        var path = this.FormPath.Trim();
        if (!Directory.Exists(path))
        {
            this.FormError = L.Sharing_FormNoFolder;
            return;
        }
        if (this.FormUseKey && String.IsNullOrWhiteSpace(this.FormKey))
        {
            this.FormError = L.Sharing_FormNoKey;
            return;
        }

        int? maxDownloads = null;
        if (!String.IsNullOrWhiteSpace(this.FormMaxDownloads))
        {
            if (!Int32.TryParse(this.FormMaxDownloads.Trim(), out var max) || max <= 0)
            {
                this.FormError = L.Sharing_FormBadDownloads;
                return;
            }
            maxDownloads = max;
        }

        var access = (ShareAccess)Math.Clamp(this.FormAccess, 0, 2);
        var options = new ServeOptions
        {
            RootPath = path,
            Access = access,
            AllowModify = access == ShareAccess.ReadWrite && this.FormAllowModify,
            Key = this.FormUseKey ? this.FormKey.Trim() : null,
            ExpiresAfter = Expiries[Math.Clamp(this.FormExpiry, 0, Expiries.Length - 1)],
            MaxDownloads = maxDownloads,
            BandwidthLimit = Bandwidths[Math.Clamp(this.FormBandwidth, 0, Bandwidths.Length - 1)],
            Advertise = this.FormAdvertise,
            ShowHidden = this.FormShowHidden
        };

        try
        {
            if (this.editing is { } item)
            {
                await this.sharing.UpdateAsync(item.Share, options);
                item.RefreshAll();
            }
            else
            {
                var share = await this.sharing.ServeAsync(options);
                this.Sync();
                // the new share opens with its QR code showing - the quickest way onto a phone
                if (this.Shares.FirstOrDefault(x => x.Share == share) is { } added)
                    added.ShowQr = true;
            }
            this.ShowForm = false;
            this.editing = null;
        }
        catch (Exception ex)
        {
            this.FormError = ex.Message;
        }
        this.Sync();
    }

    // ------------------------------------------------------------------------------------------- settings

    [ObservableProperty]
    public partial bool ShowSettings { get; set; }

    [ObservableProperty]
    public partial string SettingsPort { get; set; } = "";

    [ObservableProperty]
    public partial bool SettingsTrustForwarded { get; set; }

    [ObservableProperty]
    public partial string SettingsTunnelToken { get; set; } = "";

    [ObservableProperty]
    public partial int SettingsKeepAwake { get; set; }

    [ObservableProperty]
    public partial bool SettingsNotifyUploads { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsError))]
    public partial string? SettingsError { get; private set; }

    void LoadSettings()
    {
        this.SettingsPort = this.Server.Settings.Port.ToString();
        this.SettingsTrustForwarded = this.Server.Settings.TrustForwardedHeaders;
        this.SettingsTunnelToken = this.sharing.TunnelToken ?? "";
        this.SettingsKeepAwake = (int)this.sharing.KeepAwake;
        this.SettingsNotifyUploads = this.sharing.NotifyUploads;
    }

    [RelayCommand]
    void ToggleSettings()
    {
        if (!this.ShowSettings)
            this.LoadSettings();
        this.SettingsError = null;
        this.ShowSettings = !this.ShowSettings;
    }

    [RelayCommand]
    async Task SaveSettings()
    {
        this.SettingsError = null;
        if (!Int32.TryParse(this.SettingsPort.Trim(), out var port) || port is < 1024 or > 65535)
        {
            this.SettingsError = L.Sharing_SettingsBadPort;
            return;
        }

        try
        {
            await this.sharing.SaveSettingsAsync(port, this.SettingsTrustForwarded, this.SettingsTunnelToken, (KeepAwakeMode)Math.Clamp(this.SettingsKeepAwake, 0, 2), this.SettingsNotifyUploads);
            this.ShowSettings = false;
        }
        catch (Exception ex)
        {
            this.SettingsError = ex.Message;
        }
        this.Sync();
    }
}
