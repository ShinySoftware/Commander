namespace ShinyCommander.Services;

/// <summary>A published release on GitHub, with the download this head installs.</summary>
/// <param name="Version">The tag without its "v": 1.2.0, or 1.2.0-beta2.</param>
/// <param name="Notes">The release notes, as GitHub's markdown.</param>
public sealed record AppRelease(
    string Version,
    string Name,
    string Notes,
    DateTimeOffset PublishedAt,
    bool IsPrerelease,
    Uri Page,
    Uri Download
)
{
    /// <summary>"0.2.0", "0.2.0-beta2", or "0.2.0 (beta)" for a prerelease whose tag doesn't say so.</summary>
    public string DisplayVersion => this.IsPrerelease && !this.Version.Contains('-') ? L.Update_BetaVersionFormat(this.Version) : this.Version;
}


/// <summary>
/// Orders versions the way semantic versioning does: by the numbers, then a release above any of its
/// prereleases (1.2.0 &gt; 1.2.0-beta2), then prerelease labels part by part. Build metadata (+abc) is ignored.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>Negative when <paramref name="a"/> is older, positive when newer; null when either doesn't parse.</summary>
    public static int? Compare(string a, string b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y)
            return null;

        for (var i = 0; i < 3; i++)
        {
            if (x.Numbers[i] != y.Numbers[i])
                return x.Numbers[i].CompareTo(y.Numbers[i]);
        }

        // The one without a label is the release, and newer than its prereleases.
        if (x.Label.Length == 0 || y.Label.Length == 0)
            return (x.Label.Length == 0).CompareTo(y.Label.Length == 0);

        for (var i = 0; i < Math.Min(x.Label.Length, y.Label.Length); i++)
        {
            var c = ComparePart(x.Label[i], y.Label[i]);
            if (c != 0)
                return c;
        }
        return x.Label.Length.CompareTo(y.Label.Length);
    }

    static int ComparePart(string a, string b)
    {
        var aNumber = Int64.TryParse(a, out var na);
        var bNumber = Int64.TryParse(b, out var nb);
        if (aNumber && bNumber)
            return na.CompareTo(nb);
        if (aNumber != bNumber)
            return aNumber ? -1 : 1; // numbers sort before words
        return String.CompareOrdinal(a, b);
    }

    static (long[] Numbers, string[] Label)? Parse(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0)
            v = v[..plus];

        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var label = dash >= 0 ? v[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : [];

        var parts = core.Split('.');
        if (parts.Length is 0 or > 3)
            return null;

        var numbers = new long[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!Int64.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                return null;
        }
        return (numbers, label);
    }
}
