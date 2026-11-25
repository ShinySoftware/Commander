using Shiny;
using ShinyCommander.Services;

namespace ShinyCommander;

/// <summary>The keyboard's modifier state from GDK - Ctrl is the Primary key here.</summary>
[Singleton]
sealed class LinuxKeyModifiers : IKeyModifiers
{
    public ModifierKeys Current
    {
        get
        {
            var state = Gdk.Display.GetDefault()?.GetDefaultSeat()?.GetKeyboard()?.GetModifierState() ?? Gdk.ModifierType.NoModifierMask;
            var result = ModifierKeys.None;
            if (state.HasFlag(Gdk.ModifierType.ShiftMask))
                result |= ModifierKeys.Shift;
            if (state.HasFlag(Gdk.ModifierType.ControlMask))
                result |= ModifierKeys.Primary;
            if (state.HasFlag(Gdk.ModifierType.AltMask))
                result |= ModifierKeys.Alt;
            return result;
        }
    }
}
