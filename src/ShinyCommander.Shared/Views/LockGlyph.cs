namespace ShinyCommander.Views;

/// <summary>
/// A small padlock in the subtle text colour, for locked tabs - drawn, so it sits with the line icons around it
/// (the emoji is a full colour picture and the motion icon set has no lock).
/// </summary>
public class LockGlyph : GraphicsView, IDrawable
{
    public LockGlyph()
    {
        this.Drawable = this;
        this.WidthRequest = 10;
        this.HeightRequest = 12;
        this.VerticalOptions = LayoutOptions.Center;
        if (Application.Current is { } app)
            app.RequestedThemeChanged += (_, _) => this.Invalidate();
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var w = rect.Width;
        var h = rect.Height;
        var bodyTop = h * 0.45f;

        canvas.StrokeColor = ThemeColors.Get("Subtle");
        canvas.FillColor = ThemeColors.Get("Subtle");
        canvas.StrokeSize = 1.3f;

        // shackle: an arch over the body
        var shackleInset = w * 0.22f;
        canvas.DrawArc(shackleInset, 1, w - shackleInset * 2, h * 0.6f, 0, 180, false, false);
        canvas.DrawLine(shackleInset, h * 0.3f, shackleInset, bodyTop);
        canvas.DrawLine(w - shackleInset, h * 0.3f, w - shackleInset, bodyTop);

        canvas.FillRoundedRectangle(0.5f, bodyTop, w - 1, h - bodyTop - 0.5f, 1.5f);
    }
}
