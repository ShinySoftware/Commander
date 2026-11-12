namespace ShinyCommander.ViewModels;

/// <summary>A pane toolbar button: the command, with an automation id unique to its pane.</summary>
public sealed record PaneToolbarItem(AppCommand Command, string AutomationId)
{
    public static IReadOnlyList<PaneToolbarItem> For(PanelSide side, IEnumerable<AppCommand> commands)
        => commands.Select(x => new PaneToolbarItem(x, side + x.Key switch
        {
            // the ids the two original buttons had - the UI tests tap them
            "ThumbnailView" => "ViewMode",
            "ToggleTree" => "TreeToggle",
            _ => "Toolbar" + x.Key
        })).ToList();
}
