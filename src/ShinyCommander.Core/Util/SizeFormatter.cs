using System.Globalization;

namespace ShinyCommander.Core.Util;

public static class SizeFormatter
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>"12.3 MB" style.</summary>
    public static string Friendly(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value < 10 ? "0.##" : value < 100 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    /// <summary>TC's default "1,234,567" style.</summary>
    public static string Exact(long bytes) => bytes.ToString("N0", CultureInfo.CurrentCulture);

    public static string Format(long bytes, bool friendly) => friendly ? Friendly(bytes) : Exact(bytes);
}
