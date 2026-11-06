using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Hosting;
using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent.Gtk;
#endif

namespace ShinyCommander;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiAppLinuxGtk4<App>()
            .AddLinuxGtk4Essentials()
            .UseShinyCommander();

#if DEBUG
        builder.AddMauiDevFlowAgent(o => ShinyCommander.Testing.UiTestHooks.Register(o));
#endif
        return builder.Build();
    }
}
