using Shiny.Maui.Controls.DataGrid;

namespace ShinyCommander.Views;

/// <summary>The rows grids of the database editor and the preview panel: dense, striped, one positional column per value.</summary>
static class SqliteGrid
{
    // the grid's own cell binds "Data.<PropertyName>" and never asks ValueGetter, so a row that is an array of values
    // shows nothing - this is that cell (dense padding, theme text colour) reading through the getter instead
    public static DataTemplate CellText(Func<object, object?> getter) => new(() =>
    {
        var label = new Label
        {
            Padding = new Thickness(10, 6),
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            FontSize = 12
        };
        label.SetDynamicResource(Label.TextColorProperty, "Shiny.Color.OnSurfaceVariant");
        label.SetDynamicResource(Label.FontFamilyProperty, "MonoFont");
        label.BindingContextChanged += (_, _) => label.Text = label.BindingContext is { } row ? getter(row) as string : null;
        return label;
    });

    public static DataGrid New() => new()
    {
        Dense = true,
        Striped = true,
        HorizontalScroll = true,
        AllowColumnResize = true,
        DefaultColumnWidth = 160
    };

    // ids are positional: a query can return two columns with the same name ("a.id, b.id")
    public static DataGridColumn Column(int index, string title) => new()
    {
        Title = title,
        PropertyName = "c" + index,
        Filterable = false,
        MinWidth = 60
    };
}
