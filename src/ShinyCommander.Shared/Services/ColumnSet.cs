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
/// Which detail columns the panels show, and in what order (the Columns screen) - the built-in ones and any custom
/// ones together. A folder's column set and a folder setup add to or replace this per folder (ColumnLayout).
/// </summary>
[Singleton]
public partial class ColumnSet(AppSettings settings) : ObservableObject
{
    /// <summary>A fresh install's columns.</summary>
    public static readonly IReadOnlyList<string> Default = [nameof(PanelColumn.Extension), nameof(PanelColumn.Size), nameof(PanelColumn.Modified), nameof(PanelColumn.Attributes)];

    /// <summary>The shown columns, left to right: PanelColumn names and IFileColumn keys.</summary>
    public IReadOnlyList<string> Order => settings.ColumnOrder is { } saved ? Split(saved) : this.Before();

    // what the choice was before it had an order: the built-in flags, then the custom columns turned on everywhere
    IReadOnlyList<string> Before() => Enum.GetValues<PanelColumn>()
        .Where(c => c switch
        {
            PanelColumn.Extension => !settings.HideExtColumn,
            PanelColumn.Size => !settings.HideSizeColumn,
            PanelColumn.Modified => !settings.HideDateColumn,
            PanelColumn.Created => settings.ShowCreatedColumn,
            _ => !settings.HideAttrColumn
        })
        .Select(x => x.ToString())
        .Concat(Split(settings.AlwaysColumns))
        .ToList();

    public static IReadOnlyList<string> Split(string? keys) => (keys ?? String.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct()
        .ToList();

    public void SetOrder(IEnumerable<string> keys)
    {
        settings.ColumnOrder = String.Join(',', keys.Distinct());
        this.OnPropertyChanged(String.Empty); // every width/flag
    }

    public void Reset() => this.SetOrder(Default);

    public bool ShowExtension => this.IsVisible(PanelColumn.Extension);
    public bool ShowSize => this.IsVisible(PanelColumn.Size);
    public bool ShowModified => this.IsVisible(PanelColumn.Modified);
    public bool ShowCreated => this.IsVisible(PanelColumn.Created);
    public bool ShowAttributes => this.IsVisible(PanelColumn.Attributes);

    public bool IsVisible(PanelColumn column) => this.Order.Contains(column.ToString());

    /// <summary>On (at the end of the built-in ones) or off.</summary>
    public void Set(PanelColumn column, bool visible)
    {
        var order = this.Order.Where(x => x != column.ToString()).ToList();
        if (visible)
            order.Insert(order.FindLastIndex(x => Enum.TryParse<PanelColumn>(x, out _)) + 1, column.ToString());
        this.SetOrder(order);
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

    /// <summary>The short header text.</summary>
    public static string Header(PanelColumn column) => column switch
    {
        PanelColumn.Extension => L.Column_Ext,
        PanelColumn.Size => L.Column_Size,
        PanelColumn.Modified => L.Column_Date,
        PanelColumn.Created => L.Column_Created,
        _ => L.Column_Attr
    };

    public static double Width(PanelColumn column) => column switch
    {
        PanelColumn.Extension => ColumnLayout.ExtColumnWidth,
        PanelColumn.Size => ColumnLayout.SizeColumnWidth,
        PanelColumn.Attributes => ColumnLayout.AttrColumnWidth,
        _ => ColumnLayout.DateColumnWidth
    };
}

/// <summary>One detail column as a panel lays it out: a built-in one or a custom one.</summary>
public sealed record DetailColumn(string Key, PanelColumn? BuiltIn, IFileColumn? Custom, double Width, bool AlignRight)
{
    public static DetailColumn For(PanelColumn column) => new(column.ToString(), column, null, ColumnSet.Width(column), column == PanelColumn.Size);
    public static DetailColumn For(IFileColumn column) => new(column.Key, null, column, column.Width, column.AlignRight);
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

    readonly ColumnSet columns;
    readonly HashSet<PanelColumn> dropped = [];
    readonly HashSet<IFileColumn> droppedCustom = [];
    IReadOnlyList<IFileColumn> custom = [];
    IReadOnlySet<PanelColumn>? @override;
    IReadOnlyList<string>? order;
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

    /// <summary>A folder setup's column order, in place of the user's global one; null for the global one.</summary>
    public IReadOnlyList<string>? Order
    {
        get => this.order;
        set
        {
            if (ReferenceEquals(this.order, value) || this.order != null && value != null && this.order.SequenceEqual(value))
                return;
            this.order = value;
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

    /// <summary>The columns that fit, left to right.</summary>
    public IReadOnlyList<DetailColumn> Shown { get; private set; } = [];

    /// <summary>The custom columns that fit, left to right.</summary>
    public IReadOnlyList<IFileColumn> CustomShown => this.Shown.Select(x => x.Custom).OfType<IFileColumn>().ToList();

    // every column that's on, in the order chosen; ones on but not placed (a folder's column set) go at the end
    IReadOnlyList<DetailColumn> Ordered()
    {
        var order = this.order ?? this.columns.Order;
        var on = Enum.GetValues<PanelColumn>().Where(this.IsOn).Select(DetailColumn.For)
            .Concat(this.custom.Select(DetailColumn.For))
            .DistinctBy(x => x.Key)
            .ToList();
        return order.Select(k => on.FirstOrDefault(x => x.Key == k)).OfType<DetailColumn>()
            .Concat(on.Where(x => !order.Contains(x.Key)))
            .ToList();
    }

    bool Shows(PanelColumn c) => this.IsOn(c) && !this.dropped.Contains(c);

    public bool ShowExtension => this.Shows(PanelColumn.Extension);
    public bool ShowSize => this.Shows(PanelColumn.Size);
    public bool ShowModified => this.Shows(PanelColumn.Modified);
    public bool ShowCreated => this.Shows(PanelColumn.Created);
    public bool ShowAttributes => this.Shows(PanelColumn.Attributes);


    /// <summary>Total width of the detail columns that show - the drawn details view in each row (FileDetailsView).</summary>
    public double DetailsWidth => this.Shown.Sum(x => x.Width);

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
        this.Shown = this.Ordered().Where(x => x.BuiltIn is { } c ? !this.dropped.Contains(c) : !this.droppedCustom.Contains(x.Custom!)).ToList();

        if (changed || !before.SetEquals(this.dropped) || !beforeCustom.SetEquals(this.droppedCustom) || before.Count + beforeCustom.Count == 0)
            this.OnPropertyChanged(String.Empty);
    }
}
