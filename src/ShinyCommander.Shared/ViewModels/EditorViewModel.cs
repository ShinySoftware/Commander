using System.Text;

namespace ShinyCommander.ViewModels;

/// <summary>F4 internal editor. Markdown files get Shiny's MarkdownEditor (toolbar + live preview).</summary>
[ShellMap<EditorPage>("Editor")]
public partial class EditorViewModel(IAppDialogs dialogs, INavigator navigator, AppSettings settings) : ObservableObject, IPageLifecycleAware
{
    string original = String.Empty;
    Encoding encoding = new UTF8Encoding(false);
    bool loaded;

    [ShellProperty]
    [ObservableProperty]
    public partial string FilePath { get; set; } = String.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(Title))]
    public partial string Text { get; set; } = String.Empty;

    [ObservableProperty]
    public partial bool IsMarkdown { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = String.Empty;

    public bool IsPlainText => !this.IsMarkdown;
    public bool IsDirty => !String.Equals(this.Text, this.original, StringComparison.Ordinal);
    public string Title => (this.IsDirty ? "● " : String.Empty) + Path.GetFileName(this.FilePath);
    public int FontSize => Math.Max(9, settings.ViewerFontSize);

    partial void OnIsMarkdownChanged(bool value) => this.OnPropertyChanged(nameof(this.IsPlainText));

    public async void OnAppearing()
    {
        if (this.loaded)
            return;

        this.loaded = true;
        try
        {
            var bytes = await File.ReadAllBytesAsync(this.FilePath);
            using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
            {
                this.original = await reader.ReadToEndAsync();
                this.encoding = reader.CurrentEncoding;
            }
            this.IsMarkdown = FileIcons.IsMarkdown(Path.GetExtension(this.FilePath).TrimStart('.'));
            this.Text = this.original;
            this.Status = $"{this.encoding.WebName}  •  {this.original.Split('\n').Length} lines";
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Cannot open file", ex.Message);
            await navigator.GoBack();
        }
    }

    public void OnDisappearing() { }

    [RelayCommand]
    async Task Save()
    {
        try
        {
            await File.WriteAllTextAsync(this.FilePath, this.Text, this.encoding);
            this.original = this.Text;
            this.OnPropertyChanged(nameof(this.IsDirty));
            this.OnPropertyChanged(nameof(this.Title));
            await dialogs.Toast("Saved", ToastKind.Success);
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Save failed", ex.Message);
        }
    }

    [RelayCommand]
    async Task Close()
    {
        if (this.IsDirty)
        {
            var choice = await dialogs.Choose("Save changes?", ["Save", "Discard"], "Cancel");
            if (choice == null)
                return;
            if (choice == "Save")
                await this.Save();
        }
        await navigator.GoBack();
    }
}
