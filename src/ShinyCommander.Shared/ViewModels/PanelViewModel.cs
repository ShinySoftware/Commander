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
    readonly FileColumns fileColumns;
    readonly FolderSetups folderSetups;
    bool applyingSetup;

    public PanelViewModel(PanelSide side, VirtualFileSystem vfs, AppSettings settings, ColumnSet columns, FileColumns fileColumns, FolderSetups folderSetups, PaneToolbar toolbar)
    {
        this.Side = side;
        this.vfs = vfs;
        this.settings = settings;
        this.fileColumns = fileColumns;
        this.folderSetups = folderSetups;
        this.Columns = new ColumnLayout(columns);
        this.Columns.PropertyChanged += this.OnColumnsChanged;
        this.Tree = new FolderTreeViewModel(settings);
        this.ShowTree = side == PanelSide.Left ? settings.LeftShowTree : settings.RightShowTree;
        var savedWidth = side == PanelSide.Left ? settings.LeftTreeWidth : settings.RightTreeWidth;
        this.TreeWidth = savedWidth >= 120 ? savedWidth : 240;
        this.ViewMode = this.GlobalViewMode;
        this.RefreshDrives();

        this.ToolbarItems = PaneToolbarItem.For(side, toolbar.Items);
        toolbar.Changed += (_, _) => this.ToolbarItems = PaneToolbarItem.For(side, toolbar.Items);
    }

    public PanelSide Side { get; }

    /// <summary>The buttons beside the path (PaneToolbar).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PaneToolbarItem> ToolbarItems { get; set; }
    /// <summary>This panel's columns - the user's choice, narrowed to what fits the panel.</summary>
    public ColumnLayout Columns { get; }

    /// <summary>The headers of the custom columns that fit, right of the built-in ones.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ColumnHeader> Headers { get; set; } = [];

    /// <summary>A Folder Setup menu asked for from this panel's badge.</summary>
    public event EventHandler? FolderSetupRequested;

    [RelayCommand]
    void RequestFolderSetup() => this.FolderSetupRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The active tab's folder setup - its built-in columns and view mode - or the global ones.</summary>
    void ApplyFolderSetup()
    {
        var setup = this.ActiveTab?.Setup;
        this.Columns.Override = setup?.Columns.Select(x => Enum.TryParse<PanelColumn>(x, out var c) ? (PanelColumn?)c : null).OfType<PanelColumn>().ToHashSet();
        // a setup's Columns is its order too: the built-in ones and (saved from the Columns screen) the custom ones
        this.Columns.Order = setup?.Columns;
        this.applyingSetup = true;
        try
        {
            this.ViewMode = setup != null && Enum.TryParse<PanelViewMode>(setup.ViewMode, out var mode) ? mode : this.GlobalViewMode;
        }
        finally
        {
            this.applyingSetup = false;
        }
    }

    // the active tab's folder decides the custom columns
    void UpdateCustomColumns()
    {
        this.Columns.Custom = this.ActiveTab?.CustomColumns ?? [];
        this.UpdateHeaders();
    }

    void OnColumnsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => this.UpdateHeaders();

    // every detail column's header, in the columns' order - built-in and custom alike
    void UpdateHeaders()
    {
        var tab = this.ActiveTab;
        string Arrow(bool sorted) => sorted ? (tab!.SortDescending ? " ▼" : " ▲") : String.Empty;
        var headers = this.Columns.Shown
            .Select(x => new ColumnHeader(
                x.Key,
                x.BuiltIn is { } c
                    ? ColumnSet.Header(c) + Arrow(tab != null && tab.SortColumn.ToString() == c.ToString())
                    : x.Custom!.Title + Arrow(tab is { SortColumn: SortColumn.Custom } && tab.SortKey == x.Key),
                x.Width,
                x.AlignRight,
                x.Custom != null,
                this.SortCommand
            ))
            .ToList();
        // the layout says it changed on every resize - only rebuild the header labels when they're different
        if (!headers.SequenceEqual(this.Headers))
            this.Headers = headers;
    }

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
    [NotifyPropertyChangedFor(nameof(IsThumbnails), nameof(IsDetails))]
    public partial PanelViewMode ViewMode { get; set; }

    public bool IsThumbnails => this.ViewMode == PanelViewMode.Thumbnails;
    public bool IsDetails => this.ViewMode == PanelViewMode.Details;

    /// <summary>Items per row in thumbnail view - the view sets it from its width; Up/Down step by it.</summary>
    public int ThumbnailSpan { get; set; } = 4;

    public double ThumbnailSize => Math.Clamp(this.settings.ThumbnailSize <= 0 ? 112 : this.settings.ThumbnailSize, 64, 256);

    partial void OnShowTreeChanged(bool value)
    {
        if (this.Side == PanelSide.Left)
            this.settings.LeftShowTree = value;
        else
            this.settings.RightShowTree = value;
    }

    // the panel's own view mode, for folders without a setup
    PanelViewMode GlobalViewMode => Enum.TryParse<PanelViewMode>(this.Side == PanelSide.Left ? this.settings.LeftViewMode : this.settings.RightViewMode, out var mode) ? mode : PanelViewMode.Details;

    partial void OnViewModeChanged(PanelViewMode value)
    {
        if (this.applyingSetup)
        {
            this.RequestScroll();
            return;
        }
        // in a folder with a setup the change is the setup's
        if (this.ActiveTab?.Setup is { } setup)
        {
            this.folderSetups.Save(setup with { ViewMode = value.ToString() });
            this.RequestScroll();
            return;
        }
        if (this.Side == PanelSide.Left)
            this.settings.LeftViewMode = value.ToString();
        else
            this.settings.RightViewMode = value.ToString();
        this.RequestScroll();
    }

    /// <summary>Raised when the UI should scroll the cursor into view.</summary>
    public event EventHandler<int>? ScrollRequested;

    /// <summary>Asks the view to edit a row's, tile's or tab's name in place; it sets <see cref="InlineEdit.IsShown"/> when it can.</summary>
    public event EventHandler<InlineEdit>? InlineEditRequested;

    public bool BeginInlineEdit(InlineEdit edit)
    {
        this.InlineEditRequested?.Invoke(this, edit);
        return edit.IsShown;
    }

    /// <summary>Raised when the panel wants keyboard focus to return to the main page (e.g. after quick filter).</summary>
    public event EventHandler? Activated;

    public ObservableCollection<PanelTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial PanelTabViewModel? ActiveTab { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Files dragged over the panel would drop into its folder - outlines it.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    [ObservableProperty]
    public partial bool ShowQuickFilter { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<DriveEntry> Drives { get; set; } = [];

    /// <summary>What the drive bar shows: volumes only - the places (Home, Desktop...) are tabs.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DriveEntry> Volumes { get; set; } = [];

    [ObservableProperty]
    public partial DriveEntry? CurrentDrive { get; set; }

    [ObservableProperty]
    public partial string FreeSpaceText { get; set; } = String.Empty;

    [ObservableProperty]
    public partial double DriveUsage { get; set; }

    [ObservableProperty]
    public partial string PathText { get; set; } = String.Empty;

    /// <summary>The breadcrumb trail shown in place of the path box while it isn't being edited.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PathCrumb> PathCrumbs { get; set; } = [];

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
            this.UpdateCustomColumns();
            this.ApplyFolderSetup();
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
        else if (e.PropertyName is nameof(PanelTabViewModel.SortColumn) or nameof(PanelTabViewModel.SortDescending) or nameof(PanelTabViewModel.SortKey))
        {
            // each tab's sort is saved with it
            this.UpdateHeaders();
            this.TabsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(PanelTabViewModel.CustomColumns))
        {
            this.UpdateCustomColumns();
        }
        else if (e.PropertyName == nameof(PanelTabViewModel.Setup))
        {
            this.ApplyFolderSetup();
        }
    }

    void UpdateLocation()
    {
        var path = this.ActiveTab?.Path ?? String.Empty;
        this.PathText = path;
        this.PathCrumbs = Core.Util.PathCrumbs.Split(path);
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

        if (drive is { Kind: DriveKind.Remote })
        {
            // servers don't say how full they are
            this.FreeSpaceText = drive.Label;
            this.DriveUsage = 0;
        }
        else if (drive != null)
        {
            // re-query free space - it changes as we copy
            var fresh = DriveService.FromPath(drive.Name, drive.RootPath, drive.Label);
            this.FreeSpaceText = L.Panel_FreeSpaceFormat(SizeFormatter.Friendly(fresh.FreeSpace), SizeFormatter.Friendly(fresh.TotalSize));
            this.DriveUsage = fresh.UsedRatio;
        }
    }

    public void RefreshDrives()
    {
        // saved servers and storage accounts go after the volumes and network shares
        var remote = this.vfs.Remote.Connections.Select(x => new DriveEntry(
            x.Name,
            x.Root + "/",
            x.Root,
            0,
            0,
            DriveType.Network,
            DriveKind.Remote,
            x.StartUri
        ));
        this.Volumes = [.. DriveService.GetVolumes(), .. remote];
        this.Drives = [.. DriveService.GetPlaces(), .. this.Volumes];
        this.UpdateLocation();
    }

    /// <summary>Tabs were opened, closed or locked/unlocked - the set worth saving changed.</summary>
    public event EventHandler? TabsChanged;

    /// <param name="index">Where the tab goes; the end when null.</param>
    public async Task<PanelTabViewModel> AddTabAsync(string path, bool activate = true, string? focusName = null, bool locked = false, int? index = null)
    {
        var tab = new PanelTabViewModel(this.vfs, this.settings, this.Columns, this.fileColumns, this.folderSetups, VirtualFileSystem.Normalize(path));
        tab.OpenInNewTabRequested += this.OnOpenInNewTabRequested;
        // nothing goes in front of the first tab
        this.Tabs.Insert(Math.Clamp(index ?? this.Tabs.Count, Math.Min(1, this.Tabs.Count), this.Tabs.Count), tab);
        this.UpdateFirstTab();
        this.OnPropertyChanged(nameof(this.HasMultipleTabs));
        if (activate)
            this.ActiveTab = tab;

        if (!await tab.LoadAsync(focusName))
        {
            // folder vanished since last run - fall back to home
            await tab.NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), addHistory: false);
        }
        // locked once it's where it belongs: the fallback above has to be able to move it. The first tab never is.
        tab.IsLocked = locked && !tab.IsFirst;
        this.TabsChanged?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    // a locked tab stays put - where it was asked to go opens next to it
    async void OnOpenInNewTabRequested(object? sender, (string Path, string? FocusName) e)
    {
        if (sender is PanelTabViewModel tab)
            await this.AddTabAsync(e.Path, focusName: e.FocusName, index: this.Tabs.IndexOf(tab) + 1);
    }

    public void SetTabLocked(PanelTabViewModel tab, bool locked)
    {
        if (!tab.IsFirst)
            this.EditTab(tab, x => x.IsLocked = locked);
    }

    // the first tab stays open and goes anywhere: it can't be closed, so it's never locked either
    void UpdateFirstTab()
    {
        for (var i = 0; i < this.Tabs.Count; i++)
            this.Tabs[i].IsFirst = i == 0;

        if (this.Tabs.Count > 0)
            this.Tabs[0].IsLocked = false;
    }

    /// <summary>Changes something saved about a tab (lock, name, colour) and has it saved.</summary>
    public void EditTab(PanelTabViewModel tab, Action<PanelTabViewModel> edit)
    {
        edit(tab);
        this.TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tabs can be turned off in Settings - the row goes, the tabs are kept for when they come back.</summary>
    public bool ShowTabs => !this.settings.HideTabs;

    public async Task RefreshShowTabsAsync()
    {
        this.OnPropertyChanged(nameof(this.ShowTabs));
        if (!this.ShowTabs)
            return;

        // locked tabs that wandered while tabs were off go home - where they'd got to opens next to them
        foreach (var tab in this.Tabs.Where(x => x.IsLocked).ToList())
        {
            var here = tab.Path;
            if (await tab.ReturnToLockedPathAsync())
                await this.AddTabAsync(here, activate: tab == this.ActiveTab, index: this.Tabs.IndexOf(tab) + 1);
        }
    }

    public void CloseTab(PanelTabViewModel? tab)
    {
        tab ??= this.ActiveTab;
        if (tab == null || !tab.CanClose || this.Tabs.Count <= 1)
            return;

        var index = this.Tabs.IndexOf(tab);
        this.Tabs.Remove(tab);
        this.UpdateFirstTab();
        tab.OpenInNewTabRequested -= this.OnOpenInNewTabRequested;
        tab.Dispose();
        if (this.ActiveTab == tab)
            this.ActiveTab = this.Tabs[Math.Clamp(index, 0, this.Tabs.Count - 1)];
        this.OnPropertyChanged(nameof(this.HasMultipleTabs));
        this.TabsChanged?.Invoke(this, EventArgs.Empty);
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
    Task SelectDrive(DriveEntry? drive) => drive == null ? Task.CompletedTask : this.NavigateAsync(drive.Target);

    [RelayCommand]
    void SelectTab(PanelTabViewModel tab) => this.ActiveTab = tab;

    [RelayCommand]
    void Sort(string column)
    {
        if (column.StartsWith(ColumnHeader.CustomPrefix, StringComparison.Ordinal))
            this.ActiveTab?.SortByCustom(column[ColumnHeader.CustomPrefix.Length..]);
        else if (Enum.TryParse<SortColumn>(column, out var c))
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

    /// <summary>The folders inside <paramref name="path"/>, for a breadcrumb's dropdown. Empty when it can't be read.</summary>
    public async Task<IReadOnlyList<PathCrumbFolder>> ListSubfoldersAsync(string path)
    {
        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = await this.vfs.ListAsync(path, this.settings.ShowHidden);
        }
        catch
        {
            entries = [];
        }

        var current = this.Path;
        return entries
            .Where(x => x.IsDirectory && !x.IsParentLink)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new PathCrumbFolder(x.Name, x.FullPath, IsOnPath(current, x.FullPath)))
            .ToList();
    }

    static bool IsOnPath(string current, string folder)
    {
        var sep = RemotePath.IsRemote(folder) ? '/' : VirtualFileSystem.Sep;
        return current.Equals(folder, StringComparison.OrdinalIgnoreCase)
           || current.StartsWith(folder.TrimEnd(sep) + sep, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    Task NavigatePathText() => String.IsNullOrWhiteSpace(this.PathText) ? Task.CompletedTask : this.NavigateAsync(this.PathText);

    [RelayCommand]
    void ToggleTree() => this.ShowTree = !this.ShowTree;

    [RelayCommand]
    void ToggleViewMode() => this.ViewMode = this.IsThumbnails ? PanelViewMode.Details : PanelViewMode.Thumbnails;
}

/// <summary>A detail column's header label: its title (with the sort arrow), width and alignment; a tap sorts by it.</summary>
public sealed record ColumnHeader(string Key, string Text, double Width, bool AlignRight, bool IsCustom, IRelayCommand<string> Sort)
{
    public const string CustomPrefix = "custom:";

    public string SortParameter => this.IsCustom ? CustomPrefix + this.Key : this.Key;
    public TextAlignment Alignment => this.AlignRight ? TextAlignment.End : TextAlignment.Start;

    // the drawn rows pad the same way (FileDetailsView): a built-in column's text starts flush
    public Thickness Padding => this.AlignRight ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, this.IsCustom ? 6 : 0, 0);
}
