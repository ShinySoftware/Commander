using System.Diagnostics;
using System.Text;

namespace ShinyCommander.Core.Platform;

public sealed record CommandResult(int ExitCode, string Output, TimeSpan Duration);

/// <summary>Opens files with their default app, opens terminals and runs command-line commands.</summary>
public static class ShellLauncher
{
    /// <summary>When set, files are handed to this instead of their app - UI tests record them rather than open Excel.</summary>
    public static Action<string>? Intercept { get; set; }

    /// <summary>
    /// The platform's own launcher, when the head has one (NSWorkspace on macOS). Since macOS 14 an app comes to the
    /// front only when the active app hands it over - the `open` tool is a background process, so what it opened stayed
    /// behind the window. Asked first; the processes below are the fallback.
    /// </summary>
    public static INativeLauncher? Native { get; set; }

    public static void OpenWithDefaultApp(string path)
    {
        if (Intercept is { } intercept)
        {
            intercept(path);
            return;
        }
        if (Native is { } native)
        {
            _ = native.OpenAsync(path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Start("open", path);
        }
        else
        {
            Start("xdg-open", path);
        }
    }

    /// <summary>
    /// Opens the file with the app its type is registered to; false when there is none, for the caller to offer a
    /// choice. Windows asks "How do you want to open this file?" itself, so it's always true there.
    /// </summary>
    public static async Task<bool> TryOpenWithDefaultAppAsync(string path)
    {
        if (Intercept is { } intercept)
        {
            intercept(path);
            return true;
        }
        if (Native is { } native)
            return await native.OpenAsync(path).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == NoAssociation)
            {
                Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path);
            }
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            // open hands the file to Launch Services and exits at once - non-zero when no app claims the type
            using var process = Process.Start(new ProcessStartInfo("open") { ArgumentList = { path }, UseShellExecute = false, RedirectStandardError = true });
            if (process == null)
                return false;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0;
        }

        // xdg-open can run the app in the foreground - don't wait on it
        Start("xdg-open", path);
        return true;
    }

    const int NoAssociation = 1155; // ERROR_NO_ASSOCIATION

    public static void OpenWith(string application, string path)
    {
        if (Intercept is { } intercept)
        {
            intercept(path);
            return;
        }
        if (Native is { } native && application.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            _ = native.OpenWithAsync(application, path);
            return;
        }
        if (OperatingSystem.IsMacOS() && application.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            Start("open", "-a", application, path);
        else
            Process.Start(new ProcessStartInfo(application) { ArgumentList = { path }, UseShellExecute = OperatingSystem.IsWindows() });
    }

    public static void RevealInFileManager(string path)
    {
        if (OperatingSystem.IsWindows())
            Start("explorer.exe", $"/select,\"{path}\"");
        else if (OperatingSystem.IsMacOS())
            Start("open", "-R", path);
        else
            Start("xdg-open", Path.GetDirectoryName(path) ?? path);
    }

    public static void OpenTerminal(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            // prefer Windows Terminal, fall back to cmd
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", directory }, UseShellExecute = true });
            }
            catch
            {
                Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = directory, UseShellExecute = true });
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            Start("open", "-a", "Terminal", directory);
        }
        else
        {
            foreach (var term in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "xterm" })
            {
                try
                {
                    Process.Start(new ProcessStartInfo(term) { WorkingDirectory = directory, UseShellExecute = false });
                    return;
                }
                catch
                {
                }
            }
        }
    }

    public static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".exe" or ".bat" or ".cmd" or ".com" or ".ps1";
        }
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0
                   && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs a command line through the platform shell in the given folder, capturing output.</summary>
    public static async Task<CommandResult> RunAsync(string commandLine, string workingDirectory, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", commandLine } }
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh ? sh : "/bin/sh") { ArgumentList = { "-c", commandLine } };

        psi.WorkingDirectory = workingDirectory;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Append(string? line)
        {
            if (line == null)
                return;
            lock (sb)
                sb.AppendLine(line);
            onOutput?.Invoke(line);
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        return new CommandResult(process.ExitCode, sb.ToString(), sw.Elapsed);
    }

    static void Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }
}

/// <summary>Opens files and links through the platform's own API - ShellLauncher.Native.</summary>
public interface INativeLauncher
{
    /// <summary>A file in its default app, or a link in the browser; false when no app takes it.</summary>
    Task<bool> OpenAsync(string pathOrUri);

    /// <summary>A file in the given application (an .app bundle).</summary>
    Task<bool> OpenWithAsync(string application, string path);
}
