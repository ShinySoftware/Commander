namespace ShinyCommander.Services;

/// <summary>
/// Closing the window or quitting while background jobs run would stop them part-way, so it asks first. Each head
/// routes its native close/quit through <see cref="CanQuit"/> / <see cref="ConfirmAsync"/>: the window's close button
/// via <see cref="Attach"/>, and on macOS also ⌘Q (Program.ApplicationShouldTerminate).
/// </summary>
[Singleton]
public class QuitGuard
{
    readonly JobQueue jobs;
    readonly IAppDialogs dialogs;
    bool confirmed;
    bool asking;

    public QuitGuard(JobQueue jobs, IAppDialogs dialogs)
    {
        this.jobs = jobs;
        this.dialogs = dialogs;
        // a job started after "quit anyway" was answered (and the quit didn't happen) deserves its own question
        jobs.Started += (_, _) => this.confirmed = false;
    }

    /// <summary>True when the app may close right now without asking.</summary>
    public bool CanQuit => this.confirmed || this.jobs.RunningCount == 0;

    /// <summary>
    /// Asks whether to stop the running jobs and quit. On yes the jobs are stopped (and given a moment to clean up
    /// partial files) before it returns true; the caller then closes/quits.
    /// </summary>
    public async Task<bool> ConfirmAsync()
    {
        if (this.CanQuit)
            return true;

        // a second close click while the question is up
        if (this.asking)
            return false;

        this.asking = true;
        try
        {
            var running = this.jobs.Jobs.Where(x => x.IsRunning).ToList();
            var list = String.Join("\n", running.Take(5).Select(x => "•  " + x.Title));
            if (running.Count > 5)
                list += "\n" + L.Job_AndMoreFormat(running.Count - 5);

            var ok = await this.dialogs.Confirm(
                L.Quit_Title,
                L.Quit_MessageFormat(running.Count, list),
                L.Quit_StopAndQuit,
                L.Quit_KeepRunning);
            if (!ok)
                return false;

            await this.jobs.StopAllAndWait(TimeSpan.FromSeconds(3));
            this.confirmed = true;
            return true;
        }
        finally
        {
            this.asking = false;
        }
    }

    /// <summary>Puts the guard in front of the native window's close button.</summary>
    public void Attach(Window window)
    {
        window.HandlerChanged += (_, _) => this.AttachNative(window);
        if (window.Handler != null)
            this.AttachNative(window);
    }

    object? attachedTo;

    void AttachNative(Window window)
    {
        var platformView = window.Handler?.PlatformView;
        if (platformView == null || ReferenceEquals(platformView, this.attachedTo))
            return;
        this.attachedTo = platformView;

#if MACOS_APPKIT
        if (platformView is AppKit.NSWindow nsWindow && nsWindow.Delegate is AppKit.NSWindowDelegate inner)
            nsWindow.Delegate = new GuardedWindowDelegate(inner, this, nsWindow);
#elif WINDOWS_WINUI
        if (platformView is Microsoft.UI.Xaml.Window winui)
        {
            winui.AppWindow.Closing += async (_, e) =>
            {
                if (this.CanQuit)
                    return;

                e.Cancel = true;
                if (await this.ConfirmAsync())
                    winui.Close();
            };
        }
#elif LINUX_GTK
        if (platformView is Gtk.Window gtk)
            this.AttachGtk(window, gtk);
#endif
    }

#if MACOS_APPKIT
    /// <summary>
    /// The backend's own window delegate always lets the window close. This one asks first and hands everything else
    /// (WillClose tears the MAUI window down) to the original.
    /// </summary>
    sealed class GuardedWindowDelegate(AppKit.NSWindowDelegate inner, QuitGuard guard, AppKit.NSWindow window) : AppKit.NSWindowDelegate
    {
        public override bool WindowShouldClose(Foundation.NSObject sender)
        {
            if (guard.CanQuit)
                return inner.WindowShouldClose(sender);

            _ = this.AskThenClose();
            return false;
        }

        async Task AskThenClose()
        {
            // Close() (unlike PerformClose) doesn't ask WindowShouldClose again
            if (await guard.ConfirmAsync())
                window.Close();
        }

        public override void WillClose(Foundation.NSNotification notification) => inner.WillClose(notification);
    }
#endif

#if LINUX_GTK
    /// <summary>
    /// GTK runs close-request handlers in the order they were connected, and the first to return true stops the
    /// close. The backend's handler (connected when the window was created) tears the MAUI window down and returns
    /// false - so take it off, connect ours, then put it back behind ours.
    /// </summary>
    void AttachGtk(Window window, Gtk.Window gtk)
    {
        GObject.ReturningSignalHandler<Gtk.Window, bool>? backend = null;
        try
        {
            var method = window.Handler!.GetType().GetMethod("OnCloseRequest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method != null)
                backend = (GObject.ReturningSignalHandler<Gtk.Window, bool>?)Delegate.CreateDelegate(typeof(GObject.ReturningSignalHandler<Gtk.Window, bool>), window.Handler, method, false);
        }
        catch
        {
        }

        if (backend != null)
            gtk.OnCloseRequest -= backend;

        gtk.OnCloseRequest += (_, _) =>
        {
            if (this.CanQuit)
                return false;

            _ = this.AskThenCloseGtk(gtk);
            return true;
        };

        if (backend != null)
            gtk.OnCloseRequest += backend;
    }

    async Task AskThenCloseGtk(Gtk.Window gtk)
    {
        // CanQuit is now true, so the close request goes through
        if (await this.ConfirmAsync())
            gtk.Close();
    }
#endif
}
