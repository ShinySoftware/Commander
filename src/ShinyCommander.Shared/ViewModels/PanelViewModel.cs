using ShinyCommander.Core.Platform;

namespace ShinyCommander.ViewModels;

public enum PanelSide
{
    Left,
    Right
}

public enum PanelViewMode
{
    Details,
    Thumbnails
}

/// <summary>
/// One side of the dual pane: folder tabs, a drive bar, an optional folder tree, details or thumbnail view,
/// and - while the other side has the focus - an optional quick-view preview in place of the file list.
/// </summary>
public partial class PanelViewModel : ObservableObject
{
    readonly VirtualFileSystem vfs;
    readonly AppSettings settings;

    public PanelViewModel(PanelSide side, VirtualFileSystem vfs, AppSettings settings, ColumnSet columns)
    {
        this.Side = side;
        this.vfs = vfs;
        this.settings = settings;
        this.Columns = new ColumnLayout(columns);
        this.QuickView = new QuickViewViewModel(vfs);
        this.Tree = new FolderTreeViewModel(settings);
        this.ShowTree = side == PanelSide.Left ? settings.LeftShowTree : settings.RightShowTree;
        var savedWidth = side == PanelSide.Left ? settings.LeftTreeWidth : settings.RightTreeWidth;
        this.TreeWidth = savedWidth >= 120 ? savedWidth : 240;
        this.ViewMode = Enum.TryParse<PanelViewMode>(side == PanelSide.Left ? settings.LeftViewMode : settings.RightViewMode, out var mode) ? mode : PanelViewMode.Details;
        this.RefreshDrives();
    }

    public PanelSide Side { get; }
    /// <summary>This panel's columns - the user's choice, narrowed to what fits the panel.</summary>
    public ColumnLayout Columns { get; }
    public QuickViewViewModel QuickView { get; }

    /// <summary>This panel's folder tree (rows for a virtualized list).</summary>
    public FolderTreeViewModel Tree { get; }

    /// <summary>Width of this panel's tree - dragged with the splitter, remembered per side.</summary>
    [ObservableProperty]
    public partial double TreeWidth { get; set; }

    public void SaveTreeWidth()
    {
        if (this.Side == PanelSide.Left)
            this.settings.LeftTreeWidth = (int)this.TreeWidth;
        else
            this.settings.RightTreeWidth = (int)this.TreeWidth;
    }

    /// <summary>The folder the panel's tree has highlighted (it follows the panel's location).</summary>
    public string? TreePath { get; set; }

