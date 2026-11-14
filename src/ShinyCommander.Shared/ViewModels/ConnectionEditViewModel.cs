namespace ShinyCommander.ViewModels;

/// <summary>Adding or changing a saved connection. The password goes to the keychain, never to settings.</summary>
[ShellMap<ConnectionEditPage>("ConnectionEdit")]
public partial class ConnectionEditViewModel(RemoteConnections connections, INavigator navigator, IAppDialogs dialogs) : ObservableObject
{
    static readonly RemoteProtocol[] ProtocolOrder =
    [
        RemoteProtocol.Sftp,
        RemoteProtocol.Ftp,
        RemoteProtocol.Ftps,
        RemoteProtocol.WebDav,
        RemoteProtocol.WebDavs,
        RemoteProtocol.S3,
        RemoteProtocol.AzureBlob
    ];

    string id = Guid.NewGuid().ToString("N");
    bool hasSavedSecret;

    public IReadOnlyList<string> Protocols { get; } = ProtocolOrder.Select(PanelTabViewModel.RemoteProtocolText).ToList();

    public string Title => this.IsNew ? L.ConnectionEdit_NewTitle : L.ConnectionEdit_EditTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CanForget))]
    public partial bool IsNew { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Protocol), nameof(IsObjectStorage), nameof(IsS3), nameof(IsSftp), nameof(IsServer), nameof(ShowHost), nameof(ShowUser), nameof(HostLabel), nameof(UserLabel), nameof(SecretLabel), nameof(UserPlaceholder), nameof(HostPlaceholder), nameof(PortPlaceholder), nameof(Preview), nameof(HasPreview))]
    public partial int ProtocolIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasPreview))]
    public partial string Name { get; set; } = String.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasPreview))]
    public partial string Host { get; set; } = String.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasPreview))]
    public partial string Port { get; set; } = String.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasPreview))]
    public partial string UserName { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string Secret { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string KeyFile { get; set; } = String.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasPreview))]
    public partial string StartPath { get; set; } = "/";

    [ObservableProperty]
    public partial string Region { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string Endpoint { get; set; } = String.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public RemoteProtocol Protocol => ProtocolOrder[Math.Clamp(this.ProtocolIndex, 0, ProtocolOrder.Length - 1)];
    public bool IsObjectStorage => this.Protocol is RemoteProtocol.S3 or RemoteProtocol.AzureBlob;
    public bool IsServer => !this.IsObjectStorage;
    public bool IsS3 => this.Protocol == RemoteProtocol.S3;
    public bool IsSftp => this.Protocol == RemoteProtocol.Sftp;
    public bool ShowHost => !this.IsS3;
    public bool ShowUser => this.Protocol != RemoteProtocol.AzureBlob;
    public bool CanForget => !this.IsNew && this.hasSavedSecret;

    public string HostLabel => this.Protocol == RemoteProtocol.AzureBlob ? L.ConnectionEdit_Account : L.ConnectionEdit_Host;
    public string HostPlaceholder => this.Protocol == RemoteProtocol.AzureBlob ? "mystorageaccount" : "server.example.com";
    public string UserLabel => this.IsS3 ? L.ConnectionEdit_AccessKey : L.ConnectionEdit_User;
    public string UserPlaceholder => this.Protocol is RemoteProtocol.Ftp or RemoteProtocol.Ftps or RemoteProtocol.WebDav or RemoteProtocol.WebDavs || this.IsS3
        ? L.ConnectionEdit_Anonymous
        : String.Empty;
    public string PortPlaceholder => new RemoteConnection { Name = "x", Protocol = this.Protocol }.DefaultPort.ToString();

    public string SecretLabel => this.Protocol switch
    {
        RemoteProtocol.S3 => L.ConnectionEdit_SecretKey,
        RemoteProtocol.AzureBlob => L.ConnectionEdit_AccountKey,
        RemoteProtocol.Sftp when this.KeyFile.Length > 0 => L.ConnectionEdit_Passphrase,
        _ => L.ConnectionEdit_Password
    };

    /// <summary>What the panel path will look like - once there's somewhere to go.</summary>
    public string Preview
    {
        get
        {
            var c = this.Build();
            return c.Name.Length == 0 || (c.Host.Length == 0 && c.Protocol != RemoteProtocol.S3) ? String.Empty : c.StartUri;
        }
    }

    public bool HasPreview => this.Preview.Length > 0;

    public string SecretPlaceholder => this.hasSavedSecret ? L.ConnectionEdit_SecretSaved : String.Empty;

    partial void OnKeyFileChanged(string value) => this.OnPropertyChanged(nameof(this.SecretLabel));

    /// <summary>Fills the form: null for a blank one, a saved connection to change it, or a draft (found on the network).</summary>
    public void Load(RemoteConnection? connection, bool isNew = false)
    {
        this.IsNew = connection == null || isNew;
        var c = connection ?? new RemoteConnection { Name = String.Empty };
        this.id = c.Id;
        this.hasSavedSecret = !this.IsNew && connections.HasSecret(c);
        this.ProtocolIndex = Math.Max(0, Array.IndexOf(ProtocolOrder, c.Protocol));
        this.Name = c.Name;
        this.Host = c.Host;
        this.Port = c.Port == 0 ? String.Empty : c.Port.ToString();
        this.UserName = c.UserName;
        this.Secret = String.Empty;
        this.KeyFile = c.KeyFile ?? String.Empty;
        this.StartPath = c.StartPath;
        this.Region = c.Region ?? String.Empty;
        this.Endpoint = c.Endpoint ?? String.Empty;
        this.OnPropertyChanged(nameof(this.SecretPlaceholder));
        this.OnPropertyChanged(nameof(this.CanForget));
    }

    RemoteConnection Build()
    {
        var name = this.Name.Trim();
        if (name.Length == 0)
            name = this.Host.Trim();
        return new RemoteConnection
        {
            Id = this.id,
            Name = name,
            Protocol = this.Protocol,
            Host = this.Host.Trim(),
            Port = Int32.TryParse(this.Port, out var port) && port > 0 && !this.IsObjectStorage ? port : 0,
            UserName = this.Protocol == RemoteProtocol.AzureBlob ? String.Empty : this.UserName.Trim(),
            KeyFile = this.IsSftp && !String.IsNullOrWhiteSpace(this.KeyFile) ? this.KeyFile.Trim() : null,
            StartPath = "/" + this.StartPath.Trim().Trim('/'),
            Region = this.IsS3 && !String.IsNullOrWhiteSpace(this.Region) ? this.Region.Trim() : null,
            Endpoint = this.IsObjectStorage && !String.IsNullOrWhiteSpace(this.Endpoint) ? this.Endpoint.Trim() : null
        };
    }

    string? Validate(RemoteConnection c)
    {
        if (c.Name.Length == 0)
            return L.ConnectionEdit_NeedName;
        if (c.Protocol != RemoteProtocol.S3 && c.Host.Length == 0)
            return this.Protocol == RemoteProtocol.AzureBlob ? L.ConnectionEdit_NeedAccount : L.ConnectionEdit_NeedHost;
        if (c.KeyFile != null && !File.Exists(c.KeyFile.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + c.KeyFile[1..] : c.KeyFile))
            return L.ConnectionEdit_KeyFileMissing;
        if (connections.Clash(c) is { } other)
            return L.ConnectionEdit_ClashFormat(other.Name);
        return null;
    }

    [RelayCommand]
    Task Close() => navigator.GoBack();

    [RelayCommand]
    async Task Save()
    {
        var c = this.Build();
        if (this.Validate(c) is { } problem)
        {
            await dialogs.Alert(this.Title, problem);
            return;
        }
        // an empty box keeps the saved secret - "Forget password" is how one is removed
        connections.Save(c, this.Secret.Length > 0 ? this.Secret : null);
        await navigator.GoBack();
    }

    [RelayCommand]
    async Task Test()
    {
        var c = this.Build();
        if (this.Validate(c) is { } problem)
        {
            await dialogs.Alert(this.Title, problem);
            return;
        }

        this.IsBusy = true;
        try
        {
            var secret = this.Secret.Length > 0 ? this.Secret : connections.Sessions.Secrets.Get(c.Id);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await connections.Sessions.TestAsync(c, secret, cts.Token);
            await dialogs.Toast(L.ConnectionEdit_TestOkFormat(c.Name), ToastKind.Success);
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.ConnectionEdit_TestFailed, ex is OperationCanceledException ? L.ConnectionEdit_TestTimeout : ex.Message);
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    [RelayCommand]
    async Task ForgetSecret()
    {
        connections.ForgetSecret(this.Build());
        this.hasSavedSecret = false;
        this.OnPropertyChanged(nameof(this.SecretPlaceholder));
        this.OnPropertyChanged(nameof(this.CanForget));
        await dialogs.Toast(L.ConnectionEdit_SecretForgotten);
    }
}
