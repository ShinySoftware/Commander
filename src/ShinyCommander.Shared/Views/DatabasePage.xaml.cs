using Shiny.Controls.Diagramming;
using Shiny.Maui.Controls.Diagram;
using Shiny.Maui.Controls.DataGrid;
using ShinyCommander.Core.Database;

namespace ShinyCommander.Views;

public partial class DatabasePage : ContentPage
{
    readonly Dictionary<int, string?> edits = [];
    DatabaseViewModel? vm;

    public DatabasePage()
    {
        this.InitializeComponent();
        this.ThemeDiagram();
        if (Application.Current is { } app)
            app.RequestedThemeChanged += (_, _) => this.ThemeDiagram();
    }

    /// <summary>
    /// The diagram colours itself through hidden BoxView "probes" bound to Shiny's theme tokens, but on this head the
    /// probes never take a token (they stay transparent, wherever the resource is defined), so the canvas came out
    /// white in dark mode. Until that's fixed in Shiny.Maui.Controls.Diagram, the probes get the app's own panel colours
    /// directly. Anything missing (a renamed field) just leaves the diagram's defaults.
    /// </summary>
    void ThemeDiagram()
    {
        try
        {
            var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            var r = Application.Current!.Resources;
            Color Pick(string key) => (Color)r[key + (dark ? "Dark" : "Light")];

            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var palette = typeof(DiagramView).GetProperty("Palette", flags)?.GetValue(this.Diagram);
            if (palette == null)
                return;

            void Set(string probe, Color color)
            {
                if (palette.GetType().GetField(probe, flags)?.GetValue(palette) is BoxView box)
                    box.Color = color;
            }
            Set("surface", Pick("Window"));
            Set("surfaceContainer", Pick("Panel"));
            Set("onSurface", Pick("Text"));
            Set("onSurfaceVariant", Pick("Subtle"));
            Set("outline", Pick("Subtle"));
            Set("outlineVariant", Pick("Border").WithAlpha(0.45f));
            Set("primary", (Color)r["Accent"]);
            this.Diagram.Repaint();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Diagram theme: " + ex.Message);
        }
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (this.vm != null)
        {
            this.vm.DataChanged -= this.OnDataChanged;
            this.vm.QueryChanged -= this.OnQueryChanged;
            this.vm.DiagramChanged -= this.OnDiagramChanged;
        }
        this.vm = this.BindingContext as DatabaseViewModel;
        if (this.vm != null)
        {
            this.vm.DataChanged += this.OnDataChanged;
            this.vm.QueryChanged += this.OnQueryChanged;
            this.vm.DiagramChanged += this.OnDiagramChanged;
        }
    }

    // a fresh model - fit it, once the diagram has laid it out
    void OnDiagramChanged(object? sender, EventArgs e) => this.Dispatcher.Dispatch(() => this.Diagram.ZoomToFit());

    void OnFitDiagram(object? sender, EventArgs e) => this.Diagram.ZoomToFit();

    void OnDiagramNodeTapped(object? sender, DiagramNodeEventArgs e) => this.vm?.SelectTable(e.Node.Id);

    async void OnDeleteInDiagram(object? sender, EventArgs e)
    {
        if (this.vm != null)
            await this.vm.DeleteFromDiagram(this.Diagram.Selection.Nodes, this.Diagram.Selection.Connections);
    }

    /// <summary>
    /// Moving tables is the diagram's own business. Anything that would change the schema is stopped here and done
    /// properly instead - a new line becomes a foreign key once its columns are picked, and the diagram is then rebuilt
    /// from the database, so it never shows a key the database doesn't have.
    /// </summary>
    void OnDiagramEditing(object? sender, DiagramEditingEventArgs e)
    {
        if (e.Plan.Kind == DiagramEditKind.Move)
            return;

        e.Cancel = true;
        if (e.Plan.Kind == DiagramEditKind.AddConnection && e.Plan.AffectedConnections.FirstOrDefault() is { } link && this.vm != null)
        {
            var vm = this.vm;
            this.Dispatcher.Dispatch(async () => await vm.ConnectTables(link.SourceId, link.TargetId));
        }
    }

    void OnObjectTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject { BindingContext: DatabaseObjectItem item })
            this.vm?.SelectCommand.Execute(item);
    }

    // a fresh grid per table (and after each insert/delete): its columns are the table's, and a new grid reads page one
    void OnDataChanged(object? sender, EventArgs e)
    {
        if (this.vm?.Selected == null)
        {
            this.GridHost.Content = null;
            return;
        }

        var vm = this.vm;
        var editable = vm.CanEditRows;
        var grid = SqliteGrid.New();
        grid.PageSize = DatabaseViewModel.PageSize;
        grid.SortMode = DataGridSortMode.Single;
        grid.EmptyText = L.Database_NoRows;
        grid.ReadOnly = !editable;
        if (editable)
        {
            grid.EditMode = DataGridEditMode.Form;
            grid.EditTrigger = DataGridEditTrigger.OnRowClick;
        }

        for (var i = 0; i < vm.Columns.Count; i++)
        {
            var index = i;
            var column = SqliteGrid.Column(index, vm.Columns[i].Name);
            column.ValueGetter = row => SqliteDatabase.Format(((SqliteRow)row).Values[index]);
            column.CellTemplate = SqliteGrid.CellText(column.ValueGetter);
            column.ValueSetter = (_, value) => this.edits[index] = value?.ToString();
            column.Editable = editable;
            grid.Columns.Add(column);
        }

        grid.ServerData = async state =>
        {
            var sort = state.SortDefinitions
                .Where(x => x.Direction != DataGridSortDirection.None)
                .OrderBy(x => x.Order)
                .Select(x => (Int32.Parse(x.ColumnId[1..]), x.Direction == DataGridSortDirection.Descending))
                .ToList();
            try
            {
                var page = await vm.LoadPage(state.Page, sort);
                return new DataGridGridData(page.Rows.ToList(), (int)Math.Min(page.Total, Int32.MaxValue));
            }
            catch (Exception ex)
            {
                vm.Status = ex.Message;
                return new DataGridGridData(new List<SqliteRow>(), 0);
            }
        };

        grid.StartedEditingItem += (_, item) =>
        {
            this.edits.Clear();
            vm.CurrentRow = item as SqliteRow;
        };
        grid.CommittedItemChanges += (_, item) =>
        {
            vm.CommitRow((SqliteRow)item, this.edits);
            this.edits.Clear();
        };

        this.GridHost.Content = grid;
    }

    // query results: read-only, every row in memory (capped by SqliteDatabase.MaxQueryRows)
    void OnQueryChanged(object? sender, EventArgs e)
    {
        if (this.vm?.QueryResult is not { HasRows: true } result)
        {
            this.QueryHost.Content = null;
            return;
        }

        var grid = SqliteGrid.New();
        grid.ReadOnly = true;
        grid.SortMode = DataGridSortMode.Single;
        grid.EmptyText = L.Database_NoRows;
        for (var i = 0; i < result.Columns.Count; i++)
        {
            var index = i;
            var column = SqliteGrid.Column(index, result.Columns[i]);
            column.ValueGetter = row => SqliteDatabase.Format(((object?[])row)[index]);
            column.CellTemplate = SqliteGrid.CellText(column.ValueGetter);
            grid.Columns.Add(column);
        }
        grid.ItemsSource = result.Rows.ToList();
        this.QueryHost.Content = grid;
    }
}
