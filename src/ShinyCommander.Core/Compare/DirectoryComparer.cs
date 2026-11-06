using ShinyCommander.Core.Models;
using ShinyCommander.Core.Search;

namespace ShinyCommander.Core.Compare;

public enum CompareState
{
    Equal,
    LeftNewer,
    RightNewer,
    LeftOnly,
    RightOnly,
    Different
}

public enum SyncAction
{
    None,
    CopyToRight,
    CopyToLeft
}

public sealed record CompareOptions
{
    public bool Recursive { get; init; } = true;
    public bool CompareContent { get; init; }
    public bool IgnoreDate { get; init; }
    public bool IncludeHidden { get; init; }
    public string Mask { get; init; } = "*";
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2); // FAT has 2 second resolution
}

public sealed record CompareItem(string RelativePath, bool IsDirectory, FileEntry? Left, FileEntry? Right, CompareState State)
{
    public SyncAction DefaultAction => this.State switch
    {
        CompareState.LeftOnly or CompareState.LeftNewer => SyncAction.CopyToRight,
        CompareState.RightOnly or CompareState.RightNewer => SyncAction.CopyToLeft,
        _ => SyncAction.None
    };
}

/// <summary>"Synchronize directories" - compares two folder trees.</summary>
public static class DirectoryComparer
{
    public static Task<IReadOnlyList<CompareItem>> CompareAsync(string left, string right, CompareOptions options, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var mask = new WildcardMask(options.Mask);
            var results = new List<CompareItem>();
            await Walk(left, right, String.Empty, mask, options, results, ct).ConfigureAwait(false);
            return (IReadOnlyList<CompareItem>)results;
        }, ct);

    static async Task Walk(string left, string right, string rel, WildcardMask mask, CompareOptions o, List<CompareItem> results, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var l = List(Path.Combine(left, rel), o);
        var r = List(Path.Combine(right, rel), o);
        var names = l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            l.TryGetValue(name, out var le);
            r.TryGetValue(name, out var re);
            var childRel = rel.Length == 0 ? name : Path.Combine(rel, name);
            var isDir = (le ?? re)!.IsDirectory;

            if (isDir)
            {
                if (mask.IsDirectoryExcluded(name))
                    continue;

                if (le != null && re != null)
                {
                    if (o.Recursive)
                        await Walk(left, right, childRel, mask, o, results, ct).ConfigureAwait(false);
                }
                else
                {
                    results.Add(new CompareItem(childRel, true, le, re, le != null ? CompareState.LeftOnly : CompareState.RightOnly));
                }
                continue;
            }

            if (!mask.IsMatch(name))
                continue;

            CompareState state;
            if (le == null)
                state = CompareState.RightOnly;
            else if (re == null)
                state = CompareState.LeftOnly;
            else
                state = await CompareFiles(le, re, o, ct).ConfigureAwait(false);

            results.Add(new CompareItem(childRel, false, le, re, state));
        }
    }

    static Dictionary<string, FileEntry> List(string path, CompareOptions o)
    {
        var dict = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(path))
            return dict;

        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opts))
        {
            var e = FileEntry.FromInfo(info);
            if (!o.IncludeHidden && e.IsHidden)
                continue;
            dict[e.Name] = e;
        }
        return dict;
    }

    static async Task<CompareState> CompareFiles(FileEntry l, FileEntry r, CompareOptions o, CancellationToken ct)
    {
        var sameSize = l.Size == r.Size;
        var diff = l.Modified - r.Modified;
        var sameTime = o.IgnoreDate || diff.Duration() <= o.TimeTolerance;

        if (o.CompareContent)
        {
            if (!sameSize)
                return o.IgnoreDate ? CompareState.Different : TimeState(diff, o);

            var equal = await ContentEqualAsync(l.FullPath, r.FullPath, ct).ConfigureAwait(false);
            if (equal)
                return CompareState.Equal;

            return o.IgnoreDate || sameTime ? CompareState.Different : TimeState(diff, o);
        }

        if (sameSize && sameTime)
            return CompareState.Equal;

        if (o.IgnoreDate || sameTime)
            return CompareState.Different;

        return TimeState(diff, o);
    }

    static CompareState TimeState(TimeSpan diff, CompareOptions o)
        => diff.Duration() <= o.TimeTolerance ? CompareState.Different : diff > TimeSpan.Zero ? CompareState.LeftNewer : CompareState.RightNewer;

    public static async Task<bool> ContentEqualAsync(string a, string b, CancellationToken ct)
    {
        const int size = 64 * 1024;
        await using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, size, true);
        await using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, size, true);
        if (fa.Length != fb.Length)
            return false;

        var ba = new byte[size];
        var bb = new byte[size];
        while (true)
        {
            var ra = await fa.ReadAtLeastAsync(ba, size, false, ct).ConfigureAwait(false);
            var rb = await fb.ReadAtLeastAsync(bb, size, false, ct).ConfigureAwait(false);
            if (ra != rb)
                return false;
            if (ra == 0)
                return true;
            if (!ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb)))
                return false;
        }
    }
}
