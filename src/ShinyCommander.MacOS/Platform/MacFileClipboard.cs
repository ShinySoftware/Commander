using AppKit;
using Foundation;
using ObjCRuntime;
using Shiny;
using ShinyCommander.Services;

namespace ShinyCommander;

/// <summary>The general pasteboard's file URLs - what Finder's Copy (⌘C) puts there.</summary>
[Singleton]
sealed class MacFileClipboard : IFileClipboard
{
    static readonly Class[] Urls = [new(typeof(NSUrl))];

    // only file:// URLs - a link copied in a browser stays text
    static readonly string[] FileUrl = [NSPasteboard.NSPasteboardTypeFileUrl];

    public bool HasFiles => NSPasteboard.GeneralPasteboard.CanReadItemWithDataConformingToTypes(FileUrl);

    public Task<IReadOnlyList<string>> GetFilesAsync()
    {
        var urls = NSPasteboard.GeneralPasteboard.ReadObjectsForClasses(Urls, new NSDictionary()) ?? [];
        IReadOnlyList<string> paths = urls
            .OfType<NSUrl>()
            .Where(x => x.IsFileUrl)
            .Select(x => x.Path)
            .Where(x => !String.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToList();
        return Task.FromResult(paths);
    }
}
