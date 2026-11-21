using Shiny;
using ShinyCommander.Services;
using Windows.ApplicationModel.DataTransfer;
// MAUI's own Clipboard (text only) is imported everywhere too
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace ShinyCommander;

/// <summary>The clipboard's storage items - what Explorer's Copy (Ctrl+C) puts there.</summary>
[Singleton]
sealed class WindowsFileClipboard : IFileClipboard
{
    public bool HasFiles => WinClipboard.GetContent().Contains(StandardDataFormats.StorageItems);

    public async Task<IReadOnlyList<string>> GetFilesAsync()
    {
        var content = WinClipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems))
            return [];

        var items = await content.GetStorageItemsAsync();
        return items
            .Select(x => x.Path)
            .Where(x => !String.IsNullOrEmpty(x))
            .ToList();
    }
}
