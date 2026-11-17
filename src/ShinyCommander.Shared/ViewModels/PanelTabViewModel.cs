using Shiny.Mediator;
using ShinyCommander.Core.Platform;

namespace ShinyCommander.ViewModels;

/// <summary>One folder tab inside a panel: its listing, cursor, marks, sort and history.</summary>
public partial class PanelTabViewModel : ObservableObject, IDisposable
{
    readonly VirtualFileSystem vfs;
    readonly AppSettings settings;
    readonly ColumnLayout columns;
    readonly FileColumns fileColumns;
    readonly Stack<string> back = new();
    readonly Stack<string> forward = new();
    List<FileItemViewModel> all = [];
    FileSystemWatcher? watcher;
    CancellationTokenSource? loadCts;
    CancellationTokenSource? metadataCts;
    Timer? refreshDebounce;

    public PanelTabViewModel(VirtualFileSystem vfs, AppSettings settings, ColumnLayout columns, FileColumns fileColumns, string path)
    {
        this.vfs = vfs;
        this.settings = settings;
        this.columns = columns;
        this.fileColumns = fileColumns;
        this.Path = path;
        fileColumns.Changed += this.OnFileColumnsChanged;
    }

    public event EventHandler? CursorMoved;

    /// <summary>A locked tab was asked to go somewhere else - the panel opens that in a new tab instead.</summary>
    public event EventHandler<(string Path, string? FocusName)>? OpenInNewTabRequested;

    /// <summary>Stays on its folder: going anywhere else opens a new tab, and it can't be closed until unlocked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    public partial bool IsLocked { get; set; }

    /// <summary>The folder a locked tab belongs to - it can wander while tabs are turned off, and goes back here.</summary>
    public string? LockedPath { get; private set; }

    partial void OnIsLockedChanged(bool value) => this.LockedPath = value ? this.Path : null;

    /// <summary>A locked tab that wandered (tabs were off) goes back to its folder. False when it was already there.</summary>
    public async Task<bool> ReturnToLockedPathAsync()
    {
        if (this.LockedPath is not { } home || String.Equals(home, this.Path, StringComparison.Ordinal))
            return false;

        await this.GoAsync(home, null, addHistory: false);
        return true;
    }

    /// <summary>
    /// The panel's first tab: always there - it can't be closed or locked (it's permanently locked in place), but unlike a
    /// locked tab it can still go to any folder.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    public partial bool IsFirst { get; set; }

    /// <summary>Every tab but the first and locked ones - they show the ✕, whether they're the one you're on or not.</summary>
    public bool CanClose => !this.IsFirst && !this.IsLocked;

    /// <summary>The name the user gave the tab - shown instead of the folder's when set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string? Alias { get; set; }

    /// <summary>The tab's colour (a <see cref="TabColors"/> key), or null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabColor), nameof(HasColor), nameof(TabBackground), nameof(IconColor))]
    public partial string? ColorKey { get; set; }

    public Color? TabColor => TabColors.Get(this.ColorKey);
    public bool HasColor => this.TabColor != null;

    /// <summary>The panel's colour on the tab you're on (it opens into the panel), otherwise a tint of the tab's colour.</summary>
    public Color TabBackground => this.IsSelected
        ? ThemeColors.Get("Panel")
        : this.TabColor?.WithAlpha(ThemeColors.IsDark ? 0.3f : 0.2f) ?? ThemeColors.Get("Header");

    /// <summary>The tab's own colour when it has one, otherwise the accent on the tab you're on.</summary>
    public Color IconColor => this.TabColor ?? ThemeColors.Get(this.IsSelected ? "Accent" : "Subtle");

    /// <summary>The light/dark theme changed - these colours are picked in code, so ask for them again.</summary>
    public void RefreshTheme()
    {
        this.OnPropertyChanged(nameof(this.TabBackground));
        this.OnPropertyChanged(nameof(this.IconColor));
    }

    /// <summary>The icon the user picked for the tab, or null for the automatic one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon))]
    public partial string? IconKey { get; set; }

