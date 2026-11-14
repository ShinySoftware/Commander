using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ShinyCommander.Core.Platform;

/// <summary>
/// Connects SMB shares the way the OS does, so they become ordinary folders (and drive bar buttons): macOS mounts
/// under /Volumes, Linux through gvfs (/run/user/UID/gvfs), Windows maps the UNC path. Passwords never go on a command
/// line where other processes could read them - stdin on macOS/Linux, the WNet API on Windows.
/// </summary>
public static class NetworkShares
{
    /// <summary>
    /// Mounts <c>smb://host/share</c> and returns the local folder it appears at. Without a password the OS asks for one
    /// itself where it can (macOS's sign-in sheet, which also offers the Keychain). macOS also offers a share picker when
    /// <paramref name="share"/> is empty; the others need the share name.
    /// </summary>
    public static Task<string> MountAsync(string host, string? share, string? user, string? password, CancellationToken ct = default)
    {
        host = host.Trim().TrimStart('/', '\\');
        share = share?.Trim().Trim('/', '\\');
        if (String.IsNullOrEmpty(share) && !OperatingSystem.IsMacOS())
            throw new ArgumentException("Enter the share's name");

        if (OperatingSystem.IsMacOS())
            return MountMacAsync(host, share, user, password, ct);
        if (OperatingSystem.IsWindows())
            return Task.Run(() => MountWindows(host, share!, user, password), ct);
        if (OperatingSystem.IsLinux())
            return MountLinuxAsync(host, share!, user, password, ct);

        throw new PlatformNotSupportedException();
    }

    public static string SmbUri(string host, string? share, string? user)
    {
        var who = String.IsNullOrEmpty(user) ? String.Empty : Uri.EscapeDataString(user) + "@";
        return $"smb://{who}{host}/{(String.IsNullOrEmpty(share) ? String.Empty : Uri.EscapeDataString(share))}";
    }

    static async Task<string> MountMacAsync(string host, string? share, string? user, string? password, CancellationToken ct)
    {
        // AppleScript's mount volume is what Finder's Connect to Server uses: Keychain, the sign-in sheet, the share list
        var script = new StringBuilder("POSIX path of (mount volume \"").Append(Quote(SmbUri(host, share, user))).Append('"');
        if (!String.IsNullOrEmpty(user) && !String.IsNullOrEmpty(password))
            script.Append(" as user name \"").Append(Quote(user)).Append("\" with password \"").Append(Quote(password)).Append('"');
        script.Append(')');

        var (code, output, error) = await RunAsync("osascript", ["-"], script.ToString(), ct).ConfigureAwait(false);
        if (code != 0)
            throw new IOException(String.IsNullOrWhiteSpace(error) ? $"Couldn't connect to {host}" : CleanAppleScriptError(error));

        return output.Trim().TrimEnd('/');
    }

    static string Quote(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // "0:57: execution error: The operation couldn't be completed. (-128)" -> the sentence
    static string CleanAppleScriptError(string error)
    {
        var text = error.Trim();
        var marker = text.IndexOf("error: ", StringComparison.Ordinal);
        if (marker >= 0)
            text = text[(marker + "error: ".Length)..];
        return text.Contains("(-128)", StringComparison.Ordinal) ? "Cancelled" : text;
    }

    static async Task<string> MountLinuxAsync(string host, string share, string? user, string? password, CancellationToken ct)
    {
        var uri = SmbUri(host, share, null);
        // gio asks on stdin: "User [me]:", "Domain [WORKGROUP]:", "Password:" - anonymous takes -a and asks nothing
        var anonymous = String.IsNullOrEmpty(user);
        var answers = anonymous ? null : $"{user}\n\n{password}\n";
        var (code, _, error) = await RunAsync("gio", anonymous ? ["mount", "-a", uri] : ["mount", uri], answers, ct).ConfigureAwait(false);
        if (code != 0 && !error.Contains("already mounted", StringComparison.OrdinalIgnoreCase))
            throw new IOException(String.IsNullOrWhiteSpace(error) ? $"Couldn't connect to {host}" : error.Trim());

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{GetUid()}";
        var gvfs = Path.Combine(runtime, "gvfs");
        var mounted = Directory.Exists(gvfs)
            ? Directory.GetDirectories(gvfs).FirstOrDefault(x =>
            {
                var name = Path.GetFileName(x);
                return name.StartsWith("smb-share:", StringComparison.Ordinal)
                    && name.Contains($"server={host}", StringComparison.OrdinalIgnoreCase)
                    && name.Contains($"share={share}", StringComparison.OrdinalIgnoreCase);
            })
            : null;
        return mounted ?? throw new IOException($"{host}/{share} was mounted but its folder wasn't found under {gvfs}");
    }

    [DllImport("libc", EntryPoint = "getuid")]
    static extern uint GetUid();

    static string MountWindows(string host, string share, string? user, string? password)
    {
        var unc = $@"\\{host}\{share}";
        // already reachable with the signed-in user's credentials (or ones Windows remembers) - nothing to do
        if (String.IsNullOrEmpty(user) && Directory.Exists(unc))
            return unc;

        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = unc };
        var result = WNetAddConnection2W(ref resource, String.IsNullOrEmpty(password) ? null : password, String.IsNullOrEmpty(user) ? null : user, 0);
        // 1219: already connected with other credentials - use what's there
        if (result != 0 && result != 1219)
            throw new IOException($"Couldn't connect to {unc} (error {result})");
        return unc;
    }

    const uint ResourceTypeDisk = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetAddConnection2W(ref NetResource netResource, string? password, string? userName, uint flags);

    static async Task<(int Code, string Output, string Error)> RunAsync(string file, string[] args, string? input, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new IOException($"{file} couldn't be started");
        if (input != null)
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            throw;
        }
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
