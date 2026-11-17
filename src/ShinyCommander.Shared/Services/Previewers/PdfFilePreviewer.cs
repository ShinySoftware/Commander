namespace ShinyCommander.Services.Previewers;

/// <summary>PDF documents, in the platform's web view (WebKit on macOS, WebView2 on Windows).</summary>
public sealed class PdfFilePreviewer : IFilePreviewer
{
    public string Key => "pdf";
    public string Title => L.Preview_Pdf;
    public int Order => 100;

    public bool Handles(string fileName) => Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    // a real PDF starts "%PDF-" - anything else would only get the web view's download prompt
    public bool CanPreview(string path)
    {
        Span<byte> header = stackalloc byte[5];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, header.Length, false) == header.Length && header.SequenceEqual("%PDF-"u8);
    }

    public Task<View> CreateView(string path, CancellationToken ct)
        => MainThread.InvokeOnMainThreadAsync<View>(() => WebPreviewView.ForFile(path));
}
