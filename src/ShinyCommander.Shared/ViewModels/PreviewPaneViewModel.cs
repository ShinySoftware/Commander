namespace ShinyCommander.ViewModels;

public enum PreviewPosition
{
    Right,
    Bottom
}

/// <summary>
/// The preview panel (Ctrl/⌘+Q), docked beside or under the file panels: whatever is under the active panel's cursor,
/// shown by the first preview plugin (IFilePreviewer) that takes it - images, Markdown, HTML, PDF, SQLite - or as
/// text / hex, and a summary for folders and archives.
/// </summary>
public partial class PreviewPaneViewModel : ObservableObject
{
    const int MaxTextBytes = 256 * 1024;
    const int MaxHexBytes = 16 * 1024;
    // a file in an archive or on a server is copied out for a previewer up to this size; past it, text / hex
    const long MaxCopyBytes = 48 * 1024 * 1024;
    const double DefaultWidth = 420;
    const double DefaultHeight = 280;
    public const double MinSize = 160;

    readonly VirtualFileSystem vfs;
    readonly FilePreviewers previewers;
    readonly AppSettings settings;
    CancellationTokenSource? cts;
    FileEntry? current;
    // the temp copy (its folder) behind the plugin view on show - deleted once the panel moves on
    string? tempCopy;

