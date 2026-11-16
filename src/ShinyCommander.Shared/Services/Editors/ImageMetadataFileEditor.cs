using ShinyCommander.Core.Metadata;

namespace ShinyCommander.Services.Editors;

/// <summary>JPEG photos: their EXIF fields - title, author, date taken, location - rather than their bytes as text.</summary>
public sealed class ImageMetadataFileEditor(INavigator navigator) : IFileEditor
{
    public int Order => 100;

    public bool CanEdit(string path) => ImageMetadata.CanEdit(path);

    public Task Open(string path) => navigator.NavigateTo<ImageMetadataViewModel>(vm => vm.FilePath = path);
}
