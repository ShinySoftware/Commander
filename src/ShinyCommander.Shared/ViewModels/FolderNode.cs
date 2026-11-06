using ShinyCommander.Core.Platform;

namespace ShinyCommander.ViewModels;

/// <summary>A node in the panel's folder tree (Shiny TreeView, lazily loaded).</summary>
public sealed record FolderNode(string Name, string Path, string Icon = "📁")
{
    public static IEnumerable<object> Roots() => DriveService.GetDrives()
        .Select(d => new FolderNode(d.Name == "~" ? "Home" : d.Name, d.RootPath, d.Name == "~" ? "🏠" : "💽"));

    public static Task<IEnumerable<object>> LoadChildrenAsync(object parent, bool showHidden) => Task.Run(() =>
    {
        if (parent is not FolderNode node)
            return Enumerable.Empty<object>();

        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden };
            return new DirectoryInfo(node.Path)
                .EnumerateDirectories("*", opts)
                .Where(d => showHidden || !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => (object)new FolderNode(d.Name, d.FullName))
                .ToList()
                .AsEnumerable();
        }
        catch
        {
            return Enumerable.Empty<object>();
        }
    });
}
