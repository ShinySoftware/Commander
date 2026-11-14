namespace ShinyCommander.Services;

/// <summary>A colour swatch in a tab's menu; <see cref="Key"/> null is "no colour".</summary>
public sealed record TabColorChoice(string? Key, Color? Color, string Name, bool IsSelected)
{
    public bool IsNone => this.Color == null;
}

/// <summary>An icon in a tab's menu; <see cref="Key"/> null is "automatic" - what the place is, or a folder.</summary>
public sealed record TabIconChoice(string? Key, string Icon, string Name, bool IsSelected);

/// <summary>The colours a folder tab can be given - saved by key, so the palette can be retuned later.</summary>
public static class TabColors
{
    // mid-tones that read on both the light and the dark theme
    static readonly (string Key, Color Color)[] Palette =
    [
        ("Red", Color.FromArgb("#E5484D")),
        ("Orange", Color.FromArgb("#F76B15")),
        ("Yellow", Color.FromArgb("#E2A336")),
        ("Green", Color.FromArgb("#30A46C")),
        ("Teal", Color.FromArgb("#12A594")),
        ("Blue", Color.FromArgb("#3E63DD")),
        ("Purple", Color.FromArgb("#8E4EC6")),
        ("Pink", Color.FromArgb("#D6409F")),
        ("Gray", Color.FromArgb("#8B8D98"))
    ];

    public static Color? Get(string? key) => Palette.FirstOrDefault(x => x.Key == key).Color;

    /// <summary>The swatches for a tab's menu, "none" first, with the tab's own colour selected.</summary>
    public static IReadOnlyList<TabColorChoice> Choices(string? selected)
        => Palette
            .Select(x => new TabColorChoice(x.Key, x.Color, Strings.Get("TabColor_" + x.Key), x.Key == selected))
            .Prepend(new TabColorChoice(null, null, L.TabColor_None, selected == null))
            .ToList();
}

/// <summary>The icons a folder tab can be given instead of its automatic one.</summary>
public static class TabIcons
{
    static readonly string[] Icons =
    [
        "folder", "star", "home", "file-text", "download", "upload", "image", "cloud",
        "database", "tag", "compass", "activity", "settings", "search", "clipboard", "link"
    ];

    /// <summary>The icons for a tab's menu, "automatic" (showing what that would be) first, the tab's own selected.</summary>
    public static IReadOnlyList<TabIconChoice> Choices(string? selected, string automatic)
        => Icons
            .Where(x => x != automatic) // "automatic" already shows it
            .Select(x => new TabIconChoice(x, x, x, x == selected))
            .Prepend(new TabIconChoice(null, automatic, L.Tab_IconAuto, selected == null))
            .ToList();
}
