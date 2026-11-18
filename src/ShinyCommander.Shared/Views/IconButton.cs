using Shiny.Controls.MotionIcons;
using Shiny.Maui.Controls;

namespace ShinyCommander.Views;

/// <summary>
/// A small line-icon button that takes real mouse clicks on the AppKit head. A ShinyButton, or a tap recognizer on a
/// MotionIconView, gets none there (UPSTREAM_FIXES #17); a Label's does - so a clear Label lies over the icon and takes it.
/// Nothing else here has a recognizer: one on this Grid would make AppKit hand it every click inside, and it gets none
/// either. <see cref="TestId"/> names the Label, for UI automation - which taps the element it finds.
/// </summary>
public class IconButton : Grid
{
    public static readonly BindableProperty IconProperty = BindableProperty.Create(nameof(Icon), typeof(string), typeof(IconButton), null);
    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(System.Windows.Input.ICommand), typeof(IconButton), null);
    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(IconButton), null);
    public static readonly BindableProperty TestIdProperty = BindableProperty.Create(nameof(TestId), typeof(string), typeof(IconButton), null);

    public IconButton()
    {
        this.WidthRequest = 26;
        this.HeightRequest = 26;
        this.VerticalOptions = LayoutOptions.Center;

        var icon = new MotionIconView
        {
            Trigger = MotionTrigger.Hover,
            StrokeWidth = 2,
            WidthRequest = 13,
            HeightRequest = 13,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        icon.SetBinding(MotionIconView.IconProperty, static (IconButton x) => x.Icon, source: this);
        icon.SetDynamicResource(MotionIconView.ColorProperty, "Accent");
        this.Children.Add(icon);

        var tap = new TapGestureRecognizer();
        tap.SetBinding(TapGestureRecognizer.CommandProperty, static (IconButton x) => x.Command, source: this);
        tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, static (IconButton x) => x.CommandParameter, source: this);
        var hit = new Label { Text = " " };
        hit.SetBinding(AutomationIdProperty, static (IconButton x) => x.TestId, source: this);
        hit.GestureRecognizers.Add(tap);
        this.Children.Add(hit);
    }

    public string? TestId
    {
        get => (string?)this.GetValue(TestIdProperty);
        set => this.SetValue(TestIdProperty, value);
    }

    public string? Icon
    {
        get => (string?)this.GetValue(IconProperty);
        set => this.SetValue(IconProperty, value);
    }

    public System.Windows.Input.ICommand? Command
    {
        get => (System.Windows.Input.ICommand?)this.GetValue(CommandProperty);
        set => this.SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => this.GetValue(CommandParameterProperty);
        set => this.SetValue(CommandParameterProperty, value);
    }
}
