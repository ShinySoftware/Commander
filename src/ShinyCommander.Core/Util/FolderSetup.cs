using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShinyCommander.Core.Util;

/// <summary>
/// How one folder is shown instead of the global settings: its columns, sort and view mode. Saved on purpose for the
/// few folders that need it (photos as thumbnails, music by duration) - every other folder stays global.
/// </summary>
public sealed record FolderSetup
{
    public required string Path { get; init; }

    /// <summary>Also for every folder below it, unless one of those has its own.</summary>
    public bool IncludeSubfolders { get; init; }

    /// <summary>The built-in detail columns that show (Shared: PanelColumn names).</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The custom columns (IFileColumn keys), in order; null follows the folder's column set.</summary>
    public IReadOnlyList<string>? CustomColumns { get; init; }

    public SortColumn Sort { get; init; } = SortColumn.Name;
    public string? SortKey { get; init; }
    public bool Descending { get; init; }

    /// <summary>"Details" or "Thumbnails".</summary>
    public string ViewMode { get; init; } = "Details";

    /// <summary>Records with lists compare the lists by reference - this compares what's in them.</summary>
    public bool SameAs(FolderSetup? other) => other != null
        && SameFolder(this.Path, other.Path)
        && this.IncludeSubfolders == other.IncludeSubfolders
        && this.Columns.SequenceEqual(other.Columns)
        && (this.CustomColumns ?? []).SequenceEqual(other.CustomColumns ?? [])
        && (this.CustomColumns == null) == (other.CustomColumns == null)
        && this.Sort == other.Sort
        && this.SortKey == other.SortKey
        && this.Descending == other.Descending
        && this.ViewMode == other.ViewMode;

    // macOS and Windows file systems ignore case; Linux doesn't
    static readonly StringComparison PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static bool SameFolder(string a, string b) => String.Equals(Trim(a), Trim(b), PathComparison);

    /// <summary>
    /// The setup a folder gets: its own, otherwise the nearest folder above it saved with its subfolders. Null - the
    /// global settings - for everything else.
    /// </summary>
    public static FolderSetup? Find(IEnumerable<FolderSetup> setups, string path)
    {
        var folder = Trim(path);
        FolderSetup? nearest = null;
        foreach (var setup in setups)
        {
            var saved = Trim(setup.Path);
            if (String.Equals(saved, folder, PathComparison))
                return setup;

            if (setup.IncludeSubfolders && IsBelow(folder, saved) && (nearest == null || saved.Length > Trim(nearest.Path).Length))
                nearest = setup;
        }
        return nearest;
    }

    static bool IsBelow(string folder, string parent)
    {
        if (folder.Length <= parent.Length || !folder.StartsWith(parent, PathComparison))
            return false;

        // "/Music" is above "/Music/Live", not "/Musicals"; a root ("/", "C:\", "sftp://host/") already ends in one
        var separator = folder[parent.Length];
        return parent.EndsWith('/') || parent.EndsWith('\\') || separator is '/' or '\\';
    }

    // "/Music/" and "/Music" are one folder - but "/" stays "/"
    static string Trim(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }

    public static IReadOnlyList<FolderSetup> Parse(string? json)
    {
        if (String.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize(json, FolderSetupJson.Default.ListFolderSetup) ?? [];
        }
        catch (JsonException)
        {
            // a damaged setting loses the setups, not the app
            return [];
        }
    }

    public static string Format(IEnumerable<FolderSetup> setups) => JsonSerializer.Serialize(setups.ToList(), FolderSetupJson.Default.ListFolderSetup);
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<FolderSetup>))]
partial class FolderSetupJson : JsonSerializerContext;
