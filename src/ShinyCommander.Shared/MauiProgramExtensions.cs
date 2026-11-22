using Microsoft.Extensions.Logging;
using Shiny.Mediator;
using ShinyCommander.Services.Editors;
using ShinyCommander.Services.Previewers;

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

        // preview plugins: audio / video (GTK has its own backend) and the Office viewers (SkiaSharp; none on GTK)
#if LINUX_GTK
        builder.UseShinyMediaElementGtk();
#else
        builder.UseShinyMediaElement();
        // files open (and come to the front) through NSWorkspace, not the `open` tool
#if MACOS_APPKIT
        ShinyCommander.Core.Platform.ShellLauncher.Native = new MacWorkspaceLauncher();
#endif
        Shiny.Maui.Controls.Office.MauiAppBuilderExtensions.UseShinyOffice(builder);
#endif

        builder.Services.AddShinyStores();
        // UI text: Resources/Strings/Strings*.resx -> StringsLocalized (Shiny.Extensions.Localization.Generator)
        builder.Services.AddLocalization();
        builder.Services.AddStronglyTypedLocalizations();
        builder.Services.AddGeneratedServices();   // [Singleton] AppSettings etc. (Shiny.Extensions.DependencyInjection)

        // servers and storage accounts: the providers, their sessions and the saved connections (secrets in the keychain)
        builder.Services.TryAddSingleton<ISecretStore, OsSecretStore>();
        builder.Services.AddSingleton(sp => new RemoteSessions(sp.GetRequiredService<ISecretStore>(), ShinyCommander.Remote.RemoteProviders.All()));
        builder.Services.AddSingleton<RemoteConnections>();
        builder.Services.AddSingleton(sp => new VirtualFileSystem(sp.GetRequiredService<RemoteSessions>()));
        builder.Services.AddMdns();
        builder.Services.AddSingleton<NetworkBrowser>();
        builder.Services.TryAddSingleton<ITrashService, TrashService>();
        builder.Services.AddSingleton(sp => new FileOperationService(sp.GetRequiredService<VirtualFileSystem>(), sp.GetRequiredService<ITrashService>()));
        builder.Services.TryAddSingleton<IAppDialogs, ShinyAppDialogs>();
        // F4: an editor per file type, asked in order - the text editor takes whatever none of them claims
        builder.Services.AddSingleton<FileEditors>();
        builder.Services
            .AddFileEditor<ImageMetadataFileEditor>()
            .AddFileEditor<SqliteFileEditor>()
            .AddFileEditor<TextFileEditor>();
        // the preview panel: a previewer per file type, asked in order - text / hex for whatever none of them takes
        builder.Services.AddSingleton<FilePreviewers>();
        builder.Services.AddSingleton<PreviewPaneViewModel>();
        builder.Services
            .AddFilePreviewer<ImageFilePreviewer>()
            .AddFilePreviewer<MarkdownFilePreviewer>()
            .AddFilePreviewer<HtmlFilePreviewer>()
            .AddFilePreviewer<PdfFilePreviewer>()
            .AddFilePreviewer<SqliteFilePreviewer>()
            .AddFilePreviewer<MediaFilePreviewer>();
#if !LINUX_GTK
        builder.Services
            .AddFilePreviewer<WordFilePreviewer>()
            .AddFilePreviewer<ExcelFilePreviewer>()
            .AddFilePreviewer<PowerPointFilePreviewer>();
#endif

        // the Sharing window: one view model for the app's life (its QR toggles and form survive closing the window)
        builder.Services.AddSingleton<SharingViewModel>();
        builder.Services.AddTransient<SharingPage>();

        builder.Services.AddSingleton<FileColumns>();
        builder.Services.AddBuiltInFileColumns();
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
