namespace ShinyCommander.ViewModels;

[ShellMap<SettingsPage>("Settings")]
public partial class SettingsViewModel(AppSettings settings, ColumnSet columns, UpdateService updates, INavigator navigator, IAppDialogs dialogs) : ObservableObject
{
    public bool ShowExtColumn { get => columns.ShowExtension; set { columns.Set(PanelColumn.Extension, value); this.OnPropertyChanged(); } }
    public bool ShowSizeColumn { get => columns.ShowSize; set { columns.Set(PanelColumn.Size, value); this.OnPropertyChanged(); } }
    public bool ShowDateColumn { get => columns.ShowModified; set { columns.Set(PanelColumn.Modified, value); this.OnPropertyChanged(); } }
    public bool ShowCreatedColumn { get => columns.ShowCreated; set { columns.Set(PanelColumn.Created, value); this.OnPropertyChanged(); } }
    public bool ShowAttrColumn { get => columns.ShowAttributes; set { columns.Set(PanelColumn.Attributes, value); this.OnPropertyChanged(); } }

    public bool ShowHidden { get => settings.ShowHidden; set { settings.ShowHidden = value; this.OnPropertyChanged(); } }
    public bool ConfirmDelete { get => settings.ConfirmDelete; set { settings.ConfirmDelete = value; this.OnPropertyChanged(); } }
    public bool UseTrash { get => settings.UseTrash; set { settings.UseTrash = value; this.OnPropertyChanged(); } }
    public bool FriendlySizes { get => settings.FriendlySizes; set { settings.FriendlySizes = value; this.OnPropertyChanged(); } }
    public bool MotionFileIcons { get => settings.MotionFileIcons; set { settings.MotionFileIcons = value; this.OnPropertyChanged(); } }
    public bool ExtractToFolderPerArchive { get => settings.ExtractToFolderPerArchive; set { settings.ExtractToFolderPerArchive = value; this.OnPropertyChanged(); } }
    public string ExternalEditor { get => settings.ExternalEditor ?? String.Empty; set { settings.ExternalEditor = value; this.OnPropertyChanged(); } }

    public string Theme
    {
        get => settings.Theme ?? "System";
        set
        {
            if (value == null)
                return;
            settings.Theme = value;
            ApplyTheme(value);
            this.OnPropertyChanged();
        }
    }

    /// <summary>"System" or a culture name. The pages take their text when they're built, so it applies on the next launch.</summary>
    public string Language
    {
        get => settings.Language ?? "System";
        set
        {
            if (value == null || value == this.Language)
                return;
            settings.Language = value;
            this.OnPropertyChanged();
            _ = dialogs.Toast(L.Settings_LanguageRestart);
        }
    }

    public string PanelFontSize
    {
        get => settings.PanelFontSize.ToString();
        set
        {
            if (Int32.TryParse(value, out var v))
                settings.PanelFontSize = v;
            this.OnPropertyChanged();
        }
    }

    public string ViewerFontSize
    {
        get => settings.ViewerFontSize.ToString();
        set
        {
            if (Int32.TryParse(value, out var v))
                settings.ViewerFontSize = v;
            this.OnPropertyChanged();
        }
    }

    public string AppVersion => updates.CurrentVersion;
    public bool IsUpdateSupported => updates.IsSupported;

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = updates.Available is { } release ? L.Settings_UpdateAvailableFormat(release.DisplayVersion) : String.Empty;

    public string UpdateButtonText => updates.Available is { } release ? L.Settings_InstallUpdateFormat(release.DisplayVersion) : L.Settings_CheckForUpdates;

    /// <summary>Checks GitHub - or, once a newer release is known, opens its notes with Install.</summary>
    [RelayCommand]
    async Task Update()
    {
        if (updates.Available is { } known)
        {
            updates.ShowRelease?.Invoke(known);
            return;
        }

        this.UpdateStatus = L.Settings_Checking;
        var reached = await updates.CheckAsync();
        this.UpdateStatus = !reached
            ? L.Settings_UpdateUnreachable
            : updates.Available is { } release
                ? L.Settings_UpdateAvailableFormat(release.DisplayVersion)
                : L.Settings_UpToDate;
        this.OnPropertyChanged(nameof(this.UpdateButtonText));

        // "Don't show again" only stops the automatic window - asking by hand always shows it
        if (updates.Available is { } found)
            updates.ShowRelease?.Invoke(found);
    }
    public string RuntimeInfo => $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} • {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

    public static void ApplyTheme(string? theme)
    {
        if (Application.Current == null)
            return;

        Application.Current.UserAppTheme = theme switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    [RelayCommand]
    Task CustomizeToolbar() => navigator.NavigateTo<CustomizeToolbarViewModel>();

    [RelayCommand]
    Task Close() => navigator.GoBack();
}
