using System.Diagnostics;
using AppKit;
using Foundation;
using Shiny;
using ShinyCommander.Services;

namespace ShinyCommander;

/// <summary>
/// Installs a release over the running app: downloads the release's zip (the signed, notarized bundle the
/// release workflow publishes), checks it, and swaps it in for this bundle once the app has quit.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is touched until the download has checked out: one .app, the same bundle id, a valid
/// signature, and the same signing team as the running app.
/// </para>
/// <para>
/// The swap is two renames done here, while the app is still running - a running app's bundle can be
/// renamed under it. A script left to do them after the app quits isn't the app any more as far as
/// privacy goes: for an app kept in Desktop or Documents, macOS stops it to ask for folder access, with
/// nothing on screen waiting for the answer. So the script only waits for this process to exit, deletes
/// the old bundle and opens the new one.
/// </para>
/// </remarks>
[Singleton]
sealed class MacAppUpdater : IAppUpdater
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string AssetName(string version) => $"ShinyCommander-{version}-macos-arm64.zip";

    public async Task InstallAsync(AppRelease release, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var bundle = NSBundle.MainBundle.BundlePath;
        // Run from a quarantined download, macOS runs a read-only copy somewhere random.
        if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal))
            throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorTranslocated);
        var folder = Path.GetDirectoryName(bundle)!;
        if (!CanWrite(folder))
            throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorCantWriteFormat(folder));

        var work = Directory.CreateTempSubdirectory("ShinyCommander-update-").FullName;
        try
        {
            var zip = Path.Combine(work, AssetName(release.Version));
            await this.DownloadAsync(release.Download, zip, progress, cancellationToken).ConfigureAwait(false);

            var unpacked = Path.Combine(work, "unpacked");
            if (await Run("/usr/bin/ditto", "-x", "-k", zip, unpacked) is not { ExitCode: 0 })
                throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorBadZip);

            var apps = Directory.GetDirectories(unpacked, "*.app");
            if (apps.Length != 1)
                throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorNoApp);
            var fresh = apps[0];
            Verify(fresh, bundle);
            await VerifySignatureAsync(fresh, bundle).ConfigureAwait(false);

            // Into the same folder first: the swap is then two renames on one volume, and can't half-happen.
            var staged = Path.Combine(folder, $".ShinyCommander-{release.Version}.app");
            var old = Path.Combine(folder, $".ShinyCommander-replaced-{Guid.NewGuid():N}.app");
            if (Directory.Exists(staged))
                Directory.Delete(staged, true);

            // Started before the swap, so a swapped app always gets relaunched; it only waits until then.
            var script = Path.Combine(work, "relaunch.sh");
            await File.WriteAllTextAsync(script, RelaunchScript, cancellationToken).ConfigureAwait(false);
            using var relaunch = Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { script, Environment.ProcessId.ToString(), old, bundle, work },
                UseShellExecute = false
            }) ?? throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorInstallerStart);

            try
            {
                if (await Run("/usr/bin/ditto", fresh, staged) is not { ExitCode: 0 })
                    throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorCopyFormat(folder));

                Directory.Move(bundle, old);
                try
                {
                    Directory.Move(staged, bundle);
                }
                catch
                {
                    Directory.Move(old, bundle);
                    throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorMoveFormat(folder));
                }
            }
            catch
            {
                // Still the old app: no relaunch when it quits, and nothing left beside it.
                try { relaunch.Kill(); } catch { }
                try { Directory.Delete(staged, true); } catch { }
                throw;
            }
        }
        catch
        {
            try { Directory.Delete(work, true); } catch { }
            throw;
        }

        // The script is waiting for this process to go.
        await MainThread.InvokeOnMainThreadAsync(() => NSApplication.SharedApplication.Terminate(null));
    }

    async Task DownloadAsync(Uri url, string path, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var response = await this.http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            if (total is > 0)
                progress.Report(Math.Min(1, (double)done / total.Value));
        }
        progress.Report(1);
    }

    /// <summary>The same app: a bundle id that matches the running one's.</summary>
    static void Verify(string fresh, string running)
    {
        static string? BundleId(string app)
            => NSDictionary.FromFile(Path.Combine(app, "Contents", "Info.plist"))?["CFBundleIdentifier"]?.ToString();

        if (BundleId(fresh) is not { } id || id != BundleId(running))
            throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorNotTheApp);
    }

    /// <summary>
    /// A valid signature on the whole bundle, by the team that signed this one. A running app with no team
    /// (a local build) has nothing to match, so only the signature itself is checked.
    /// </summary>
    static async Task VerifySignatureAsync(string fresh, string running)
    {
        if (await Run("/usr/bin/codesign", "--verify", "--deep", "--strict", fresh) is not { ExitCode: 0 })
            throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorSignature);

        if (await TeamAsync(running) is { } team && await TeamAsync(fresh) != team)
            throw new InvalidOperationException(ShinyCommander.Strings.L.Update_ErrorDeveloper);
    }

    static async Task<string?> TeamAsync(string app)
    {
        // codesign prints the details on stderr.
        var result = await Run("/usr/bin/codesign", "-dv", "--verbose=2", app);
        var line = result?.Error.Split('\n').FirstOrDefault(l => l.StartsWith("TeamIdentifier=", StringComparison.Ordinal));
        var team = line?["TeamIdentifier=".Length..].Trim();
        return team is { Length: > 0 } and not "not set" ? team : null;
    }

    static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".ShinyCommander-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task<(int ExitCode, string Error)?> Run(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process == null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await output.ConfigureAwait(false);
            return (process.ExitCode, await error.ConfigureAwait(false));
        }
        catch
        {
            return null;
        }
    }

    // $1 this process, $2 the old bundle (moved aside), $3 the new one (in its place), $4 the work folder.
    const string RelaunchScript = """
        #!/bin/sh
        while kill -0 "$1" 2>/dev/null; do sleep 0.2; done
        rm -rf "$2"
        open "$3"
        rm -rf "$4"
        """;
}
