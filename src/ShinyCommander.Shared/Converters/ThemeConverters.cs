using System.Globalization;

namespace ShinyCommander.Converters;

/// <summary>
/// Resolves a palette entry from App resources for the current theme: "Header" finds HeaderLight/HeaderDark,
/// "Accent" finds Accent. Converters (rather than DataTriggers) are used for row/tab state because they
/// always produce an explicit value - the AppKit backend doesn't restore a trigger's setter on exit.
/// </summary>
public static class ThemeColors
{
    public static bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;

    public static Color Get(string key)
    {
        var resources = Application.Current?.Resources;
        if (resources == null)
            return Colors.Transparent;

        if (key == "Transparent")
            return Colors.Transparent;

        if (key == "White")
            return Colors.White;

        var themed = key + (IsDark ? "Dark" : "Light");
        if (resources.TryGetValue(themed, out var t) && t is Color tc)
            return tc;

        return resources.TryGetValue(key, out var v) && v is Color c ? c : Colors.Transparent;
    }
}

public class RowBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "Active" or "ActiveMarked" => ThemeColors.Get("Accent"),
        "Inactive" or "InactiveMarked" => ThemeColors.Get("CursorInactive"),
        _ => Colors.Transparent
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class RowTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "Active" => Colors.White,
        "ActiveMarked" => ThemeColors.Get("MarkedOnCursor"),
        "Marked" or "InactiveMarked" => ThemeColors.Get("Marked"),
        _ => ThemeColors.Get("Text")
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class RowFontConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.EndsWith("Marked") || value as string == "Marked" ? FontAttributes.Bold : FontAttributes.None;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>ConverterParameter "TrueKey|FalseKey", e.g. "Accent|Header" or "White|Text".</summary>
public class BoolToThemeColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "Accent|Transparent").Split('|');
        return ThemeColors.Get(value is true ? parts[0] : parts.ElementAtOrDefault(1) ?? "Transparent");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>value == ConverterParameter (ribbon tab key) -> true.</summary>
public class RibbonTabVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value as string, parameter as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class RibbonTabBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value as string, parameter as string) ? ThemeColors.Get("Panel") : Colors.Transparent;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class RibbonTabTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value as string, parameter as string) ? ThemeColors.Get("Accent") : ThemeColors.Get("Text");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class DriveIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DriveEntry drive ? For(drive) : "database";

    public static string For(DriveEntry drive) => drive.Kind switch
    {
        DriveKind.Home => "home",
        DriveKind.Cloud => "cloud",
        DriveKind.Network => "wifi",
        DriveKind.Special => drive.Name switch
        {
            "Downloads" => "download",
            "Documents" => "file-text",
            _ => "folder"
        },
        _ => "database"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
