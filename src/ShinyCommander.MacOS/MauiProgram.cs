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
            .UseShinyCommander();

#if DEBUG
        builder.AddMauiDevFlowAgent();
#endif
        return builder.Build();
    }
}
