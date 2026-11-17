using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Util;

public enum SortColumn
{
    Name,
    Extension,
    Size,
    Modified,
    Attributes,
    Created,

    /// <summary>A custom column (Shared: IFileColumn) - the tab's sort key says which.</summary>
    Custom
}

/// <summary>
/// Parent link first, folders before files, then by the chosen column (TC's ordering). A custom column sorts by
/// <paramref name="customValue"/>; files without a value go last whichever way it's sorted.
/// </summary>
public sealed class FileEntryComparer(SortColumn column, bool descending, Func<FileEntry, IComparable?>? customValue = null) : IComparer<FileEntry>
{
    static readonly StringComparer NameComparer = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);

    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        if (x.IsParentLink) return -1;
        if (y.IsParentLink) return 1;
        if (x.IsDirectory != y.IsDirectory)
            return x.IsDirectory ? -1 : 1;

        if (column == SortColumn.Custom && customValue != null)
        {
            var a = customValue(x);
            var b = customValue(y);
            if (a == null || b == null)
                return a == b ? NameComparer.Compare(x.Name, y.Name) : a == null ? 1 : -1;

            var custom = a is string sa && b is string sb ? NameComparer.Compare(sa, sb) : a.CompareTo(b);
            if (custom == 0)
                custom = NameComparer.Compare(x.Name, y.Name);
            return descending ? -custom : custom;
        }

        var result = column switch
        {
            SortColumn.Extension => NameComparer.Compare(x.Extension, y.Extension),
            // folders have no size until calculated - TC sorts them by name
            SortColumn.Size => x.IsDirectory ? 0 : x.Size.CompareTo(y.Size),
            SortColumn.Modified => x.Modified.CompareTo(y.Modified),
            SortColumn.Created => x.Created.CompareTo(y.Created),
            SortColumn.Attributes => String.CompareOrdinal(x.AttributeText, y.AttributeText),
            _ => 0
        };

        if (result == 0)
            result = NameComparer.Compare(x.Name, y.Name);

        return descending ? -result : result;
    }
}
