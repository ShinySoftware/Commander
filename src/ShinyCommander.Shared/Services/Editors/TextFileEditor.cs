using ShinyCommander.Core.Platform;

namespace ShinyCommander.Services.Editors;

/// <summary>Everything no other editor claimed: the external editor from Settings, or the built-in text editor.</summary>
public sealed class TextFileEditor(INavigator navigator, AppSettings settings) : IFileEditor
{
    const long MaxBuiltInBytes = 20 * 1024 * 1024;

    public int Order => Int32.MaxValue;

    public bool CanEdit(string path) => true;

    public async Task Open(string path)
    {
        if (!String.IsNullOrWhiteSpace(settings.ExternalEditor))
        {
            ShellLauncher.OpenWith(settings.ExternalEditor, path);
            return;
        }
        if (new FileInfo(path).Length > MaxBuiltInBytes)
        {
            ShellLauncher.OpenWithDefaultApp(path);
            return;
        }
        await navigator.NavigateTo<EditorViewModel>(vm => vm.FilePath = path);
    }
}
