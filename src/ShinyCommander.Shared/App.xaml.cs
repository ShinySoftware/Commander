namespace ShinyCommander;

public partial class App : Application
{
    readonly AppSettings settings;
    readonly UpdateService updates;
    readonly QuitGuard quitGuard;
    readonly SharingService sharing;
    readonly KeepAwakeService keepAwake;
    readonly IServiceProvider services;
    Window? updateWindow;
    Window? sharingWindow;
    Window? mainWindow;
    bool started;

    public App(AppSettings settings, UpdateService updates, QuitGuard quitGuard, SharingService sharing, KeepAwakeService keepAwake, IServiceProvider services, StringsLocalized strings)
    {
        this.sharing = sharing;
        // built here so it watches the jobs and shares from the start
        this.keepAwake = keepAwake;
        this.services = services;
        // whatever an earlier run zipped or copied out for the share sheet
        FileShareSheet.CleanUp();
        // before anything reads a string - the pages take their text as they are built
        Strings.ApplyLanguage(LanguageFor(settings));
        Strings.Initialize(strings);

        this.settings = settings;
        this.updates = updates;
        this.quitGuard = quitGuard;
        this.InitializeComponent();
        this.Resources["MonoFont"] = OperatingSystem.IsWindows() ? "Consolas" : OperatingSystem.IsMacOS() ? "Menlo" : "Monospace";
        settings.EnsureDefaults();
        SettingsViewModel.ApplyTheme(settings.Theme);

        this.updates.Found += (_, release) => this.ShowUpdate(release);
        this.updates.ShowRelease = this.ShowUpdate;
    }

    static string? LanguageFor(AppSettings settings)
    {
#if DEBUG
        // the UI tests assert on English text whatever the machine's language - SHINYCOMMANDER_LANG overrides it
        // (e.g. screenshots in French)
        if (Testing.UiTestMode.IsEnabled)
            return Environment.GetEnvironmentVariable("SHINYCOMMANDER_LANG") is { Length: > 0 } lang ? lang : "en";
#endif
        return settings.Language;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell())
        {
            Title = "ShinyCommander",
            // the size it was left at - never below the minimum
            Width = Math.Max(900, this.settings.WindowWidth > 0 ? this.settings.WindowWidth : 1280),
            Height = Math.Max(560, this.settings.WindowHeight > 0 ? this.settings.WindowHeight : 820),
            MinimumWidth = 900,
            MinimumHeight = 560
        };
        window.SizeChanged += (_, _) =>
        {
            if (window.Width <= 0 || window.Height <= 0)
                return;

            this.settings.WindowWidth = (int)window.Width;
            this.settings.WindowHeight = (int)window.Height;
        };
#if MACOS_APPKIT
        // the default unified, full-size titlebar reserves a 52pt band above the ribbon with nothing in it -
        // a compact titlebar showing the title, with the content below it, reads as a normal Mac window
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetFullSizeContentView(window, false);
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetTitlebarStyle(window, Microsoft.Maui.Platforms.MacOS.Platform.MacOSTitlebarStyle.UnifiedCompact);
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetTitleVisibility(window, Microsoft.Maui.Platforms.MacOS.Platform.MacOSTitleVisibility.Visible);
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetTitlebarSeparatorStyle(window, Microsoft.Maui.Platforms.MacOS.Platform.MacOSTitlebarSeparatorStyle.Line);
#endif
        // closing while copies/moves are still running asks first
        this.quitGuard.Attach(window);
        this.mainWindow = window;
        window.Destroying += this.OnMainWindowDestroying;
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
            Title = L.Update_WindowTitle,
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

    /// <summary>
    /// The Sharing window - one at a time, brought to the front when it's already open. With a folder, the Serve Folder
    /// form opens for it.
    /// </summary>
    public void ShowSharing(string? servePath)
    {
        if (this.sharingWindow?.Page is SharingPage open)
        {
            if (servePath != null)
                open.ViewModel.NewShare(servePath);
            this.ActivateWindow(this.sharingWindow);
            return;
        }

        var page = this.services.GetRequiredService<SharingPage>();
        page.ViewModel.CurrentFolder = this.CurrentFolder;
        if (servePath != null)
            page.ViewModel.NewShare(servePath);

        var window = new Window(page)
        {
            Title = L.Sharing_Title,
            Width = 880,
            Height = 860,
            MinimumWidth = 820,
            MinimumHeight = 480
        };
#if MACOS_APPKIT
        // a compact titlebar with the content below it, like the main window
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetFullSizeContentView(window, false);
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetTitlebarStyle(window, Microsoft.Maui.Platforms.MacOS.Platform.MacOSTitlebarStyle.UnifiedCompact);
        Microsoft.Maui.Platforms.MacOS.Platform.MacOSWindow.SetTitleVisibility(window, Microsoft.Maui.Platforms.MacOS.Platform.MacOSTitleVisibility.Visible);
#endif
        window.Destroying += this.OnSharingWindowDestroying;
        this.sharingWindow = window;
        this.OpenWindow(window);
    }

    /// <summary>The Sharing window, while it's open.</summary>
    public Window? SharingWindow => this.sharingWindow;

    /// <summary>Closes the Sharing window if it's open (the shares carry on).</summary>
    public void CloseSharing()
    {
        if (this.sharingWindow is { } window)
            this.CloseWindow(window);
    }

    Task StopSharingAsync() => this.sharing.DisposeAsync().AsTask();

    string? CurrentFolder() => (this.mainWindow?.Page as Shell)?.CurrentPage?.BindingContext is MainViewModel main ? main.ActivePanel.Path : null;

    void OnSharingWindowDestroying(object? sender, EventArgs e)
    {
        if (sender is Window window)
            window.Destroying -= this.OnSharingWindowDestroying;
        if (this.sharingWindow == sender)
            this.sharingWindow = null;
    }

    /// <summary>The app is closing: the Sharing window goes with it, and every share ends - nothing is restored next time.</summary>
    void OnMainWindowDestroying(object? sender, EventArgs e)
    {
        if (sender is Window window)
            window.Destroying -= this.OnMainWindowDestroying;

        if (this.sharingWindow is { } sharingWindow)
            this.CloseWindow(sharingWindow);
        if (this.updateWindow is { } updateWindow)
            this.CloseWindow(updateWindow);

        // off the UI thread (nothing it awaits may need this thread back), and bounded - the server drains for at most a
        // few seconds
        Task.Run(this.StopSharingAsync).Wait(TimeSpan.FromSeconds(4));
        this.keepAwake.Dispose();
    }
}
