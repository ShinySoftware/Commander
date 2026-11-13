using ShinyCommander.Core.Search;
using Shiny.Mediator;

namespace ShinyCommander.ViewModels;

public sealed class SearchResultItem(FileEntry entry, string startPath)
{
    public FileEntry Entry { get; } = entry;
    public string Icon => FileIcons.For(this.Entry);
    public string MotionIcon => FileIcons.Motion(this.Entry).Icon;
    public Color IconColor => ThemeColors.Get(FileIcons.Motion(this.Entry).Color);
    public string Name => this.Entry.Name;
    public string Folder { get; } = Path.GetRelativePath(startPath, Path.GetDirectoryName(entry.FullPath) ?? startPath);
    public string SizeText => this.Entry.IsDirectory ? "<DIR>" : SizeFormatter.Friendly(this.Entry.Size);
    public long Size => this.Entry.Size;
    public DateTime Modified => this.Entry.Modified;
}

/// <summary>Alt+F7 "Find files".</summary>
[ShellMap<SearchPage>("Search")]
public partial class SearchViewModel(IMediator mediator, INavigator navigator, IAppDialogs dialogs) : ObservableObject, IPageLifecycleAware
{
    CancellationTokenSource? cts;

    [ShellProperty]
    [ObservableProperty]
    public partial string StartPath { get; set; } = String.Empty;

    [ObservableProperty]
    public partial string NameMask { get; set; } = "*";

    [ObservableProperty]
    public partial string ContainingText { get; set; } = String.Empty;

    [ObservableProperty]
    public partial bool CaseSensitive { get; set; }

    [ObservableProperty]
    public partial bool Recursive { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeHidden { get; set; }

    [ObservableProperty]
    public partial bool FoldersOnly { get; set; }

    [ObservableProperty]
    public partial string MinSizeKb { get; set; } = String.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText))]
    public partial bool IsSearching { get; set; }

    public string StartText => this.IsSearching ? L.Common_Stop : L.Common_Start;

    [ObservableProperty]
    public partial string Status { get; set; } = L.Search_Prompt;

    [ObservableProperty]
    public partial object? SelectedResult { get; set; }

    const int MaxShown = 5000;
    readonly List<SearchResultItem> found = [];

    /// <summary>
    /// Replaced wholesale in batches rather than Add-ed to: each collection change makes the DataGrid
    /// rebuild its rows, which is quadratic when thousands of hits stream in.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<SearchResultItem> Results { get; set; } = [];

    public void OnAppearing() { }

    public void OnDisappearing() => this.cts?.Cancel();

    [RelayCommand]
    async Task Start()
    {
        if (this.IsSearching)
        {
            this.cts?.Cancel();
            return;
        }
        if (!Directory.Exists(this.StartPath))
        {
            await dialogs.Alert(L.Search_Title, L.Search_FolderNotFoundFormat(this.StartPath));
            return;
        }

        this.found.Clear();
        this.Results = [];
        var cts = this.cts = new CancellationTokenSource();
        this.IsSearching = true;

        var options = new SearchOptions
        {
            StartPath = this.StartPath,
            NameMask = this.NameMask,
            ContainingText = String.IsNullOrEmpty(this.ContainingText) ? null : this.ContainingText,
            CaseSensitive = this.CaseSensitive,
            Recursive = this.Recursive,
            IncludeHidden = this.IncludeHidden,
            DirectoriesOnly = this.FoldersOnly,
            MinSize = Int64.TryParse(this.MinSizeKb, out var kb) ? kb * 1024 : null
        };

        var progress = new Progress<SearchStatus>(s =>
        {
            if (!this.IsSearching)
                return; // a late report must not overwrite the final summary
            this.Status = String.IsNullOrEmpty(s.CurrentDirectory)
            ? L.Search_ProgressFormat(s.Scanned.ToString("N0"), s.Found.ToString("N0"))
            : L.Search_ProgressInFormat(s.Scanned.ToString("N0"), s.Found.ToString("N0"), s.CurrentDirectory);
        });

        var started = DateTime.Now;
        try
        {
            var lastFlush = Environment.TickCount64;
            await foreach (var entry in FileSearcher.SearchAsync(options, progress, cts.Token))
            {
                this.found.Add(new SearchResultItem(entry, this.StartPath));
                if (Environment.TickCount64 - lastFlush > 500 && this.found.Count <= MaxShown)
                {
                    lastFlush = Environment.TickCount64;
                    this.Results = this.found.ToList();
                }
            }
            this.Results = this.found.Take(MaxShown).ToList();

            var count = this.found.Count.ToString("N0");
            var seconds = (DateTime.Now - started).TotalSeconds.ToString("0.0");
            this.Status = this.found.Count > MaxShown
                ? L.Search_DoneTruncatedFormat(count, seconds, MaxShown.ToString("N0"))
                : L.Search_DoneFormat(count, seconds);
        }
        catch (OperationCanceledException)
        {
            this.Results = this.found.Take(MaxShown).ToList();
            this.Status = L.Search_StoppedFormat(this.found.Count.ToString("N0"));
        }
        catch (Exception ex)
        {
            this.Status = ex.Message;
        }
        finally
        {
            this.IsSearching = false;
        }
    }

    SearchResultItem? Current => this.SelectedResult as SearchResultItem ?? this.Results.FirstOrDefault();

    [RelayCommand]
    async Task GoToFile()
    {
        if (this.Current is not { } item)
            return;

        this.cts?.Cancel();
        await navigator.GoBack();
        await mediator.Publish(new GoToPathEvent(Path.GetDirectoryName(item.Entry.FullPath)!, item.Entry.Name));
    }

    [RelayCommand]
    Task ViewFile()
        => this.Current is { Entry.IsDirectory: false } item
            ? navigator.NavigateTo<ViewerViewModel>(vm => vm.FilePath = item.Entry.FullPath)
            : Task.CompletedTask;

    [RelayCommand]
    Task Close()
    {
        this.cts?.Cancel();
        return navigator.GoBack();
    }
}
