namespace ShinyCommander.Services;

public enum PanelColumn
{
    Extension,
    Size,
    Modified,
    Created,
    Attributes
}

/// <summary>
/// Which detail columns the panels show. Header and rows bind their ColumnDefinition widths here, so hiding a
/// column collapses it to zero width everywhere at once without rebuilding a row.
/// </summary>
[Singleton]
public partial class ColumnSet(AppSettings settings) : ObservableObject
{
    static readonly GridLength Hidden = new(0);

    public bool ShowExtension => !settings.HideExtColumn;
    public bool ShowSize => !settings.HideSizeColumn;
    public bool ShowModified => !settings.HideDateColumn;
    public bool ShowCreated => settings.ShowCreatedColumn;
    public bool ShowAttributes => !settings.HideAttrColumn;

    public GridLength ExtWidth => this.ShowExtension ? new GridLength(56) : Hidden;
    public GridLength SizeWidth => this.ShowSize ? new GridLength(92) : Hidden;
    public GridLength ModifiedWidth => this.ShowModified ? new GridLength(122) : Hidden;
    public GridLength CreatedWidth => this.ShowCreated ? new GridLength(122) : Hidden;
    public GridLength AttrWidth => this.ShowAttributes ? new GridLength(84) : Hidden;

    public bool IsVisible(PanelColumn column) => column switch
    {
        PanelColumn.Extension => this.ShowExtension,
        PanelColumn.Size => this.ShowSize,
        PanelColumn.Modified => this.ShowModified,
        PanelColumn.Created => this.ShowCreated,
        _ => this.ShowAttributes
    };

    public void Set(PanelColumn column, bool visible)
    {
        switch (column)
        {
            case PanelColumn.Extension: settings.HideExtColumn = !visible; break;
            case PanelColumn.Size: settings.HideSizeColumn = !visible; break;
            case PanelColumn.Modified: settings.HideDateColumn = !visible; break;
            case PanelColumn.Created: settings.ShowCreatedColumn = visible; break;
            default: settings.HideAttrColumn = !visible; break;
        }
        this.OnPropertyChanged(String.Empty); // every width/flag
    }

    public void Toggle(PanelColumn column) => this.Set(column, !this.IsVisible(column));

    public static string Label(PanelColumn column) => column switch
    {
        PanelColumn.Extension => L.Column_Extension,
        PanelColumn.Size => L.Column_Size,
        PanelColumn.Modified => L.Column_DateModified,
        PanelColumn.Created => L.Column_DateCreated,
        _ => L.Column_Attributes
    };
}

/// <summary>
/// One panel's columns: the user's choice (<see cref="ColumnSet"/>) plus the folder's custom columns
/// (<see cref="Custom"/>), narrowed to what fits. When the panel is too narrow for a readable Name column - a folder
/// tree open, panels stacked in a small window - the least useful columns go first (Attr, Created, the custom ones from
/// the right, Date, Ext). Rows and the header bind their widths here.
/// </summary>
public partial class ColumnLayout : ObservableObject
{
    public const double ExtColumnWidth = 56, SizeColumnWidth = 92, DateColumnWidth = 122, AttrColumnWidth = 84;
    const double MinNameWidth = 150;
    const double Fixed = 24 + 16; // icon column + row padding
    static readonly GridLength Hidden = new(0);

    readonly ColumnSet columns;
    readonly HashSet<PanelColumn> dropped = [];
    readonly HashSet<IFileColumn> droppedCustom = [];
    IReadOnlyList<IFileColumn> custom = [];
    IReadOnlySet<PanelColumn>? @override;
    double width;

    public ColumnLayout(ColumnSet columns)
    {
        this.columns = columns;
        columns.PropertyChanged += (_, _) => this.Recalculate(true);
    }

    /// <summary>The list's width - the panel view sets it as it resizes.</summary>
    public double AvailableWidth
    {
        get => this.width;
        set
        {
            if (Math.Abs(this.width - value) < 1)
                return;
            this.width = value;
            this.Recalculate(false);
        }
    }

