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
        return window;
    }
}
