namespace ShinyCommander.ViewModels;

[ShellMap<SettingsPage>("Settings")]
public partial class SettingsViewModel(AppSettings settings, ColumnSet columns, INavigator navigator) : ObservableObject
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

    public string AppVersion => AppInfo.Current.VersionString;
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
    Task Close() => navigator.GoBack();
}
