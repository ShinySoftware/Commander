using System.ComponentModel;
using ShinyCommander.Core.Rename;
using Shiny.Mediator;

namespace ShinyCommander.ViewModels;

public sealed class RenamePreviewRow(RenamePreview preview)
{
    public RenamePreview Preview { get; } = preview;
    public string OldName => this.Preview.Source.Name;
    public string NewName => this.Preview.NewName;
    public string Status => this.Preview.Error ?? (this.Preview.Changed ? "✓" : L.Rename_Unchanged);
}

/// <summary>Ctrl+M multi-rename tool with TC's placeholder syntax and live preview.</summary>
[ShellMap<MultiRenamePage>("MultiRename")]
public partial class MultiRenameViewModel(IAppDialogs dialogs, INavigator navigator, IMediator mediator) : ObservableObject, IPageLifecycleAware
{
    IReadOnlyList<RenameUndoStep> lastUndo = [];

    [ShellProperty]
    public List<FileEntry> Items { get; set; } = [];

    [ObservableProperty] public partial string NameMask { get; set; } = "[N]";
    [ObservableProperty] public partial string ExtensionMask { get; set; } = "[E]";
    [ObservableProperty] public partial string Search { get; set; } = String.Empty;
    [ObservableProperty] public partial string Replace { get; set; } = String.Empty;
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial int CaseIndex { get; set; }
    [ObservableProperty] public partial string CounterStart { get; set; } = "1";
    [ObservableProperty] public partial string CounterStep { get; set; } = "1";
    [ObservableProperty] public partial string CounterDigits { get; set; } = "1";
    [ObservableProperty] public partial ObservableCollection<RenamePreviewRow> Preview { get; set; } = [];
    [ObservableProperty] public partial string Status { get; set; } = String.Empty;
    [ObservableProperty] public partial bool CanUndo { get; set; }

    public IReadOnlyList<string> CaseOptions { get; } = [L.Rename_CaseUnchanged, L.Rename_CaseLower, L.Rename_CaseUpper, L.Rename_CaseFirstUpper, L.Rename_CaseTitle];

    public void OnAppearing() => this.Refresh();
    public void OnDisappearing() { }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(this.Preview) or nameof(this.Status) or nameof(this.CanUndo)))
            this.Refresh();
    }

    MultiRenameOptions Options => new()
    {
        NameMask = this.NameMask,
        ExtensionMask = this.ExtensionMask,
        Search = this.Search,
        Replace = this.Replace,
        UseRegex = this.UseRegex,
        Case = (RenameCase)this.CaseIndex,
        CounterStart = Int32.TryParse(this.CounterStart, out var s) ? s : 1,
        CounterStep = Int32.TryParse(this.CounterStep, out var st) ? st : 1,
        CounterDigits = Int32.TryParse(this.CounterDigits, out var d) ? Math.Clamp(d, 1, 10) : 1
    };

    void Refresh()
    {
        if (this.Items.Count == 0)
            return;

        var preview = MultiRenameEngine.Preview(this.Items, this.Options);
        this.Preview = new ObservableCollection<RenamePreviewRow>(preview.Select(x => new RenamePreviewRow(x)));
        var errors = preview.Count(x => x.Error != null);
        var changed = preview.Count(x => x.Changed);
        this.Status = errors > 0
            ? L.Rename_StatusProblemsFormat(changed, preview.Count, errors)
            : L.Rename_StatusFormat(changed, preview.Count);
    }

    [RelayCommand]
    void InsertPlaceholder(string token) => this.NameMask += token;

    [RelayCommand]
    async Task Execute()
    {
        var preview = MultiRenameEngine.Preview(this.Items, this.Options);
        if (preview.Any(x => x.Error != null))
        {
            await dialogs.Alert(L.Rename_AlertTitle, L.Rename_FixProblems);
            return;
        }

        try
        {
            this.lastUndo = await MultiRenameEngine.ExecuteAsync(preview);
            this.CanUndo = this.lastUndo.Count > 0;
            await dialogs.Toast(L.Rename_DoneFormat(this.lastUndo.Count), ToastKind.Success);

            // continue editing against the new names
            var map = this.lastUndo.ToDictionary(x => x.ToPath, x => x.FromPath);
            this.Items = this.Items.Select(e => map.FirstOrDefault(kv => kv.Value == e.FullPath).Key is { } newPath
                ? FileEntry.FromInfo(e.IsDirectory ? new DirectoryInfo(newPath) : new FileInfo(newPath))
                : e).ToList();
            this.NameMask = "[N]";
            this.Search = String.Empty;
            await this.Publish();
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Rename_AlertTitle, ex.Message);
        }
    }

    [RelayCommand]
    async Task Undo()
    {
        await MultiRenameEngine.UndoAsync(this.lastUndo);
        var map = this.lastUndo.ToDictionary(x => x.FromPath, x => x.ToPath);
        this.Items = this.Items.Select(e => map.TryGetValue(e.FullPath, out var original)
            ? FileEntry.FromInfo(e.IsDirectory ? new DirectoryInfo(original) : new FileInfo(original))
            : e).ToList();
        this.lastUndo = [];
        this.CanUndo = false;
        this.Refresh();
        await this.Publish();
    }

    Task Publish() => mediator.Publish(new FileSystemChangedEvent(this.Items.Select(x => Path.GetDirectoryName(x.FullPath)!).Distinct().ToList()));

    [RelayCommand]
    Task Close() => navigator.GoBack();
}
