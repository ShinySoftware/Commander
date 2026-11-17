using ShinyCommander.Core.Database;

namespace ShinyCommander.Views;

/// <summary>
/// A database's tables and views and the first rows of the one picked (SqliteFilePreviewer). Read only, and the file is
/// open only while a table loads - a preview must not keep it from being moved or deleted.
/// </summary>
public partial class SqlitePreviewView : ContentView
{
    const int MaxRows = 100;

    readonly string path;
    List<DatabaseObjectItem> objects = [];
    CancellationTokenSource? cts;

    public SqlitePreviewView(string path)
    {
        this.path = path;
        this.InitializeComponent();
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var list = await Task.Run(() =>
        {
            using var db = SqliteDatabase.Open(this.path, readOnly: true);
            return db.GetObjects()
                .Where(x => x.Kind is SqliteObjectKind.Table or SqliteObjectKind.View)
                .Select(x => new DatabaseObjectItem(x))
                .ToList();
        }, ct);

        this.objects = list;
        BindableLayout.SetItemsSource(this.ObjectList, list);
        if (list.Count == 0)
            this.RowsLabel.Text = L.Preview_NoTables;
        else
            await this.ShowAsync(list[0]);
    }

    void OnObjectTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is DatabaseObjectItem item)
            _ = this.ShowAsync(item);
    }

    async Task ShowAsync(DatabaseObjectItem item)
    {
        foreach (var o in this.objects)
            o.IsSelected = o == item;

        this.cts?.Cancel();
        var cts = this.cts = new CancellationTokenSource();
        try
        {
            var (result, total) = await Task.Run(() =>
            {
                using var db = SqliteDatabase.Open(this.path, readOnly: true);
                var rows = db.Execute($"SELECT * FROM {SqliteDatabase.Quote(item.Name)} LIMIT {MaxRows}");
                return (rows, db.Count(item.Name));
            }, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            var grid = SqliteGrid.New();
            grid.ReadOnly = true;
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
            this.GridHost.Content = grid;
            this.RowsLabel.Text = total > result.Rows.Count
                ? L.Preview_RowsTruncatedFormat(result.Rows.Count.ToString("N0"), total.ToString("N0"))
                : L.Preview_RowsFormat(total.ToString("N0"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                this.GridHost.Content = null;
                this.RowsLabel.Text = ex.Message;
            }
        }
    }
}
