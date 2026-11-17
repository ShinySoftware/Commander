#if MACOS_APPKIT
using AppKit;
using Foundation;
using ShinyCommander.Core.Platform;

namespace ShinyCommander.Services;

/// <summary>
/// Opens files through NSWorkspace from inside the app. The app is the active one when the user double-clicks, so the
/// app it opens is allowed to come to the front - `open` run as a child process isn't, and since macOS 14 what it opened
/// stayed behind the window, which looked like nothing happening.
/// </summary>
public sealed class MacWorkspaceLauncher : INativeLauncher
{
    public Task<bool> OpenAsync(string pathOrUri) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var url = ToUrl(pathOrUri);
        // no app claims the type - the caller offers a choice
        if (url == null || NSWorkspace.SharedWorkspace.UrlForApplication(url) == null)
            return false;

        return await Open(c => NSWorkspace.SharedWorkspace.OpenUrl(url, Configuration(), c));
    });

    public Task<bool> OpenWithAsync(string application, string path) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var url = ToUrl(path);
        if (url == null)
            return false;
        return await Open(c => NSWorkspace.SharedWorkspace.OpenUrls([url], NSUrl.FromFilename(application), Configuration(), c));
    });

    static NSWorkspaceOpenConfiguration Configuration()
    {
        var configuration = NSWorkspaceOpenConfiguration.Create();
        configuration.Activates = true;
        return configuration;
    }

    static Task<bool> Open(Action<Action<NSRunningApplication?, NSError?>> open)
    {
        var done = new TaskCompletionSource<bool>();
        open((_, error) => done.TrySetResult(error == null));
        return done.Task;
    }

    // a path on disk, or a link (the HTML preview hands its links to the browser)
    static NSUrl? ToUrl(string pathOrUri) => Uri.TryCreate(pathOrUri, UriKind.Absolute, out var uri) && !uri.IsFile
        ? NSUrl.FromString(pathOrUri)
        : NSUrl.FromFilename(pathOrUri);
}
#endif
