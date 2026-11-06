using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Util;

public enum SortColumn
{
    Name,
    Extension,
    Size,
    Modified,
    Attributes,
    Created
}

/// <summary>Parent link first, folders before files, then by the chosen column (TC's ordering).</summary>
public sealed class FileEntryComparer(SortColumn column, bool descending) : IComparer<FileEntry>
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
