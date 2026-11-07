namespace ShinyCommander;

public partial class App : Application
{
    readonly AppSettings settings;

    public App(AppSettings settings)
    {
        this.settings = settings;
        this.InitializeComponent();
        this.Resources["MonoFont"] = OperatingSystem.IsWindows() ? "Consolas" : OperatingSystem.IsMacOS() ? "Menlo" : "Monospace";
        settings.EnsureDefaults();
        SettingsViewModel.ApplyTheme(settings.Theme);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell())
        {
            Title = "ShinyCommander",
            Width = 1280,
            Height = 820,
            MinimumWidth = 900,
            MinimumHeight = 560
        };
#if MACOS_APPKIT
        // the default unified, full-size titlebar reserves a 52pt band above the ribbon with nothing in it -
        // a compact titlebar showing the title, with the content below it, reads as a normal Mac window
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetFullSizeContentView(window, false);
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetTitlebarStyle(window, Microsoft.Maui.Platforms.MacOS.Platform.MacOSTitlebarStyle.UnifiedCompact);
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetTitleVisibility(window, Microsoft.Maui.Platforms.MacOS.Platform.MacOSTitleVisibility.Visible);
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetTitlebarSeparatorStyle(window, Microsoft.Maui.Platforms.MacOS.Platform.MacOSTitlebarSeparatorStyle.Line);
#endif
        return window;
    }
}
