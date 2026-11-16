using System.Text;
using ShinyCommander.Core.Platform;
using Shiny.Mediator;

namespace ShinyCommander.ViewModels;

[ShellMap<MainPage>(registerRoute: false)]
public partial class MainViewModel : ObservableObject,
    IPageLifecycleAware,
    IEventHandler<FileSystemChangedEvent>,
    IEventHandler<GoToPathEvent>
{
    readonly VirtualFileSystem vfs;
    readonly FileOperationService ops;
    readonly AppSettings settings;
    readonly IAppDialogs dialogs;
    readonly INavigator navigator;
    readonly IMediator mediator;
    readonly IMainThread mainThread;
    readonly Shiny.Maui.Controls.IKeyboardShortcutService shortcuts;
    readonly ColumnSet columns;
    readonly QuitGuard quitGuard;
    readonly RemoteConnections connections;
    readonly FileEditors editors;
    // one conflict question at a time, whichever job asks
    readonly SemaphoreSlim conflictGate = new(1, 1);
    // files opened out of archives, saved back into them
    readonly ArchiveWriteBack writeBack = new();
    CancellationTokenSource? commandCts;
    bool initialized;

    public MainViewModel(
        VirtualFileSystem vfs,
        FileOperationService ops,
        AppSettings settings,
        IAppDialogs dialogs,
        INavigator navigator,
        IMediator mediator,
        IMainThread mainThread,
        Shiny.Maui.Controls.IKeyboardShortcutService shortcuts,
        ColumnSet columns,
        PaneToolbar toolbar,
        JobQueue jobs,
        QuitGuard quitGuard,
        RemoteConnections connections,
        FileEditors editors
    )
    {
        this.connections = connections;
        this.editors = editors;
        this.shortcuts = shortcuts;
        this.columns = columns;
        this.Jobs = jobs;
        this.quitGuard = quitGuard;
        jobs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.IsBusy))
                this.OnPropertyChanged(nameof(this.IsOperationRunning));
        };
        jobs.Started += this.OnJobStarted;
#if DEBUG
        Current = this; // UI test hooks (Testing/UiTestHooks.cs) drive the live instance
#endif
        dialogs.DialogClosed += (_, _) => this.RequestCommandLineFocus();
        this.vfs = vfs;
        this.ops = ops;
        this.settings = settings;
        this.dialogs = dialogs;
        this.navigator = navigator;
        this.mediator = mediator;
        this.mainThread = mainThread;
        this.writeBack.Saved += (_, e) => this.mainThread.BeginInvokeOnMainThread(async () =>
        {
            // a server's folder (uploads), or an archive file (and the folder it's in)
            await this.dialogs.Toast(L.Main_ArchiveSavedFormat(Path.GetFileName(e.Key), VirtualFileSystem.GetName(e.ArchiveFile)), ToastKind.Success);
            await this.Changed(RemotePath.IsRemote(e.ArchiveFile) ? e.ArchiveFile : Path.GetDirectoryName(e.ArchiveFile));
        });
        this.writeBack.Failed += (_, e) => this.mainThread.BeginInvokeOnMainThread(async () =>
            await this.dialogs.Toast(L.Main_ArchiveSaveFailedFormat(Path.GetFileName(e.Key), VirtualFileSystem.GetName(e.ArchiveFile), e.Error?.Message ?? String.Empty), ToastKind.Error));

        // the ribbon's tab colours come from converters, which only run when RibbonTab changes - a theme switch while
        // running would leave light colours on a dark ribbon
        if (Application.Current is { } app)
            app.RequestedThemeChanged += (_, _) =>
            {
                this.OnPropertyChanged(nameof(this.RibbonTab));
                foreach (var tab in this.Left.Tabs.Concat(this.Right.Tabs))
                    tab.RefreshTheme();
            };

        this.Left = new PanelViewModel(PanelSide.Left, vfs, settings, columns, toolbar);
        this.Right = new PanelViewModel(PanelSide.Right, vfs, settings, columns, toolbar);
        this.Left.PropertyChanged += this.OnPanelPropertyChanged;
        this.Right.PropertyChanged += this.OnPanelPropertyChanged;
        this.Left.TabsChanged += (_, _) => this.SaveState();
        this.Right.TabsChanged += (_, _) => this.SaveState();
        this.Left.ScrollRequested += this.OnPanelCursorMoved;
        this.Right.ScrollRequested += this.OnPanelCursorMoved;
        this.StackPanels = settings.StackPanels;
        this.RibbonTab = settings.RibbonTab is { Length: > 0 } ribbonTab ? ribbonTab : "files";

        // a connection added, removed, opened or closed - the drive bars show them
        connections.Changed += (_, _) => mainThread.BeginInvokeOnMainThread(() =>
        {
            this.Left.RefreshDrives();
            this.Right.RefreshDrives();
        });

        // row colours come from converters - rebuild rows so they pick up the new palette
        if (Application.Current != null)
            Application.Current.RequestedThemeChanged += (_, _) => this.mainThread.BeginInvokeOnMainThread(() =>
            {
                _ = this.Left.ActiveTab?.RefreshAsync();
                _ = this.Right.ActiveTab?.RefreshAsync();
            });
    }

#if DEBUG
    public static MainViewModel? Current { get; private set; }
