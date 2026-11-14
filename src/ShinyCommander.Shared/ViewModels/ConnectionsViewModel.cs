using Shiny.Mediator;
using ShinyCommander.Core.Platform;

namespace ShinyCommander.ViewModels;

/// <summary>A saved connection as the Connections page lists it.</summary>
public sealed record ConnectionRow(RemoteConnection Connection, bool IsConnected)
{
    public string Name => this.Connection.Name;
    public string Address => this.Connection.StartUri;
    public string ProtocolText => PanelTabViewModel.RemoteProtocolText(this.Connection.Protocol);
    public string Icon => this.Connection.IsObjectStorage ? "cloud" : "wifi";
    public bool IsDisconnected => !this.IsConnected;
}

/// <summary>
/// ⌘K / Ctrl+K: the saved servers and storage accounts (open, edit, disconnect, delete) beside the file servers
/// announcing themselves on the network - an SMB share mounts through the OS, anything else becomes a new connection.
/// </summary>
[ShellMap<ConnectionsPage>("Connections")]
public partial class ConnectionsViewModel(
    RemoteConnections connections,
    NetworkBrowser browser,
    INavigator navigator,
    IAppDialogs dialogs,
    IMediator mediator
) : ObservableObject, IPageLifecycleAware
{
    CancellationTokenSource? scanCts;

    [ObservableProperty]
    public partial IReadOnlyList<ConnectionRow> Saved { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<NetworkServer> Nearby { get; set; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanStatus))]
    public partial string ScanStatus { get; set; } = String.Empty;

    public bool HasScanStatus => this.ScanStatus.Length > 0;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void OnAppearing()
    {
        connections.Changed += this.OnChanged;
        this.Load();
        if (this.Nearby.Count == 0 && !this.IsScanning)
            _ = this.Scan();
    }

    public void OnDisappearing()
    {
        connections.Changed -= this.OnChanged;
        this.scanCts?.Cancel();
    }

    void OnChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(this.Load);

    void Load() => this.Saved = connections.All.Select(x => new ConnectionRow(x, connections.IsConnected(x))).ToList();

    [RelayCommand]
    Task Close() => navigator.GoBack();

    [RelayCommand]
    Task New() => navigator.NavigateTo<ConnectionEditViewModel>(vm => vm.Load(null));

    [RelayCommand]
    Task Edit(ConnectionRow row) => navigator.NavigateTo<ConnectionEditViewModel>(vm => vm.Load(row.Connection));

    /// <summary>Opens the connection in the active panel (connecting, and asking for a password if none is saved).</summary>
    [RelayCommand]
    async Task Open(ConnectionRow row)
    {
        await navigator.GoBack();
        await mediator.Publish(new GoToPathEvent(row.Connection.StartUri));
    }

    [RelayCommand]
    async Task Disconnect(ConnectionRow row)
    {
        await connections.Disconnect(row.Connection);
        await dialogs.Toast(L.Connections_DisconnectedFormat(row.Name));
    }

    [RelayCommand]
    async Task Delete(ConnectionRow row)
    {
        var ok = await dialogs.Confirm(L.Connections_DeleteTitle, L.Connections_DeleteMessageFormat(row.Name), L.Common_Delete);
        if (ok)
            await connections.Remove(row.Connection);
    }

    [RelayCommand]
    async Task Scan()
    {
        this.scanCts?.Cancel();
        var cts = this.scanCts = new CancellationTokenSource();
        this.IsScanning = true;
        this.ScanStatus = L.Connections_Scanning;
        try
        {
            this.Nearby = await browser.ScanAsync(TimeSpan.FromSeconds(4), cts.Token);
            this.ScanStatus = this.Nearby.Count == 0 ? L.Connections_NoneFound : String.Empty;
        }
        catch (OperationCanceledException)
        {
            // left the page
        }
        catch (Exception ex)
        {
            this.ScanStatus = ex.Message;
        }
        finally
        {
            if (this.scanCts == cts)
                this.IsScanning = false;
        }
    }

    /// <summary>A server found on the network: SMB mounts through the OS, the rest become a new saved connection.</summary>
    [RelayCommand]
    Task ConnectNearby(NetworkServer server)
    {
        if (server.Kind == NetworkServiceKind.Smb)
            return this.MountShare(server.Host, null);

        var protocol = server.Kind switch
        {
            NetworkServiceKind.Sftp => RemoteProtocol.Sftp,
            NetworkServiceKind.WebDav => RemoteProtocol.WebDav,
            NetworkServiceKind.WebDavs => RemoteProtocol.WebDavs,
            _ => RemoteProtocol.Ftp
        };
        var draft = new RemoteConnection
        {
            Name = server.Name,
            Protocol = protocol,
            Host = server.Host,
            Port = server.Port,
            StartPath = String.IsNullOrWhiteSpace(server.Path) ? "/" : server.Path
        };
        return navigator.NavigateTo<ConnectionEditViewModel>(vm => vm.Load(draft with { Port = server.Port == draft.DefaultPort ? 0 : server.Port }, isNew: true));
    }

    /// <summary>"Connect to Share…" - an smb:// address typed in.</summary>
    [RelayCommand]
    async Task ConnectShare()
    {
        var input = await dialogs.Prompt(L.Connections_ShareTitle, L.Connections_ShareMessage, "smb://", L.Remote_Connect, "smb://server/share");
        if (String.IsNullOrWhiteSpace(input))
            return;

        var text = input.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "smb://" + text.TrimStart('\\', '/').Replace('\\', '/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            await dialogs.Alert(L.Connections_ShareTitle, L.Connections_ShareInvalid);
            return;
        }
        await this.MountShare(uri.Host, Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')));
    }

    async Task MountShare(string host, string? share)
    {
        // macOS offers the share list and the sign-in sheet itself; elsewhere the share is asked for here
        if (String.IsNullOrEmpty(share) && !OperatingSystem.IsMacOS())
        {
            share = await dialogs.Prompt(L.Connections_ShareTitle, L.Connections_ShareNameFormat(host), null, L.Remote_Connect);
            if (String.IsNullOrWhiteSpace(share))
                return;
        }

        this.IsBusy = true;
        try
        {
            string folder;
            try
            {
                folder = await NetworkShares.MountAsync(host, share, null, null);
            }
            catch (IOException) when (!OperatingSystem.IsMacOS())
            {
                // the signed-in user wasn't let in - ask for an account on the server
                var user = await dialogs.Prompt(L.Connections_ShareTitle, L.Connections_ShareUserFormat(host), Environment.UserName, L.Common_OK);
                if (String.IsNullOrWhiteSpace(user))
                    return;
                var password = await dialogs.PromptSecret(L.Connections_ShareTitle, L.Remote_PasswordMessageFormat($"{user}@{host}"), L.Remote_Connect);
                if (password == null)
                    return;
                folder = await NetworkShares.MountAsync(host, share, user, password);
            }

            await navigator.GoBack();
            await mediator.Publish(new GoToPathEvent(folder));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.Message != "Cancelled")
                await dialogs.Alert(L.Connections_ShareTitle, ex.Message);
        }
        finally
        {
            this.IsBusy = false;
        }
    }
}
