namespace ShinyCommander.Services;

/// <summary>
/// Files another app put on the clipboard - copied in Finder or Explorer - for Primary+V to copy into a panel.
/// Optional - a head registers one, and without it Primary+V only ever pastes text into the command line.
/// </summary>
public interface IFileClipboard
{
    /// <summary>Whether the clipboard holds files. Asked as the key is pressed, so it has to be quick. Called on the UI thread.</summary>
    bool HasFiles { get; }

    /// <summary>Their full paths - empty when there are none. Called on the UI thread.</summary>
    Task<IReadOnlyList<string>> GetFilesAsync();
}
