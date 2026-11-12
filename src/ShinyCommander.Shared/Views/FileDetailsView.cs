using System.ComponentModel;
using Microsoft.Maui.Graphics;
using ShinyCommander.Converters;
using Font = Microsoft.Maui.Graphics.Font;

namespace ShinyCommander.Views;

/// <summary>
/// A file row's detail columns (Ext, Size, Date, Created, Attr) drawn into one view instead of five labels. Rows are
/// created and bound as the list scrolls, and on the AppKit head every label is a native text field with its own
/// bindings - drawing the columns halves the native views per row and drops most of its bindings.
/// </summary>
public class FileDetailsView : GraphicsView, IDrawable
{
    public static readonly BindableProperty ItemProperty = BindableProperty.Create(
        nameof(Item), typeof(FileItemViewModel), typeof(FileDetailsView), null,
        propertyChanged: (b, o, n) => ((FileDetailsView)b).OnItemChanged(o as FileItemViewModel, n as FileItemViewModel));

    ColumnLayout? columns;

    public FileDetailsView()
    {
        this.Drawable = this;
        this.InputTransparent = true; // taps belong to the row
    }

    public FileItemViewModel? Item
    {
        get => (FileItemViewModel?)this.GetValue(ItemProperty);
        set => this.SetValue(ItemProperty, value);
    }

    void OnItemChanged(FileItemViewModel? oldItem, FileItemViewModel? newItem)
    {
        if (oldItem != null)
            oldItem.PropertyChanged -= this.OnItemPropertyChanged;
        if (this.columns != null)
            this.columns.PropertyChanged -= this.OnColumnsChanged;

        this.columns = newItem?.Columns;
        if (newItem != null)
            newItem.PropertyChanged += this.OnItemPropertyChanged;
        if (this.columns != null)
            this.columns.PropertyChanged += this.OnColumnsChanged;

        this.UpdateWidth();
        this.Invalidate();
    }

    void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileItemViewModel.RowState) or nameof(FileItemViewModel.SizeText))
            this.Invalidate();
    }

    void OnColumnsChanged(object? sender, PropertyChangedEventArgs e)
    {
        this.UpdateWidth();
        this.Invalidate();
    }

    void UpdateWidth()
    {
        var w = this.columns?.DetailsWidth ?? 0;
        if (Math.Abs(this.WidthRequest - w) > 0.5)
            this.WidthRequest = w;
    }

    static string? monoFamily;
    static string MonoFamily => monoFamily ??= Application.Current?.Resources.TryGetValue("MonoFont", out var f) == true && f is string s ? s : "Menlo";

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (this.Item is not { } item || this.columns is not { } c)
            return;

        var state = item.RowState;
        // the name is the one strong column - the details sit back in the subtle colour unless marked
        var bold = state.EndsWith("Marked", StringComparison.Ordinal);
        canvas.FontColor = ThemeColors.Get(bold ? "Marked" : "Subtle");
        var mono = new Font(MonoFamily, bold ? FontWeights.Bold : FontWeights.Normal);
        // the system font's digits are tabular, so sizes and dates still line up
        var plain = new Font(null, bold ? FontWeights.Bold : FontWeights.Normal);

        float x = 0;
        void Column(bool show, double width, string text, Font font, float size, HorizontalAlignment align, float rightPad = 0)
        {
            if (!show)
                return;
            if (!String.IsNullOrEmpty(text))
            {
                canvas.Font = font;
                canvas.FontSize = size;
                canvas.DrawString(text, x, 0, (float)width - rightPad, rect.Height, align, VerticalAlignment.Center, TextFlow.ClipBounds);
            }
            x += (float)width;
        }

        Column(c.ShowExtension, ColumnLayout.ExtColumnWidth, item.Extension, plain, 12, HorizontalAlignment.Left);
        Column(c.ShowSize, ColumnLayout.SizeColumnWidth, item.SizeText, plain, 12, HorizontalAlignment.Right, 8);
        Column(c.ShowModified, ColumnLayout.DateColumnWidth, item.DateText, plain, 12, HorizontalAlignment.Left);
        Column(c.ShowCreated, ColumnLayout.DateColumnWidth, item.CreatedText, plain, 12, HorizontalAlignment.Left);
        Column(c.ShowAttributes, ColumnLayout.AttrColumnWidth, item.AttributeText, mono, 12, HorizontalAlignment.Left);
    }
}