#endif

    public PanelViewModel Left { get; }
    public PanelViewModel Right { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InactivePanel))]
    public partial PanelViewModel ActivePanel { get; set; } = null!;

    public PanelViewModel InactivePanel => this.ActivePanel == this.Left ? this.Right : this.Left;
    PanelTabViewModel? Tab => this.ActivePanel?.ActiveTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackspaceCommand), nameof(SpaceCommand), nameof(HomeCommand), nameof(EndCommand), nameof(CursorLeftCommand), nameof(CursorRightCommand), nameof(DeleteKeyCommand), nameof(DeletePermanentlyKeyCommand))]
    public partial string CommandText { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string CommandPrompt { get; set; } = String.Empty;

    /// <summary>True while the path box has focus - navigation keys then belong to it.</summary>
    [ObservableProperty]
    public partial bool IsPathEditing { get; set; }

    [ObservableProperty]
    public partial bool IsFilterEditing { get; set; }

    // --- background jobs ----------------------------------------------------------------------
    /// <summary>Copy/move/delete/pack/unpack run here - the panels stay usable meanwhile.</summary>
    public JobQueue Jobs { get; }

    /// <summary>True until every job (and its refresh/toast) has finished.</summary>
    public bool IsOperationRunning => this.Jobs.IsBusy;

    /// <summary>The jobs pane above the command line (Primary+J).</summary>
    [ObservableProperty]
    public partial bool ShowJobs { get; set; }

    // --- command output -----------------------------------------------------------------------
    [ObservableProperty]
    public partial bool ShowOutput { get; set; }

    [ObservableProperty]
    public partial string CommandOutput { get; set; } = String.Empty;

    [ObservableProperty]
    public partial bool IsCommandRunning { get; set; }

    /// <summary>Selected tab of the AppKit ribbon bar (the Shiny Ribbon tracks its own).</summary>
    [ObservableProperty]
    public partial string RibbonTab { get; set; } = "files";

    [RelayCommand]
    void SelectRibbonTab(string key) => this.RibbonTab = key;

    partial void OnRibbonTabChanged(string value) => this.settings.RibbonTab = value;

    public int PanelFontSize => Math.Max(10, this.settings.PanelFontSize);

    /// <summary>The page hooks this to move keyboard focus into the command line.</summary>
    public event EventHandler? FocusCommandLineRequested;

    public void RequestCommandLineFocus() => this.FocusCommandLineRequested?.Invoke(this, EventArgs.Empty);

    partial void OnIsPathEditingChanged(bool value) => this.RefreshKeyStates();
    partial void OnIsFilterEditingChanged(bool value) => this.RefreshKeyStates();

    void RefreshKeyStates()
    {
        foreach (var cmd in new IRelayCommand[] { this.CursorLeftCommand, this.CursorRightCommand, this.CursorUpCommand, this.CursorDownCommand, this.MarkUpCommand, this.MarkDownCommand, this.PageUpCommand, this.PageDownCommand, this.HomeCommand, this.EndCommand, this.InsertCommand, this.SpaceCommand, this.BackspaceCommand, this.SwitchPanelCommand, this.EnterCommand, this.DeleteKeyCommand, this.DeletePermanentlyKeyCommand })
            cmd.NotifyCanExecuteChanged();
    }

    // ===========================================================================================
    // lifecycle
    // ===========================================================================================

    public async void OnAppearing()
    {
        if (this.initialized)
        {
            // coming back from viewer/editor/search etc. Folders on disk are kept current by their watchers (and by
            // FileSystemChangedEvent from our own operations), so only archive listings - which nothing watches - are
            // reread. Rereading everything rebuilt both lists on every return and made "back" take seconds.
            var listingChanged = this.listingSettings != (this.settings.ShowHidden, this.settings.FriendlySizes, this.settings.MotionFileIcons);
            foreach (var tab in new[] { this.Left.ActiveTab, this.Right.ActiveTab })
            {
                if (listingChanged || tab is { IsArchive: true } || tab?.IsBranchView == true)
                    _ = tab?.RefreshAsync();
            }
            // Settings may have turned tabs on or off
            await this.Left.RefreshShowTabsAsync();
            await this.Right.RefreshShowTabsAsync();
            this.FocusCommandLineRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        this.initialized = true;
        this.settings.EnsureDefaults();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await this.RestoreTabs(this.Left, AppSettings.SplitLines(this.settings.LeftTabs), this.settings.LeftActiveTab, home);
        await this.RestoreTabs(this.Right, AppSettings.SplitLines(this.settings.RightTabs), this.settings.RightActiveTab, DefaultRightPath(home));
        if (!this.settings.PlaceTabsSeeded)
        {
            await SeedPlaceTabs(this.Left);
            await SeedPlaceTabs(this.Right);
            this.settings.PlaceTabsSeeded = true;
        }
        this.Activate(this.settings.RightPanelActive ? this.Right : this.Left);
        this.SaveState();
    }

    // settings that change what a listing shows (Settings page) - compared when coming back
    (bool, bool, bool) listingSettings;

    public void OnDisappearing() => this.listingSettings = (this.settings.ShowHidden, this.settings.FriendlySizes, this.settings.MotionFileIcons);

    static string DefaultRightPath(string home)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(docs) && docs != home ? docs : home;
    }

    async Task RestoreTabs(PanelViewModel panel, IReadOnlyList<string> lines, int active, string fallback)
    {
        if (lines.Count == 0)
            lines = [fallback];

        foreach (var state in lines.Select(TabState.Parse))
        {
            var tab = await panel.AddTabAsync(state.Path, activate: false, locked: state.Locked);
            tab.Alias = state.Alias;
            tab.ColorKey = state.Color;
            tab.IconKey = state.Icon;
            tab.SortColumn = state.Sort;
            tab.SortDescending = state.Descending;
        }

        panel.ActiveTab = panel.Tabs[Math.Clamp(active, 0, panel.Tabs.Count - 1)];
    }

    // the places used to be drive bar buttons: they come back as locked tabs, after whatever was open already
    static async Task SeedPlaceTabs(PanelViewModel panel)
    {
        var open = panel.Tabs.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var place in DriveService.GetPlaces().Where(x => !open.Contains(VirtualFileSystem.Normalize(x.RootPath))))
            await panel.AddTabAsync(place.RootPath, activate: false, locked: true);
    }

    // a locked tab is saved at its own folder, even if it wandered while tabs were off
    static string TabLine(PanelTabViewModel tab)
        => new TabState(tab.LockedPath ?? tab.Path, tab.IsLocked, tab.Alias, tab.ColorKey, tab.IconKey, tab.SortColumn, tab.SortDescending).Format();

    void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelViewModel.Path) or nameof(PanelViewModel.ActiveTab))
        {
            this.UpdatePrompt();
            this.SaveState();
        }
    }

    void UpdatePrompt() => this.CommandPrompt = (this.Tab?.Path ?? String.Empty) + (OperatingSystem.IsWindows() ? ">" : " $");

    public void SaveState()
    {
        if (!this.initialized || this.ActivePanel == null)
            return;

        this.settings.LeftTabs = AppSettings.JoinLines(this.Left.Tabs.Select(TabLine));
        this.settings.RightTabs = AppSettings.JoinLines(this.Right.Tabs.Select(TabLine));
        this.settings.LeftActiveTab = this.Left.ActiveTab == null ? 0 : this.Left.Tabs.IndexOf(this.Left.ActiveTab);
        this.settings.RightActiveTab = this.Right.ActiveTab == null ? 0 : this.Right.Tabs.IndexOf(this.Right.ActiveTab);
        this.settings.RightPanelActive = this.ActivePanel == this.Right;
    }

    public void Activate(PanelViewModel panel)
    {
        if (this.ActivePanel == panel)
            return;

        if (this.ActivePanel != null)
            this.ActivePanel.IsActive = false;

        this.ActivePanel = panel;
        panel.IsActive = true;

        // the preview belongs to the side you're not working in - focusing it ends quick view
        if (panel.IsQuickView)
            panel.IsQuickView = false;
        this.UpdateQuickView();

        this.UpdatePrompt();
        this.SaveState();
    }

    void OnPanelCursorMoved(object? sender, int index)
    {
        if (sender == this.ActivePanel)
            this.UpdateQuickView();
    }

    void UpdateQuickView()
    {
        if (this.ActivePanel is { } active && this.InactivePanel.IsQuickView)
            _ = this.InactivePanel.QuickView.ShowAsync(active.ActiveTab?.CursorItem?.Entry);
    }

    // Mediator events --------------------------------------------------------------------------

    public Task Handle(FileSystemChangedEvent @event, IMediatorContext context, CancellationToken ct)
        => this.mainThread.InvokeOnMainThreadAsync(async () =>
        {
            var dirs = @event.Directories.Select(VirtualFileSystem.Normalize).ToHashSet();
            foreach (var tab in this.Left.Tabs.Concat(this.Right.Tabs))
            {
                if (dirs.Contains(tab.Path) || dirs.Any(d => tab.Path.StartsWith(d + (RemotePath.IsRemote(d) ? '/' : Path.DirectorySeparatorChar))) || tab.IsArchive)
                    await tab.RefreshAsync();
            }
            this.Left.RefreshDrives();
            this.Right.RefreshDrives();
        });

    public Task Handle(GoToPathEvent @event, IMediatorContext context, CancellationToken ct)
        => this.mainThread.InvokeOnMainThreadAsync(async () =>
        {
            var panel = @event.OtherPanel ? this.InactivePanel : this.ActivePanel;
            await panel.NavigateAsync(@event.Directory, @event.FocusName);
        });

    Task Changed(params string?[] dirs)
        => this.mediator.Publish(new FileSystemChangedEvent(dirs.Where(x => !String.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList()));

    // ===========================================================================================
    // keyboard navigation (CanExecute == false hands the key back to the focused text box)
    // ===========================================================================================

    bool CanNavigate() => !this.IsPathEditing;
    bool CommandLineEmpty() => !this.IsPathEditing && !this.IsFilterEditing && String.IsNullOrEmpty(this.CommandText);

    // thumbnail view is a grid: Up/Down step a whole row, Left/Right one tile
    int RowStep => this.ActivePanel is { IsThumbnails: true } p ? Math.Max(1, p.ThumbnailSpan) : 1;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    void CursorUp() => this.Tab?.MoveCursor(-this.RowStep);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    void CursorDown() => this.Tab?.MoveCursor(this.RowStep);

    // only claimed in thumbnail view with an empty command line - otherwise the caret keeps them
    bool CanMoveSideways() => this.CommandLineEmpty() && this.ActivePanel is { IsThumbnails: true };

    [RelayCommand(CanExecute = nameof(CanMoveSideways))]
    void CursorLeft() => this.Tab?.MoveCursor(-1);

    [RelayCommand(CanExecute = nameof(CanMoveSideways))]
    void CursorRight() => this.Tab?.MoveCursor(1);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    void MarkUp() => this.Tab?.MoveCursor(-1, mark: true);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    void MarkDown() => this.Tab?.MoveCursor(1, mark: true);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    void PageUp() => this.Tab?.MoveCursor(-20);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    void PageDown() => this.Tab?.MoveCursor(20);

    [RelayCommand(CanExecute = nameof(CommandLineEmpty))]
    void Home() => this.Tab?.SetCursor(0);

    [RelayCommand(CanExecute = nameof(CommandLineEmpty))]
    void End() => this.Tab?.SetCursor(Int32.MaxValue);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    void Insert() => this.Tab?.MoveCursor(1, mark: true);

    [RelayCommand(CanExecute = nameof(CommandLineEmpty))]
    async Task Space()
    {
        var tab = this.Tab;
        if (tab?.CursorItem is not { } item || item.Entry.IsParentLink)
            return;

        tab.ToggleMark(item);
        if (item is { IsDirectory: true, IsMarked: true, CalculatedSize: null })
        {
            item.CalculatedSize = await this.FolderSize(item.Entry);
            tab.UpdateStatus();
        }
    }

    /// <summary>
    /// The marked folders' sizes, or the folder under the cursor, or (on a file or "..") every folder in the list - TC's
    /// Alt+Shift+Enter. Unlike Space, the marks stay as they are.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    async Task CalculateFolderSizes()
    {
        var tab = this.Tab;
        if (tab == null)
            return;

        var folders = tab.MarkedItems.Where(x => x.IsDirectory).ToList();
        if (folders.Count == 0 && tab.CursorItem is { IsDirectory: true, Entry.IsParentLink: false } cursor)
            folders = [cursor];
        if (folders.Count == 0)
            folders = tab.Items.Where(x => x.IsDirectory && !x.Entry.IsParentLink).ToList();

        foreach (var folder in folders)
        {
            folder.CalculatedSize = await this.FolderSize(folder.Entry);
            tab.UpdateStatus();
        }
    }

    Task<long> FolderSize(FileEntry entry) => entry.IsRemote
        ? this.vfs.CalculateRemoteFolderSizeAsync(entry.FullPath)
        : entry.IsInArchive
            ? this.vfs.CalculateArchiveFolderSizeAsync(entry.FullPath)
            : FileOperationService.CalculateSizeAsync(entry.FullPath);

    [RelayCommand(CanExecute = nameof(CommandLineEmpty))]
    Task Backspace() => this.Tab?.GoUpAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    void SwitchPanel()
    {
        this.Activate(this.InactivePanel);
        this.FocusCommandLineRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    void Escape()
    {
        if (this.ActivePanel.ShowQuickFilter)
        {
            this.CloseQuickFilter();
        }
        else if (!String.IsNullOrEmpty(this.CommandText))
        {
            this.CommandText = String.Empty;
        }
        else if (this.ShowOutput)
        {
            this.ShowOutput = false;
        }
        else if (this.ShowJobs)
        {
            // jobs keep running - Esc only hides the pane; each job has its own Stop
            this.jobsPaneToggles++;
            this.ShowJobs = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    async Task Enter()
    {
        if (!String.IsNullOrWhiteSpace(this.CommandText))
        {
            await this.RunCommandLine();
            return;
        }
        if (this.IsFilterEditing)
            this.CloseQuickFilter(keepFilter: true);

        await this.OpenItem(this.Tab?.CursorItem);
    }

    public async Task OpenItem(FileItemViewModel? item)
    {
        var tab = this.Tab;
        if (item == null || tab == null)
            return;

        try
        {
            if (item.Entry.IsParentLink)
            {
                await tab.GoUpAsync();
            }
            // an archive on a server opens like any other file (downloaded) - browsing into it needs it on disk
            else if (item.IsDirectory || item.Entry.IsArchive && !item.Entry.IsInArchive && !item.Entry.IsRemote)
            {
                await tab.NavigateAsync(item.Entry.FullPath);
            }
            else
            {
                ShellLauncher.OpenWithDefaultApp(await this.LocalCopy(item.Entry));
            }
        }
        catch (Exception ex)
        {
            await this.dialogs.Alert(L.Main_CannotOpen, ex.Message);
        }
    }

    public void ClickItem(PanelViewModel panel, FileItemViewModel item, bool doubleTap)
    {
        this.Activate(panel);
        var tab = panel.ActiveTab;
        if (tab == null)
            return;

        tab.SetCursor(item);
        if (doubleTap)
            _ = this.OpenItem(item);
        this.RequestCommandLineFocus();
    }

    public void RightClickItem(PanelViewModel panel, FileItemViewModel item)
    {
        // TC's "right button selects" mode - the most useful on desktops without Insert keys (Mac)
        this.Activate(panel);
        panel.ActiveTab?.SetCursor(item);
        panel.ActiveTab?.ToggleMark(item);
    }

    // ===========================================================================================
    // tabs, panels & navigation
    // ===========================================================================================

    [RelayCommand]
    async Task NewTab()
    {
        if (this.settings.HideTabs)
            return;

        var item = this.Tab?.CursorItem;
        var path = item is { IsDirectory: true, Entry.IsParentLink: false } ? item.Entry.FullPath : this.ActivePanel.Path;
        await this.ActivePanel.AddTabAsync(path);
    }

    [RelayCommand]
    async Task CloseTab()
    {
        if (this.settings.HideTabs)
            return;

        if (this.Tab is { IsFirst: true })
            await this.dialogs.Toast(L.Tab_FirstCantClose);
        else if (this.Tab is { IsLocked: true })
            await this.dialogs.Toast(L.Tab_UnlockToClose);
        else
            this.ActivePanel.CloseTab(null);
    }

    /// <summary>"Rename tab…" in a tab's menu: empty goes back to the folder's own name, cancel leaves it be.</summary>
    public async Task RenameTab(PanelViewModel panel, PanelTabViewModel tab)
    {
        var alias = await this.dialogs.Prompt(L.Tab_Rename, L.Tab_RenameMessage, tab.Alias, placeholder: tab.Path);
        if (alias != null)
            panel.EditTab(tab, x => x.Alias = String.IsNullOrWhiteSpace(alias) ? null : alias.Trim());
    }

    [RelayCommand]
    void NextTab()
    {
        if (!this.settings.HideTabs)
            this.ActivePanel.NextTab(1);
    }

    [RelayCommand]
    void PreviousTab()
    {
        if (!this.settings.HideTabs)
            this.ActivePanel.NextTab(-1);
    }

    [RelayCommand]
    async Task SwapPanels()
    {
        var left = this.Left.Path;
        var right = this.Right.Path;
        await this.Left.NavigateAsync(right);
        await this.Right.NavigateAsync(left);
    }

    [RelayCommand]
    Task TargetEqualsSource() => this.InactivePanel.NavigateAsync(this.ActivePanel.Path);

    [RelayCommand]
    Task OpenInOtherPanel()
    {
        var item = this.Tab?.CursorItem;
        var path = item is { IsDirectory: true } ? item.Entry.FullPath : this.ActivePanel.Path;
        return this.InactivePanel.NavigateAsync(path);
    }

    [RelayCommand]
    Task Refresh()
    {
        this.Left.RefreshDrives();
        this.Right.RefreshDrives();
        return this.Tab?.RefreshAsync() ?? Task.CompletedTask;
    }

    [RelayCommand]
    async Task ToggleHidden()
    {
        this.settings.ShowHidden = !this.settings.ShowHidden;
        await (this.Left.ActiveTab?.RefreshAsync() ?? Task.CompletedTask);
        await (this.Right.ActiveTab?.RefreshAsync() ?? Task.CompletedTask);
        await this.dialogs.Toast(this.settings.ShowHidden ? L.Main_ShowingHidden : L.Main_HidingHidden);
    }

    [RelayCommand]
    async Task BranchView()
    {
        if (this.Tab is not { } tab)
            return;

        if (tab.IsRemote && !tab.IsBranchView)
        {
            await this.dialogs.Toast(L.Remote_NoBranchView, ToastKind.Warning);
            return;
        }

        tab.IsBranchView = !tab.IsBranchView;
        await tab.RefreshAsync();
    }

    /// <summary>Each panel has its own folder tree; Primary+F8 toggles the active one's.</summary>
    [RelayCommand]
    void ToggleTree() => this.ActivePanel.ShowTree = !this.ActivePanel.ShowTree;

    /// <summary>Panels top/bottom instead of left/right - the page re-arranges its grid when this flips.</summary>
    [ObservableProperty]
    public partial bool StackPanels { get; set; }

    partial void OnStackPanelsChanged(bool value) => this.settings.StackPanels = value;

    [RelayCommand]
    void TogglePanelLayout() => this.StackPanels = !this.StackPanels;

    [RelayCommand]
    void ThumbnailView()
    {
        this.ActivePanel.ViewMode = this.ActivePanel.IsThumbnails ? PanelViewMode.Details : PanelViewMode.Thumbnails;
        this.CursorLeftCommand.NotifyCanExecuteChanged();
        this.CursorRightCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    void DetailsView()
    {
        this.ActivePanel.ViewMode = PanelViewMode.Details;
        this.CursorLeftCommand.NotifyCanExecuteChanged();
        this.CursorRightCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ctrl+Q - the other panel previews the file under the cursor.</summary>
    [RelayCommand]
    void QuickView()
    {
        var target = this.InactivePanel;
        target.IsQuickView = !target.IsQuickView;
        this.UpdateQuickView();
    }

    /// <summary>Pick which detail columns the panels show; the list stays open until Done.</summary>
    [RelayCommand]
    async Task ChooseColumns()
    {
        var done = L.Common_Done;
        while (true)
        {
            var options = Enum.GetValues<PanelColumn>()
                .Select(c => (this.columns.IsVisible(c) ? "✓  " : "     ") + ColumnSet.Label(c))
                .ToList();

            var choice = await this.dialogs.Choose(L.Main_Columns, options, done);
            if (choice == null || choice == done)
                break;

            var index = options.IndexOf(choice);
            if (index >= 0)
                this.columns.Toggle(Enum.GetValues<PanelColumn>()[index]);
        }
        this.RequestCommandLineFocus();
    }

    [RelayCommand]
    void QuickFilter()
    {
        this.ActivePanel.ShowQuickFilter = true;
        this.IsFilterEditing = true;
    }

    public void CloseQuickFilter(bool keepFilter = false)
    {
        this.ActivePanel.ShowQuickFilter = keepFilter && !String.IsNullOrEmpty(this.Tab?.QuickFilter);
        this.IsFilterEditing = false;
        if (!keepFilter && this.Tab != null)
            this.Tab.QuickFilter = String.Empty;
        this.FocusCommandLineRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    Task DriveMenuLeft() => this.DriveMenu(this.Left);

    [RelayCommand]
    Task DriveMenuRight() => this.DriveMenu(this.Right);

    async Task DriveMenu(PanelViewModel panel)
    {
        panel.RefreshDrives();
        var options = panel.Drives.Select(d => d.Kind == DriveKind.Remote
            ? $"{d.Name}   {d.Label}"
            : $"{d.Name}   {d.Label}   ({L.Main_DriveFreeFormat(SizeFormatter.Friendly(d.FreeSpace))})").ToList();
        var choice = await this.dialogs.Choose(panel.Side == PanelSide.Left ? L.Main_DriveMenuLeft : L.Main_DriveMenuRight, options);
        var index = choice == null ? -1 : options.IndexOf(choice);
        if (index >= 0)
        {
            this.Activate(panel);
            await panel.NavigateAsync(panel.Drives[index].Target);
        }
    }

    [RelayCommand]
    async Task Hotlist()
    {
        var add = "➕ " + L.Main_HotlistAdd;
        var remove = "➖ " + L.Main_HotlistRemove;
        var list = AppSettings.SplitLines(this.settings.Hotlist).ToList();
        var options = list.Concat([add]).Concat(list.Count > 0 ? [remove] : Array.Empty<string>()).ToList();

        var choice = await this.dialogs.Choose(L.Main_Hotlist, options);
        if (choice == null)
            return;

        if (choice == add)
        {
            if (!list.Contains(this.ActivePanel.Path))
                list.Add(this.ActivePanel.Path);
            this.settings.Hotlist = AppSettings.JoinLines(list);
            await this.dialogs.Toast(L.Main_HotlistAddedFormat(this.ActivePanel.Path), ToastKind.Success);
        }
        else if (choice == remove)
        {
            var doomed = await this.dialogs.Choose(L.Main_HotlistRemoveTitle, list, destructive: null);
            if (doomed != null)
            {
                list.Remove(doomed);
                this.settings.Hotlist = AppSettings.JoinLines(list);
            }
        }
        else
        {
            await this.ActivePanel.NavigateAsync(choice);
        }
    }

    [RelayCommand]
    Task GoBack() => this.Tab?.GoBackAsync() ?? Task.CompletedTask;

    [RelayCommand]
    Task GoForward() => this.Tab?.GoForwardAsync() ?? Task.CompletedTask;

    [RelayCommand]
    Task GoRoot() => this.Tab?.GoRootAsync() ?? Task.CompletedTask;

    [RelayCommand]
    Task GoParent() => this.Tab?.GoUpAsync() ?? Task.CompletedTask;

    [RelayCommand]
    Task GoHome() => this.ActivePanel.NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // ===========================================================================================
    // marking
    // ===========================================================================================

    [RelayCommand]
    void SelectAll() => this.Tab?.MarkAll(true);

    [RelayCommand]
    void DeselectAll() => this.Tab?.MarkAll(false);

    [RelayCommand]
    void InvertSelection() => this.Tab?.InvertMarks();

    [RelayCommand]
    void SelectSameExtension() => this.Tab?.MarkSameExtension();

    [RelayCommand]
    async Task SelectGroup()
    {
        var mask = await this.dialogs.Prompt(L.Main_SelectFiles, L.Main_SelectMask, "*.*");
        if (!String.IsNullOrWhiteSpace(mask))
            this.Tab?.MarkByMask(mask, true);
    }

    [RelayCommand]
    async Task DeselectGroup()
    {
        var mask = await this.dialogs.Prompt(L.Main_UnselectFiles, L.Main_UnselectMask, "*.*");
        if (!String.IsNullOrWhiteSpace(mask))
            this.Tab?.MarkByMask(mask, false);
    }

    // ===========================================================================================
    // command line
    // ===========================================================================================

    [RelayCommand]
    void CopyNameToCommandLine()
    {
        if (this.Tab?.CursorItem is { Entry.IsParentLink: false } item)
            this.AppendToCommandLine(Quote(item.Entry.Name));
    }

    [RelayCommand]
    void CopyPathToCommandLine()
    {
        if (this.Tab?.CursorItem is { Entry.IsParentLink: false } item)
            this.AppendToCommandLine(Quote(item.Entry.FullPath));
    }

    void AppendToCommandLine(string text)
    {
        this.CommandText = String.IsNullOrEmpty(this.CommandText) ? text : this.CommandText.TrimEnd() + " " + text;
        this.FocusCommandLineRequested?.Invoke(this, EventArgs.Empty);
    }

    static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    [RelayCommand]
    async Task CopyPathsToClipboard()
    {
        var paths = this.Tab?.SelectedEntries.Select(x => x.FullPath).ToList() ?? [];
        if (paths.Count == 0)
            return;

        await Clipboard.Default.SetTextAsync(String.Join(Environment.NewLine, paths));
        await this.dialogs.Toast(L.Main_CopiedPathsFormat(paths.Count), ToastKind.Success);
    }

    async Task RunCommandLine()
    {
        var text = this.CommandText.Trim();
        this.CommandText = String.Empty;
        var tab = this.Tab;
        if (tab == null)
            return;

        // "cd" is handled by us, like TC
        if (text == "cd" || text.StartsWith("cd ", StringComparison.Ordinal))
        {
            var arg = text.Length > 2 ? text[3..].Trim().Trim('"') : "~";
            var target = arg switch
            {
                "~" or "" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "-" => tab.CanGoBack ? null : tab.Path,
                _ => Path.IsPathRooted(arg) || arg.StartsWith('~') ? arg : Path.Combine(tab.Path, arg)
            };
            if (target == null)
                await tab.GoBackAsync();
            else
                await tab.NavigateAsync(target);
            return;
        }

        if (tab.IsArchive)
        {
            await this.dialogs.Toast(L.Main_NoCommandsInArchive, ToastKind.Warning);
            return;
        }

        this.commandCts?.Cancel();
        var cts = this.commandCts = new CancellationTokenSource();
        var output = new StringBuilder();
        output.AppendLine($"{this.CommandPrompt} {text}");
        this.CommandOutput = output.ToString();
        this.ShowOutput = true;
        this.IsCommandRunning = true;

        var lastFlush = Environment.TickCount64;
        try
        {
            var result = await ShellLauncher.RunAsync(text, tab.Path, line =>
            {
                lock (output)
                    output.AppendLine(line);

                if (Environment.TickCount64 - lastFlush > 150)
                {
                    lastFlush = Environment.TickCount64;
                    this.mainThread.BeginInvokeOnMainThread(() =>
                    {
                        lock (output)
                            this.CommandOutput = output.ToString();
                    });
                }
            }, cts.Token);

            lock (output)
                output.AppendLine($"[exit {result.ExitCode} in {result.Duration.TotalSeconds:0.00}s]");
        }
        catch (OperationCanceledException)
        {
            output.AppendLine(L.Main_ConsoleCancelled);
        }
        catch (Exception ex)
        {
            output.AppendLine($"[error] {ex.Message}");
        }
        finally
        {
            this.IsCommandRunning = false;
            lock (output)
                this.CommandOutput = output.ToString();
            await tab.RefreshAsync();
        }
    }

    [RelayCommand]
    void CancelCommand() => this.commandCts?.Cancel();

    [RelayCommand]
    void CloseOutput() => this.ShowOutput = false;

    [RelayCommand]
    void OpenTerminal() => ShellLauncher.OpenTerminal(this.vfs.Resolve(this.ActivePanel.Path) is { IsArchive: false, IsRemote: false } l ? l.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [RelayCommand]
    void RevealInFileManager()
    {
        if (this.Tab?.CursorItem is not { Entry.IsParentLink: false } item)
            return;

        if (item.Entry.IsRemote)
        {
            _ = this.dialogs.Toast(L.Remote_NoFileManager, ToastKind.Warning);
            return;
        }

        // inside an archive, the archive itself is what the file manager can show
        var path = item.Entry.IsInArchive ? this.vfs.Resolve(Path.GetDirectoryName(item.Entry.FullPath)!).ArchiveFile! : item.Entry.FullPath;
        ShellLauncher.RevealInFileManager(path);
    }

    // ===========================================================================================
    // F-key operations
    // ===========================================================================================

    [RelayCommand]
    async Task View()
    {
        if (this.Tab?.CursorItem is not { } item || item.Entry.IsParentLink)
            return;

        if (item.IsDirectory)
        {
            // TC: F3 on a folder shows its size
            item.CalculatedSize = await this.FolderSize(item.Entry);
            this.Tab.UpdateStatus();
            return;
        }
        await this.navigator.NavigateTo<ViewerViewModel>(vm => vm.FilePath = item.Entry.FullPath);
    }

    [RelayCommand]
    async Task Edit()
    {
        if (this.Tab?.CursorItem is not { IsDirectory: false } item || item.Entry.IsParentLink)
            return;

        await this.EditFile(item.Entry);
    }

    async Task EditFile(FileEntry entry)
    {
        try
        {
            if (entry.IsInArchive && !ArchiveFormats.IsWritable(this.vfs.Resolve(Path.GetDirectoryName(entry.FullPath)!).ArchiveFile!))
            {
                await this.dialogs.Alert(L.Main_ReadOnly, L.Main_ReadOnlyArchive);
                return;
            }

            // inside an archive this edits a copy, and each save goes back in
            var path = await this.LocalCopy(entry);
            // the editor for the file's type (Services/Editors) - the text editor when there's none
            await this.editors.Open(path);
        }
        catch (Exception ex)
        {
            await this.dialogs.Alert(L.Cmd_Edit, ex.Message);
        }
    }

    /// <summary>
    /// A path another program (or the editor) can open: the file itself on disk; from an archive a temporary copy,
    /// whose saves are written back into a writable archive.
    /// </summary>
    async Task<string> LocalCopy(FileEntry entry)
    {
        var path = await this.vfs.MaterializeAsync(entry.FullPath);
        if (entry.IsRemote)
        {
            // a download - each save goes back up to the server
            var remotePath = entry.FullPath;
            this.writeBack.Watch(path, VirtualFileSystem.GetDirectory(remotePath)!, entry.Name, copy =>
            {
                var (fs, inner) = this.vfs.Remote.Get(remotePath);
                using var stream = File.OpenRead(copy);
                fs.WriteAsync(inner, stream, stream.Length, File.GetLastWriteTime(copy)).GetAwaiter().GetResult();
            });
        }
        else if (entry.IsInArchive)
        {
            var loc = this.vfs.Resolve(Path.GetDirectoryName(entry.FullPath)!);
            var key = loc.InnerPath.Length == 0 ? entry.Name : loc.InnerPath + "/" + entry.Name;
            this.writeBack.Watch(path, loc.ArchiveFile!, key, entry.UnixMode);
        }
        return path;
    }

    [RelayCommand]
    async Task NewFile()
    {
        var tab = this.Tab;
        if (tab == null)
            return;

        var name = await this.dialogs.Prompt(L.Main_NewFile, L.Main_NewFileMessage, "new.txt");
        if (String.IsNullOrWhiteSpace(name))
            return;

        await this.Try(L.Main_NewFile, async () =>
        {
            await this.ops.CreateFileAsync(tab.Path, name);
            await tab.LoadAsync(name);
            if (tab.CursorItem is { } item)
                await this.EditFile(item.Entry);
        });
    }

    [RelayCommand]
    async Task MakeDirectory()
    {
        var tab = this.Tab;
        if (tab == null)
            return;

        var suggestion = tab.CursorItem is { IsDirectory: true, Entry.IsParentLink: false } c ? c.Entry.Name : String.Empty;
        var name = await this.dialogs.Prompt(L.Main_NewFolder, L.Main_NewFolderMessage, suggestion);
        if (String.IsNullOrWhiteSpace(name))
            return;

        await this.Try(L.Main_NewFolder, async () =>
        {
            await this.ops.CreateDirectoryAsync(tab.Path, name);
            await tab.LoadAsync(name.Replace('\\', '/').Split('/')[0]);
            await this.Changed(tab.Path);
        });
    }

    [RelayCommand]
    async Task Rename()
    {
        var tab = this.Tab;
        if (tab?.CursorItem is not { Entry.IsParentLink: false } item)
            return;

        var name = await this.dialogs.Prompt(L.Cmd_Rename, L.Main_RenameMessageFormat(item.Entry.Name), item.Entry.Name);
        if (String.IsNullOrWhiteSpace(name) || name == item.Entry.Name)
            return;

        await this.Try(L.Cmd_Rename, async () =>
        {
            await this.ops.RenameAsync(item.Entry, name);
            await tab.LoadAsync(name);
            await this.Changed(tab.Path);
        });
    }

    [RelayCommand]
    Task Copy() => this.Transfer(false);

    [RelayCommand]
    Task Move() => this.Transfer(true);

    async Task Transfer(bool move)
    {
        var tab = this.Tab;
        var sources = tab?.SelectedEntries ?? [];
        if (tab == null || sources.Count == 0)
            return;

        var verb = move ? L.Cmd_Move : L.Cmd_Copy;
        var what = What(sources);
        var title = move ? L.Op_MoveTitleFormat(what) : L.Op_CopyTitleFormat(what);
        var defaultTarget = this.InactivePanel.Path + Path.DirectorySeparatorChar;
        if (sources.Count == 1 && move && this.InactivePanel.Path == tab.Path)
            defaultTarget = sources[0].Name; // F6 with both panels on the same folder == rename

        var input = await this.dialogs.Prompt(title, move ? L.Op_MoveTo : L.Op_CopyTo, defaultTarget, verb);
        if (String.IsNullOrWhiteSpace(input))
            return;

        OperationTarget target;
        try
        {
            target = this.ops.ResolveTarget(input, tab.Path, sources.Count);
        }
        catch (Exception ex)
        {
            await this.dialogs.Alert(verb, ex.Message);
            return;
        }

        var sourceDir = tab.Path;
        var destination = new JobDestination(target.Directory, sources.Count == 1 ? target.RenameTo ?? sources[0].Name : null);
        this.RunOperation(title, move ? FileOperationKind.Move : FileOperationKind.Copy, destination, async (progress, ct) =>
        {
            var result = move
                ? await this.ops.MoveAsync(sources, target, this.ResolveConflict, progress, ct)
                : await this.ops.CopyAsync(sources, target, this.ResolveConflict, progress, ct);
            await this.Changed(sourceDir, target.Directory);
            return result;
        });
        tab.MarkAll(false);
    }

    /// <summary>"'a.txt'" for one item, "3 items" for more - what a title or question is about.</summary>
    static string What(IReadOnlyList<FileEntry> items) => items.Count == 1 ? L.Op_QuotedFormat(items[0].Name) : L.Op_ItemsFormat(items.Count);

    async Task<ConflictAction> ResolveConflict(FileConflict conflict, CancellationToken ct)
    {
        string[] options = [L.Conflict_Overwrite, L.Conflict_OverwriteAll, L.Conflict_OverwriteAllOlder, L.Conflict_Skip, L.Conflict_SkipAll, L.Conflict_KeepBoth];
        var msg = L.Conflict_MessageFormat(
            Path.GetFileName(conflict.TargetPath),
            SizeFormatter.Exact(conflict.SourceSize), conflict.SourceModified.ToString("g"),
            SizeFormatter.Exact(conflict.TargetSize), conflict.TargetModified.ToString("g"));

        // jobs run side by side - their questions take turns
        await this.conflictGate.WaitAsync(ct);
        string? choice;
        try
        {
            choice = await this.mainThread.InvokeOnMainThreadAsync(() => this.dialogs.Choose(msg, options, L.Common_Cancel));
        }
        finally
        {
            this.conflictGate.Release();
        }
        return Array.IndexOf(options, choice) switch
        {
            0 => ConflictAction.Overwrite,
            1 => ConflictAction.OverwriteAll,
            2 => ConflictAction.OverwriteAllOlder,
            3 => ConflictAction.Skip,
            4 => ConflictAction.SkipAll,
            5 => ConflictAction.KeepBoth,
            _ => ConflictAction.Cancel
        };
    }

    [RelayCommand]
    Task Delete() => this.DeleteItems(this.settings.UseTrash);

    [RelayCommand]
    Task DeletePermanently() => this.DeleteItems(false);

    // ⌘⌫ / ⌥⌘⌫ like Finder: a Mac keyboard has no F8 without fn and its ⌫ is Backspace (parent folder). While the
    // command line has text the keys stay with it (Ctrl+Backspace deletes a word on Windows/Linux).
    [RelayCommand(CanExecute = nameof(CommandLineEmpty))]
    Task DeleteKey() => this.Delete();

    [RelayCommand(CanExecute = nameof(CommandLineEmpty))]
    Task DeletePermanentlyKey() => this.DeletePermanently();

    async Task DeleteItems(bool trash)
    {
        var tab = this.Tab;
        var items = tab?.SelectedEntries ?? [];
        if (tab == null || items.Count == 0)
            return;

        // servers have no trash - asked as a permanent delete
        trash = trash && !tab.IsArchive && !tab.IsRemote;
        var what = What(items);
        if (this.settings.ConfirmDelete || !trash)
        {
            var ok = await this.dialogs.Confirm(
                trash ? L.Delete_TrashTitle : L.Delete_PermanentTitle,
                trash ? L.Delete_TrashMessageFormat(what) : L.Delete_PermanentMessageFormat(what),
                trash ? L.Delete_TrashButton : L.Common_Delete);
            if (!ok)
                return;
        }

        var index = tab.CursorIndex;
        var path = tab.Path;
        this.RunOperation(L.Op_DeleteTitleFormat(what), FileOperationKind.Delete, null, async (progress, ct) =>
        {
            var result = await this.ops.DeleteAsync(items, trash, progress, ct);
            await this.Changed(path);
            return result;
        }, () =>
        {
            // the cursor stays at the same row - unless you've gone elsewhere meanwhile
            if (tab.Path == path)
                tab.SetCursor(index);
        });
        tab.MarkAll(false);
    }

    [RelayCommand]
    async Task Pack()
    {
        var tab = this.Tab;
        var sources = tab?.SelectedEntries ?? [];
        if (tab == null || sources.Count == 0)
            return;

        var name = sources.Count == 1 ? sources[0].NameWithoutExtension : new DirectoryInfo(tab.Path).Name;
        var input = await this.dialogs.Prompt(L.Pack_Title, L.Pack_MessageFormat(sources.Count, String.Join(", ", ArchiveFormats.PackExtensions)), Path.Combine(this.InactivePanel.Path, name + ".zip"), L.Cmd_Pack);
        if (String.IsNullOrWhiteSpace(input))
            return;

        var archive = VirtualFileSystem.Normalize(input);
        var what = What(sources);
        var destination = new JobDestination(Path.GetDirectoryName(archive) ?? archive, Path.GetFileName(archive));
        this.RunOperation(L.Op_PackTitleFormat(what, Path.GetFileName(archive)), FileOperationKind.Pack, destination, async (progress, ct) =>
        {
            var result = await this.ops.PackAsync(sources, input, this.ResolveConflict, progress, ct);
            await this.Changed(Path.GetDirectoryName(archive));
            return result;
        });
        tab.MarkAll(false);
    }

    [RelayCommand]
    async Task Unpack()
    {
        var tab = this.Tab;
        var archives = tab?.SelectedEntries.Where(x => x.IsArchive).ToList() ?? [];
        if (tab == null)
            return;

        if (archives.Count == 0)
        {
            await this.dialogs.Toast(L.Unpack_SelectArchives, ToastKind.Warning);
            return;
        }

        var target = await this.dialogs.Prompt(L.Cmd_Unpack, L.Unpack_MessageFormat(archives.Count), this.InactivePanel.Path, L.Cmd_Unpack);
        if (String.IsNullOrWhiteSpace(target))
            return;

        var what = archives.Count == 1 ? L.Op_QuotedFormat(archives[0].Name) : L.Op_ArchivesFormat(archives.Count);
        this.RunOperation(L.Op_UnpackTitleFormat(what), FileOperationKind.Extract, new JobDestination(VirtualFileSystem.Normalize(target)), async (progress, ct) =>
        {
            var result = await this.ops.ExtractAsync(archives, VirtualFileSystem.Normalize(target), this.settings.ExtractToFolderPerArchive, this.ResolveConflict, progress, ct);
            await this.Changed(target);
            return result;
        });
        tab.MarkAll(false);
    }

    /// <summary>Hands the work to the background job queue and returns straight away.</summary>
    void RunOperation(
        string title,
        FileOperationKind kind,
        JobDestination? destination,
        Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult>> work,
        Action? finished = null
    )
        => this.Jobs.Start(title, kind, destination, work, async job =>
        {
            finished?.Invoke();
            await (this.Left.ActiveTab?.RefreshAsync() ?? Task.CompletedTask);
            await (this.Right.ActiveTab?.RefreshAsync() ?? Task.CompletedTask);

            switch (job.State)
            {
                case JobState.Completed:
                    var done = job.Result is { Skipped: > 0 } r
                        ? L.Job_DoneSkippedFormat(title, r.Processed, r.Skipped)
                        : L.Job_DoneFormat(title, job.Result?.Processed ?? 0);
                    await this.dialogs.Toast(done, ToastKind.Success);
                    break;

                case JobState.CompletedWithErrors:
                    this.ShowJobs = true;
                    await this.dialogs.Toast(L.Job_FinishedWithErrorsFormat(title, job.Errors.Count), ToastKind.Warning);
                    break;

                case JobState.Failed:
                    this.ShowJobs = true;
                    await this.dialogs.Toast(L.Job_FailedFormat(title, job.Errors.FirstOrDefault() ?? String.Empty), ToastKind.Error);
                    break;

                case JobState.Cancelled:
                    await this.dialogs.Toast(L.Job_StoppedFormat(title), ToastKind.Warning);
                    break;
            }
        });

    // bumped whenever you show/hide the pane yourself - the auto-open never overrides that
    int jobsPaneToggles;

    // a quick job is over before the pane would be any use - only one still going after a moment opens it
    async void OnJobStarted(object? sender, JobViewModel job)
    {
        var toggles = this.jobsPaneToggles;
        await Task.Delay(700);
        if (job.IsRunning && toggles == this.jobsPaneToggles)
            this.ShowJobs = true;
    }

    [RelayCommand]
    void ToggleJobs()
    {
        this.jobsPaneToggles++;
        this.ShowJobs = !this.ShowJobs;
    }

    [RelayCommand]
    void StopJob(JobViewModel? job) => job?.Stop();

    [RelayCommand]
    void DismissJob(JobViewModel? job)
    {
        if (job != null)
            this.Jobs.Remove(job);
    }

    [RelayCommand]
    void ClearCompletedJobs() => this.Jobs.ClearFinished();

    [RelayCommand]
    Task ShowJobErrors(JobViewModel? job)
    {
        if (job is not { HasErrors: true })
            return Task.CompletedTask;

        var errors = String.Join("\n", job.Errors.Take(15));
        if (job.Errors.Count > 15)
            errors += "\n" + L.Job_AndMoreFormat(job.Errors.Count - 15);
        return this.dialogs.Alert(job.State == JobState.Failed ? L.Job_FailedTitleFormat(job.Title) : L.Job_ErrorsTitleFormat(job.Title), errors);
    }

    /// <summary>
    /// Shows where a finished job put things, cursor on the item. A panel already showing that folder takes it;
    /// otherwise the other panel goes there, so the folder you copied from stays open beside it.
    /// </summary>
    [RelayCommand]
    async Task GoToJob(JobViewModel? job)
    {
        if (job is not { CanGoTo: true, Destination: { } dest })
            return;

        var dir = VirtualFileSystem.Normalize(dest.Directory);
        var panel = this.ActivePanel.Path == dir ? this.ActivePanel : this.InactivePanel;
        this.Activate(panel);
        await panel.NavigateAsync(dir, dest.FocusName);
        this.RequestCommandLineFocus();
    }

    async Task Try(string title, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await this.dialogs.Alert(title, ex.Message);
        }
    }

    // ===========================================================================================
    // tools
    // ===========================================================================================

    [RelayCommand]
    Task Find() => this.navigator.NavigateTo<SearchViewModel>(vm => vm.StartPath = this.vfs.Resolve(this.ActivePanel.Path) is { IsArchive: false, IsRemote: false } l ? l.Path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [RelayCommand]
    async Task MultiRename()
    {
        var items = this.Tab?.SelectedEntries.ToList() ?? [];
        if (items.Count == 0)
        {
            await this.dialogs.Toast(L.Main_SelectToRename, ToastKind.Warning);
            return;
        }
        await this.navigator.NavigateTo<MultiRenameViewModel>(vm => vm.Items = items);
    }

    [RelayCommand]
    Task Synchronize() => this.navigator.NavigateTo<CompareViewModel>(vm =>
    {
        vm.LeftPath = this.Left.Path;
        vm.RightPath = this.Right.Path;
    });

    [RelayCommand]
    async Task CompareFiles()
    {
        // two marked files in one panel, or the cursor file in each panel
        var marked = this.Tab?.MarkedItems.Where(x => !x.IsDirectory).ToList() ?? [];
        FileEntry? a, b;
        if (marked.Count == 2)
        {
            (a, b) = (marked[0].Entry, marked[1].Entry);
        }
        else
        {
            a = this.Left.ActiveTab?.CursorItem?.Entry;
            b = this.Right.ActiveTab?.CursorItem?.Entry;
        }

        if (a is not { IsDirectory: false } || b is not { IsDirectory: false })
        {
            await this.dialogs.Toast(L.Main_CompareNeedsTwo, ToastKind.Warning);
            return;
        }

        var la = await this.vfs.MaterializeAsync(a.FullPath);
        var lb = await this.vfs.MaterializeAsync(b.FullPath);
        var equal = await ShinyCommander.Core.Compare.DirectoryComparer.ContentEqualAsync(la, lb, CancellationToken.None);
        await this.dialogs.Alert(L.Main_CompareByContent, equal
            ? L.Compare_IdenticalFormat(a.Name, b.Name)
            : L.Compare_DifferentFormat(a.Name, SizeFormatter.Exact(a.Size), b.Name, SizeFormatter.Exact(b.Size)));
    }

    [RelayCommand]
    Task Settings() => this.navigator.NavigateTo<SettingsViewModel>();

    /// <summary>⌘K / Ctrl+K - saved servers and storage accounts, and the file servers on the network.</summary>
    [RelayCommand]
    Task Connections() => this.navigator.NavigateTo<ConnectionsViewModel>();

    [RelayCommand]
    Task CustomizeToolbar() => this.navigator.NavigateTo<CustomizeToolbarViewModel>();

    /// <summary>A pane toolbar button: the pane it sits in becomes the active one, then the command runs there.</summary>
    public void RunToolbarCommand(PanelViewModel panel, string key)
    {
        this.Activate(panel);
        if (CommandCatalog.Resolve(this, key) is { } command && command.CanExecute(null))
            command.Execute(null);
    }

    /// <summary>
    /// The help table spells out keys the Mac glyphs make cryptic (⇞ ⇟ ↖ ↘ have no glyph in the mono font and render
    /// as specks) - modifier glyphs stay, as they read well and match the menus.
    /// </summary>
    static string ReadableKeys(string formatted)
    {
        var sb = new StringBuilder();
        foreach (var ch in formatted)
        {
            var word = ch switch
            {
                '⇞' => "PgUp",
                '⇟' => "PgDn",
                '↖' => "Home",
                '↘' => "End",
                '↩' => "Return",
                '⌫' => "Delete",
                '⌦' => "Fwd Delete",
                '⇥' => "Tab",
                '⎋' => "Esc",
                '␣' => "Space",
                _ => null
            };
            if (word == null)
            {
                sb.Append(ch);
                continue;
            }
            if (sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(word);
        }
        return sb.ToString();
    }

    [RelayCommand]
    Task Help()
    {
        // snapshot what is live on this page *before* navigating - the help page silences these
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine(L.Help_Intro);
        sb.AppendLine();

        var active = this.shortcuts.GetActiveShortcuts()
            .Where(x => !x.IsShadowed && !String.IsNullOrEmpty(x.Binding.Description))
            .GroupBy(x => x.Binding.Category ?? L.Help_General);

        foreach (var group in active)
        {
            sb.AppendLine($"## **{group.Key}**");
            sb.AppendLine();
            sb.AppendLine($"| {L.Help_Keys} | {L.Help_Action} |");
            sb.AppendLine("|---|---|");
            foreach (var s in group)
                sb.AppendLine($"| `{ReadableKeys(this.shortcuts.Format(s.Binding.Gesture))}` | {s.Binding.Description} |");
            sb.AppendLine();
        }

        sb.AppendLine($"## **{L.Help_CommandLine}**");
        sb.AppendLine();
        sb.AppendLine("* " + L.Help_Cd);
        sb.AppendLine("* " + L.Help_Shell);
        sb.AppendLine();
        sb.AppendLine($"## **{L.Help_Archives}**");
        sb.AppendLine();
        sb.AppendLine(L.Help_ArchivesText);

        var markdown = sb.ToString();
        return this.navigator.NavigateTo<HelpViewModel>(vm => vm.Markdown = markdown);
    }

    [RelayCommand]
    async Task Exit()
    {
        if (await this.quitGuard.ConfirmAsync())
            Application.Current?.Quit();
    }
}
