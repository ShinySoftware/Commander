using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent.Gtk;
#endif

namespace ShinyCommander;

public class Program : GtkMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

#if DEBUG
    // the GTK agent starts once the MAUI Application exists
    protected override void OnStarted()
    {
        base.OnStarted();
        (this.Application as Application)?.StartDevFlowAgent();
    }
#endif

    public static void Main(string[] args) => new Program().Run(args);
}
