namespace ShinyCommander.ViewModels;

public enum ViewerMode
{
    Text,
    Hex,
    Image,
    Markdown
}

/// <summary>F3 "Lister" - text, hex, image and rendered markdown, for files on disk or inside archives.</summary>
[ShellMap<ViewerPage>("Viewer")]
public partial class ViewerViewModel(VirtualFileSystem vfs, AppSettings settings, INavigator navigator) : ObservableObject, IPageLifecycleAware
{
    const int MaxTextBytes = 4 * 1024 * 1024;
    const int MaxHexBytes = 256 * 1024;
    byte[] bytes = [];
    bool truncated;

    [ShellProperty]
    [ObservableProperty]
    public partial string FilePath { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = String.Empty;

    [ObservableProperty]
    public partial ViewerMode Mode { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = String.Empty;

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    [ObservableProperty]
    public partial string Info { get; set; } = String.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool WordWrap { get; set; } = true;

    public int FontSize => Math.Max(9, settings.ViewerFontSize);

    public bool IsText => this.Mode is ViewerMode.Text or ViewerMode.Hex;
    public bool IsImage => this.Mode == ViewerMode.Image;
    public bool IsMarkdown => this.Mode == ViewerMode.Markdown;

    partial void OnModeChanged(ViewerMode value)
    {
        this.OnPropertyChanged(nameof(this.IsText));
        this.OnPropertyChanged(nameof(this.IsImage));
        this.OnPropertyChanged(nameof(this.IsMarkdown));
        this.Render();
    }

    public async void OnAppearing()
    {
        if (this.bytes.Length > 0 || String.IsNullOrEmpty(this.FilePath))
            return;

        await this.LoadAsync(this.FilePath);
    }

    public void OnDisappearing() { }

    async Task LoadAsync(string path)
    {
        this.IsLoading = true;
        try
        {
            this.FilePath = path;
            this.Title = Path.GetFileName(path);
            var content = await FilePreview.LoadAsync(vfs, path, MaxTextBytes);
            this.bytes = content.Bytes;
            this.truncated = content.Truncated;
            var length = content.Length;

            this.Info = $"{SizeFormatter.Exact(length)} bytes" + (this.truncated ? $"  •  showing first {SizeFormatter.Friendly(this.bytes.Length)}" : String.Empty);

            var mode = content.Kind switch
            {
                PreviewKind.Image => ViewerMode.Image,
                PreviewKind.Markdown => ViewerMode.Markdown,
                PreviewKind.Hex => ViewerMode.Hex,
                _ => ViewerMode.Text
            };

            if (this.Mode == mode)
                this.Render();
            else
                this.Mode = mode;
        }
        catch (Exception ex)
        {
            this.Text = ex.Message;
            this.Mode = ViewerMode.Text;
        }
        finally
        {
            this.IsLoading = false;
        }
    }

    void Render()
    {
        switch (this.Mode)
        {
            case ViewerMode.Text:
            case ViewerMode.Markdown:
                this.Text = FilePreview.DecodeText(this.bytes);
                break;

            case ViewerMode.Hex:
                this.Text = FilePreview.ToHex(this.bytes, MaxHexBytes);
                break;

            case ViewerMode.Image:
                var copy = this.bytes;
                this.Image = ImageSource.FromStream(() => new MemoryStream(copy));
                break;
        }
    }

    [RelayCommand]
    void SetMode(string mode)
    {
        if (Enum.TryParse<ViewerMode>(mode, out var m))
            this.Mode = m;
    }

    [RelayCommand]
    void ToggleWrap() => this.WordWrap = !this.WordWrap;

    [RelayCommand]
    Task Close() => navigator.GoBack();

    [RelayCommand]
    Task NextFile() => this.Step(1);

    [RelayCommand]
    Task PreviousFile() => this.Step(-1);

    // TC lister: N/P walk the files in the same folder without leaving the viewer
    async Task Step(int delta)
    {
        var dir = Path.GetDirectoryName(this.FilePath);
        if (dir == null)
            return;

        var files = (await vfs.ListAsync(dir, settings.ShowHidden))
            .Where(x => !x.IsDirectory)
            .OrderBy(x => x, new FileEntryComparer(SortColumn.Name, false))
            .ToList();

        var index = files.FindIndex(x => x.FullPath == this.FilePath);
        var next = index + delta;
        if (next < 0 || next >= files.Count)
            return;

        this.bytes = [];
        this.Image = null;
        await this.LoadAsync(files[next].FullPath);
    }
}
