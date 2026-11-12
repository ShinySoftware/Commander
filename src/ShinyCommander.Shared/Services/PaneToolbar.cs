namespace ShinyCommander.Services;

/// <summary>
/// What the toolbar across each file pane carries, beside the path - any of the ribbon's commands, in the user's order.
/// Both panes share it; a button acts on the pane it was clicked in.
/// </summary>
[Singleton]
public sealed class PaneToolbar
{
    /// <summary>What a fresh install shows - the two buttons the pane always had.</summary>
    public static readonly IReadOnlyList<string> DefaultKeys = ["ThumbnailView", "ToggleTree"];

    readonly AppSettings settings;

    public PaneToolbar(AppSettings settings)
    {
        this.settings = settings;
        this.Items = Load(settings.PaneToolbar);
    }

    public IReadOnlyList<AppCommand> Items { get; private set; }

    /// <summary><see cref="Items"/> changed.</summary>
    public event EventHandler? Changed;

    public void Set(IEnumerable<string> keys)
    {
        var list = keys.Distinct().ToList();
        this.settings.PaneToolbar = String.Join(",", list);
        this.Items = Load(this.settings.PaneToolbar);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset() => this.Set(DefaultKeys);

    // null = never customized; "" = deliberately empty. Keys of commands that have since gone are dropped.
    static IReadOnlyList<AppCommand> Load(string? saved) => (saved?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [.. DefaultKeys])
        .Select(CommandCatalog.Find)
        .OfType<AppCommand>()
        .ToList();
}
