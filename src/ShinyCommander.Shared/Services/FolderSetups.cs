namespace ShinyCommander.Services;

/// <summary>
/// The folders saved with their own columns, sort and view mode (FolderSetup) - Folder Setup in the ribbon, or the
/// badge on a panel's path row. Kept in AppSettings.FolderSetups as JSON.
/// </summary>
[Singleton]
public sealed class FolderSetups(AppSettings settings)
{
    IReadOnlyList<FolderSetup>? all;

    public IReadOnlyList<FolderSetup> All => this.all ??= FolderSetup.Parse(settings.FolderSetups);

    /// <summary>A setup was saved, changed or forgotten - tabs look up theirs again.</summary>
    public event EventHandler? Changed;

    public FolderSetup? Find(string path) => FolderSetup.Find(this.All, path);

    /// <summary>Adds the setup, or replaces the one saved for the same folder.</summary>
    public void Save(FolderSetup setup)
    {
        var existing = this.All.FirstOrDefault(x => FolderSetup.SameFolder(x.Path, setup.Path));
        if (setup.SameAs(existing))
            return;

        this.Store(this.All.Where(x => x != existing).Append(setup));
    }

    public void Remove(FolderSetup setup) => this.Store(this.All.Where(x => !FolderSetup.SameFolder(x.Path, setup.Path)));

    public void Clear() => this.Store([]);

    void Store(IEnumerable<FolderSetup> setups)
    {
        var list = setups.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        this.all = list;
        settings.FolderSetups = list.Count == 0 ? null : FolderSetup.Format(list);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }
}
