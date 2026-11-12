namespace ShinyCommander.Services;

/// <summary>One command the app offers - a ribbon button, and something the pane toolbar can carry.</summary>
/// <param name="Key">Stable id, stored in settings: the command property's name without "Command".</param>
/// <param name="Shortcut">As the ribbon writes it: "F5", "Primary+Shift+C".</param>
/// <param name="Group">The ribbon tab it lives on.</param>
public sealed record AppCommand(string Key, string Text, string Icon, string Shortcut, string Group)
{
    /// <summary>"Copy (F5)", "Copy Paths (⌘+Shift+C)" - Primary spelled the way this platform says it.</summary>
    public string Tooltip => $"{this.Text} ({this.Shortcut.Replace("Primary", OperatingSystem.IsMacOS() ? "⌘" : "Ctrl")})";
}

public static partial class CommandCatalog
{
    public static AppCommand? Find(string key) => All.FirstOrDefault(x => x.Key == key);
}
