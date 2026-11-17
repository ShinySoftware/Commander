namespace ShinyCommander.Core.Util;

/// <summary>
/// What's saved for one folder tab: its folder, whether it's locked, the name, colour and icon the user gave it, and how
/// it's sorted. One line per tab - the path, then tab separated <c>locked</c>, <c>alias=…</c>, <c>color=…</c>,
/// <c>icon=…</c>, <c>sort=…</c>, <c>sortkey=…</c> (the custom column it's sorted by), <c>desc</c> as they apply, so a
/// plain tab is still just its path.
/// </summary>
public sealed record TabState(
    string Path,
    bool Locked = false,
    string? Alias = null,
    string? Color = null,
    string? Icon = null,
    SortColumn Sort = SortColumn.Name,
    bool Descending = false,
    string? SortKey = null
)
{
    const string LegacyLockedPrefix = "locked|";

    public static TabState Parse(string line)
    {
        // the first locked tabs were saved as "locked|<path>"
        if (line.StartsWith(LegacyLockedPrefix, StringComparison.Ordinal))
            return new TabState(line[LegacyLockedPrefix.Length..], Locked: true);

        var fields = line.Split('\t');
        var options = fields.Skip(1).ToList();
        return new TabState(
            fields[0],
            options.Contains("locked"),
            Value(options, "alias="),
            Value(options, "color="),
            Value(options, "icon="),
            Enum.TryParse<SortColumn>(Value(options, "sort="), out var sort) ? sort : SortColumn.Name,
            options.Contains("desc"),
            Value(options, "sortkey=")
        );
    }

    public string Format()
    {
        var fields = new List<string> { this.Path };
        if (this.Locked)
            fields.Add("locked");
        if (!String.IsNullOrWhiteSpace(this.Alias))
            fields.Add("alias=" + Clean(this.Alias));
        if (!String.IsNullOrWhiteSpace(this.Color))
            fields.Add("color=" + this.Color);
        if (!String.IsNullOrWhiteSpace(this.Icon))
            fields.Add("icon=" + this.Icon);
        if (this.Sort != SortColumn.Name)
            fields.Add("sort=" + this.Sort);
        if (this.Sort == SortColumn.Custom && !String.IsNullOrWhiteSpace(this.SortKey))
            fields.Add("sortkey=" + Clean(this.SortKey));
        if (this.Descending)
            fields.Add("desc");
        return String.Join('\t', fields);
    }

    static string? Value(IEnumerable<string> options, string key)
        => options.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal)) is { } option && option.Length > key.Length
            ? option[key.Length..]
            : null;

    // the separators can't be part of a name
    static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
}
