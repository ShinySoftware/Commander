using System.Runtime.InteropServices;
using Shiny;
using ShinyCommander.Services;

namespace ShinyCommander;

/// <summary>The keys' state as of the message being handled (GetKeyState) - Ctrl is the Primary key here.</summary>
[Singleton]
sealed class WindowsKeyModifiers : IKeyModifiers
{
    const int VK_SHIFT = 0x10;
    const int VK_CONTROL = 0x11;
    const int VK_MENU = 0x12;

    [DllImport("user32.dll")]
    static extern short GetKeyState(int virtualKey);

    static bool IsDown(int key) => (GetKeyState(key) & 0x8000) != 0;

    public ModifierKeys Current
    {
        get
        {
            var result = ModifierKeys.None;
            if (IsDown(VK_SHIFT))
                result |= ModifierKeys.Shift;
            if (IsDown(VK_CONTROL))
                result |= ModifierKeys.Primary;
            if (IsDown(VK_MENU))
                result |= ModifierKeys.Alt;
            return result;
        }
    }
}
