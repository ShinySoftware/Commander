using System.Text;

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
            var ext = Path.GetExtension(path).TrimStart('.');
            (this.bytes, this.truncated, var length) = await Task.Run(() =>
            {
                using var stream = vfs.OpenRead(path);
                var len = stream.CanSeek ? stream.Length : -1;
                var limit = FileIcons.IsImage(ext) ? Int32.MaxValue : MaxTextBytes;
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0 && ms.Length < limit)
                    ms.Write(buffer, 0, read);
                return (ms.ToArray(), len > ms.Length, len);
            });

            this.Info = $"{SizeFormatter.Exact(length)} bytes" + (this.truncated ? $"  •  showing first {SizeFormatter.Friendly(this.bytes.Length)}" : String.Empty);

            var mode = FileIcons.IsImage(ext) ? ViewerMode.Image
                : FileIcons.IsMarkdown(ext) ? ViewerMode.Markdown
                : LooksBinary(this.bytes) ? ViewerMode.Hex
                : ViewerMode.Text;

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

    static bool LooksBinary(byte[] data)
    {
        var sample = Math.Min(data.Length, 8000);
        var zeros = 0;
        for (var i = 0; i < sample; i++)
        {
            if (data[i] == 0)
                zeros++;
        }
        return zeros > 0;
    }

    void Render()
    {
        switch (this.Mode)
        {
            case ViewerMode.Text:
            case ViewerMode.Markdown:
                this.Text = DecodeText(this.bytes);
                break;

            case ViewerMode.Hex:
                this.Text = ToHex(this.bytes, MaxHexBytes);
                break;

            case ViewerMode.Image:
                var copy = this.bytes;
                this.Image = ImageSource.FromStream(() => new MemoryStream(copy));
                break;
        }
    }

    static string DecodeText(byte[] data)
    {
        // BOM aware, otherwise UTF-8 with replacement
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    static string ToHex(byte[] data, int max)
    {
        var length = Math.Min(data.Length, max);
        var sb = new StringBuilder(length * 4);
        for (var offset = 0; offset < length; offset += 16)
        {
            sb.Append(offset.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                if (offset + i < length)
                    sb.Append(data[offset + i].ToString("X2")).Append(' ');
                else
                    sb.Append("   ");
                if (i == 7)
                    sb.Append(' ');
            }
            sb.Append(' ');
            for (var i = 0; i < 16 && offset + i < length; i++)
            {
                var b = data[offset + i];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }
            sb.AppendLine();
        }
        if (data.Length > max)
            sb.AppendLine($"… {SizeFormatter.Exact(data.Length - max)} more bytes not shown");
        return sb.ToString();
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
