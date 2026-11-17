namespace ShinyCommander.Services.Previewers;

/// <summary>Web pages, rendered - with their scripts switched off, since a preview opens on nothing more than a cursor move.</summary>
public sealed class HtmlFilePreviewer : IFilePreviewer
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { "html", "htm", "xhtml" };
    const long MaxBytes = 8 * 1024 * 1024;

    // first in the document, so it's in force before any <script> is reached
    const string NoScripts = """<meta http-equiv="Content-Security-Policy" content="script-src 'none'; object-src 'none'; frame-src 'none'">""";

    public string Key => "html";
    public string Title => L.Preview_Html;
    public int Order => 100;

    public bool Handles(string fileName) => Extensions.Contains(Path.GetExtension(fileName).TrimStart('.'));

    public bool CanPreview(string path) => new FileInfo(path).Length <= MaxBytes;

    public async Task<View> CreateView(string path, CancellationToken ct)
    {
        var html = FilePreview.DecodeText(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
        // the folder as the base, so the page's own images and stylesheets load
        var folder = new Uri(Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar).AbsoluteUri;
        return await MainThread.InvokeOnMainThreadAsync(() => WebPreviewView.ForHtml(NoScripts + html, folder));
    }
}
