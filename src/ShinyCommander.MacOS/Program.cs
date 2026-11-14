using AppKit;
using Foundation;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace ShinyCommander;

[Register("Program")]
public class Program : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>⌘Q (and the app menu's Quit) while background jobs run asks first - see QuitGuard.</summary>
    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
    {
        var guard = this.Services?.GetService<QuitGuard>();
        if (guard == null || guard.CanQuit)
            return NSApplicationTerminateReply.Now;

        // TerminateLater would park the run loop in a modal mode the in-app dialog can't animate in - cancel, ask,
        // then quit again (CanQuit is true by then)
        NSApplication.SharedApplication.BeginInvokeOnMainThread(async () =>
        {
            if (await guard.ConfirmAsync())
                NSApplication.SharedApplication.Terminate(this);
        });
        return NSApplicationTerminateReply.Cancel;
    }

    /// <summary>
    /// The backend tears the MAUI window down when its close button is clicked and doesn't handle the Dock icon's
    /// reopen, so the app would sit there with no window and no way back - closing the last window quits instead
    /// (the close button already asks first while jobs run, see QuitGuard).
    /// </summary>
    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    public static void Main(string[] args)
    {
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new Program();
        NSApplication.Main(args);
    }
}
