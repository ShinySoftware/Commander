using ShinyCommander.Core.Metadata;

namespace ShinyCommander.Services.Previewers;

/// <summary>Photos and pictures, fitted to the panel - with the camera and date taken under a JPEG.</summary>
public sealed class ImageFilePreviewer : IFilePreviewer
{
    const long MaxBytes = 48 * 1024 * 1024;

    public string Key => "image";
    public string Title => L.Preview_Images;
    public int Order => 100;

    public bool Handles(string fileName) => FileIcons.IsImage(Path.GetExtension(fileName).TrimStart('.'));

    public bool CanPreview(string path) => new FileInfo(path).Length <= MaxBytes;

    public async Task<View> CreateView(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var details = await Task.Run(() => Describe(path), ct).ConfigureAwait(false);
        return await MainThread.InvokeOnMainThreadAsync(() => new ImagePreviewView(bytes, details));
    }

    // "Canon EOS R5  •  2024-05-01 14:03" - whatever the photo says
    static string? Describe(string path)
    {
        if (!ImageMetadata.CanEdit(path))
            return null;

        try
        {
            var values = ImageMetadata.Read(path).Values;
            var camera = String.Join(' ', new[] { values.Make, values.Model }.Where(x => !String.IsNullOrWhiteSpace(x)));
            var parts = new[] { values.Title, camera, values.DateTaken?.ToString("yyyy-MM-dd HH:mm") }
                .Where(x => !String.IsNullOrWhiteSpace(x))
                .ToList();
            return parts.Count == 0 ? null : String.Join("  •  ", parts);
        }
        catch
        {
            return null;
        }
    }
}
