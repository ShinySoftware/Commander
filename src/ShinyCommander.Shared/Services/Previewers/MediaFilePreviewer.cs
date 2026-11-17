namespace ShinyCommander.Services.Previewers;

/// <summary>Audio and video, played by Shiny's MediaElement - AVPlayer on macOS, Windows.Media on Windows, GStreamer on Linux.</summary>
public sealed class MediaFilePreviewer : IFilePreviewer
{
    public string Key => "media";
    public string Title => L.Preview_Media;
    public int Order => 100;

    public bool Handles(string fileName) => FileKinds.OfName(fileName) is FileKind.Audio or FileKind.Video;

    public async Task<View> CreateView(string path, CancellationToken ct)
    {
        var isAudio = FileKinds.OfName(path) == FileKind.Audio;
        // a song's title and artist, from its tags - the file name when it has none
        var metadata = isAudio
            ? await Task.Run(() => FileMetadataReader.Read(FileEntry.FromInfo(new FileInfo(path)), ct), ct).ConfigureAwait(false)
            : FileMetadata.Empty;
        var title = metadata.Title ?? Path.GetFileNameWithoutExtension(path);
        var subtitle = String.Join("  •  ", new[] { metadata.Artist, metadata.Album }.Where(x => x != null));

        return await MainThread.InvokeOnMainThreadAsync<View>(() =>
        {
            var view = new MediaPreviewView(path, isAudio, title, subtitle.Length == 0 ? null : subtitle);
            return new PreviewHost(view, view.Release);
        });
    }
}