    /// <summary>The tab's icon: the one picked for it, otherwise <see cref="AutomaticIcon"/>.</summary>
    public string Icon => this.IconKey ?? this.AutomaticIcon;

    /// <summary>What the place is (home, cloud...), otherwise a folder.</summary>
    public string AutomaticIcon => this.Place is { } place ? DriveIconConverter.For(place) : "folder";

    DriveEntry? Place => Places.Value.FirstOrDefault(x => String.Equals(x.RootPath, this.Path, StringComparison.Ordinal));

    // Home, Desktop, iCloud Drive... - their tabs are titled by what they are, not the folder name ("com~apple~CloudDocs")
    static readonly Lazy<IReadOnlyList<DriveEntry>> Places = new(DriveService.GetPlaces);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Icon), nameof(AutomaticIcon))]
    public partial string Path { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<FileItemViewModel> Items { get; set; } = [];

    [ObservableProperty]
    public partial int CursorIndex { get; set; } = -1;

    [ObservableProperty]
    public partial SortColumn SortColumn { get; set; } = SortColumn.Name;

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    /// <summary>The custom column (IFileColumn.Key) it's sorted by when <see cref="SortColumn"/> is Custom.</summary>
    [ObservableProperty]
    public partial string? SortKey { get; set; }

    /// <summary>The folder's column set (FileColumns.Detect): null for a mixed folder, an archive or a server.</summary>
    [ObservableProperty]
    public partial FileColumnSet? ColumnSet { get; set; }

    /// <summary>The set's columns, shown right of the built-in ones.</summary>
    public IReadOnlyList<IFileColumn> CustomColumns { get; private set; } = [];

    /// <summary>The custom columns' metadata is still being read.</summary>
    [ObservableProperty]
    public partial bool IsReadingMetadata { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string QuickFilter { get; set; } = String.Empty;

    [ObservableProperty]
    public partial bool IsBranchView { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = String.Empty;

    [ObservableProperty]
    public partial int MarkedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveTab), nameof(TabBackground), nameof(IconColor))]
    public partial bool IsSelected { get; set; }

    public bool IsArchive { get; private set; }

    /// <summary>On a server or storage account.</summary>
    public bool IsRemote { get; private set; }

    /// <summary>Inside a tar / 7z / rar / gz - browse and extract only.</summary>
    public bool IsReadOnlyArchive { get; private set; }

    /// <summary>"7z • read-only" etc. for the panel's location pill; empty on disk.</summary>
    public string ArchiveBadge { get; private set; } = String.Empty;

    public string Title
    {
        get
        {
            if (!String.IsNullOrWhiteSpace(this.Alias))
                return this.Alias;

            if (this.Place is { } place)
                return place.Kind == DriveKind.Home ? L.Tree_Home : place.Label;

            // a server's root is titled by the connection ("Work NAS"), not "me@nas"
            if (RemotePath.IsRemote(this.Path) && RemotePath.GetParent(this.Path) == null)
                return this.vfs.Remote.Find(this.Path)?.Name ?? RemotePath.GetName(this.Path);

            var name = VirtualFileSystem.GetName(this.Path);
            return String.IsNullOrEmpty(name) ? this.Path : name;
        }
    }

    public FileItemViewModel? CursorItem => this.CursorIndex >= 0 && this.CursorIndex < this.Items.Count ? this.Items[this.CursorIndex] : null;

    public bool CanGoBack => this.back.Count > 0;
    public bool CanGoForward => this.forward.Count > 0;

    public IReadOnlyList<FileItemViewModel> MarkedItems => this.Items.Where(x => x.IsMarked).ToList();

    /// <summary>TC semantics: operations apply to the marked items, or to the item under the cursor when nothing is marked.</summary>
    public IReadOnlyList<FileEntry> SelectedEntries
    {
        get
        {
            var marked = this.Items.Where(x => x.IsMarked).Select(x => x.Entry).ToList();
            if (marked.Count > 0)
                return marked;

            return this.CursorItem is { Entry.IsParentLink: false } c ? [c.Entry] : [];
        }
    }

    partial void OnQuickFilterChanged(string value) => this.ApplyView(this.CursorItem?.Entry.Name);
    partial void OnSortColumnChanged(SortColumn value) => this.SortChanged();
    partial void OnSortDescendingChanged(bool value) => this.SortChanged();
    partial void OnSortKeyChanged(string? value) => this.SortChanged();
    partial void OnMarkedCountChanged(int value) => this.OnPropertyChanged(nameof(this.MarkedBadge));

    /// <summary>BadgeView hides itself on an empty string.</summary>
    public string MarkedBadge => this.MarkedCount > 0 ? this.MarkedCount.ToString() : String.Empty;

    void SortChanged()
    {
        foreach (var name in new[] { nameof(this.NameHeader), nameof(this.ExtHeader), nameof(this.SizeHeader), nameof(this.DateHeader), nameof(this.CreatedHeader), nameof(this.AttrHeader) })
            this.OnPropertyChanged(name);
        this.ApplyView(this.CursorItem?.Entry.Name);
    }

    string Header(string text, SortColumn column) => this.SortColumn == column ? text + (this.SortDescending ? " ▼" : " ▲") : text;
    public string NameHeader => this.Header(L.Column_Name, SortColumn.Name);
    public string ExtHeader => this.Header(L.Column_Ext, SortColumn.Extension);
    public string SizeHeader => this.Header(L.Column_Size, SortColumn.Size);
    public string DateHeader => this.Header(L.Column_Date, SortColumn.Modified);
    public string AttrHeader => this.Header(L.Column_Attr, SortColumn.Attributes);
    public string CreatedHeader => this.Header(L.Column_Created, SortColumn.Created);

    partial void OnCursorIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < this.Items.Count)
            this.Items[oldValue].Cursor = CursorState.None;

        this.UpdateCursorVisual();
        this.OnPropertyChanged(nameof(this.CursorItem));
        this.CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    bool panelActive;
    public void SetPanelActive(bool active)
    {
        this.panelActive = active;
        this.UpdateCursorVisual();
        this.OnPropertyChanged(nameof(this.IsActiveTab));
    }

    /// <summary>The tab you're on, in the panel that has the focus - outlined in the accent like the panel itself.</summary>
    public bool IsActiveTab => this.IsSelected && this.panelActive;

    void UpdateCursorVisual()
    {
        if (this.CursorItem is { } c)
            c.Cursor = this.panelActive ? CursorState.Active : CursorState.Inactive;
    }

    public async Task NavigateAsync(string path, string? focusName = null, bool addHistory = true)
    {
        var normalized = VirtualFileSystem.Normalize(path);
        // with tabs turned off in Settings there's nowhere to open another one - a lock is kept, but doesn't apply
        if (this.IsLocked && !this.settings.HideTabs && !String.Equals(normalized, this.Path, StringComparison.Ordinal))
        {
            this.OpenInNewTabRequested?.Invoke(this, (normalized, focusName));
            return;
        }
        await this.GoAsync(normalized, focusName, addHistory);
    }

    async Task GoAsync(string normalized, string? focusName, bool addHistory)
    {
        if (addHistory && !String.Equals(normalized, this.Path, StringComparison.Ordinal))
        {
            this.back.Push(this.Path);
            this.forward.Clear();
        }
        var previous = this.Path;
        this.Path = normalized;
        this.QuickFilter = String.Empty;
        this.IsBranchView = false;

        if (!await this.LoadAsync(focusName))
        {
            // couldn't open - stay where we were
            this.Path = previous;
            if (addHistory && this.back.Count > 0)
                this.back.Pop();
            await this.LoadAsync(focusName);
        }
        this.NotifyHistory();
    }

    public async Task GoBackAsync()
    {
        if (this.back.Count == 0)
            return;

        // a locked tab keeps its history as it is - the step opens in a new tab
        if (this.IsLocked && !this.settings.HideTabs)
        {
            await this.NavigateAsync(this.back.Peek(), null, false);
            return;
        }

        this.forward.Push(this.Path);
        var target = this.back.Pop();
        await this.NavigateAsync(target, null, false);
    }

    public async Task GoForwardAsync()
    {
        if (this.forward.Count == 0)
            return;

        // a locked tab keeps its history as it is - the step opens in a new tab
        if (this.IsLocked && !this.settings.HideTabs)
        {
            await this.NavigateAsync(this.forward.Peek(), null, false);
            return;
        }

        this.back.Push(this.Path);
        var target = this.forward.Pop();
        await this.NavigateAsync(target, null, false);
    }

    void NotifyHistory()
    {
        this.OnPropertyChanged(nameof(this.CanGoBack));
        this.OnPropertyChanged(nameof(this.CanGoForward));
    }

    public async Task GoUpAsync()
    {
        var parent = this.vfs.GetParent(this.Path);
        if (parent == null)
            return;

        var child = VirtualFileSystem.GetName(this.Path);
        await this.NavigateAsync(parent, child);
    }

    public Task GoRootAsync()
    {
        var loc = this.vfs.Resolve(this.Path);
        var root = loc.IsRemote
            ? RemotePath.Split(loc.Path).Root + "/"
            : System.IO.Path.GetPathRoot(loc.ArchiveFile ?? loc.Path) ?? "/";
        return this.NavigateAsync(root);
    }

    /// <summary>Reloads the listing, keeping the cursor and the marks where they were (Ctrl+R / watcher).</summary>
    public Task<bool> RefreshAsync() => this.LoadAsync(this.CursorItem?.Entry.Name, keepMarks: true);

    public async Task<bool> LoadAsync(string? focusName = null, bool keepMarks = false)
    {
        this.loadCts?.Cancel();
        var cts = this.loadCts = new CancellationTokenSource();
        var marked = keepMarks ? this.all.Where(x => x.IsMarked).Select(x => x.Entry.FullPath).ToHashSet() : [];
        var sizes = keepMarks ? this.all.Where(x => x.CalculatedSize != null).ToDictionary(x => x.Entry.FullPath, x => x.CalculatedSize) : [];

        this.IsLoading = true;
        try
        {
            IReadOnlyList<FileEntry> entries;
            if (this.IsBranchView)
                entries = await this.LoadBranchAsync(cts.Token);
            else
                entries = await this.vfs.ListAsync(this.Path, this.settings.ShowHidden, cts.Token);

            if (cts.IsCancellationRequested)
                return true;

            var friendly = this.settings.FriendlySizes;
            this.all = entries.Select(e => new FileItemViewModel(e, friendly)
            {
                Columns = this.columns,
                IsMarked = marked.Contains(e.FullPath),
                CalculatedSize = sizes.GetValueOrDefault(e.FullPath),
                Location = this.IsBranchView && !e.IsParentLink ? System.IO.Path.GetRelativePath(this.Path, VirtualFileSystem.GetDirectory(e.FullPath)!) : null
            }).ToList();

            this.Error = null;
            var location = this.vfs.Resolve(this.Path);
            this.IsArchive = location.IsArchive;
            this.IsRemote = location.IsRemote;
            this.IsReadOnlyArchive = location.IsReadOnlyArchive;
            this.ArchiveBadge = location.IsArchive
                ? (location.IsReadOnlyArchive
                    ? L.Panel_ArchiveReadOnlyFormat(ArchiveFormats.Describe(ArchiveFormats.GetKind(location.ArchiveFile!)))
                    : ArchiveFormats.Describe(ArchiveFormats.GetKind(location.ArchiveFile!)))
                : location.Connection is { } connection
                    ? $"{RemoteProtocolText(connection.Protocol)} • {connection.Name}"
                    : String.Empty;
            this.OnPropertyChanged(nameof(this.IsArchive));
            this.OnPropertyChanged(nameof(this.IsRemote));
            this.OnPropertyChanged(nameof(this.Title));
            this.OnPropertyChanged(nameof(this.IsReadOnlyArchive));
            this.OnPropertyChanged(nameof(this.ArchiveBadge));
            this.UpdateColumnSet();
            this.ApplyView(focusName);
            this.LoadMetadata();
            this.Watch();
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
            return false;
        }
        finally
        {
            if (this.loadCts == cts)
                this.IsLoading = false;
        }
    }

    public static string RemoteProtocolText(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => "SFTP",
        RemoteProtocol.Ftp => "FTP",
        RemoteProtocol.Ftps => "FTPS",
        RemoteProtocol.WebDav => "WebDAV",
        RemoteProtocol.WebDavs => "WebDAV (https)",
        RemoteProtocol.S3 => "S3",
        _ => "Azure Blob"
    };

    async Task<IReadOnlyList<FileEntry>> LoadBranchAsync(CancellationToken ct)
    {
        // Ctrl+B: every file below the current folder in one flat list
        var list = new List<FileEntry>();
        if (this.vfs.GetParent(this.Path) is { } parent)
            list.Add(FileEntry.ParentLink(parent));

        await foreach (var e in ShinyCommander.Core.Search.FileSearcher.SearchAsync(new ShinyCommander.Core.Search.SearchOptions
        {
            StartPath = this.Path,
            IncludeHidden = this.settings.ShowHidden,
            FilesOnly = true
        }, null, ct))
        {
            list.Add(e);
            if (list.Count > 50_000)
                break;
        }
        return list;
    }

    void ApplyView(string? focusName)
    {
        IEnumerable<FileItemViewModel> view = this.all;
        if (!String.IsNullOrWhiteSpace(this.QuickFilter))
        {
            var mask = new ShinyCommander.Core.Search.WildcardMask(this.QuickFilter);
            view = view.Where(x => x.Entry.IsParentLink || mask.IsMatch(x.Entry.Name));
        }

        // sorted by a column this folder doesn't have (one of the music folder's, in a photo folder) - by name until it's back
        var column = this.SortedColumn;
        var comparer = this.SortColumn == SortColumn.Custom && column == null
            ? new FileEntryComparer(SortColumn.Name, false)
            : new FileEntryComparer(this.SortColumn, this.SortDescending, column == null ? null : e => this.CustomValue(column, e));
        var sorted = view.OrderBy(x => x.Entry, comparer).ToList();

        // parent link always first even when sorting descending
        var parent = sorted.FindIndex(x => x.Entry.IsParentLink);
        if (parent > 0)
        {
            var p = sorted[parent];
            sorted.RemoveAt(parent);
            sorted.Insert(0, p);
        }

        foreach (var item in this.Items)
            item.Cursor = CursorState.None;

        this.Items = new ObservableCollection<FileItemViewModel>(sorted);

        var index = 0;
        if (focusName != null)
        {
            var found = sorted.FindIndex(x => String.Equals(x.Entry.Name, focusName, StringComparison.Ordinal));
            if (found < 0)
                found = sorted.FindIndex(x => String.Equals(x.Entry.Name, focusName, StringComparison.OrdinalIgnoreCase));
            index = found >= 0 ? found : Math.Min(Math.Max(this.CursorIndex, 0), sorted.Count - 1);
        }

        this.CursorIndex = -1;
        this.CursorIndex = sorted.Count == 0 ? -1 : Math.Clamp(index, 0, sorted.Count - 1);
        this.UpdateStatus();
    }

    public void UpdateStatus()
    {
        var files = this.Items.Where(x => !x.IsDirectory).ToList();
        var dirs = this.Items.Where(x => x.IsDirectory && !x.Entry.IsParentLink).ToList();
        var markedFiles = files.Where(x => x.IsMarked).ToList();
        var markedDirs = dirs.Count(x => x.IsMarked);
        var friendly = this.settings.FriendlySizes;

        var markedSize = markedFiles.Sum(x => x.Entry.Size) + dirs.Where(x => x.IsMarked).Sum(x => x.CalculatedSize ?? 0);
        var totalSize = files.Sum(x => x.Entry.Size);

        this.MarkedCount = markedFiles.Count + markedDirs;
        this.StatusText = L.Panel_StatusFormat(SizeFormatter.Format(markedSize, friendly), SizeFormatter.Format(totalSize, friendly), markedFiles.Count, files.Count, markedDirs, dirs.Count);
    }

    // --- cursor & marking -------------------------------------------------------------------

    public void MoveCursor(int delta, bool mark = false)
    {
        if (this.Items.Count == 0)
            return;

        if (mark && this.CursorItem is { Entry.IsParentLink: false } current)
        {
            current.IsMarked = !current.IsMarked;
            this.UpdateStatus();
        }
        this.CursorIndex = Math.Clamp(this.CursorIndex + delta, 0, this.Items.Count - 1);
    }

    public void SetCursor(int index) => this.CursorIndex = Math.Clamp(index, 0, Math.Max(0, this.Items.Count - 1));

    public void SetCursor(FileItemViewModel item)
    {
        var index = this.Items.IndexOf(item);
        if (index >= 0)
            this.CursorIndex = index;
    }

    public void ToggleMark(FileItemViewModel? item)
    {
        if (item == null || item.Entry.IsParentLink)
            return;

        item.IsMarked = !item.IsMarked;
        this.UpdateStatus();
    }

    public void MarkRange(int from, int to)
    {
        var (a, b) = from <= to ? (from, to) : (to, from);
        for (var i = Math.Max(0, a); i <= Math.Min(b, this.Items.Count - 1); i++)
        {
            if (!this.Items[i].Entry.IsParentLink)
                this.Items[i].IsMarked = true;
        }
        this.UpdateStatus();
    }

    public void MarkAll(bool marked)
    {
        foreach (var item in this.Items.Where(x => !x.Entry.IsParentLink))
            item.IsMarked = marked;
        this.UpdateStatus();
    }

    public void InvertMarks()
    {
        foreach (var item in this.Items.Where(x => !x.Entry.IsParentLink && !x.IsDirectory))
            item.IsMarked = !item.IsMarked;
        this.UpdateStatus();
    }

    public void MarkByMask(string mask, bool marked)
    {
        var m = new ShinyCommander.Core.Search.WildcardMask(mask);
        foreach (var item in this.Items.Where(x => !x.Entry.IsParentLink && m.IsMatch(x.Entry.Name)))
            item.IsMarked = marked;
        this.UpdateStatus();
    }

    public void MarkSameExtension()
    {
        if (this.CursorItem is not { IsDirectory: false } c)
            return;
        foreach (var item in this.Items.Where(x => !x.IsDirectory && String.Equals(x.Extension, c.Extension, StringComparison.OrdinalIgnoreCase)))
            item.IsMarked = true;
        this.UpdateStatus();
    }

    /// <summary>Type-ahead: jump to the next item starting with the typed text.</summary>
    public void JumpTo(string prefix)
    {
        var start = Math.Max(this.CursorIndex, 0);
        for (var i = 0; i < this.Items.Count; i++)
        {
            var idx = (start + i) % this.Items.Count;
            if (this.Items[idx].Entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                this.CursorIndex = idx;
                return;
            }
        }
    }

    /// <summary>The custom column the tab is sorted by, if it's one this folder shows.</summary>
    IFileColumn? SortedColumn => this.SortColumn == SortColumn.Custom ? this.CustomColumns.FirstOrDefault(x => x.Key == this.SortKey) : null;

    IComparable? CustomValue(IFileColumn column, FileEntry entry)
        => column.Kinds.Contains(FileKinds.Of(entry)) && this.fileColumns.Cached(entry) is { } metadata ? column.Value(metadata) : null;

    /// <summary>A custom column's header: by it, then the other way.</summary>
    public void SortByCustom(string key)
    {
        if (this.SortColumn == SortColumn.Custom && this.SortKey == key)
        {
            this.SortDescending = !this.SortDescending;
            return;
        }
        this.SortKey = key;
        this.SortDescending = false;
        this.SortColumn = SortColumn.Custom;
    }

    public void SortBy(SortColumn column)
    {
        if (this.SortColumn == column)
        {
            this.SortDescending = !this.SortDescending;
        }
        else
        {
            this.SortDescending = column is SortColumn.Size or SortColumn.Modified; // TC: newest/largest first
            this.SortColumn = column;
        }
    }

    // --- auto refresh -----------------------------------------------------------------------

    void Watch()
    {
        this.watcher?.Dispose();
        this.watcher = null;
        if (this.IsArchive || this.IsBranchView || !Directory.Exists(this.Path))
            return;

        try
        {
            var w = new FileSystemWatcher(this.Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            w.Created += this.OnWatcher;
            w.Deleted += this.OnWatcher;
            w.Renamed += this.OnWatcher;
            w.Changed += this.OnWatcher;
            w.EnableRaisingEvents = true;
            this.watcher = w;
        }
        catch
        {
            // network shares etc. may not support watching
        }
    }

    void OnWatcher(object sender, FileSystemEventArgs e)
    {
        this.refreshDebounce?.Dispose();
        this.refreshDebounce = new Timer(_ => MainThread.BeginInvokeOnMainThread(() => _ = this.RefreshAsync()), null, 400, Timeout.Infinite);
    }

    // --- custom columns ---------------------------------------------------------------------

    void UpdateColumnSet()
    {
        // an archive's or a server's files would have to be downloaded to read
        this.ColumnSet = this.IsArchive || this.IsRemote ? null : this.fileColumns.Detect(this.all.Select(x => x.Entry).ToList());
        this.CustomColumns = this.ColumnSet == null ? [] : this.fileColumns.ColumnsOf(this.ColumnSet);
        this.OnPropertyChanged(nameof(this.CustomColumns));
    }

    // a set turned on or off, or its columns changed in Settings
    void OnFileColumnsChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
    {
        this.UpdateColumnSet();
        this.ApplyView(this.CursorItem?.Entry.Name);
        this.LoadMetadata();
    });

    /// <summary>
    /// Fills the rows' metadata for the custom columns: what's cached at once, the rest read in the background in list
    /// order (the rows on screen first) and handed to the rows in batches. Cloud-only files aren't read - that would
    /// download them.
    /// </summary>
    void LoadMetadata()
    {
        this.metadataCts?.Cancel();
        this.IsReadingMetadata = false;
        var kinds = this.CustomColumns.SelectMany(x => x.Kinds).ToHashSet();
        if (kinds.Count == 0)
            return;

        var position = this.Items.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i);
        var pending = new List<FileItemViewModel>();
        foreach (var item in this.all.Where(x => kinds.Contains(FileKinds.Of(x.Entry))))
        {
            item.Metadata ??= this.fileColumns.Cached(item.Entry);
            if (item.Metadata == null && !item.Entry.IsCloudOnly)
                pending.Add(item);
        }
        if (pending.Count == 0)
            return;

        var cts = this.metadataCts = new CancellationTokenSource();
        var ordered = pending.OrderBy(x => position.GetValueOrDefault(x, Int32.MaxValue)).ToList();
        this.IsReadingMetadata = true;
        _ = Task.Run(() => this.ReadMetadata(ordered, cts));
    }

    void ReadMetadata(IReadOnlyList<FileItemViewModel> pending, CancellationTokenSource cts)
    {
        var batch = new List<(FileItemViewModel Item, FileMetadata Metadata)>();
        var since = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (var item in pending)
            {
                cts.Token.ThrowIfCancellationRequested();
                batch.Add((item, this.fileColumns.Read(item.Entry, cts.Token)));
                if (since.ElapsedMilliseconds > 150)
                {
                    this.ApplyMetadata(batch, cts, false);
                    batch = [];
                    since.Restart();
                }
            }
            this.ApplyMetadata(batch, cts, true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    void ApplyMetadata(IReadOnlyList<(FileItemViewModel Item, FileMetadata Metadata)> batch, CancellationTokenSource cts, bool done) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (cts.IsCancellationRequested)
            return;

        foreach (var (item, metadata) in batch)
            item.Metadata = metadata;

        if (!done)
            return;

        this.IsReadingMetadata = false;
        // the values it's sorted by have all come in
        if (this.SortedColumn != null)
            this.ApplyView(this.CursorItem?.Entry.Name);
    });

    public void Dispose()
    {
        this.fileColumns.Changed -= this.OnFileColumnsChanged;
        this.metadataCts?.Cancel();
        this.loadCts?.Cancel();
        this.watcher?.Dispose();
        this.refreshDebounce?.Dispose();
    }
}
