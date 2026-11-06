using System.Text;
using System.Text.RegularExpressions;

namespace ShinyCommander.Core.Search;

/// <summary>
/// Total ShinyCommander style masks: <c>*.cs;*.xaml | bin\ obj\</c> - include masks separated by ';' or
/// space, an optional '|' followed by exclude masks. Excludes ending in a separator match folders.
/// A mask with no wildcards and no dot means "contains" (TC's behaviour for the search box).
/// </summary>
public sealed class WildcardMask
{
    readonly Regex[] includes;
    readonly Regex[] excludeFiles;
    readonly Regex[] excludeDirs;

    public static WildcardMask All { get; } = new("*");

    public WildcardMask(string? mask)
    {
        mask = String.IsNullOrWhiteSpace(mask) ? "*" : mask.Trim();
        var pipe = mask.IndexOf('|');
        var inc = pipe >= 0 ? mask[..pipe] : mask;
        var exc = pipe >= 0 ? mask[(pipe + 1)..] : String.Empty;

        var incParts = Split(inc);
        this.includes = (incParts.Length == 0 ? ["*"] : incParts).Select(x => ToRegex(x, true)).ToArray();

        var excParts = Split(exc);
        this.excludeDirs = excParts.Where(x => x.EndsWith('/') || x.EndsWith('\\')).Select(x => ToRegex(x.TrimEnd('/', '\\'), false)).ToArray();
        this.excludeFiles = excParts.Where(x => !(x.EndsWith('/') || x.EndsWith('\\'))).Select(x => ToRegex(x, false)).ToArray();
    }

    static string[] Split(string value) => value
        .Split([';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsMatch(string fileName)
    {
        if (this.excludeFiles.Any(x => x.IsMatch(fileName)))
            return false;

        return this.includes.Any(x => x.IsMatch(fileName));
    }

    public bool IsDirectoryExcluded(string directoryName) => this.excludeDirs.Any(x => x.IsMatch(directoryName));

    static Regex ToRegex(string mask, bool containsWhenPlain)
    {
        if (containsWhenPlain && mask.IndexOfAny(['*', '?', '.']) < 0)
            mask = "*" + mask + "*";

        // "*.*" in DOS land also matches names without a dot
        if (mask == "*.*")
            mask = "*";

        var sb = new StringBuilder("^");
        foreach (var c in mask)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString())
            });
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
