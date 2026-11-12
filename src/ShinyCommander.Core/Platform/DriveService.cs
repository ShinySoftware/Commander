using System.Runtime.InteropServices;
using System.Text.Json;
using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Platform;

/// <summary>
/// The drive button bar. Places come first - Home (unix only, since there are no letters), the special folders
/// (Desktop, Documents, Downloads) and any cloud sync folders (iCloud, OneDrive, Dropbox, Google Drive...) - then the
/// volumes: drive letters on Windows, "/" + /Volumes on macOS, "/" + the usual mount points on Linux, and network
/// shares last.
/// </summary>
public static class DriveService
{
    static readonly StringComparison PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static IReadOnlyList<DriveEntry> GetDrives()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new List<DriveEntry>();

        if (!OperatingSystem.IsWindows())
            places.Add(FromPath("~", home, "Home", DriveKind.Home));

        foreach (var (name, path) in SpecialFolders(home))
            AddPlace(places, name, path, name, DriveKind.Special);

        foreach (var (name, path, label) in CloudFolders(home))
            AddPlace(places, name, path, label, DriveKind.Cloud);

        var volumes = new List<DriveEntry>();
        foreach (var d in SafeDrives())
        {
            try
            {
                var entry = FromDriveInfo(d);
                if (entry != null)
                    volumes.Add(entry);
            }
            catch
            {
                // not ready / permission denied
            }
        }
        if (OperatingSystem.IsLinux())
            volumes.AddRange(GvfsMounts());

