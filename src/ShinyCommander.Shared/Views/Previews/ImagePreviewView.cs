namespace ShinyCommander.Views;

/// <summary>A picture fitted to the panel, with an optional line under it (ImageFilePreviewer).</summary>
public class ImagePreviewView : Grid
{
    public ImagePreviewView(byte[] bytes, string? details)
    {
        this.RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)];
        this.Add(new Image
        {
            Source = ImageSource.FromStream(() => new MemoryStream(bytes)),
            Aspect = Aspect.AspectFit,
            Margin = new Thickness(10)
        });
        if (details != null)
        {
            var label = new Label
            {
                Text = details,
                FontSize = 11,
                Padding = new Thickness(10, 0, 10, 6),
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            label.Style = (Style)Application.Current!.Resources["Subtle"];
            this.Add(label, 0, 1);
        }
    }
}
