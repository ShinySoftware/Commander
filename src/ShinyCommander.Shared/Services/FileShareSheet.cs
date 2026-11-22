using MauiShare = Microsoft.Maui.ApplicationModel.DataTransfer.Share;

namespace ShinyCommander.Services;

/// <summary>
/// The operating system's share sheet (AirDrop, Mail, Messages… on macOS; Nearby Share and apps on Windows; the portal
/// on Linux). Only plain files on this machine can go to it, so a folder is zipped first and a file inside an archive or
/// on a server is copied out first - both into <see cref="TempRoot"/>, which is emptied when the app next starts (there
/// is no telling when the receiving app has finished reading).
/// </summary>
public static class FileShareSheet
{
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "ShinyCommander-share");

    /// <summary>Whether this platform has a share sheet at all.</summary>
    public static bool IsSupported => MauiShare.Default != null;

    /// <summary>Files that can go to the share sheet as they are: on this machine, not folders.</summary>
    public static bool IsPlainFile(FileEntry entry) => !entry.IsDirectory && !entry.IsInArchive && !entry.IsRemote;

    /// <summary>A new, empty folder under <see cref="TempRoot"/> for one share's prepared files.</summary>
    public static string NewTempFolder()
    {
        var folder = Path.Combine(TempRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static Task ShareAsync(IReadOnlyList<string> files, string title)
        => MainThread.InvokeOnMainThreadAsync(() => MauiShare.Default.RequestAsync(new ShareMultipleFilesRequest
        {
            Title = title,
            Files = files.Select(x => new ShareFile(x)).ToList()
        }));

    /// <summary>Removes what earlier runs prepared - called once at startup.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
