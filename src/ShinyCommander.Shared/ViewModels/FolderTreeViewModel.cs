using ShinyCommander.Core.Platform;

namespace ShinyCommander.ViewModels;

/// <summary>One visible row of a panel's folder tree.</summary>
public partial class FolderRow(string name, string path, int depth, string icon) : ObservableObject
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public int Depth { get; } = depth;
    public string Icon { get; } = icon;
    public Thickness Indent => new(6 + this.Depth * 14, 0, 6, 0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsExpanded { get; set; }

    /// <summary>Unknown until the folder is opened - assume it has subfolders, like Finder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool HasChildren { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Chevron => !this.HasChildren ? " " : this.IsExpanded ? "▾" : "▸";
}

/// <summary>
/// A panel's folder tree as a flat list of visible rows (a virtualized CollectionView draws it). Expanding inserts a
/// folder's subfolders under it; revealing a path loads one level per ancestor in the background and updates the
/// list once per level - so a folder with hundreds of subfolders, or a deep path, never stalls the UI thread.
/// </summary>
public partial class FolderTreeViewModel(AppSettings settings) : ObservableObject
{
    /// <summary>Replaced as a whole on every change - one list update instead of one per inserted folder.</summary>
    [ObservableProperty]
    public partial ObservableCollection<FolderRow> Rows { get; set; } = [];

    [ObservableProperty]
    public partial FolderRow? Selected { get; set; }

    partial void OnSelectedChanged(FolderRow? oldValue, FolderRow? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
            newValue.IsSelected = true;
    }

    public void EnsureRoots()
    {
        if (this.Rows.Count > 0)
            return;
        this.Rows = new(DriveService.GetDrives().Select(d => new FolderRow(d.Kind == DriveKind.Home ? L.Tree_Home : d.Name, d.RootPath, 0, DriveIconConverter.For(d))));
    }

    Task<List<string>> LoadChildren(string path) => Task.Run(() =>
    {
        try
        {
            var hidden = settings.ShowHidden;
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = hidden ? 0 : FileAttributes.Hidden };
            return new DirectoryInfo(path)
                .EnumerateDirectories("*", opts)
                .Where(d => hidden || !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.FullName)
                .ToList();
        }
        catch
        {
            return [];
        }
    });

    public async Task ToggleAsync(FolderRow row)
    {
        if (row.IsExpanded)
            this.Collapse(row);
        else
            await this.ExpandAsync(row);
    }

    void Collapse(FolderRow row)
    {
        var index = this.Rows.IndexOf(row);
        if (index < 0)
            return;
        var end = index + 1;
        while (end < this.Rows.Count && this.Rows[end].Depth > row.Depth)
            end++;
        if (this.Selected != null && this.Rows.IndexOf(this.Selected) is var si && si > index && si < end)
            this.Selected = row;
        row.IsExpanded = false;
        if (end > index + 1)
            this.Rows = new(this.Rows.Take(index + 1).Concat(this.Rows.Skip(end)));
    }

    async Task ExpandAsync(FolderRow row)
    {
        var children = await this.LoadChildren(row.Path);
        var index = this.Rows.IndexOf(row);
        if (index < 0 || row.IsExpanded)
            return;

        row.HasChildren = children.Count > 0;
        row.IsExpanded = true;
        if (children.Count == 0)
            return;
        var rows = children.Select(path => new FolderRow(System.IO.Path.GetFileName(path), path, row.Depth + 1, "folder"));
        this.Rows = new(this.Rows.Take(index + 1).Concat(rows).Concat(this.Rows.Skip(index + 1)));
    }

    /// <summary>Expand down to <paramref name="path"/> (or its nearest shown ancestor) and select it. Returns the selected row.</summary>
    public async Task<FolderRow?> RevealAsync(string path, CancellationToken ct)
    {
        this.EnsureRoots();
        var target = path;
        while (!Directory.Exists(target) && System.IO.Path.GetDirectoryName(target) is { } up)
            target = up;

        var row = this.Rows.Where(r => r.Depth == 0 && IsUnder(target, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
        while (row != null && !ct.IsCancellationRequested && !SamePath(row.Path, target))
        {
            if (!row.IsExpanded)
                await this.ExpandAsync(row);

            var next = this.ChildrenOf(row).FirstOrDefault(c => IsUnder(target, c.Path));
            if (next == null && row.IsExpanded)
            {
                // listed before this folder existed - reread that level
                this.Collapse(row);
                await this.ExpandAsync(row);
                next = this.ChildrenOf(row).FirstOrDefault(c => IsUnder(target, c.Path));
            }
            if (next == null)
                break; // hidden folder etc. - stop at the nearest ancestor shown
            row = next;
        }

        if (row != null && !ct.IsCancellationRequested)
            this.Selected = row;
        return row;
    }

    IEnumerable<FolderRow> ChildrenOf(FolderRow row)
    {
        var index = this.Rows.IndexOf(row);
        for (var i = index + 1; i < this.Rows.Count && this.Rows[i].Depth > row.Depth; i++)
        {
            if (this.Rows[i].Depth == row.Depth + 1)
                yield return this.Rows[i];
        }
    }

    static bool SamePath(string a, string b) => String.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    static bool IsUnder(string path, string folder)
    {
        if (SamePath(path, folder) || folder == "/")
            return true;
        return path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
    }
}
