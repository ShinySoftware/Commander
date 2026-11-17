namespace ShinyCommander.Services;

/// <summary>
/// Picks the app to open a file with when its type has none (Enter / double-click). Windows never gets here - the shell
/// asks there.
/// </summary>
public static class AppChooser
{
    public static async Task<string?> ChooseAsync(IAppDialogs dialogs, string fileName)
    {
#if MACOS_APPKIT
        await Task.CompletedTask;
        var panel = AppKit.NSOpenPanel.OpenPanel;
        panel.Title = L.OpenWith_TitleFormat(fileName);
        panel.Prompt = L.OpenWith_Open;
        panel.CanChooseFiles = true;
        panel.CanChooseDirectories = false;
        panel.AllowsMultipleSelection = false;
        panel.AllowedContentTypes = [UniformTypeIdentifiers.UTTypes.Application];
        panel.DirectoryUrl = Foundation.NSUrl.FromFilename("/Applications");
        return panel.RunModal() == 1 ? panel.Url?.Path : null;
#else
        var app = await dialogs.Prompt(L.OpenWith_TitleFormat(fileName), L.OpenWith_Message, null, L.OpenWith_Open);
        return String.IsNullOrWhiteSpace(app) ? null : app.Trim();
#endif
    }
}
