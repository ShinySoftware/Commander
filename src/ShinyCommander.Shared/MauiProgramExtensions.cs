using Microsoft.Extensions.Logging;
using Shiny.Mediator;

namespace ShinyCommander;

public static class MauiProgramExtensions
{
    /// <summary>Everything shared by the Windows, macOS (AppKit) and Linux (GTK4) heads.</summary>
    public static MauiAppBuilder UseShinyCommander(this MauiAppBuilder builder)
    {
#if DEBUG
        // SHINYCOMMANDER_UITEST=1 only: in-memory settings + scripted dialogs for tests/ShinyCommander.UITests
        Testing.UiTestMode.Configure(builder);
#endif
        builder
            .UseShinyControls()
            // keyboard shortcuts on AppKit + GTK4 come from the Desktop add-on's key sources
            .UseShinyDesktop()
            .UseShinyShell(x => x.AddGeneratedMaps())
            .AddShinyMediator(x => x
                .UseMaui()
                .PreventEventExceptions()
            );

        builder.Services.AddShinyStores();
        builder.Services.AddGeneratedServices();   // [Singleton] AppSettings etc. (Shiny.Extensions.DependencyInjection)

        builder.Services.AddSingleton<VirtualFileSystem>();
        builder.Services.AddSingleton<ITrashService, TrashService>();
        builder.Services.AddSingleton(sp => new FileOperationService(sp.GetRequiredService<VirtualFileSystem>(), sp.GetRequiredService<ITrashService>()));
        builder.Services.TryAddSingleton<IAppDialogs, ShinyAppDialogs>();
        // for UpdateService's hourly check
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IDispatcherProvider>().GetForCurrentThread()!);

#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddDebug();
#endif
        return builder;
    }

    static void TryAddSingleton<TService, TImpl>(this IServiceCollection services)
        where TService : class
        where TImpl : class, TService
    {
        if (!services.Any(x => x.ServiceType == typeof(TService)))
            services.AddSingleton<TService, TImpl>();
    }
}