    /// <summary>The active tab's custom columns (its folder's column set), right of the built-in ones.</summary>
    public IReadOnlyList<IFileColumn> Custom
    {
        get => this.custom;
        set
        {
            if (this.custom.SequenceEqual(value))
                return;
            this.custom = value;
            this.Recalculate(true);
        }
    }

    /// <summary>A folder setup's built-in columns, in place of the user's global choice; null for the global one.</summary>
    public IReadOnlySet<PanelColumn>? Override
    {
        get => this.@override;
        set
        {
            if (this.@override == value || this.@override != null && value != null && this.@override.SetEquals(value))
                return;
            this.@override = value;
            this.Recalculate(true);
        }
    }

    /// <summary>On - before narrowing to what fits.</summary>
    public bool IsOn(PanelColumn column) => this.@override?.Contains(column) ?? this.columns.IsVisible(column);

    /// <summary>The custom columns that fit, left to right.</summary>
    public IReadOnlyList<IFileColumn> CustomShown { get; private set; } = [];

    bool Shows(PanelColumn c) => this.IsOn(c) && !this.dropped.Contains(c);

    public bool ShowExtension => this.Shows(PanelColumn.Extension);
    public bool ShowSize => this.Shows(PanelColumn.Size);
    public bool ShowModified => this.Shows(PanelColumn.Modified);
    public bool ShowCreated => this.Shows(PanelColumn.Created);
    public bool ShowAttributes => this.Shows(PanelColumn.Attributes);

    public GridLength ExtWidth => this.ShowExtension ? new GridLength(56) : Hidden;
    public GridLength SizeWidth => this.ShowSize ? new GridLength(92) : Hidden;
    public GridLength ModifiedWidth => this.ShowModified ? new GridLength(122) : Hidden;
    public GridLength CreatedWidth => this.ShowCreated ? new GridLength(122) : Hidden;
    public GridLength AttrWidth => this.ShowAttributes ? new GridLength(84) : Hidden;
    public GridLength CustomWidth => new(this.CustomShown.Sum(x => x.Width));

    /// <summary>Total width of the detail columns that show - the drawn details view in each row (FileDetailsView).</summary>
    public double DetailsWidth =>
        (this.ShowExtension ? ExtColumnWidth : 0) + (this.ShowSize ? SizeColumnWidth : 0) + (this.ShowModified ? DateColumnWidth : 0)
        + (this.ShowCreated ? DateColumnWidth : 0) + (this.ShowAttributes ? AttrColumnWidth : 0) + this.CustomShown.Sum(x => x.Width);

    void Recalculate(bool changed)
    {
        var before = this.dropped.ToHashSet();
        var beforeCustom = this.droppedCustom.ToHashSet();
        this.dropped.Clear();
        this.droppedCustom.Clear();
        if (this.width > 0)
        {
            // what goes first when it doesn't fit, of the columns the user has on
            var order = new List<(PanelColumn? Column, IFileColumn? Custom, double Width)>
            {
                (PanelColumn.Attributes, null, AttrColumnWidth),
                (PanelColumn.Created, null, DateColumnWidth)
            };
            order.AddRange(this.custom.Reverse().Select(x => ((PanelColumn?)null, (IFileColumn?)x, x.Width)));
            order.Add((PanelColumn.Modified, null, DateColumnWidth));
            order.Add((PanelColumn.Extension, null, ExtColumnWidth));
            order.Add((PanelColumn.Size, null, SizeColumnWidth));
            var visible = order.Where(x => x.Column is not { } c || this.IsOn(c)).ToList();

            var used = Fixed + visible.Sum(x => x.Width);
            foreach (var (column, customColumn, w) in visible)
            {
                if (this.width - used >= MinNameWidth)
                    break;
                if (column is { } c)
                    this.dropped.Add(c);
                else
                    this.droppedCustom.Add(customColumn!);
                used -= w;
            }
        }
        this.CustomShown = this.custom.Where(x => !this.droppedCustom.Contains(x)).ToList();

        if (changed || !before.SetEquals(this.dropped) || !beforeCustom.SetEquals(this.droppedCustom) || before.Count + beforeCustom.Count == 0)
            this.OnPropertyChanged(String.Empty);
    }
}
