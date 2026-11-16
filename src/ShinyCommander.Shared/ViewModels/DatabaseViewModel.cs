using Shiny.Controls.Diagramming;
using ShinyCommander.Core.Database;

namespace ShinyCommander.ViewModels;

public enum DatabaseMode
{
    Data,
    Schema,
    Diagram,
    Sql
}

public partial class DatabaseObjectItem(SqliteObject obj) : ObservableObject
{
    public SqliteObject Object { get; } = obj;
    public string Name => this.Object.Name;
    public string Icon => this.Object.Kind switch
    {
        SqliteObjectKind.View => "eye",
        SqliteObjectKind.Index => "sort",
        SqliteObjectKind.Trigger => "activity",
        _ => "database"
    };
    public bool HasRows => this.Object.Kind is SqliteObjectKind.Table or SqliteObjectKind.View;

    [ObservableProperty]
    public partial string Detail { get; set; } = String.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One field of the insert form; left empty, the column takes its default.</summary>
public partial class DatabaseInsertField(SqliteColumn column) : ObservableObject
{
    public SqliteColumn Column { get; } = column;
    public string Name => this.Column.Name;

    public string Placeholder => this.Column switch
    {
        { IsPrimaryKey: true, Affinity: "INTEGER" } => L.Database_AutoNumber,
        { DefaultValue: { } d } => L.Database_DefaultFormat(d),
        { NotNull: true } => L.Database_Required,
        _ => "NULL"
    };

    [ObservableProperty]
    public partial string? Text { get; set; }
}

/// <summary>
/// F4 on a SQLite database: its tables, views, indexes and triggers down the side; the rows of the one picked (a page
/// at a time, edited in place); its schema in a designer (DatabaseViewModel.Schema.cs); every table and foreign key as
/// a diagram (DatabaseViewModel.Diagram.cs); and a box to run SQL in. The grids and the diagram's gestures live in
/// <see cref="DatabasePage"/> - grid columns only exist once a table is picked.
/// </summary>
[ShellMap<DatabasePage>("Database")]
public partial class DatabaseViewModel(IAppDialogs dialogs, INavigator navigator) : ObservableObject, IPageLifecycleAware
{
    public const int PageSize = 100;
    SqliteDatabase? db;
    bool loaded;

    [ShellProperty]
    [ObservableProperty]
    public partial string FilePath { get; set; } = String.Empty;

