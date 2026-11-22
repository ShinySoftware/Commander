namespace ShinyCommander.Web.Services;

public static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } t)
            return "";

        if (t.TotalSeconds < 60)
            return $"{Math.Max(1, (int)t.TotalSeconds)}s left";

        if (t.TotalMinutes < 60)
            return $"{(int)t.TotalMinutes}m {t.Seconds:00}s left";

        return $"{(int)t.TotalHours}h {t.Minutes:00}m left";
    }

    public static string When(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date)
            return local.ToString("HH:mm");

        return local.Year == now.Year ? local.ToString("d MMM HH:mm") : local.ToString("d MMM yyyy");
    }
}
