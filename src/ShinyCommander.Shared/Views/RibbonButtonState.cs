using ShinyCommander.Converters;

namespace ShinyCommander.Views;

/// <summary>
/// The AppKit ribbon's buttons (plain Borders): hover, held down, and pressed in for a toggle that's on - Folder Tree
/// with the active panel's tree showing. Turned on by the RibbonLarge / RibbonSmall styles; a toggle binds IsChecked.
/// </summary>
/// <remarks>
/// A visual state group can't do this: there's no Pressed state for a Border, and a checked fill set beside the
/// PointerOver setters fights them. The AppKit head never raises PointerPressed/Released, so there the press comes
/// from a native mouse monitor instead.
/// </remarks>
public static class RibbonButtonState
{
    public static readonly BindableProperty IsAttachedProperty = BindableProperty.CreateAttached(
        "IsAttached", typeof(bool), typeof(RibbonButtonState), false, propertyChanged: OnIsAttachedChanged);

    public static readonly BindableProperty IsCheckedProperty = BindableProperty.CreateAttached(
        "IsChecked", typeof(bool), typeof(RibbonButtonState), false, propertyChanged: (b, _, _) => Apply((Border)b));

    static readonly BindableProperty IsHoveredProperty = BindableProperty.CreateAttached(
        "IsHovered", typeof(bool), typeof(RibbonButtonState), false, propertyChanged: (b, _, _) => Apply((Border)b));

    static readonly BindableProperty IsPressedProperty = BindableProperty.CreateAttached(
        "IsPressed", typeof(bool), typeof(RibbonButtonState), false, propertyChanged: (b, _, _) => Apply((Border)b));

    public static bool GetIsAttached(BindableObject view) => (bool)view.GetValue(IsAttachedProperty);
    public static void SetIsAttached(BindableObject view, bool value) => view.SetValue(IsAttachedProperty, value);
    public static bool GetIsChecked(BindableObject view) => (bool)view.GetValue(IsCheckedProperty);
    public static void SetIsChecked(BindableObject view, bool value) => view.SetValue(IsCheckedProperty, value);

    static readonly List<WeakReference<Border>> buttons = [];

    static void OnIsAttachedChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Border border || newValue is not true)
            return;

        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => border.SetValue(IsHoveredProperty, true);
        pointer.PointerExited += (_, _) =>
        {
            border.SetValue(IsHoveredProperty, false);
            border.SetValue(IsPressedProperty, false);
        };
        pointer.PointerPressed += (_, _) => border.SetValue(IsPressedProperty, true);
        pointer.PointerReleased += (_, _) => border.SetValue(IsPressedProperty, false);
        border.GestureRecognizers.Add(pointer);

        if (buttons.Count == 0)
        {
            if (Application.Current is { } app)
                app.RequestedThemeChanged += (_, _) => ForEach(Apply);
#if MACOS_APPKIT
            WatchMouse();
#endif
        }
        buttons.Add(new WeakReference<Border>(border));
        Apply(border);
    }

    static void ForEach(Action<Border> action)
    {
        buttons.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var border in buttons.Select(w => w.TryGetTarget(out var b) ? b : null).OfType<Border>().ToList())
            action(border);
    }

    static void Apply(Border border)
    {
        if (!GetIsAttached(border))
            return;

        var isChecked = GetIsChecked(border);
        var hovered = (bool)border.GetValue(IsHoveredProperty);
        var pressed = (bool)border.GetValue(IsPressedProperty);

        border.BackgroundColor = (pressed, isChecked, hovered) switch
        {
            (true, _, _) => ThemeColors.Get("RibbonPressed"),
            (_, true, true) => ThemeColors.Get("RibbonCheckedHover"),
            (_, true, _) => ThemeColors.Get("RibbonChecked"),
            (_, _, true) => ThemeColors.Get("RibbonHover"),
            _ => Colors.Transparent
        };
        // the styles give every button a 1px stroke, so the ring doesn't move the content when it appears
        border.Stroke = isChecked ? ThemeColors.Get("RibbonCheckedRing") : Colors.Transparent;
    }

#if MACOS_APPKIT
    // the press, on mouse down; any mouse up lets go of it. The event goes on untouched - the tap still fires
    static void WatchMouse()
        => AppKit.NSEvent.AddLocalMonitorForEventsMatchingMask(AppKit.NSEventMask.LeftMouseDown | AppKit.NSEventMask.LeftMouseUp, e =>
        {
            var down = e.Type == AppKit.NSEventType.LeftMouseDown;
            ForEach(border => border.SetValue(IsPressedProperty, down && IsUnder(border, e)));
            return e;
        });

    static bool IsUnder(Border border, AppKit.NSEvent e)
    {
        if (border.Handler?.PlatformView is not AppKit.NSView view || view.Window != e.Window || view.IsHiddenOrHasHiddenAncestor)
            return false;

        var point = view.ConvertPointFromView(e.LocationInWindow, null);
        return view.Bounds.Contains(point);
    }
#endif
}
