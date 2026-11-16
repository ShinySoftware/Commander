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

    // room for the cloud badge, left of the columns - only on rows that show it, so the columns stay aligned
    const float CloudWidth = 20;

    void UpdateWidth()
    {
        var w = (this.columns?.DetailsWidth ?? 0) + (this.Item?.IsCloudOnly == true ? CloudWidth : 0);
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
        if (item.IsCloudOnly)
        {
            DrawCloud(canvas, rect.Height / 2);
            x = CloudWidth;
        }

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

    // a line cloud like the motion icons: three bumps on a flat base, 14 x 9, centred on the row
    static void DrawCloud(ICanvas canvas, float midY)
    {
        var top = midY - 4.5f;
        var path = new PathF();
        path.MoveTo(4, top + 9);
        path.CurveTo(1.5f, top + 9, 0.5f, top + 7.5f, 0.5f, top + 6);
        path.CurveTo(0.5f, top + 4.2f, 1.9f, top + 3, 3.5f, top + 3.1f);
        path.CurveTo(4.1f, top + 1.2f, 5.8f, top, 7.6f, top);
        path.CurveTo(9.8f, top, 11.4f, top + 1.6f, 11.6f, top + 3.6f);
        path.CurveTo(13, top + 3.8f, 14, top + 4.9f, 14, top + 6.3f);
        path.CurveTo(14, top + 7.8f, 12.8f, top + 9, 11.3f, top + 9);
        path.Close();

        canvas.StrokeColor = ThemeColors.Get("Subtle");
        canvas.StrokeSize = 1.3f;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(path);
    }
}
