namespace ShinyCommander.Services.Previewers;

/// <summary>Markdown, rendered.</summary>
public sealed class MarkdownFilePreviewer : IFilePreviewer
{
    const long MaxBytes = 2 * 1024 * 1024;

    public string Key => "markdown";
    public string Title => L.Preview_Markdown;
    public int Order => 100;

    public bool Handles(string fileName) => FileIcons.IsMarkdown(Path.GetExtension(fileName).TrimStart('.'));

    public bool CanPreview(string path) => new FileInfo(path).Length <= MaxBytes;

    public async Task<View> CreateView(string path, CancellationToken ct)
    {
        var text = FilePreview.DecodeText(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
        return await MainThread.InvokeOnMainThreadAsync(() => new MarkdownPreviewView { Markdown = text });
    }
}
