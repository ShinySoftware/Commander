namespace ShinyCommander.Services;

/// <summary>One command the app offers - a ribbon button, and something the pane toolbar can carry.</summary>
/// <param name="Key">Stable id, stored in settings: the command property's name without "Command".</param>
/// <param name="Shortcut">As the ribbon writes it: "F5", "Primary+Shift+C".</param>
/// <param name="Tab">Key of the ribbon tab it lives on ("files").</param>
public sealed record AppCommand(string Key, string Icon, string Shortcut, string Tab)
{
    /// <summary>Its name in the current language (Strings: Cmd_&lt;Key&gt;).</summary>
    public string Text => Strings.Get("Cmd_" + this.Key);

    /// <summary>The ribbon tab's name in the current language.</summary>
    public string Group => Strings.Get("Ribbon_Tab_" + this.Tab);

    /// <summary>"Copy (F5)", "Copy Paths (⌘+Shift+C)" - Primary spelled the way this platform says it.</summary>
    public string Tooltip => $"{this.Text} ({this.Shortcut.Replace("Primary", OperatingSystem.IsMacOS() ? "⌘" : "Ctrl")})";
}

public static partial class CommandCatalog
{
    public static AppCommand? Find(string key) => All.FirstOrDefault(x => x.Key == key);
}
