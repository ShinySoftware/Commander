using ShinyCommander.Core.Platform;

namespace ShinyCommander.ViewModels;

public enum PanelSide
{
    Left,
    Right
}

/// <summary>One side of the dual pane: a set of folder tabs, a drive bar and an optional folder tree.</summary>
public partial class PanelViewModel : ObservableObject
{
    readonly VirtualFileSystem vfs;
    readonly AppSettings settings;

    public PanelViewModel(PanelSide side, VirtualFileSystem vfs, AppSettings settings)
    {
        this.Side = side;
        this.vfs = vfs;
        this.settings = settings;
        this.RefreshDrives();
    }

    public PanelSide Side { get; }

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
        var tab = new PanelTabViewModel(this.vfs, this.settings, VirtualFileSystem.Normalize(path));
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
    void ToggleTree() => this.TreeToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The folder tree is window-wide; the panel's button asks the page to toggle it.</summary>
    public event EventHandler? TreeToggleRequested;
}
