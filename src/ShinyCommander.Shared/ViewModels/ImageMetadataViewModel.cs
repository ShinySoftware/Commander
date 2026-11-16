using System.Globalization;
using Shiny.Maui.Controls.ImageEditor;
using ShinyCommander.Core.Metadata;

namespace ShinyCommander.ViewModels;

/// <summary>
/// F4 on a JPEG: its EXIF fields, edited in place without touching the picture - and, in the Edit image mode, the
/// picture itself (crop, rotate, draw, annotate) with Shiny's ImageEditor, saved back with the metadata kept.
/// </summary>
[ShellMap<ImageMetadataPage>("ImageMetadata")]
public partial class ImageMetadataViewModel(IAppDialogs dialogs, INavigator navigator) : ObservableObject, IPageLifecycleAware
{
    const string DateFormat = "yyyy-MM-dd HH:mm:ss";
    bool loaded;
    bool loading;

    [ShellProperty]
    [ObservableProperty]
    public partial string FilePath { get; set; } = String.Empty;

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    [ObservableProperty]
    public partial string Info { get; set; } = String.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMetadata), nameof(ModeIndex))]
    public partial bool IsEditingImage { get; set; }

    /// <summary>The picture for the image editor - read when its mode is first opened, and again after a save.</summary>
    [ObservableProperty]
    public partial ImageSource? EditorImage { get; set; }

    /// <summary>The editor has edits that aren't saved (its undo stack isn't empty).</summary>
    [ObservableProperty]
    public partial bool HasImageEdits { get; set; }

    public bool IsMetadata => !this.IsEditingImage;

    public int ModeIndex
    {
        get => this.IsEditingImage ? 1 : 0;
        set => this.IsEditingImage = value == 1;
    }

    partial void OnIsEditingImageChanged(bool value)
    {
        if (value && this.EditorImage == null)
            this.EditorImage = FromBytes(File.ReadAllBytes(this.FilePath));
    }

    [ObservableProperty] public partial string? PhotoTitle { get; set; }
    [ObservableProperty] public partial string? Description { get; set; }
    [ObservableProperty] public partial string? Keywords { get; set; }
    [ObservableProperty] public partial string? Comment { get; set; }
    [ObservableProperty] public partial string? Artist { get; set; }
    [ObservableProperty] public partial string? Copyright { get; set; }
    [ObservableProperty] public partial string? DateTaken { get; set; }
    [ObservableProperty] public partial string? Software { get; set; }
    [ObservableProperty] public partial string? Make { get; set; }
    [ObservableProperty] public partial string? Model { get; set; }
    [ObservableProperty] public partial string? Latitude { get; set; }
    [ObservableProperty] public partial string? Longitude { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTags))]
    public partial IReadOnlyList<ImageMetadataTag> Tags { get; set; } = [];

    public bool HasNoTags => this.Tags.Count == 0;
    public string Title => (this.IsDirty ? "● " : String.Empty) + Path.GetFileName(this.FilePath);

    // every editable field marks the page dirty - except while a read fills them in
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!this.loading && e.PropertyName is nameof(PhotoTitle) or nameof(Description) or nameof(Keywords) or nameof(Comment)
            or nameof(Artist) or nameof(Copyright) or nameof(DateTaken) or nameof(Software) or nameof(Make) or nameof(Model)
            or nameof(Latitude) or nameof(Longitude))
            this.IsDirty = true;
    }

    public async void OnAppearing()
    {
        if (this.loaded)
            return;

        this.loaded = true;
        try
        {
            var path = this.FilePath;
            this.Image = ImageSource.FromFile(path);
            await this.Load();
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Editor_CannotOpen, ex.Message);
            await navigator.GoBack();
        }
    }

    public void OnDisappearing() { }

    async Task Load()
    {
        var path = this.FilePath;
        var snapshot = await Task.Run(() => ImageMetadata.Read(path));
        var v = snapshot.Values;

        this.loading = true;
        this.PhotoTitle = v.Title;
        this.Description = v.Description;
        this.Keywords = v.Keywords;
        this.Comment = v.Comment;
        this.Artist = v.Artist;
        this.Copyright = v.Copyright;
        this.DateTaken = v.DateTaken?.ToString(DateFormat, CultureInfo.InvariantCulture);
        this.Software = v.Software;
        this.Make = v.Make;
        this.Model = v.Model;
        this.Latitude = v.Latitude?.ToString("0.######", CultureInfo.InvariantCulture);
        this.Longitude = v.Longitude?.ToString("0.######", CultureInfo.InvariantCulture);
        this.loading = false;

        this.Tags = snapshot.Tags;
        this.Info = L.ImageMeta_InfoFormat(SizeFormatter.Friendly(new FileInfo(path).Length), snapshot.Tags.Count);
        this.IsDirty = false;
    }

    [RelayCommand]
    async Task Save()
    {
        DateTime? taken = null;
        if (!String.IsNullOrWhiteSpace(this.DateTaken))
        {
            if (!DateTime.TryParse(this.DateTaken, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && !DateTime.TryParse(this.DateTaken, CultureInfo.CurrentCulture, DateTimeStyles.None, out d))
            {
                await dialogs.Alert(L.Editor_SaveFailed, L.ImageMeta_InvalidDate);
                return;
            }
            taken = d;
        }

        var lat = Coordinate(this.Latitude, 90);
        var lon = Coordinate(this.Longitude, 180);
        var latGiven = !String.IsNullOrWhiteSpace(this.Latitude);
        var lonGiven = !String.IsNullOrWhiteSpace(this.Longitude);
        if ((latGiven && lat == null) || (lonGiven && lon == null) || latGiven != lonGiven)
        {
            await dialogs.Alert(L.Editor_SaveFailed, L.ImageMeta_InvalidLocation);
            return;
        }

        var values = new ImageMetadataValues
        {
            Title = this.PhotoTitle,
            Description = this.Description,
            Keywords = this.Keywords,
            Comment = this.Comment,
            Artist = this.Artist,
            Copyright = this.Copyright,
            DateTaken = taken,
            Software = this.Software,
            Make = this.Make,
            Model = this.Model,
            Latitude = lat,
            Longitude = lon
        };
        await this.Change(path => ImageMetadata.Write(path, values));
    }

    [RelayCommand]
    async Task RemoveLocation()
    {
        if (await dialogs.Confirm(L.ImageMeta_RemoveLocation, L.ImageMeta_RemoveLocationConfirm, L.Common_Remove, L.Common_Cancel))
            await this.Change(ImageMetadata.RemoveLocation);
    }

    [RelayCommand]
    async Task RemoveAll()
    {
        if (await dialogs.Confirm(L.ImageMeta_RemoveAll, L.ImageMeta_RemoveAllConfirm, L.Common_Remove, L.Common_Cancel))
            await this.Change(ImageMetadata.RemoveAll);
    }

    // writes, then reads back what's really in the file now
    async Task Change(Action<string> write)
    {
        try
        {
            var path = this.FilePath;
            await Task.Run(() => write(path));
            await this.Load();
            await dialogs.Toast(L.Editor_Saved, ToastKind.Success);
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Editor_SaveFailed, ex.Message);
        }
    }

    /// <summary>
    /// The editor's Save: the edited picture as a JPEG, then the original's metadata copied onto it (an encoder writes
    /// none), then it replaces the file. Written next to it first, so a failure leaves the photo as it was.
    /// </summary>
    [RelayCommand]
    async Task SaveImage(EditedImage edited)
    {
        var path = this.FilePath;
        var temp = path + ".shinyedit";
        try
        {
            await using (var stream = await edited.ToStreamAsync(ImageExportFormat.Jpeg, 0.92f))
            await using (var file = File.Create(temp))
                await stream.CopyToAsync(file);

            // Shiny.Maui.Controls 2.5.0 only renders the edit on iOS / Android - its desktop builds return an empty
            // stream, and that must never replace the photo
            if (new FileInfo(temp).Length == 0)
            {
                await dialogs.Alert(L.Editor_SaveFailed, L.ImageMeta_ExportUnsupported);
                return;
            }

            await Task.Run(() =>
            {
                ImageMetadata.CopyMetadata(path, temp);
                File.Move(temp, path, true);
            });

            var bytes = await File.ReadAllBytesAsync(path);
            this.Image = FromBytes(bytes);
            this.EditorImage = FromBytes(bytes);
            this.HasImageEdits = false;
            await this.Load();
            await dialogs.Toast(L.Editor_Saved, ToastKind.Success);
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Editor_SaveFailed, ex.Message);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    [RelayCommand]
    async Task Close()
    {
        if (this.HasImageEdits && !await dialogs.Confirm(L.ImageMeta_DiscardEdits, L.ImageMeta_DiscardEditsMessage, L.Editor_Discard, L.Common_Cancel))
            return;

        if (this.IsDirty)
        {
            var choice = await dialogs.Choose(L.Editor_SaveChanges, [L.Common_Save, L.Editor_Discard], L.Common_Cancel);
            if (choice == null)
                return;
            if (choice == L.Common_Save)
            {
                await this.Save();
                if (this.IsDirty)
                    return;
            }
        }
        await navigator.GoBack();
    }

    // from memory, not the path: a FileImageSource of a file that was just rewritten can come back from a cache
    static ImageSource FromBytes(byte[] bytes) => ImageSource.FromStream(() => new MemoryStream(bytes));

    static double? Coordinate(string? text, double limit)
    {
        if (String.IsNullOrWhiteSpace(text))
            return null;

        var ok = Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || Double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        return ok && Math.Abs(value) <= limit ? value : null;
    }
}
