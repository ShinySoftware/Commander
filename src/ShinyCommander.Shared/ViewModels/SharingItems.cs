using ShinyCommander.Share;
using ShinyCommander.Share.Contracts;

namespace ShinyCommander.ViewModels;

/// <summary>One shared folder in the Sharing window.</summary>
public partial class ShareItemViewModel(ServedFolder share, ShareServer server) : ObservableObject
{
    public ServedFolder Share => share;

    public string Name => share.Name;
    public string Path => share.RootPath;
    public string AccessText => SharingViewModel.AccessName(share.Options.Access, share.CanModify);
    public bool HasKey => share.HasKey;
    public bool NoKey => !share.HasKey;
    public string Key => share.Options.Key ?? "";

    public bool HasExpiry => share.ExpiresUtc != null || share.Options.MaxDownloads != null;
    public string ExpiryText
    {
        get
        {
            var parts = new List<string>();
            if (share.ExpiresUtc is { } until)
                parts.Add(L.Sharing_UntilFormat(until.ToLocalTime().ToString("t")));
            if (share.Options.MaxDownloads is { } max)
                parts.Add(L.Sharing_DownloadsLeftFormat(Math.Max(0, max - share.FilesDown)));
            return String.Join(" · ", parts);
        }
    }

    public bool HasLimit => share.Options.BandwidthLimit > 0;
    public string LimitText => FormatBytes.Speed(share.Options.BandwidthLimit);
    public bool Advertised => share.Options.Advertise;

    /// <summary>The address to give out: the public one while the tunnel is up (and the share has a key), else this network's.</summary>
    public bool UsesTunnel => server.TunnelStatus == TunnelStatus.Connected && server.TunnelUrl != null && share.HasKey;

    string? BaseUrl => this.UsesTunnel ? server.TunnelUrl : server.LanUrls.FirstOrDefault();

    public string PageUrl => this.BaseUrl is { } b ? ShareServer.PageUrl(share, b) : L.Sharing_NotRunning;

    /// <summary>The link with the key in it - opens straight in, no key page. What the QR code holds.</summary>
    public string LinkWithKey => this.BaseUrl is { } b ? ShareServer.PageUrl(share, b, true) : "";
    public string DavUrl => this.BaseUrl is { } b ? ShareServer.DavUrl(share, b) : "";

    /// <summary>Only reachable on this network: the tunnel is open but the share has no key.</summary>
    public bool TunnelBlocked => server.TunnelStatus == TunnelStatus.Connected && !share.HasKey;

    public string Stats => L.Sharing_ShareStatsFormat(FormatBytes.Size(share.BytesDown), share.FilesDown, FormatBytes.Size(share.BytesUp), share.FilesUp);

    [ObservableProperty]
    public partial bool ShowQr { get; set; }

    public void Refresh()
    {
        this.OnPropertyChanged(nameof(this.ExpiryText));
        this.OnPropertyChanged(nameof(this.HasExpiry));
        this.OnPropertyChanged(nameof(this.UsesTunnel));
        this.OnPropertyChanged(nameof(this.PageUrl));
        this.OnPropertyChanged(nameof(this.LinkWithKey));
        this.OnPropertyChanged(nameof(this.DavUrl));
        this.OnPropertyChanged(nameof(this.TunnelBlocked));
        this.OnPropertyChanged(nameof(this.Stats));
    }

    /// <summary>After the share's options changed.</summary>
    public void RefreshAll() => this.OnPropertyChanged(String.Empty);
}

/// <summary>One transfer - running, or among the last few finished.</summary>
public partial class TransferItemViewModel(Transfer transfer) : ObservableObject
{
    public Transfer Transfer => transfer;
    public long Id => transfer.Id;

    public string Icon => transfer.Direction == TransferDirection.Download ? "download" : "upload";
    public string Name => transfer.Name;
    public string Where => L.Sharing_TransferWhereFormat(transfer.Share.Name, transfer.ClientIp) + (transfer.IsTunneled ? " · " + L.Sharing_ViaTunnel : "");
    public bool IsRunning => transfer.State == TransferState.Running;
    public bool IsFinished => !this.IsRunning;
    public double Progress => transfer.Fraction ?? 0;
    public bool HasProgress => this.IsRunning && transfer.Fraction != null;

    public string Detail
    {
        get
        {
            var bytes = transfer.TotalBytes is { } total
                ? L.Sharing_BytesOfFormat(FormatBytes.Size(transfer.Bytes), FormatBytes.Size(total))
                : FormatBytes.Size(transfer.Bytes);

            if (transfer.State == TransferState.Running)
            {
                var parts = new List<string> { bytes, FormatBytes.Speed(transfer.BytesPerSecond) };
                if (transfer.Remaining is { } left)
                    parts.Add(L.Sharing_LeftFormat(FormatBytes.Duration(left)));
                return String.Join(" · ", parts.Where(x => x.Length > 0));
            }

            var done = $"{bytes} · {FormatBytes.Duration(transfer.Elapsed)}";
            return transfer.Error is { } error ? $"{done} · {error}" : done;
        }
    }

    public string StateText => transfer.State switch
    {
        TransferState.Running => transfer.Direction == TransferDirection.Download ? L.Sharing_StateDownloading : L.Sharing_StateUploading,
        TransferState.Completed => L.Sharing_StateDone,
        TransferState.Cancelled => L.Sharing_StateStopped,
        _ => L.Sharing_StateFailed
    };

    public Shiny.Maui.Controls.PillType StatePill => transfer.State switch
    {
        TransferState.Running => Shiny.Maui.Controls.PillType.Info,
        TransferState.Completed => Shiny.Maui.Controls.PillType.Success,
        TransferState.Cancelled => Shiny.Maui.Controls.PillType.Warning,
        _ => Shiny.Maui.Controls.PillType.Critical
    };

    public void Refresh() => this.OnPropertyChanged(String.Empty);
}

/// <summary>An address seen in the last few minutes.</summary>
public sealed record ClientItem(string Ip, string Detail, bool IsBlocked, bool IsTunneled)
{
    public bool CanBlock => !this.IsBlocked;
}

/// <summary>One line of the activity log, ready to show.</summary>
public sealed record ActivityItem(string Time, string Who, string What, bool IsProblem);

/// <summary>Sizes, speeds and durations the way the jobs pane writes them.</summary>
public static class FormatBytes
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "" : Size((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
            return $"{Math.Max(0, (int)Math.Round(span.TotalSeconds))}s";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes}m {span.Seconds:00}s";
        return $"{(int)span.TotalHours}h {span.Minutes:00}m";
    }
}