    public ObservableCollection<DatabaseObjectItem> Objects { get; } = [];
    public ObservableCollection<DatabaseInsertField> InsertFields { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTable), nameof(CanEditRows), nameof(ShowGrid))]
    public partial DatabaseObjectItem? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsData), nameof(IsSchema), nameof(IsDiagram), nameof(IsSql), nameof(ModeIndex), nameof(ShowGrid))]
    public partial DatabaseMode Mode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid))]
    public partial bool IsInserting { get; set; }

    /// <summary>The row clicked last - what Delete removes. The grid's own Save/Cancel bar edits it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentRow))]
    public partial SqliteRow? CurrentRow { get; set; }

    [ObservableProperty]
    public partial string SqlText { get; set; } = String.Empty;

    [ObservableProperty]
    public partial SqliteQueryResult? QueryResult { get; set; }

    [ObservableProperty]
    public partial string QueryStatus { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = String.Empty;

    public IReadOnlyList<SqliteColumn> Columns { get; private set; } = [];
    public IReadOnlyList<string>? KeyColumns { get; private set; }

    public bool HasTable => this.Selected?.HasRows == true;
    public bool CanEditRows => this.Selected != null && this.KeyColumns != null;
    public bool HasCurrentRow => this.CurrentRow != null;
    public bool IsData => this.Mode == DatabaseMode.Data;
    public bool IsSchema => this.Mode == DatabaseMode.Schema;
    public bool IsDiagram => this.Mode == DatabaseMode.Diagram;
    public bool IsSql => this.Mode == DatabaseMode.Sql;
    public bool ShowGrid => this.IsData && !this.IsInserting && this.HasTable;
    public string Title => Path.GetFileName(this.FilePath);

    public int ModeIndex
    {
        get => (int)this.Mode;
        set => this.Mode = (DatabaseMode)value;
    }

    /// <summary>The table changed, or its rows did - the page builds a fresh grid, which reads the first page again.</summary>
    public event EventHandler? DataChanged;

    /// <summary>A query finished - the page shows its rows.</summary>
    public event EventHandler? QueryChanged;

    public async void OnAppearing()
    {
        if (this.loaded)
            return;

        this.loaded = true;
        try
        {
            var path = this.FilePath;
            this.db = await Task.Run(() => SqliteDatabase.Open(path));
            this.OnPropertyChanged(nameof(this.Title));
            await this.LoadObjects(null);
            this.BuildDiagram();
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Editor_CannotOpen, ex.Message);
            await navigator.GoBack();
        }
    }

    // nothing opens on top of this page, so leaving it (however) is closing it - and the file shouldn't stay open
    public void OnDisappearing()
    {
        this.db?.Dispose();
        this.db = null;
        this.loaded = false;
    }

    async Task LoadObjects(string? select)
    {
        var db = this.db!;
        var objects = await Task.Run(db.GetObjects);
        this.Objects.Clear();
        foreach (var obj in objects)
            this.Objects.Add(new DatabaseObjectItem(obj));

        this.Status = L.Database_StatusFormat(
            objects.Count(x => x.Kind == SqliteObjectKind.Table),
            objects.Count(x => x.Kind == SqliteObjectKind.View),
            SizeFormatter.Friendly(new FileInfo(this.FilePath).Length)
        );

        var pick = this.Objects.FirstOrDefault(x => x.Name.Equals(select, StringComparison.OrdinalIgnoreCase)) ?? this.Objects.FirstOrDefault();
        if (pick != null)
        {
            this.Select(pick);
        }
        else
        {
            // the last table was dropped
            this.Selected = null;
            this.Columns = [];
            this.KeyColumns = null;
            this.LoadDesign(null);
            this.DataChanged?.Invoke(this, EventArgs.Empty);
        }

        // row counts after the first table shows - a big table's COUNT(*) takes a moment
        foreach (var item in this.Objects.Where(x => x.HasRows).ToList())
            item.Detail = await this.Try(() => L.Database_RowsFormat(db.Count(item.Name))) ?? String.Empty;
        foreach (var item in this.Objects.Where(x => !x.HasRows))
            item.Detail = L.Database_OnTableFormat(item.Object.Table);
    }

    [RelayCommand]
    void Select(DatabaseObjectItem item)
    {
        if (this.db == null)
            return;

        foreach (var o in this.Objects)
            o.IsSelected = o == item;

        this.Columns = item.HasRows ? this.db.GetColumns(item.Name) : [];
        this.KeyColumns = this.db.GetKeyColumns(item.Object, this.Columns);
        this.Selected = item;
        this.CurrentRow = null;
        this.IsInserting = false;
        this.LoadDesign(item);

        // an index or trigger has no rows to show
        if (!item.HasRows && this.Mode == DatabaseMode.Data)
            this.Mode = DatabaseMode.Schema;

        if (String.IsNullOrWhiteSpace(this.SqlText) && item.HasRows)
            this.SqlText = $"SELECT * FROM {SqliteDatabase.Quote(item.Name)} LIMIT 100;";

        this.DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A page of the selected table for the grid; <paramref name="sort"/> is (column index, descending).</summary>
    public Task<SqlitePage> LoadPage(int page, IReadOnlyList<(int Column, bool Descending)> sort)
    {
        var db = this.db!;
        var table = this.Selected!.Name;
        var columns = this.Columns;
        var keys = this.KeyColumns;
        var order = sort.Select(x => (columns[x.Column].Name, x.Descending)).ToList();
        return Task.Run(() => db.ReadPage(table, columns, keys, page * PageSize, PageSize, order));
    }

    /// <summary>
    /// Writes the cells the grid's editor changed - <paramref name="edits"/> is the text of every column by index,
    /// and only those that differ from what was read become the UPDATE.
    /// Synchronous on purpose: the grid reads the page again straight after, and must see the new values.
    /// </summary>
    public void CommitRow(SqliteRow row, IReadOnlyDictionary<int, string?> edits)
    {
        if (this.db == null || this.Selected == null || this.KeyColumns == null)
            return;

        var changes = new Dictionary<string, object?>();
        foreach (var (index, text) in edits.Where(x => x.Value != SqliteDatabase.Format(row.Values[x.Key])))
            changes[this.Columns[index].Name] = SqliteDatabase.Parse(text, this.Columns[index]);

        try
        {
            this.db.Update(this.Selected.Name, this.KeyColumns, row.Key, changes);
            this.CurrentRow = null;
        }
        catch (Exception ex)
        {
            // the grid shows what's really in the table again; the alert says why the edit didn't stick
            _ = dialogs.Alert(L.Database_UpdateFailed, ex.Message);
        }
    }

    [RelayCommand]
    void Refresh()
    {
        if (this.Selected != null)
            this.Select(this.Selected);
    }

    /// <summary>After DDL: the list again (picking <paramref name="select"/>), the designer and the diagram.</summary>
    async Task SchemaChanged(string? select)
    {
        await this.LoadObjects(select);
        this.BuildDiagram();
    }

    [RelayCommand]
    void BeginInsert()
    {
        if (!this.CanEditRows)
            return;

        this.InsertFields.Clear();
        foreach (var column in this.Columns)
            this.InsertFields.Add(new DatabaseInsertField(column));
        this.Mode = DatabaseMode.Data;
        this.IsInserting = true;
    }

    [RelayCommand]
    void CancelInsert() => this.IsInserting = false;

    [RelayCommand]
    async Task ConfirmInsert()
    {
        if (this.db == null || this.Selected == null)
            return;

        var values = this.InsertFields
            .Where(x => !String.IsNullOrEmpty(x.Text))
            .ToDictionary(x => x.Name, x => SqliteDatabase.Parse(x.Text, x.Column));
        try
        {
            this.db.Insert(this.Selected.Name, values);
            this.IsInserting = false;
            await this.AfterRowChange();
            await dialogs.Toast(L.Database_Inserted, ToastKind.Success);
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Database_InsertFailed, ex.Message);
        }
    }

    [RelayCommand]
    async Task DeleteRow()
    {
        if (this.db == null || this.Selected == null || this.KeyColumns == null || this.CurrentRow is not { } row)
        {
            await dialogs.Toast(L.Database_PickRow);
            return;
        }

        var preview = String.Join(", ", row.Values.Take(3).Select(SqliteDatabase.Format));
        if (!await dialogs.Confirm(L.Database_DeleteRow, L.Database_DeleteConfirmFormat(preview), L.Common_Delete, L.Common_Cancel))
            return;

        try
        {
            this.db.Delete(this.Selected.Name, this.KeyColumns, row.Key);
            await this.AfterRowChange();
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Database_DeleteFailed, ex.Message);
        }
    }

    async Task AfterRowChange()
    {
        var item = this.Selected!;
        this.Select(item);
        var db = this.db!;
        item.Detail = await this.Try(() => L.Database_RowsFormat(db.Count(item.Name))) ?? item.Detail;
    }

    [RelayCommand]
    async Task RunSql()
    {
        if (this.db == null || String.IsNullOrWhiteSpace(this.SqlText))
            return;

        this.Mode = DatabaseMode.Sql;
        var db = this.db;
        var sql = this.SqlText;
        try
        {
            var result = await Task.Run(() => db.Execute(sql));
            this.QueryResult = result;
            this.QueryStatus = result.HasRows
                ? (result.Truncated ? L.Database_QueryTruncatedFormat(result.Rows.Count) : L.Database_QueryRowsFormat(result.Rows.Count))
                : L.Database_QueryChangedFormat(result.RowsAffected);
            this.QueryChanged?.Invoke(this, EventArgs.Empty);

            // CREATE / DROP / ALTER change the list and the diagram; any write can change the open table's rows
            if (!result.HasRows)
                await this.SchemaChanged(this.Selected?.Name);
        }
        catch (Exception ex)
        {
            this.QueryResult = null;
            this.QueryStatus = ex.Message;
            this.QueryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    Task Close() => navigator.GoBack();

    async Task<T?> Try<T>(Func<T> work) where T : class
    {
        try
        {
            return await Task.Run(work);
        }
        catch
        {
            return null;
        }
    }

    SqliteTableDefinition? design;

    public ObservableCollection<DesignerColumn> DesignColumns { get; } = [];
    public ObservableCollection<DesignerForeignKey> DesignForeignKeys { get; } = [];
    public ObservableCollection<DatabaseObjectItem> Dependents { get; } = [];

    [ObservableProperty]
    public partial string DesignName { get; set; } = String.Empty;

    /// <summary>The designer's column names, for the foreign key column picker.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> DesignColumnNames { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> TableNames { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRebuildBlocker))]
    public partial string? RebuildBlocker { get; set; }

    /// <summary>The selected object's CREATE statement, editable for every kind.</summary>
    [ObservableProperty]
    public partial string ObjectSql { get; set; } = String.Empty;

    public bool HasRebuildBlocker => this.RebuildBlocker != null;
    public bool IsTableSelected => this.Selected?.Object.Kind == SqliteObjectKind.Table;
    public bool HasDependents => this.Dependents.Count > 0;

    void LoadDesign(DatabaseObjectItem? item)
    {
        this.DesignColumns.Clear();
        this.DesignForeignKeys.Clear();
        this.Dependents.Clear();
        this.design = null;
        this.RebuildBlocker = null;
        this.ObjectSql = item?.Object.Sql ?? String.Empty;
        this.TableNames = this.Objects.Where(x => x.Object.Kind == SqliteObjectKind.Table).Select(x => x.Name).ToList();

        if (this.db != null && item?.Object.Kind == SqliteObjectKind.Table)
        {
            this.design = this.db.GetTableDefinition(item.Name);
            this.DesignName = this.design.Name;
            this.RebuildBlocker = this.design.RebuildBlocker;
            foreach (var c in this.design.Columns)
                this.AddDesignColumn(DesignerColumn.From(c));
            foreach (var fk in this.design.ForeignKeys)
                this.DesignForeignKeys.Add(new DesignerForeignKey(this.ColumnsOf, fk));
            foreach (var dep in this.Objects.Where(x => x.Object.Kind is SqliteObjectKind.Index or SqliteObjectKind.Trigger && x.Object.Table == item.Name))
                this.Dependents.Add(dep);
        }
        else
        {
            this.DesignName = item?.Name ?? String.Empty;
        }
        this.RefreshColumnNames();
        this.OnPropertyChanged(nameof(this.IsTableSelected));
        this.OnPropertyChanged(nameof(this.HasDependents));
    }

    IReadOnlyList<string> ColumnsOf(string table)
    {
        try
        {
            return this.db?.GetColumns(table).Select(x => x.Name).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    void AddDesignColumn(DesignerColumn column)
    {
        column.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DesignerColumn.Name))
                this.RefreshColumnNames();
        };
        this.DesignColumns.Add(column);
    }

    void RefreshColumnNames() => this.DesignColumnNames = this.DesignColumns.Select(x => x.Name).Where(x => x.Length > 0).ToList();

    [RelayCommand]
    void AddColumn()
    {
        this.AddDesignColumn(new DesignerColumn { Name = "column" + (this.DesignColumns.Count + 1), Type = "TEXT" });
        this.RefreshColumnNames();
    }

    [RelayCommand]
    void RemoveColumn(DesignerColumn column)
    {
        this.DesignColumns.Remove(column);
        this.RefreshColumnNames();
    }

    [RelayCommand]
    void AddForeignKey() => this.DesignForeignKeys.Add(new DesignerForeignKey(this.ColumnsOf)
    {
        Column = this.DesignColumnNames.FirstOrDefault(),
        RefTable = this.TableNames.FirstOrDefault(x => x != this.Selected?.Name) ?? this.TableNames.FirstOrDefault()
    });

    [RelayCommand]
    void RemoveForeignKey(DesignerForeignKey fk) => this.DesignForeignKeys.Remove(fk);

    [RelayCommand]
    void RevertDesign() => this.LoadDesign(this.Selected);

    [RelayCommand]
    async Task ApplyDesign()
    {
        if (this.db == null || this.design == null || this.Selected == null)
            return;

        var def = this.design.Clone();
        def.Name = this.DesignName.Trim();
        def.Columns = this.DesignColumns.Select(x => x.ToDefinition()).ToList();
        def.ForeignKeys = this.DesignForeignKeys.Select(x => x.ToDefinition()).OfType<SqliteForeignKeyDefinition>().ToList();

        if (def.Name.Length == 0 || def.Columns.Count == 0 || def.Columns.Any(x => x.Name.Length == 0))
        {
            await dialogs.Alert(L.Database_SchemaFailed, L.Database_NamesRequired);
            return;
        }

        var table = this.Selected.Name;
        var db = this.db;
        await this.Schema(() => db.AlterTable(table, def), def.Name);
    }

    /// <summary>The CREATE statement as typed - a table is rebuilt with its rows, anything else dropped and recreated.</summary>
    [RelayCommand]
    async Task ApplySql()
    {
        if (this.db == null || this.Selected == null || String.IsNullOrWhiteSpace(this.ObjectSql))
            return;

        var obj = this.Selected.Object;
        var sql = this.ObjectSql;
        var db = this.db;
        var before = db.GetObjects().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await this.Schema(() => db.ReplaceObject(obj, sql), null);

        // the statement may have renamed it - pick whatever it created
        var created = db.GetObjects().FirstOrDefault(x => x.Kind == obj.Kind && !before.Contains(x.Name))?.Name ?? obj.Name;
        await this.SchemaChanged(created);
    }

    [RelayCommand]
    async Task DropObject()
    {
        if (this.db == null || this.Selected is not { } item)
            return;

        if (!await dialogs.Confirm(L.Database_Drop, L.Database_DropConfirmFormat(item.Name), L.Common_Delete, L.Common_Cancel))
            return;

        var db = this.db;
        await this.Schema(() => db.DropObject(item.Object), null);
    }

    [RelayCommand]
    async Task NewObject()
    {
        if (this.db == null)
            return;

        var kind = await dialogs.Choose(L.Database_New, [L.Database_NewTable, L.Database_NewView, L.Database_NewIndex, L.Database_NewTrigger], L.Common_Cancel);
        if (kind == null)
            return;

        var name = await dialogs.Prompt(kind, L.Database_NewNameMessage, null, L.Common_Add);
        if (String.IsNullOrWhiteSpace(name))
            return;

        name = name.Trim();
        var q = SqliteDatabase.Quote(name);
        var table = this.Selected?.Object.Table ?? this.TableNames.FirstOrDefault() ?? "table";
        var qt = SqliteDatabase.Quote(table);
        if (kind == L.Database_NewTable)
        {
            var db = this.db;
            var def = new SqliteTableDefinition
            {
                Name = name,
                Columns = [new SqliteColumnDefinition { Name = "id", Type = "INTEGER", PrimaryKey = true }]
            };
            await this.Schema(() => db.CreateTable(def), name);
            this.Mode = DatabaseMode.Schema;
            return;
        }

        // the rest start as a statement to finish in the SQL box
        var firstColumn = SqliteDatabase.Quote(this.ColumnsOf(table).FirstOrDefault() ?? "id");
        this.SqlText = kind == L.Database_NewView ? $"CREATE VIEW {q} AS\nSELECT * FROM {qt};"
            : kind == L.Database_NewIndex ? $"CREATE INDEX {q} ON {qt} ({firstColumn});"
            : $"CREATE TRIGGER {q} AFTER INSERT ON {qt}\nBEGIN\n    SELECT 1;\nEND;";
        this.QueryStatus = L.Database_NewSqlHint;
        this.Mode = DatabaseMode.Sql;
    }

    // DDL off the UI thread, then everything that shows the schema again; an error leaves the database as it was
    async Task Schema(Action change, string? select)
    {
        try
        {
            await Task.Run(change);
            await this.SchemaChanged(select ?? this.Selected?.Name);
            await dialogs.Toast(L.Database_SchemaSaved, ToastKind.Success);
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Database_SchemaFailed, ex.Message);
        }
    }

    const double NodeWidth = 220;
    const double NodeHeaderHeight = 34;
    const double NodeRowHeight = 18;

    // where the user dragged tables to - kept through a rebuild so the diagram doesn't jump about after each change
    readonly Dictionary<string, (double X, double Y)> pinned = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DiagramNode> DiagramNodes { get; } = [];
    public ObservableCollection<DiagramConnection> DiagramConnections { get; } = [];

    /// <summary>The diagram was rebuilt from the database - the page fits it to the view.</summary>
    public event EventHandler? DiagramChanged;

    void BuildDiagram()
    {
        if (this.db == null)
            return;

        foreach (var node in this.DiagramNodes.Where(x => x.IsPinned))
            this.pinned[node.Id] = (node.X, node.Y);

        var tables = this.db.GetObjects().Where(x => x.Kind == SqliteObjectKind.Table).ToList();
        var fks = this.db.GetAllForeignKeys();

        this.DiagramNodes.Clear();
        foreach (var table in tables)
        {
            var fkColumns = fks.Where(x => x.Table == table.Name).SelectMany(x => x.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var columns = this.db.GetColumns(table.Name)
                .Select(c => new DiagramTableColumn(c.Name, c.DeclaredType, c.IsPrimaryKey ? "PK" : fkColumns.Contains(c.Name) ? "FK" : String.Empty))
                .ToList();
            var node = new DiagramNode(table.Name, table.Name)
            {
                Shape = DiagramNodeShape.RoundedRectangle,
                Width = NodeWidth,
                Height = NodeHeaderHeight + columns.Count * NodeRowHeight + 8,
                Item = new DiagramTable(table.Name, columns)
            };
            if (this.pinned.TryGetValue(table.Name, out var at))
            {
                node.X = at.X;
                node.Y = at.Y;
                node.IsPinned = true;
            }
            this.DiagramNodes.Add(node);
        }

        this.DiagramConnections.Clear();
        foreach (var fk in fks.Where(f => tables.Any(t => t.Name.Equals(f.RefTable, StringComparison.OrdinalIgnoreCase))))
        {
            var target = tables.First(t => t.Name.Equals(fk.RefTable, StringComparison.OrdinalIgnoreCase)).Name;
            this.DiagramConnections.Add(new DiagramConnection(fk.Table, target, $"{String.Join(", ", fk.Columns)} → {String.Join(", ", fk.RefColumns)}")
            {
                Item = fk
            });
        }

        this.DiagramChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A line drawn from one table to another: which column of the first points at which column of the second (its
    /// primary key offered first), then the foreign key is added - a table rebuild, so it runs only once both are picked.
    /// </summary>
    public async Task ConnectTables(string fromTable, string toTable)
    {
        if (this.db == null)
            return;

        var fromColumns = this.ColumnsOf(fromTable);
        var column = await dialogs.Choose(L.Database_FkColumnFormat(fromTable, toTable), fromColumns, L.Common_Cancel);
        if (column == null)
            return;

        var refs = this.db.GetColumns(toTable).OrderByDescending(x => x.IsPrimaryKey).Select(x => x.Name).ToList();
        var refColumn = refs.Count == 1 ? refs[0] : await dialogs.Choose(L.Database_FkRefColumnFormat(toTable), refs, L.Common_Cancel);
        if (refColumn == null)
            return;

        var db = this.db;
        var fk = new SqliteForeignKeyDefinition { Columns = [column], RefTable = toTable, RefColumns = [refColumn] };
        await this.Schema(() => db.AddForeignKey(fromTable, fk), this.Selected?.Name);
    }

    /// <summary>Delete in the diagram: drops the selected foreign keys, or the selected tables (asking first).</summary>
    public async Task DeleteFromDiagram(IReadOnlyList<DiagramNode> nodes, IReadOnlyList<DiagramConnection> connections)
    {
        if (this.db == null)
            return;

        var db = this.db;
        var keys = connections.Select(x => x.Item).OfType<SqliteForeignKey>().ToList();
        if (keys.Count > 0)
        {
            var text = String.Join("\n", keys.Select(k => $"{k.Table} ({String.Join(", ", k.Columns)}) → {k.RefTable}"));
            if (!await dialogs.Confirm(L.Database_DropFk, text, L.Common_Delete, L.Common_Cancel))
                return;

            await this.Schema(() =>
            {
                foreach (var k in keys)
                    db.RemoveForeignKey(k.Table, new SqliteForeignKeyDefinition
                    {
                        Columns = [.. k.Columns],
                        RefTable = k.RefTable,
                        RefColumns = [.. k.RefColumns],
                        OnDelete = k.OnDelete,
                        OnUpdate = k.OnUpdate
                    });
            }, this.Selected?.Name);
            return;
        }

        var tables = nodes.Select(x => x.Id).ToList();
        if (tables.Count == 0)
        {
            await dialogs.Toast(L.Database_DiagramPick);
            return;
        }
        if (!await dialogs.Confirm(L.Database_Drop, L.Database_DropConfirmFormat(String.Join(", ", tables)), L.Common_Delete, L.Common_Cancel))
            return;

        var objects = db.GetObjects().Where(x => x.Kind == SqliteObjectKind.Table && tables.Contains(x.Name)).ToList();
        await this.Schema(() =>
        {
            foreach (var o in objects)
                db.DropObject(o);
        }, null);
    }

    /// <summary>A table clicked in the diagram becomes the selected one, without leaving the diagram.</summary>
    public void SelectTable(string name)
    {
        var item = this.Objects.FirstOrDefault(x => x.Name == name);
        if (item != null && item != this.Selected)
            this.Select(item);
    }
}
