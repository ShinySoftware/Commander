using Shiny.Controls.Media;
using Shiny.Maui.Controls;
using Shiny.Maui.Controls.Media;

namespace ShinyCommander.Views;

/// <summary>
/// Audio or video in Shiny's MediaElement (MediaFilePreviewer). Video fills the panel; audio gets its icon, title and
/// artist above the transport bar. Nothing plays until asked - a preview opens on a cursor move.
/// </summary>
public class MediaPreviewView : Grid
{
    readonly MediaElement player;

    public MediaPreviewView(string path, bool isAudio, string title, string? subtitle)
    {
        this.player = new MediaElement
        {
            Source = MediaSource.FromFile(path),
            Aspect = MediaAspect.AspectFit,
            AutomationId = "PreviewMedia",
            // a paused frame keeps its controls anyway; audio has no picture for them to cover
            AutoHideTransportBar = !isAudio,
            ShowFullScreenButton = !isAudio
        };
        this.player.MediaFailed += (_, _) => this.ShowFailure();

        if (isAudio)
        {
            // icon, title, artist and the transport bar as one block in the middle of the panel
            this.RowDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(new GridLength(56)), new(GridLength.Star)];
            this.RowSpacing = 4;
            this.Padding = new Thickness(12);
            // no picture behind the controls - the bar sits on the panel like the rest of the app's chrome
            this.player.VideoBackgroundColor = Colors.Transparent;
            this.player.TransportBarBackgroundColor = ThemeColors.Get("Header");
            this.player.ControlColor = ThemeColors.Get("Text");
            this.Add(new MotionIconView
            {
                Icon = "music",
                Color = ThemeColors.Get("Subtle"),
                StrokeWidth = 1.5,
                WidthRequest = 64,
                HeightRequest = 64,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 0, 0, 8)
            }, 0, 1);
            this.Add(new Label
            {
                Text = title,
                FontSize = 14,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            }, 0, 2);
            if (subtitle != null)
                this.Add(Subtle(subtitle), 0, 3);
            this.Add(new Border
            {
                Content = this.player,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Margin = new Thickness(0, 8, 0, 0)
            }, 0, 4);
        }
        else
        {
            this.Add(this.player);
        }
    }

    /// <summary>Stops it and lets the platform player go (PreviewHost, when the panel moves on).</summary>
    public void Release()
    {
        this.player.Stop();
        this.player.Handler?.DisconnectHandler();
    }

    // a format this platform's player doesn't decode (mkv on macOS, say) - say so instead of a dead transport bar
    void ShowFailure() => MainThread.BeginInvokeOnMainThread(() =>
    {
        var label = Subtle(L.Preview_MediaFailed);
        label.VerticalOptions = LayoutOptions.Center;
        label.AutomationId = "PreviewMediaFailed";
        this.Add(label);
        Grid.SetRowSpan(label, Math.Max(1, this.RowDefinitions.Count));
        this.player.IsVisible = false;
    });

    static Label Subtle(string text)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        label.Style = (Style)Application.Current!.Resources["Subtle"];
        return label;
    }
}