    public PreviewPaneViewModel(VirtualFileSystem vfs, FilePreviewers previewers, AppSettings settings)
    {
        this.vfs = vfs;
        this.previewers = previewers;
        this.settings = settings;
        this.IsOpen = settings.PreviewOpen;
        this.Position = Enum.TryParse<PreviewPosition>(settings.PreviewPosition, out var position) ? position : PreviewPosition.Right;
        this.Size = settings.PreviewSize >= MinSize ? settings.PreviewSize : this.DefaultSize;

        // a previewer turned on or off in Settings - show the file again with what's left
        previewers.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            var entry = this.current;
            this.current = null;
            if (this.IsOpen)
                _ = this.ShowAsync(entry);
        });
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBottom), nameof(DockIcon))]
    public partial PreviewPosition Position { get; set; }

    public bool IsBottom => this.Position == PreviewPosition.Bottom;

    /// <summary>The header button's icon: where the panel goes next.</summary>
    public string DockIcon => this.IsBottom ? "arrow-right" : "arrow-down";

    /// <summary>Width when docked right, height when docked at the bottom.</summary>
    [ObservableProperty]
    public partial double Size { get; set; }

    double DefaultSize => this.Position == PreviewPosition.Bottom ? DefaultHeight : DefaultWidth;

    [ObservableProperty]
    public partial string Title { get; set; } = L.Preview_Title;

    [ObservableProperty]
    public partial string Info { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string MotionIcon { get; set; } = "eye";

    [ObservableProperty]
    public partial Color IconColor { get; set; } = Colors.Gray;

    /// <summary>The built-in text / hex view; null while a plugin or the summary shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsSummary), nameof(HasContent))]
    public partial PreviewKind? Kind { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = String.Empty;

    /// <summary>What a preview plugin made of the file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlugin), nameof(IsSummary), nameof(HasContent))]
    public partial View? PluginView { get; set; }

    /// <summary>The <see cref="IFilePreviewer.Key"/> showing the file, if one is.</summary>
    [ObservableProperty]
    public partial string? PreviewerKey { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool IsText => this.Kind is PreviewKind.Text or PreviewKind.Hex;
    public bool IsPlugin => this.PluginView != null;

    /// <summary>Folder / archive / nothing selected - just the icon and the info line.</summary>
    public bool IsSummary => this.Kind == null && this.PluginView == null;
    public bool HasContent => !this.IsSummary;

    partial void OnIsOpenChanged(bool value)
    {
        this.settings.PreviewOpen = value;
        if (!value)
            this.Clear();
    }

    partial void OnPositionChanged(PreviewPosition value)
    {
        this.settings.PreviewPosition = value.ToString();
        // a width makes no sense as a height - start the new side at its default
        this.Size = this.DefaultSize;
        this.SaveSize();
    }

    public void SaveSize() => this.settings.PreviewSize = (int)this.Size;

    [RelayCommand]
    void Close() => this.IsOpen = false;

    [RelayCommand]
    void Dock() => this.Position = this.IsBottom ? PreviewPosition.Right : PreviewPosition.Bottom;

    public void Clear()
    {
        this.cts?.Cancel();
        this.current = null;
        this.Show(null, null, null);
        this.Title = L.Preview_Title;
        this.MotionIcon = "eye";
        this.IconColor = Colors.Gray;
        this.Info = String.Empty;
        this.IsLoading = false;
    }

    /// <summary>Called as the active panel's cursor moves; quick key repeats only load the one they stop on.</summary>
    public async Task ShowAsync(FileEntry? entry)
    {
        if (entry != null && entry.FullPath == this.current?.FullPath && entry.Modified == this.current.Modified)
            return;

        this.cts?.Cancel();
        var cts = this.cts = new CancellationTokenSource();
        this.current = entry;

        if (entry == null || entry.IsParentLink)
        {
            this.Clear();
            this.Info = L.QuickView_NothingToPreview;
            return;
        }

        var (icon, color) = FileIcons.Motion(entry);
        this.MotionIcon = icon;
        this.IconColor = ThemeColors.Get(color);
        this.Title = entry.Name;

        if (entry.IsDirectory || entry.IsArchive)
        {
            this.ShowSummary(entry.IsArchive ? L.QuickView_ArchiveFormat(ArchiveFormats.Describe(ArchiveFormats.GetKind(entry.Name)), SizeFormatter.Friendly(entry.Size)) : L.QuickView_Folder);
            if (entry.IsDirectory && !entry.IsInArchive && !entry.IsRemote)
                await this.SummarizeFolderAsync(entry, cts.Token);
            return;
        }

        string? copy = null;
        try
        {
            await Task.Delay(120, cts.Token); // debounce held-down arrow keys
            this.IsLoading = true;
            var modified = entry.Modified.ToString("yyyy-MM-dd HH:mm");

            var candidates = this.previewers.Candidates(entry.Name);
            if (candidates.Count > 0 && (entry.Size <= MaxCopyBytes || !(entry.IsRemote || entry.IsInArchive)))
            {
                var local = await this.vfs.MaterializeAsync(entry.FullPath, cts.Token);
                if (local != entry.FullPath)
                    copy = Path.GetDirectoryName(local);

                var previewer = await Task.Run(() => candidates.FirstOrDefault(x => x.CanPreview(local)), cts.Token);
                if (previewer != null)
                {
                    var view = await previewer.CreateView(local, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    this.Info = $"{SizeFormatter.Friendly(entry.Size)}  •  {modified}";
                    this.Show(null, view, previewer.Key, copy);
                    copy = null;
                    return;
                }
            }

            var content = await FilePreview.LoadAsync(this.vfs, entry.FullPath, MaxTextBytes, MaxTextBytes, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            this.Info = content.Truncated
                ? L.QuickView_InfoTruncatedFormat(SizeFormatter.Friendly(content.Length), modified, SizeFormatter.Friendly(content.Bytes.Length))
                : $"{SizeFormatter.Friendly(content.Length)}  •  {modified}";
            if (FilePreview.LooksBinary(content.Bytes))
            {
                this.Text = FilePreview.ToHex(content.Bytes, MaxHexBytes);
                this.Show(PreviewKind.Hex, null, null);
            }
            else
            {
                this.Text = FilePreview.DecodeText(content.Bytes);
                this.Show(PreviewKind.Text, null, null);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                this.ShowSummary(ex.Message);
        }
        finally
        {
            DeleteCopy(copy);
            if (this.cts == cts)
                this.IsLoading = false;
        }
    }

    void Show(PreviewKind? kind, View? view, string? previewerKey, string? copy = null)
    {
        if (kind == null)
            this.Text = String.Empty;
        this.PluginView = view;
        this.PreviewerKey = previewerKey;
        this.Kind = kind;

        // the old view is gone - and with it the last reader of its temp copy
        DeleteCopy(this.tempCopy);
        this.tempCopy = copy;
    }

    void ShowSummary(string info)
    {
        this.Info = info;
        this.Show(null, null, null);
        this.IsLoading = false;
    }

    static void DeleteCopy(string? folder)
    {
        if (folder == null)
            return;

        try
        {
            Directory.Delete(folder, true);
        }
        catch
        {
            // still open somewhere (a web view finishing a read) - it's in the temp folder either way
        }
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
                this.Info = L.QuickView_FolderSummaryFormat(files.ToString("N0"), dirs.ToString("N0"), SizeFormatter.Friendly(bytes));
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