        // network shares go to the end of the bar
        return [.. places, .. volumes.Where(x => x.Kind != DriveKind.Network), .. volumes.Where(x => x.Kind == DriveKind.Network)];
    }

    static void AddPlace(List<DriveEntry> list, string name, string path, string label, DriveKind kind)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        if (!Directory.Exists(path) || list.Any(x => String.Equals(x.RootPath, path, PathComparison)))
            return;

        list.Add(FromPath(name, path, label, kind));
    }

    static DriveEntry? FromDriveInfo(DriveInfo d)
    {
        var root = d.RootDirectory.FullName;
        var kind = d.DriveType switch
        {
            DriveType.Network => DriveKind.Network,
            DriveType.Removable or DriveType.CDRom => DriveKind.Removable,
            _ => DriveKind.Volume
        };

        if (OperatingSystem.IsWindows())
        {
            var name = root.TrimEnd('\\');

            // a mapped drive that has dropped its connection still gets a button - opening it reconnects
            if (!d.IsReady)
                return kind == DriveKind.Network ? new DriveEntry(name, root, name, 0, 0, d.DriveType, kind) : null;

            var label = SafeLabel(d);
            if (label.Equals("Google Drive", StringComparison.OrdinalIgnoreCase))
                kind = DriveKind.Cloud;

            return new DriveEntry(name, root, label, d.TotalSize, d.AvailableFreeSpace, d.DriveType, kind);
        }

        if (!d.IsReady || !IsInterestingUnixMount(root, d))
            return null;

        return new DriveEntry(
            root == "/" ? "/" : Path.GetFileName(root.TrimEnd('/')),
            root,
            SafeLabel(d),
            d.TotalSize,
            d.AvailableFreeSpace,
            d.DriveType,
            kind
        );
    }

    static IEnumerable<DriveInfo> SafeDrives()
    {
        try { return DriveInfo.GetDrives(); } catch { return []; }
    }

    static string SafeLabel(DriveInfo d)
    {
        try { return String.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : d.VolumeLabel; } catch { return d.Name; }
    }

    static bool IsInterestingUnixMount(string root, DriveInfo d)
    {
        if (root == "/")
            return true;

        // smb/nfs/afp/sshfs shares are worth a button wherever they are mounted - except automounter stubs
        if (d.DriveType == DriveType.Network)
            return !IsAutofs(d) && !root.StartsWith("/System/", StringComparison.Ordinal);

        if (d.DriveType is DriveType.Ram or DriveType.Unknown && !root.StartsWith("/Volumes/"))
            return false;

        if (OperatingSystem.IsMacOS())
            return root.StartsWith("/Volumes/", StringComparison.Ordinal) && !root.Contains("/.timemachine", StringComparison.Ordinal);

        return root.StartsWith("/media/", StringComparison.Ordinal)
            || root.StartsWith("/mnt/", StringComparison.Ordinal)
            || root.StartsWith("/run/media/", StringComparison.Ordinal)
            || root == "/home";
    }

    static bool IsAutofs(DriveInfo d)
    {
        try { return d.DriveFormat.Equals("autofs", StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }

    #region special folders

    static IEnumerable<(string Name, string Path)> SpecialFolders(string home)
    {
        if (OperatingSystem.IsWindows())
        {
            // known folders, so a Documents/Downloads that was moved (or redirected into OneDrive) is followed
            yield return ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            yield return ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            yield return ("Downloads", WindowsKnownFolder(DownloadsFolderId) ?? Path.Combine(home, "Downloads"));
        }
        else if (OperatingSystem.IsLinux())
        {
            var xdg = ReadXdgUserDirs(home);
            yield return ("Desktop", xdg.GetValueOrDefault("XDG_DESKTOP_DIR") ?? Path.Combine(home, "Desktop"));
            yield return ("Documents", xdg.GetValueOrDefault("XDG_DOCUMENTS_DIR") ?? Path.Combine(home, "Documents"));
            yield return ("Downloads", xdg.GetValueOrDefault("XDG_DOWNLOAD_DIR") ?? Path.Combine(home, "Downloads"));
        }
        else
        {
            yield return ("Desktop", Path.Combine(home, "Desktop"));
            yield return ("Documents", Path.Combine(home, "Documents"));
            yield return ("Downloads", Path.Combine(home, "Downloads"));
        }
    }

    /// <summary>~/.config/user-dirs.dirs - lines like XDG_DOWNLOAD_DIR="$HOME/Downloads".</summary>
    static Dictionary<string, string> ReadXdgUserDirs(string home)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
            var file = Path.Combine(config, "user-dirs.dirs");
            if (!File.Exists(file))
                return result;

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#'))
                    continue;

                var eq = trimmed.IndexOf('=');
                if (eq <= 0)
                    continue;

                var value = trimmed[(eq + 1)..].Trim('"').Replace("$HOME", home);
                // a dir set to $HOME itself means "disabled"
                if (value.Length > 0 && !String.Equals(value.TrimEnd('/'), home, StringComparison.Ordinal))
                    result[trimmed[..eq]] = value;
            }
        }
        catch
        {
            // unreadable config - fall back to the defaults
        }
        return result;
    }

    static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    static string? WindowsKnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out var ptr) != 0)
                return null;

            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    #endregion

    #region cloud folders

    static IEnumerable<(string Name, string Path, string Label)> CloudFolders(string home)
    {
        var found = new List<(string Name, string Path, string Label)>();

        if (OperatingSystem.IsMacOS())
        {
            found.Add(("iCloud Drive", Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs"), "iCloud Drive"));

            // File Provider apps (OneDrive, Google Drive, Dropbox, Box, Synology...) all mount here as Provider-Account
            foreach (var dir in SafeDirectories(Path.Combine(home, "Library", "CloudStorage")))
            {
                var folder = Path.GetFileName(dir);
                var dash = folder.IndexOf('-');
                var provider = dash > 0 ? folder[..dash] : folder;
                var account = dash > 0 ? folder[(dash + 1)..] : null;
                found.Add((provider, dir, account == null ? provider : $"{provider} ({account})"));
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            // the OneDrive client publishes each signed-in account's folder ("OneDrive", "OneDrive - Contoso")
            foreach (var variable in new[] { "OneDriveConsumer", "OneDriveCommercial", "OneDrive" })
            {
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path)
                    found.Add((Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, Path.GetFileName(path)));
            }
            found.Add(("iCloud Drive", Path.Combine(home, "iCloudDrive"), "iCloud Drive"));
        }

        foreach (var path in DropboxFolders(home))
            found.Add((Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, "Dropbox"));

        // the default folders the desktop sync clients create when there is no better source
        foreach (var name in new[] { "Dropbox", "OneDrive", "Google Drive", "Box", "Nextcloud", "ownCloud", "pCloudDrive", "MEGA" })
            found.Add((name, Path.Combine(home, name), name));

        // the same provider twice (two OneDrive accounts) - fall back to the label so the buttons can be told apart
        var dupes = found
            .Where(x => Directory.Exists(x.Path))
            .DistinctBy(x => x.Path.ToUpperInvariant())
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return found.Select(x => dupes.Contains(x.Name) ? (x.Label, x.Path, x.Label) : x);
    }

    /// <summary>Dropbox writes where each account (personal / business) syncs to into info.json.</summary>
    static IEnumerable<string> DropboxFolders(string home)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json")
            }
            : [Path.Combine(home, ".dropbox", "info.json")];

        var result = new List<string>();
        foreach (var file in candidates)
        {
            try
            {
                if (!File.Exists(file))
                    continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var account in doc.RootElement.EnumerateObject())
                {
                    if (account.Value.ValueKind == JsonValueKind.Object
                        && account.Value.TryGetProperty("path", out var path)
                        && path.GetString() is { Length: > 0 } p)
                        result.Add(p);
                }
            }
            catch
            {
                // malformed / locked
            }
        }
        return result;
    }

    #endregion

    /// <summary>
    /// GNOME/GTK file managers mount smb, sftp, ftp, dav and Google Drive through gvfs as folders under
    /// /run/user/UID/gvfs, named like "smb-share:server=nas,share=media". They are not real mounts so DriveInfo misses them.
    /// </summary>
    static IEnumerable<DriveEntry> GvfsMounts()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (String.IsNullOrEmpty(runtime))
            yield break;

        foreach (var dir in SafeDirectories(Path.Combine(runtime, "gvfs")))
        {
            var folder = Path.GetFileName(dir);
            var colon = folder.IndexOf(':');
            if (colon <= 0)
                continue;

            var scheme = folder[..colon];
            var parts = folder[(colon + 1)..]
                .Split(',')
                .Select(x => x.Split('=', 2))
                .Where(x => x.Length == 2)
                .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));

            var host = parts.GetValueOrDefault("host") ?? parts.GetValueOrDefault("server") ?? folder;
            if (scheme == "google-drive")
            {
                var user = parts.GetValueOrDefault("user");
                yield return new DriveEntry("Google Drive", dir, user == null ? "Google Drive" : $"Google Drive ({user}@{host})", 0, 0, DriveType.Network, DriveKind.Cloud);
                continue;
            }

            var name = parts.GetValueOrDefault("share") is { } share ? $"{share} on {host}" : host;
            yield return new DriveEntry(name, dir, $"{scheme}://{host}", 0, 0, DriveType.Network, DriveKind.Network);
        }
    }

    static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; } catch { return []; }
    }

    public static DriveEntry FromPath(string name, string path, string label, DriveKind kind = DriveKind.Volume)
    {
        try
        {
            var d = new DriveInfo(path);
            return new DriveEntry(name, path, label, d.TotalSize, d.AvailableFreeSpace, d.DriveType, kind);
        }
        catch
        {
            return new DriveEntry(name, path, label, 0, 0, DriveType.Unknown, kind);
        }
    }

    /// <summary>Finds the drive that holds the given path (longest matching root). Home and the special folders never count.</summary>
    public static DriveEntry? FindForPath(IReadOnlyList<DriveEntry> drives, string path) => drives
        .Where(x => !x.IsPlace && path.StartsWith(x.RootPath, PathComparison))
        .OrderByDescending(x => x.RootPath.Length)
        .FirstOrDefault();
}
