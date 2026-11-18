namespace ShinyCommander.ViewModels;

/// <summary>
/// The Columns screen: the detail columns beside Name as they stand, in order (move, remove), beside every column not
/// shown (click to add) - built-in and custom alike. Each change is saved and shows in both panes straight away. In a
/// folder with a setup it starts on that folder's columns, with every folder's a switch away.
/// </summary>
[ShellMap<ColumnsPage>("Columns")]
public partial class ColumnsViewModel(ColumnSet columns, FileColumns fileColumns, FolderSetups folderSetups, INavigator navigator) : ObservableObject
{
    FolderSetup? setup;

    /// <summary>The active folder's setup, if it has one - set before the screen shows.</summary>
    public FolderSetup? Setup
    {
        get => this.setup;
        set
        {
            this.setup = value;
            this.OnPropertyChanged(nameof(this.HasSetup));
            this.OnPropertyChanged(nameof(this.ThisFolder));
            if (this.ForFolder == (value != null))
                this.Load();
            else
                this.ForFolder = value != null;
        }
    }

    public bool HasSetup => this.setup != null;
    public string ThisFolder => this.setup == null ? String.Empty : L.Columns_ThisFolderFormat(VirtualFileSystem.GetName(this.setup.Path));

    /// <summary>Editing the folder setup's columns rather than every folder's.</summary>
    [ObservableProperty]
    public partial bool ForFolder { get; set; }

    partial void OnForFolderChanged(bool value) => this.Load();

    [ObservableProperty]
    public partial IReadOnlyList<ColumnChoice> Shown { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ColumnChoice> Available { get; set; } = [];

    [RelayCommand]
    void ShowAllFolders() => this.ForFolder = false;

    [RelayCommand]
    void ShowThisFolder() => this.ForFolder = this.setup != null;

    [RelayCommand]
    void Add(ColumnChoice column) => this.Save([.. this.Shown, column]);

    [RelayCommand]
    void Remove(ColumnChoice column) => this.Save(this.Shown.Where(x => x != column));

    [RelayCommand]
    void MoveLeft(ColumnChoice column) => this.Move(column, -1);

    [RelayCommand]
    void MoveRight(ColumnChoice column) => this.Move(column, 1);

    [RelayCommand]
    void Reset() => this.Save(ColumnSet.Default.Select(this.Choice).OfType<ColumnChoice>());

    [RelayCommand]
    Task Close() => navigator.GoBack();

    void Move(ColumnChoice column, int by)
    {
        var list = this.Shown.ToList();
        var from = list.IndexOf(column);
        var to = from + by;
        if (from < 0 || to < 0 || to >= list.Count)
            return;

        (list[from], list[to]) = (list[to], list[from]);
        this.Save(list);
    }

    void Save(IEnumerable<ColumnChoice> shown)
    {
        var keys = shown.Select(x => x.Key).ToList();
        if (this.ForFolder && this.setup != null)
        {
            // a setup's Columns is its order (PanelViewModel.ApplyFolderSetup), and its custom columns are spelled out
            var saved = this.setup with { Columns = keys, CustomColumns = keys.Where(x => fileColumns.Find(x) != null).ToList() };
            folderSetups.Save(saved);
            this.setup = saved;
        }
        else
        {
            columns.SetOrder(keys);
            fileColumns.NotifyChanged();
        }
        this.Load();
    }

    void Load()
    {
        // a setup saved before it kept an order: its built-in columns, then its custom ones
        var keys = this.ForFolder && this.setup != null
            ? this.setup.Columns.Concat(this.setup.CustomColumns ?? []).Distinct().ToList()
            : columns.Order;
        this.Shown = keys.Select(this.Choice).OfType<ColumnChoice>().ToList();
        this.Available = Enum.GetValues<PanelColumn>().Select(x => x.ToString())
            .Concat(fileColumns.All.Select(x => x.Key))
            .Except(this.Shown.Select(x => x.Key))
            .Select(this.Choice)
            .OfType<ColumnChoice>()
            .ToList();
    }

    ColumnChoice? Choice(string key)
    {
        if (Enum.TryParse<PanelColumn>(key, out var builtIn))
            return new ColumnChoice(key, ColumnSet.Label(builtIn), L.Columns_General);

        // what it fills in for: the column sets whose kinds it covers - "Photos · Video"
        return fileColumns.Find(key) is { } column
            ? new ColumnChoice(key, column.Title, String.Join(" · ", fileColumns.Sets.Where(x => x.Kinds.Any(column.Kinds.Contains)).Select(x => x.Title)))
            : null;
    }
}

/// <summary>A column on the Columns screen: its key (PanelColumn name or IFileColumn key), name and what it's for.</summary>
public sealed record ColumnChoice(string Key, string Title, string Group);
