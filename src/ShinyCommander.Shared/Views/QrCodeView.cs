using ShinyCommander.Share;

namespace ShinyCommander.Views;

/// <summary>
/// A QR code of <see cref="Text"/>, drawn module by module - dark on a white tile in either theme, because that's what
/// phone cameras read best.
/// </summary>
public class QrCodeView : GraphicsView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(QrCodeView), null, propertyChanged: OnTextChanged);

    readonly QrDrawable drawable = new();

    public QrCodeView()
    {
        this.Drawable = this.drawable;
        this.WidthRequest = 168;
        this.HeightRequest = 168;
    }

    public string? Text
    {
        get => (string?)this.GetValue(TextProperty);
        set => this.SetValue(TextProperty, value);
    }

    static void OnTextChanged(BindableObject bindable, object? oldValue, object? newValue)
    {
        var view = (QrCodeView)bindable;
        view.drawable.Matrix = newValue is string { Length: > 0 } text ? QrMatrix.Create(text) : null;
        view.Invalidate();
    }

    sealed class QrDrawable : IDrawable
    {
        public QrMatrix? Matrix { get; set; }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Colors.White;
            canvas.FillRoundedRectangle(dirtyRect, 8);
            if (this.Matrix is not { } m)
                return;

            // QRCoder's matrix already carries the four-module quiet zone
            const int quiet = 0;
            var count = m.Size + quiet * 2;
            var module = MathF.Floor(Math.Min(dirtyRect.Width, dirtyRect.Height) / count);
            var offsetX = dirtyRect.X + (dirtyRect.Width - module * count) / 2 + module * quiet;
            var offsetY = dirtyRect.Y + (dirtyRect.Height - module * count) / 2 + module * quiet;

            canvas.FillColor = Colors.Black;
            for (var r = 0; r < m.Size; r++)
            {
                for (var c = 0; c < m.Size; c++)
                {
                    if (m[r, c])
                        canvas.FillRectangle(offsetX + c * module, offsetY + r * module, module, module);
                }
            }
        }
    }
}
