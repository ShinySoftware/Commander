namespace ShinyCommander.ViewModels;

/// <summary>
/// Choosing what the pane toolbar carries: the toolbar as it stands (reorder, remove) beside every command not on it
/// (click to add). Each change is saved and shows in both panes straight away.
/// </summary>
[ShellMap<CustomizeToolbarPage>("CustomizeToolbar")]
public partial class CustomizeToolbarViewModel : ObservableObject
{
    readonly PaneToolbar toolbar;
    readonly INavigator navigator;

    public CustomizeToolbarViewModel(PaneToolbar toolbar, INavigator navigator)
    {
        this.toolbar = toolbar;
        this.navigator = navigator;
        this.Load();
    }

    [ObservableProperty]
    public partial IReadOnlyList<AppCommand> Current { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AppCommand> Available { get; set; } = [];

    [RelayCommand]
    void Add(AppCommand command) => this.Save([.. this.Current, command]);

    [RelayCommand]
    void Remove(AppCommand command) => this.Save(this.Current.Where(x => x != command));

    [RelayCommand]
    void MoveUp(AppCommand command) => this.Move(command, -1);

    [RelayCommand]
    void MoveDown(AppCommand command) => this.Move(command, 1);

    [RelayCommand]
    void Reset()
    {
        this.toolbar.Reset();
        this.Load();
    }

    [RelayCommand]
    Task Close() => this.navigator.GoBack();

    void Move(AppCommand command, int by)
    {
        var list = this.Current.ToList();
        var from = list.IndexOf(command);
        var to = from + by;
        if (from < 0 || to < 0 || to >= list.Count)
            return;

        (list[from], list[to]) = (list[to], list[from]);
        this.Save(list);
    }

    void Save(IEnumerable<AppCommand> commands)
    {
        this.toolbar.Set(commands.Select(x => x.Key));
        this.Load();
    }

    void Load()
    {
        this.Current = this.toolbar.Items;
        this.Available = CommandCatalog.All.Except(this.Current).ToList();
    }
}
