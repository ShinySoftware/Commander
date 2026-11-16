using Shiny.Controls.Diagramming;
using ShinyCommander.Core.Database;

namespace ShinyCommander.ViewModels;

/// <summary>A column in the table designer.</summary>
public partial class DesignerColumn : ObservableObject
{
    public string? OriginalName { get; init; }

    [ObservableProperty] public partial string Name { get; set; } = String.Empty;
    [ObservableProperty] public partial string Type { get; set; } = String.Empty;
    [ObservableProperty] public partial bool NotNull { get; set; }
    [ObservableProperty] public partial bool PrimaryKey { get; set; }
    [ObservableProperty] public partial bool Unique { get; set; }
    [ObservableProperty] public partial string? Default { get; set; }

    public static DesignerColumn From(SqliteColumnDefinition c) => new()
    {
        OriginalName = c.OriginalName,
        Name = c.Name,
        Type = c.Type,
        NotNull = c.NotNull,
        PrimaryKey = c.PrimaryKey,
        Unique = c.Unique,
        Default = c.Default
    };

    public SqliteColumnDefinition ToDefinition() => new()
    {
        OriginalName = this.OriginalName,
        Name = this.Name.Trim(),
        Type = this.Type.Trim(),
        NotNull = this.NotNull,
        PrimaryKey = this.PrimaryKey,
        Unique = this.Unique,
        Default = String.IsNullOrWhiteSpace(this.Default) ? null : this.Default
    };
}

/// <summary>
/// A foreign key in the table designer: one column to one column, picked from lists. A key over several columns (which
/// the designer can't edit) is kept as it is and only offers removal.
/// </summary>
public partial class DesignerForeignKey : ObservableObject
{
    readonly Func<string, IReadOnlyList<string>> columnsOf;
    readonly SqliteForeignKeyDefinition? composite;

    public DesignerForeignKey(Func<string, IReadOnlyList<string>> columnsOf, SqliteForeignKeyDefinition? from = null)
    {
        this.columnsOf = columnsOf;
        if (from is { Columns.Count: > 1 })
        {
            this.composite = from;
        }
        else if (from != null)
        {
            this.Column = from.Columns.FirstOrDefault();
            this.RefTable = from.RefTable;
            this.RefColumn = from.RefColumns.FirstOrDefault();
            this.OnDelete = from.OnDelete;
            this.OnUpdate = from.OnUpdate;
        }
    }

    public bool IsComposite => this.composite != null;
    public bool IsSimple => this.composite == null;
    public string Summary => this.composite == null
        ? String.Empty
        : $"({String.Join(", ", this.composite.Columns)}) → {this.composite.RefTable} ({String.Join(", ", this.composite.RefColumns)})";

    [ObservableProperty] public partial string? Column { get; set; }
    [ObservableProperty] public partial string? RefTable { get; set; }
    [ObservableProperty] public partial string? RefColumn { get; set; }
    [ObservableProperty] public partial string OnDelete { get; set; } = "NO ACTION";
    [ObservableProperty] public partial string OnUpdate { get; set; } = "NO ACTION";
    [ObservableProperty] public partial IReadOnlyList<string> RefColumns { get; set; } = [];

    public IReadOnlyList<string> Actions => SqliteDatabase.ForeignKeyActions;

    partial void OnRefTableChanged(string? value)
    {
        this.RefColumns = value == null ? [] : this.columnsOf(value);
        if (this.RefColumn == null || !this.RefColumns.Contains(this.RefColumn))
            this.RefColumn = this.RefColumns.FirstOrDefault();
    }

    public SqliteForeignKeyDefinition? ToDefinition() => this.composite ?? (this.Column == null || this.RefTable == null
        ? null
        : new SqliteForeignKeyDefinition
        {
            Columns = [this.Column],
            RefTable = this.RefTable,
            RefColumns = this.RefColumn == null ? [] : [this.RefColumn],
            OnDelete = this.OnDelete,
            OnUpdate = this.OnUpdate
        });
}

/// <summary>A table as a diagram node draws it: its name, then a line per column marked PK / FK.</summary>
public sealed record DiagramTable(string Name, IReadOnlyList<DiagramTableColumn> Columns);

public sealed record DiagramTableColumn(string Name, string Type, string Badge);
