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
            .UseMauiApp<App>()
            .UseShinyCommander();

#if DEBUG
        builder.AddMauiDevFlowAgent();
#endif
        return builder.Build();
    }
}