    /// <summary>This panel's own folder tree (Primary+F8 toggles the active panel's).</summary>
    [ObservableProperty]
    public partial bool ShowTree { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThumbnails), nameof(IsDetails), nameof(ShowDetailsList), nameof(ShowThumbnailList))]
    public partial PanelViewMode ViewMode { get; set; }

    public bool IsThumbnails => this.ViewMode == PanelViewMode.Thumbnails;
    public bool IsDetails => this.ViewMode == PanelViewMode.Details;

    /// <summary>Items per row in thumbnail view - the view sets it from its width; Up/Down step by it.</summary>
    public int ThumbnailSpan { get; set; } = 4;

    public double ThumbnailSize => Math.Clamp(this.settings.ThumbnailSize <= 0 ? 112 : this.settings.ThumbnailSize, 64, 256);

    /// <summary>Showing a preview of the other panel's cursor instead of its own files.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailsList), nameof(ShowThumbnailList))]
    public partial bool IsQuickView { get; set; }

    public bool ShowDetailsList => this.IsDetails && !this.IsQuickView;
    public bool ShowThumbnailList => this.IsThumbnails && !this.IsQuickView;

    partial void OnShowTreeChanged(bool value)
    {
        if (this.Side == PanelSide.Left)
            this.settings.LeftShowTree = value;
        else
            this.settings.RightShowTree = value;
    }

    partial void OnViewModeChanged(PanelViewMode value)
    {
        if (this.Side == PanelSide.Left)
            this.settings.LeftViewMode = value.ToString();
        else
            this.settings.RightViewMode = value.ToString();
        this.RequestScroll();
    }

    partial void OnIsQuickViewChanged(bool value)
    {
        if (!value)
        {
            this.QuickView.Clear();
            this.RequestScroll();
        }
    }

    /// <summary>Raised when the UI should scroll the cursor into view.</summary>
    public event EventHandler<int>? ScrollRequested;

    /// <summary>Raised when the panel wants keyboard focus to return to the main page (e.g. after quick filter).</summary>
    public event EventHandler? Activated;

    public ObservableCollection<PanelTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial PanelTabViewModel? ActiveTab { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool ShowQuickFilter { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<DriveEntry> Drives { get; set; } = [];

    [ObservableProperty]
    public partial DriveEntry? CurrentDrive { get; set; }

    [ObservableProperty]
    public partial string FreeSpaceText { get; set; } = String.Empty;

    [ObservableProperty]
    public partial double DriveUsage { get; set; }

    [ObservableProperty]
    public partial string PathText { get; set; } = String.Empty;

    public bool HasMultipleTabs => this.Tabs.Count > 1;

    public string Path => this.ActiveTab?.Path ?? String.Empty;

    partial void OnIsActiveChanged(bool value)
    {
        this.ActiveTab?.SetPanelActive(value);
        if (value)
            this.Activated?.Invoke(this, EventArgs.Empty);
    }

    partial void OnActiveTabChanged(PanelTabViewModel? oldValue, PanelTabViewModel? newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= this.OnTabPropertyChanged;
            oldValue.CursorMoved -= this.OnTabCursorMoved;
            oldValue.SetPanelActive(false);
            oldValue.IsSelected = false;
        }
        if (newValue != null)
        {
            newValue.PropertyChanged += this.OnTabPropertyChanged;
            newValue.CursorMoved += this.OnTabCursorMoved;
            newValue.SetPanelActive(this.IsActive);
            newValue.IsSelected = true;
            this.UpdateLocation();
            this.ScrollRequested?.Invoke(this, newValue.CursorIndex);
        }
        this.OnPropertyChanged(nameof(this.Path));
    }

    void OnTabCursorMoved(object? sender, EventArgs e)
    {
        if (this.ActiveTab is { } tab)
            this.ScrollRequested?.Invoke(this, tab.CursorIndex);
    }

    void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelTabViewModel.Path))
        {
            this.UpdateLocation();
            this.OnPropertyChanged(nameof(this.Path));
        }
    }

    void UpdateLocation()
    {
        var path = this.ActiveTab?.Path ?? String.Empty;
        this.PathText = path;
        string physical;
        try
        {
            var loc = this.vfs.Resolve(path);
            physical = loc.ArchiveFile ?? loc.Path;
        }
        catch
        {
            physical = path;
        }

        var drive = DriveService.FindForPath(this.Drives, physical);
        if (drive != null && !ReferenceEquals(drive, this.CurrentDrive))
            this.CurrentDrive = drive;

        if (drive != null)
        {
            // re-query free space - it changes as we copy
            var fresh = DriveService.FromPath(drive.Name, drive.RootPath, drive.Label);
            this.FreeSpaceText = $"{SizeFormatter.Friendly(fresh.FreeSpace)} free of {SizeFormatter.Friendly(fresh.TotalSize)}";
            this.DriveUsage = fresh.UsedRatio;
        }
    }

    public void RefreshDrives()
    {
        this.Drives = DriveService.GetDrives();
        this.UpdateLocation();
    }

    public async Task<PanelTabViewModel> AddTabAsync(string path, bool activate = true, string? focusName = null)
    {
        var tab = new PanelTabViewModel(this.vfs, this.settings, this.Columns, VirtualFileSystem.Normalize(path));
        this.Tabs.Add(tab);
        this.OnPropertyChanged(nameof(this.HasMultipleTabs));
        if (activate)
            this.ActiveTab = tab;

        if (!await tab.LoadAsync(focusName))
        {
            // folder vanished since last run - fall back to home
            await tab.NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), addHistory: false);
        }
        return tab;
    }

    public void CloseTab(PanelTabViewModel? tab)
    {
        tab ??= this.ActiveTab;
        if (tab == null || this.Tabs.Count <= 1)
            return;

        var index = this.Tabs.IndexOf(tab);
        this.Tabs.Remove(tab);
        tab.Dispose();
        if (this.ActiveTab == tab)
            this.ActiveTab = this.Tabs[Math.Clamp(index, 0, this.Tabs.Count - 1)];
        this.OnPropertyChanged(nameof(this.HasMultipleTabs));
    }

    public void NextTab(int delta)
    {
        if (this.Tabs.Count < 2 || this.ActiveTab == null)
            return;

        var index = (this.Tabs.IndexOf(this.ActiveTab) + delta + this.Tabs.Count) % this.Tabs.Count;
        this.ActiveTab = this.Tabs[index];
    }

    public void RequestScroll() => this.ScrollRequested?.Invoke(this, this.ActiveTab?.CursorIndex ?? 0);

    public Task NavigateAsync(string path, string? focusName = null)
        => this.ActiveTab?.NavigateAsync(path, focusName) ?? Task.CompletedTask;

    [RelayCommand]
    Task SelectDrive(DriveEntry? drive) => drive == null ? Task.CompletedTask : this.NavigateAsync(drive.RootPath);

    [RelayCommand]
    void SelectTab(PanelTabViewModel tab) => this.ActiveTab = tab;

    [RelayCommand]
    void Sort(string column)
    {
        if (Enum.TryParse<SortColumn>(column, out var c))
            this.ActiveTab?.SortBy(c);
    }

    [RelayCommand]
    Task GoUp() => this.ActiveTab?.GoUpAsync() ?? Task.CompletedTask;

    [RelayCommand]
    Task GoRoot() => this.ActiveTab?.GoRootAsync() ?? Task.CompletedTask;

    [RelayCommand]
    Task GoHome() => this.NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [RelayCommand]
    Task GoBack() => this.ActiveTab?.GoBackAsync() ?? Task.CompletedTask;

    [RelayCommand]
    Task GoForward() => this.ActiveTab?.GoForwardAsync() ?? Task.CompletedTask;

    [RelayCommand]
    Task NavigatePathText() => String.IsNullOrWhiteSpace(this.PathText) ? Task.CompletedTask : this.NavigateAsync(this.PathText);

    [RelayCommand]
    void ToggleTree() => this.ShowTree = !this.ShowTree;

    [RelayCommand]
    void ToggleViewMode() => this.ViewMode = this.IsThumbnails ? PanelViewMode.Details : PanelViewMode.Thumbnails;
}
