namespace ShinyCommander;

public partial class App : Application
{
    readonly AppSettings settings;
    readonly UpdateService updates;
    Window? updateWindow;
    bool started;

    public App(AppSettings settings, UpdateService updates)
    {
        this.settings = settings;
        this.updates = updates;
        this.InitializeComponent();
        this.Resources["MonoFont"] = OperatingSystem.IsWindows() ? "Consolas" : OperatingSystem.IsMacOS() ? "Menlo" : "Monospace";
        settings.EnsureDefaults();
        SettingsViewModel.ApplyTheme(settings.Theme);

        this.updates.Found += (_, release) => this.ShowUpdate(release);
        this.updates.ShowRelease = this.ShowUpdate;
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
        window.Created += (_, _) =>
        {
            if (this.started)
                return;
            this.started = true;
#if DEBUG
            // a test run never goes to GitHub or pops a window over the panels
            if (Testing.UiTestMode.IsEnabled)
                return;
#endif
            this.updates.Start();
        };
        return window;
    }

    /// <summary>
    /// The release notes with Install and Later, in a window of their own - when a check finds a release, or from
    /// Settings. One at a time - a newer release replaces the one showing.
    /// </summary>
    public void ShowUpdate(AppRelease release)
    {
        if (this.updateWindow != null)
            this.CloseWindow(this.updateWindow);

        var window = new Window(new UpdatePage(this.updates, release))
        {
            Title = "ShinyCommander Update",
            Width = 560,
            Height = 620
        };
        window.Destroying += (_, _) =>
        {
            if (this.updateWindow == window)
                this.updateWindow = null;
        };
        this.updateWindow = window;
        this.OpenWindow(window);
    }
}
