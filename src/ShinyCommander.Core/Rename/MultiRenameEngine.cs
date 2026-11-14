using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ShinyCommander.Core.FileSystem;
using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Rename;

public enum RenameCase
{
    Unchanged,
    Lower,
    Upper,
    FirstUpper,
    TitleCase
}

public sealed record MultiRenameOptions
{
    /// <summary>[N] name, [N2-5] chars 2-5, [N3] char 3, [N2-] from char 2, [C] counter, [P] parent folder, [YMD] [hms] date/time, [E] extension</summary>
    public string NameMask { get; init; } = "[N]";
    public string ExtensionMask { get; init; } = "[E]";
    public string Search { get; init; } = String.Empty;
    public string Replace { get; init; } = String.Empty;
    public bool UseRegex { get; init; }
    public bool CaseSensitiveSearch { get; init; }
    public RenameCase Case { get; init; } = RenameCase.Unchanged;
    public int CounterStart { get; init; } = 1;
    public int CounterStep { get; init; } = 1;
    public int CounterDigits { get; init; } = 1;
}

public sealed record RenamePreview(FileEntry Source, string NewName, string? Error)
{
    public bool Changed => this.Error == null && !String.Equals(this.Source.Name, this.NewName, StringComparison.Ordinal);
}

public sealed record RenameUndoStep(string FromPath, string ToPath);

