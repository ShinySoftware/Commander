using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
#endif

namespace ShinyCommander;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiAppMacOS<App>()
            .AddMacOSEssentials()
            .UseShinyCommander()
            .ConfigureMauiHandlers(h => h.AddHandler<Label, VerticallyAlignedLabelHandler>());
        VerticallyAlignedLabelHandler.Register();

#if DEBUG
        // maui-labs preview.15 only wires Essentials up in Build(); DevFlow's file logging reads
        // FileSystem.CacheDirectory before that and throws, leaving the app with no window
        builder.AddMauiDevFlowAgent(o =>
        {
            o.EnableFileLogging = false;
            ShinyCommander.Testing.UiTestHooks.Register(o);
        });
#endif
        return builder.Build();
    }
}
