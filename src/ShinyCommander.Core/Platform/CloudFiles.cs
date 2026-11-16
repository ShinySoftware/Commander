using System.Runtime.InteropServices;

namespace ShinyCommander.Core.Platform;

/// <summary>
/// Files a sync client (iCloud, OneDrive, Synology Drive, Dropbox, Google Drive...) keeps only in the cloud until
/// they're opened. Windows' Cloud Files API marks them with recall attributes; macOS's File Provider leaves them
/// "dataless" (stat's SF_DATALESS flag). Linux has no common marker.
/// </summary>
public static class CloudFiles
{
    const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    const FileAttributes OnlineOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    const uint SF_DATALESS = 0x40000000;
    const int StatSize = 144;
    const int StatFlagsOffset = 116;

    public static bool IsOnlineOnly(string path, FileAttributes attributes)
    {
        if (OperatingSystem.IsWindows())
            return (attributes & OnlineOnly) != 0;

        if (!OperatingSystem.IsMacOS())
            return false;

        // lstat reads the placeholder's metadata - it never downloads the file
        var stat = new byte[StatSize];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? lstat_x64(path, stat) : lstat(path, stat);
        return result == 0 && (BitConverter.ToUInt32(stat, StatFlagsOffset) & SF_DATALESS) != 0;
    }

    // struct stat with 64-bit inodes: st_flags sits at byte 116 of 144. Intel Macs name that variant lstat$INODE64
    [DllImport("libc", EntryPoint = "lstat")]
    static extern int lstat(string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat$INODE64")]
    static extern int lstat_x64(string path, byte[] buffer);
}