/// <summary>Ctrl+M multi-rename tool using the Total ShinyCommander placeholder syntax.</summary>
public static partial class MultiRenameEngine
{
    [GeneratedRegex(@"\[(?<tok>[^\[\]]*)\]")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^(?<k>[NEP])(?<from>-?\d+)?(?<dash>-)?(?<to>-?\d+)?$")]
    private static partial Regex RangeRegex();

    public static IReadOnlyList<RenamePreview> Preview(IReadOnlyList<FileEntry> items, MultiRenameOptions options)
    {
        var results = new List<RenamePreview>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            try
            {
                results.Add(new RenamePreview(items[i], BuildName(items[i], i, options), null));
            }
            catch (Exception ex)
            {
                results.Add(new RenamePreview(items[i], items[i].Name, ex.Message));
            }
        }

        // flag duplicate targets within the same folder
        var dupes = results
            .Where(x => x.Error == null)
            .GroupBy(x => Path.Combine(Path.GetDirectoryName(x.Source.FullPath) ?? "", x.NewName), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        return results.Select(x => dupes.Contains(x) ? x with { Error = "Duplicate name" } : x).ToList();
    }

    public static string BuildName(FileEntry entry, int index, MultiRenameOptions o)
    {
        var name = Expand(o.NameMask, entry, index, o);
        var ext = entry.IsDirectory ? String.Empty : Expand(o.ExtensionMask, entry, index, o);
        var full = ext.Length > 0 ? name + "." + ext : name;

        if (!String.IsNullOrEmpty(o.Search))
        {
            if (o.UseRegex)
            {
                var opts = o.CaseSensitiveSearch ? RegexOptions.None : RegexOptions.IgnoreCase;
                full = Regex.Replace(full, o.Search, o.Replace ?? String.Empty, opts, TimeSpan.FromSeconds(1));
            }
            else
            {
                // TC: "a|b" searches for several strings at once
                foreach (var term in o.Search.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    full = full.Replace(term, o.Replace ?? String.Empty, o.CaseSensitiveSearch ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }
        }

        full = ApplyCase(full, o.Case);
        full = full.Trim();
        if (full.Length == 0)
            throw new InvalidOperationException("Empty name");

        if (full.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Invalid characters");

        return full;
    }

    static string Expand(string mask, FileEntry entry, int index, MultiRenameOptions o)
    {
        // "[[]" is a literal '['
        mask = mask.Replace("[[]", "\u0001").Replace("[]]", "\u0002");
        var result = TokenRegex().Replace(mask, m => Token(m.Groups["tok"].Value, entry, index, o));
        return result.Replace('\u0001', '[').Replace('\u0002', ']');
    }

    static string Token(string token, FileEntry e, int index, MultiRenameOptions o)
    {
        var stamp = e.Modified;
        switch (token)
        {
            case "C":
                var value = o.CounterStart + index * o.CounterStep;
                return value.ToString(new string('0', Math.Max(1, o.CounterDigits)), CultureInfo.InvariantCulture);
            case "YMD": return stamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            case "Y": return stamp.ToString("yyyy", CultureInfo.InvariantCulture);
            case "y": return stamp.ToString("yy", CultureInfo.InvariantCulture);
            case "M": return stamp.ToString("MM", CultureInfo.InvariantCulture);
            case "D": return stamp.ToString("dd", CultureInfo.InvariantCulture);
            case "hms": return stamp.ToString("HHmmss", CultureInfo.InvariantCulture);
            case "h": return stamp.ToString("HH", CultureInfo.InvariantCulture);
            case "m": return stamp.ToString("mm", CultureInfo.InvariantCulture);
            case "s": return stamp.ToString("ss", CultureInfo.InvariantCulture);
            case "L": return e.Name.ToLowerInvariant();
            case "U": return e.Name.ToUpperInvariant();
        }

        var match = RangeRegex().Match(token);
        if (!match.Success)
            throw new InvalidOperationException($"Unknown placeholder [{token}]");

        var source = match.Groups["k"].Value switch
        {
            "N" => e.NameWithoutExtension,
            "E" => e.Extension,
            "P" => Path.GetFileName(Path.GetDirectoryName(e.FullPath)) ?? String.Empty,
            _ => String.Empty
        };

        if (!match.Groups["from"].Success)
            return source;

        var from = Int32.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
        var hasDash = match.Groups["dash"].Success;
        var to = match.Groups["to"].Success ? Int32.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture) : (int?)null;

        // negative numbers count from the end, 1 based
        int Index(int n) => n < 0 ? source.Length + n : n - 1;

        var start = Math.Clamp(Index(from), 0, source.Length);
        if (!hasDash)
            return start < source.Length ? source[start].ToString() : String.Empty;

        var end = to is { } t ? Math.Clamp(Index(t), -1, source.Length - 1) : source.Length - 1;
        return end < start ? String.Empty : source[start..(end + 1)];
    }

    static string ApplyCase(string value, RenameCase mode) => mode switch
    {
        RenameCase.Lower => value.ToLowerInvariant(),
        RenameCase.Upper => value.ToUpperInvariant(),
        RenameCase.FirstUpper => value.Length == 0 ? value : Char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant(),
        RenameCase.TitleCase => TitleCase(value),
        _ => value
    };

    static string TitleCase(string value)
    {
        var sb = new StringBuilder(value.Length);
        var upperNext = true;
        foreach (var c in value)
        {
            sb.Append(upperNext ? Char.ToUpperInvariant(c) : Char.ToLowerInvariant(c));
            upperNext = c is ' ' or '_' or '-' or '.';
        }
        return sb.ToString();
    }

    /// <summary>
    /// Performs the renames. Uses a two-phase temp rename so swaps (a→b, b→a) and case-only changes work.
    /// Returns the steps needed to undo.
    /// </summary>
    public static Task<IReadOnlyList<RenameUndoStep>> ExecuteAsync(IReadOnlyList<RenamePreview> previews, CancellationToken ct = default) => Task.Run<IReadOnlyList<RenameUndoStep>>(() =>
    {
        var work = previews.Where(x => x.Changed && !x.Source.IsInArchive && !x.Source.IsParentLink).ToList();
        var undo = RenameInArchives(previews.Where(x => x.Changed && x.Source.IsInArchive && !x.Source.IsParentLink).ToList(), ct);
        var temps = new List<(string Temp, string Final, string Original)>();

        foreach (var item in work)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(item.Source.FullPath)!;
            var temp = Path.Combine(dir, ".cmdr-rename-" + Guid.NewGuid().ToString("N")[..8]);
            Move(item.Source.FullPath, temp, item.Source.IsDirectory);
            temps.Add((temp, Path.Combine(dir, item.NewName), item.Source.FullPath));
        }

        foreach (var (temp, final, original) in temps)
        {
            var isDir = Directory.Exists(temp);
            if (Path.Exists(final))
            {
                // collision with an untouched file: roll this one back
                Move(temp, original, isDir);
                continue;
            }
            Move(temp, final, isDir);
            undo.Add(new RenameUndoStep(final, original));
        }
        return undo;
    }, ct);

    public static Task UndoAsync(IReadOnlyList<RenameUndoStep> steps) => Task.Run(() =>
    {
        var vfs = new VirtualFileSystem();
        var inArchives = new List<(string Archive, string From, string To)>();
        foreach (var step in steps)
        {
            if (Path.Exists(step.FromPath))
            {
                if (!Path.Exists(step.ToPath))
                    Move(step.FromPath, step.ToPath, Directory.Exists(step.FromPath));
            }
            else if (vfs.Resolve(Path.GetDirectoryName(step.FromPath)!) is { IsArchive: true } loc)
            {
                inArchives.Add((loc.ArchiveFile!, Key(loc, Path.GetFileName(step.FromPath)), Key(loc, Path.GetFileName(step.ToPath))));
            }
        }

        foreach (var archive in inArchives.GroupBy(x => x.Archive))
        {
            var changes = new ArchiveChanges();
            changes.Renames.AddRange(archive.Select(x => (x.From, x.To)));
            ArchiveWriter.Apply(archive.Key, changes);
        }
    });

    /// <summary>
    /// Renames inside archives - one rewrite per archive. Every rename is worked out against the archive as it was,
    /// so swaps (a→b, b→a) need no temporary names; a new name already taken by an entry that isn't moving is left.
    /// </summary>
    static List<RenameUndoStep> RenameInArchives(List<RenamePreview> work, CancellationToken ct)
    {
        var undo = new List<RenameUndoStep>();
        var vfs = new VirtualFileSystem();
        var byArchive = work
            .Select(x => (Preview: x, Location: vfs.Resolve(Path.GetDirectoryName(x.Source.FullPath)!)))
            .GroupBy(x => x.Location.ArchiveFile!);

        foreach (var archive in byArchive)
        {
            ct.ThrowIfCancellationRequested();
            if (!ArchiveFormats.IsWritable(archive.Key))
                throw new NotSupportedException($"'{Path.GetFileName(archive.Key)}' is a read-only archive");

            HashSet<string> keys;
            using (var reader = ArchiveReader.Open(archive.Key))
                keys = reader.Items.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

            var moving = archive.Select(x => Key(x.Location, x.Preview.Source.Name)).ToHashSet(StringComparer.Ordinal);
            var changes = new ArchiveChanges();
            foreach (var (preview, location) in archive)
            {
                var to = Key(location, preview.NewName);
                if (keys.Contains(to) && !moving.Contains(to))
                    continue;

                changes.Renames.Add((Key(location, preview.Source.Name), to));
                undo.Add(new RenameUndoStep(Path.Combine(Path.GetDirectoryName(preview.Source.FullPath)!, preview.NewName), preview.Source.FullPath));
            }
            ArchiveWriter.Apply(archive.Key, changes, ct);
        }
        return undo;
    }

    static string Key(VfsLocation location, string name) => location.InnerPath.Length == 0 ? name : location.InnerPath + "/" + name;

    static void Move(string from, string to, bool isDir)
    {
        if (isDir)
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }
}
