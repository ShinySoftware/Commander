using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ShinyCommander.Core.Metadata;

namespace ShinyCommander.Services;

/// <summary>
/// A detail column beyond Name/Ext/Size/Date/Attr, filled from what's inside the file (<see cref="FileMetadata"/>) -
/// a song's duration, a photo's camera, a PDF's pages. Register one with
/// <see cref="FileColumnExtensions.AddFileColumn{T}"/> and put it in a <see cref="FileColumnSet"/>.
/// </summary>
public interface IFileColumn
{
    /// <summary>Stable id - what a column set's saved list holds.</summary>
    string Key { get; }

    /// <summary>Header text and its name in Settings.</summary>
    string Title { get; }

    double Width { get; }

    /// <summary>Numbers line up on the right.</summary>
    bool AlignRight { get; }

    /// <summary>The kinds of file it has something for; the rest leave it blank and aren't opened for it.</summary>
    IReadOnlySet<FileKind> Kinds { get; }

    /// <summary>What it sorts by; null sorts after every value.</summary>
    IComparable? Value(FileMetadata metadata);

    string Format(FileMetadata metadata);
}

/// <summary>A column from two functions - every built-in one (BuiltInColumns).</summary>
public sealed class FileColumn(
    string key,
    Func<string> title,
    double width,
    bool alignRight,
    FileKind[] kinds,
    Func<FileMetadata, IComparable?> value,
    Func<FileMetadata, string?>? format = null
) : IFileColumn
{
    public string Key => key;
    public string Title => title();
    public double Width => width;
    public bool AlignRight => alignRight;
    public IReadOnlySet<FileKind> Kinds { get; } = kinds.ToHashSet();
    public IComparable? Value(FileMetadata metadata) => value(metadata);
    public string Format(FileMetadata metadata) => (format == null ? value(metadata)?.ToString() : format(metadata)) ?? String.Empty;
}

/// <summary>
/// Columns that switch on by themselves in a folder that's mostly one kind of file - photos, music, video. The user
/// turns a set off or picks its columns in Settings; <see cref="DefaultColumns"/> is what it starts with.
/// </summary>
public sealed record FileColumnSet(string Key, Func<string> GetTitle, FileKind[] Kinds, string[] DefaultColumns)
{
    public string Title => this.GetTitle();
}

/// <summary>
/// The registered columns and sets, which set a folder gets, and each file's metadata once read - kept by path, size
/// and modified time, so revisiting a folder or sorting by a column doesn't open its files again.
/// </summary>
public sealed class FileColumns
{
    // half the files (and at least one) of a set's kinds - a photo in a music folder is its cover
    const double Share = 0.5;
    const int MaxCached = 50_000;

    readonly AppSettings settings;
    readonly ConcurrentDictionary<string, (long Size, DateTime Modified, FileMetadata Metadata)> cache = new(StringComparer.Ordinal);

    public FileColumns(IEnumerable<IFileColumn> columns, IEnumerable<FileColumnSet> sets, AppSettings settings)
    {
        this.settings = settings;
        this.All = columns.ToList();
        this.Sets = sets.ToList();
    }

    public IReadOnlyList<IFileColumn> All { get; }
    public IReadOnlyList<FileColumnSet> Sets { get; }

    /// <summary>A set was turned on or off or its columns changed - the panels pick their columns again.</summary>
    public event EventHandler? Changed;

    public IFileColumn? Find(string? key) => key == null ? null : this.All.FirstOrDefault(x => x.Key == key);

    public bool IsEnabled(FileColumnSet set) => !this.Disabled().Contains(set.Key);

