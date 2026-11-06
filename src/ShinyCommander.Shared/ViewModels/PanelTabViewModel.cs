using Shiny.Mediator;

namespace ShinyCommander.ViewModels;

/// <summary>One folder tab inside a panel: its listing, cursor, marks, sort and history.</summary>
public partial class PanelTabViewModel : ObservableObject, IDisposable
{
    readonly VirtualFileSystem vfs;
    readonly AppSettings settings;
    readonly ColumnLayout columns;
    readonly Stack<string> back = new();
    readonly Stack<string> forward = new();
    List<FileItemViewModel> all = [];
    FileSystemWatcher? watcher;
    CancellationTokenSource? loadCts;
    Timer? refreshDebounce;

    public PanelTabViewModel(VirtualFileSystem vfs, AppSettings settings, ColumnLayout columns, string path)
    {
        this.vfs = vfs;
        this.settings = settings;
        this.columns = columns;
        this.Path = path;
    }

    public event EventHandler? CursorMoved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Path { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<FileItemViewModel> Items { get; set; } = [];

    [ObservableProperty]
    public partial int CursorIndex { get; set; } = -1;

    [ObservableProperty]
    public partial SortColumn SortColumn { get; set; } = SortColumn.Name;

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

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
    public partial bool IsSelected { get; set; }

    public bool IsArchive { get; private set; }

    /// <summary>Inside a tar / 7z / rar / gz - browse and extract only.</summary>
    public bool IsReadOnlyArchive { get; private set; }

    /// <summary>"7z • read-only" etc. for the panel's location pill; empty on disk.</summary>
    public string ArchiveBadge { get; private set; } = String.Empty;

    public string Title
    {
        get
        {
            var name = System.IO.Path.GetFileName(this.Path);
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
    public string NameHeader => this.Header("Name", SortColumn.Name);
    public string ExtHeader => this.Header("Ext", SortColumn.Extension);
    public string SizeHeader => this.Header("Size", SortColumn.Size);
    public string DateHeader => this.Header("Date", SortColumn.Modified);
    public string AttrHeader => this.Header("Attr", SortColumn.Attributes);
    public string CreatedHeader => this.Header("Created", SortColumn.Created);

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
    }

    void UpdateCursorVisual()
    {
        if (this.CursorItem is { } c)
            c.Cursor = this.panelActive ? CursorState.Active : CursorState.Inactive;
    }

    public async Task NavigateAsync(string path, string? focusName = null, bool addHistory = true)
    {
        var normalized = VirtualFileSystem.Normalize(path);
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

        this.forward.Push(this.Path);
        var target = this.back.Pop();
        await this.NavigateAsync(target, null, false);
    }

    public async Task GoForwardAsync()
    {
        if (this.forward.Count == 0)
            return;

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

        var child = System.IO.Path.GetFileName(this.Path);
        await this.NavigateAsync(parent, child);
    }

    public Task GoRootAsync()
    {
        var loc = this.vfs.Resolve(this.Path);
        var root = System.IO.Path.GetPathRoot(loc.ArchiveFile ?? loc.Path) ?? "/";
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
                Location = this.IsBranchView && !e.IsParentLink ? System.IO.Path.GetRelativePath(this.Path, System.IO.Path.GetDirectoryName(e.FullPath)!) : null
            }).ToList();

            this.Error = null;
            var location = this.vfs.Resolve(this.Path);
            this.IsArchive = location.IsArchive;
            this.IsReadOnlyArchive = location.IsReadOnlyArchive;
            this.ArchiveBadge = location.IsArchive
                ? ArchiveFormats.Describe(ArchiveFormats.GetKind(location.ArchiveFile!)) + (location.IsReadOnlyArchive ? " • read-only" : String.Empty)
                : String.Empty;
            this.OnPropertyChanged(nameof(this.IsArchive));
            this.OnPropertyChanged(nameof(this.IsReadOnlyArchive));
            this.OnPropertyChanged(nameof(this.ArchiveBadge));
            this.ApplyView(focusName);
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

        var comparer = new FileEntryComparer(this.SortColumn, this.SortDescending);
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
        this.StatusText = $"{SizeFormatter.Format(markedSize, friendly)} / {SizeFormatter.Format(totalSize, friendly)} in {markedFiles.Count} / {files.Count} file(s), {markedDirs} / {dirs.Count} dir(s)";
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

    public void Dispose()
    {
        this.loadCts?.Cancel();
        this.watcher?.Dispose();
        this.refreshDebounce?.Dispose();
    }
}
