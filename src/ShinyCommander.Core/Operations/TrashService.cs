namespace ShinyCommander.Core.Operations;

public interface ITrashService
{
    bool TryMoveToTrash(string path);
}

/// <summary>
/// Moves items to the OS recycle bin / trash. Windows uses the shell's recycle bin, macOS the user's
/// ~/.Trash and Linux the freedesktop.org trash specification.
/// </summary>
public sealed class TrashService : ITrashService
{
    public bool TryMoveToTrash(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsRecycle(path);

            if (OperatingSystem.IsMacOS())
                return MoveInto(Path.Combine(Home, ".Trash"), path, null);

            if (OperatingSystem.IsLinux())
                return FreeDesktopTrash(path);
        }
        catch
        {
        }
        return false;
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static bool WindowsRecycle(string path)
    {
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        return true;
    }

    static bool FreeDesktopTrash(string path)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (String.IsNullOrEmpty(dataHome))
            dataHome = Path.Combine(Home, ".local", "share");

        var trash = Path.Combine(dataHome, "Trash");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(info);

        return MoveInto(Path.Combine(trash, "files"), path, name =>
        {
            var content = $"[Trash Info]\nPath={Uri.EscapeDataString(path).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";
            File.WriteAllText(Path.Combine(info, name + ".trashinfo"), content);
        });
    }

    static bool MoveInto(string trashDir, string path, Action<string>? onMoved)
    {
        Directory.CreateDirectory(trashDir);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(trashDir, name);
        var i = 1;
        while (Path.Exists(target))
            target = Path.Combine(trashDir, $"{Path.GetFileNameWithoutExtension(name)} {i++}{Path.GetExtension(name)}");

        // Directory.Move/File.Move fail across volumes - caller falls back to a permanent delete
        if (Directory.Exists(path))
            Directory.Move(path, target);
        else
            File.Move(path, target);

        onMoved?.Invoke(Path.GetFileName(target));
        return true;
    }
}