    public void SetEnabled(FileColumnSet set, bool enabled)
    {
        var disabled = this.Disabled();
        if (enabled)
            disabled.Remove(set.Key);
        else
            disabled.Add(set.Key);
        this.settings.DisabledColumnSets = disabled.Count == 0 ? null : String.Join(',', disabled.Order());
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The set's columns, in order - the user's pick, or its defaults.</summary>
    public IReadOnlyList<IFileColumn> ColumnsOf(FileColumnSet set)
    {
        var keys = this.Saved().TryGetValue(set.Key, out var chosen) ? chosen : set.DefaultColumns;
        return keys.Select(this.Find).OfType<IFileColumn>().ToList();
    }

    /// <summary>Adds the column at the end of the set, or takes it out.</summary>
    public void Toggle(FileColumnSet set, IFileColumn column)
    {
        var keys = this.ColumnsOf(set).Select(x => x.Key).ToList();
        if (!keys.Remove(column.Key))
            keys.Add(column.Key);

        var saved = this.Saved();
        saved[set.Key] = keys.ToArray();
        // "photos=dimensions,taken|music=" - an emptied set is kept, so it doesn't come back with its defaults
        this.settings.ColumnSets = String.Join('|', saved.OrderBy(x => x.Key).Select(x => $"{x.Key}={String.Join(',', x.Value)}"));
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Custom columns on in every folder (the Columns screen), on top of a folder's set; blank for files of other kinds.</summary>
    public IReadOnlyList<IFileColumn> Always => ColumnSet.Split(this.settings.ColumnOrder ?? this.settings.AlwaysColumns)
        .Select(this.Find)
        .OfType<IFileColumn>()
        .ToList();

    /// <summary>The column order changed (ColumnSet.SetOrder) - the tabs work out their custom columns again.</summary>
    public void NotifyChanged() => this.Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The columns for the set's kinds, whether it shows them yet or not - the ones Settings offers.</summary>
    public IReadOnlyList<IFileColumn> CandidatesFor(FileColumnSet set) => this.All.Where(x => x.Kinds.Overlaps(set.Kinds)).ToList();

    /// <summary>The set for a folder's listing: the enabled one whose kinds are the biggest share of its files.</summary>
    public FileColumnSet? Detect(IReadOnlyCollection<FileEntry> entries)
    {
        var kinds = entries.Select(FileKinds.Of).Where(x => x != FileKind.Other).ToList();
        // plain folders don't count, but they also aren't Other - an .app is a folder and has a kind
        var files = entries.Count(x => !x.IsParentLink && !(x.IsDirectory && FileKinds.Of(x) == FileKind.Other));
        if (kinds.Count == 0 || files == 0)
            return null;

        var disabled = this.Disabled();
        return this.Sets
            .Where(x => !disabled.Contains(x.Key))
            .Select(x => (Set: x, Count: kinds.Count(k => x.Kinds.Contains(k))))
            .Where(x => x.Count > 0 && x.Count >= files * Share)
            .OrderByDescending(x => x.Count)
            .Select(x => x.Set)
            .FirstOrDefault();
    }

    /// <summary>The metadata read for the file as it is now, or null when it hasn't been (or has changed since).</summary>
    public FileMetadata? Cached(FileEntry entry)
        => this.cache.TryGetValue(entry.FullPath, out var hit) && hit.Size == entry.Size && hit.Modified == entry.Modified ? hit.Metadata : null;

    /// <summary>Reads (or takes from the cache) one local file's metadata - call it off the main thread.</summary>
    public FileMetadata Read(FileEntry entry, CancellationToken ct)
    {
        if (this.Cached(entry) is { } cached)
            return cached;

        var metadata = FileMetadataReader.Read(entry, ct);
        if (this.cache.Count >= MaxCached)
            this.cache.Clear();
        this.cache[entry.FullPath] = (entry.Size, entry.Modified, metadata);
        return metadata;
    }

    HashSet<string> Disabled() => new(
        (this.settings.DisabledColumnSets ?? String.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase
    );

    Dictionary<string, string[]> Saved() => (this.settings.ColumnSets ?? String.Empty)
        .Split('|', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2))
        .Where(x => x.Length == 2)
        .ToDictionary(x => x[0], x => x[1].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
}

public static class FileColumnExtensions
{
    public static IServiceCollection AddFileColumn<T>(this IServiceCollection services) where T : class, IFileColumn
    {
        services.AddSingleton<IFileColumn, T>();
        return services;
    }

    public static IServiceCollection AddFileColumn(this IServiceCollection services, IFileColumn column)
    {
        services.AddSingleton(column);
        return services;
    }

    public static IServiceCollection AddFileColumnSet(this IServiceCollection services, FileColumnSet set)
    {
        services.AddSingleton(set);
        return services;
    }
}
