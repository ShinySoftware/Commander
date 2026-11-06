namespace ShinyCommander.ViewModels;

/// <summary>
/// Quick view (Ctrl/⌘+Q): the other panel previews whatever is under the active panel's cursor - images,
/// rendered Markdown, text, hex, and a summary for folders and archives.
/// </summary>
public partial class QuickViewViewModel(VirtualFileSystem vfs) : ObservableObject
{
    const int MaxTextBytes = 256 * 1024;
    const int MaxHexBytes = 16 * 1024;
    const int MaxImageBytes = 48 * 1024 * 1024;

    CancellationTokenSource? cts;
    string? currentPath;

    [ObservableProperty]
    public partial string Title { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string Info { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string MotionIcon { get; set; } = "eye";

    [ObservableProperty]
    public partial Color IconColor { get; set; } = Colors.Gray;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsImage), nameof(IsMarkdown), nameof(IsSummary), nameof(HasContent))]
    public partial PreviewKind? Kind { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = String.Empty;

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool IsText => this.Kind is PreviewKind.Text or PreviewKind.Hex;
    public bool IsImage => this.Kind == PreviewKind.Image;
    public bool IsMarkdown => this.Kind == PreviewKind.Markdown;

    /// <summary>Folder / archive / nothing selected - just the icon and the info line.</summary>
    public bool IsSummary => this.Kind == null;
    public bool HasContent => this.Kind != null;

    public void Clear()
    {
        this.cts?.Cancel();
        this.currentPath = null;
        this.Image = null;
        this.Text = String.Empty;
        this.Kind = null;
    }

    /// <summary>Called as the active panel's cursor moves; quick key repeats only load the one they stop on.</summary>
    public async Task ShowAsync(FileEntry? entry)
    {
        if (entry?.FullPath == this.currentPath && entry != null)
            return;

        this.cts?.Cancel();
        var cts = this.cts = new CancellationTokenSource();
        this.currentPath = entry?.FullPath;

        if (entry == null || entry.IsParentLink)
        {
            this.ShowSummary(entry, "Nothing to preview", null);
            return;
        }

        var (icon, color) = FileIcons.Motion(entry);
        this.MotionIcon = icon;
        this.IconColor = Color.FromArgb(color);
        this.Title = entry.Name;

        if (entry.IsDirectory || entry.IsArchive)
        {
            this.ShowSummary(entry, entry.IsArchive ? $"{ArchiveFormats.Describe(ArchiveFormats.GetKind(entry.Name))} archive  •  {SizeFormatter.Friendly(entry.Size)}  •  Enter opens it" : "Folder", null);
            if (entry.IsDirectory && !entry.IsInArchive)
                await this.SummarizeFolderAsync(entry, cts.Token);
            return;
        }

        try
        {
            await Task.Delay(120, cts.Token); // debounce held-down arrow keys
            this.IsLoading = true;
            var content = await FilePreview.LoadAsync(vfs, entry.FullPath, MaxTextBytes, MaxImageBytes, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            this.Info = $"{SizeFormatter.Friendly(content.Length)}  •  {entry.Modified:yyyy-MM-dd HH:mm}" + (content.Truncated ? $"  •  first {SizeFormatter.Friendly(content.Bytes.Length)} shown" : String.Empty);
            switch (content.Kind)
            {
                case PreviewKind.Image:
                    var bytes = content.Bytes;
                    this.Image = ImageSource.FromStream(() => new MemoryStream(bytes));
                    this.Text = String.Empty;
                    break;

                case PreviewKind.Hex:
                    this.Image = null;
                    this.Text = FilePreview.ToHex(content.Bytes, MaxHexBytes);
                    break;

                default:
                    this.Image = null;
                    this.Text = FilePreview.DecodeText(content.Bytes);
                    break;
            }
            this.Kind = content.Kind;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                this.ShowSummary(entry, ex.Message, null);
        }
        finally
        {
            if (this.cts == cts)
                this.IsLoading = false;
        }
    }

    void ShowSummary(FileEntry? entry, string info, PreviewKind? kind)
    {
        if (entry == null || entry.IsParentLink)
        {
            this.MotionIcon = "eye";
            this.IconColor = Colors.Gray;
            this.Title = "Quick view";
        }
        this.Info = info;
        this.Image = null;
        this.Text = String.Empty;
        this.Kind = kind;
        this.IsLoading = false;
    }

    async Task SummarizeFolderAsync(FileEntry entry, CancellationToken ct)
    {
        try
        {
            await Task.Delay(150, ct);
            this.IsLoading = true;
            var (files, dirs, bytes) = await Task.Run(() =>
            {
                long f = 0, d = 0, b = 0;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var info in new DirectoryInfo(entry.FullPath).EnumerateFileSystemInfos("*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    if (info is FileInfo fi)
                    {
                        f++;
                        b += fi.Length;
                    }
                    else
                    {
                        d++;
                    }
                }
                return (f, d, b);
            }, ct);

            if (!ct.IsCancellationRequested)
                this.Info = $"Folder  •  {files:N0} file(s), {dirs:N0} folder(s)  •  {SizeFormatter.Friendly(bytes)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                this.Info = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                this.IsLoading = false;
        }
    }
}
