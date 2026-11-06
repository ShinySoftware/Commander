using ShinyCommander.Core.Search;
using Shiny.Mediator;

namespace ShinyCommander.ViewModels;

public sealed class SearchResultItem(FileEntry entry, string startPath)
{
    public FileEntry Entry { get; } = entry;
    public string Icon => FileIcons.For(this.Entry);
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

    public string StartText => this.IsSearching ? "Stop" : "Start";

    [ObservableProperty]
    public partial string Status { get; set; } = "Enter a mask and press Start (Enter)";

    [ObservableProperty]
    public partial object? SelectedResult { get; set; }

    public ObservableCollection<SearchResultItem> Results { get; } = [];

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
            await dialogs.Alert("Find files", $"Folder not found: {this.StartPath}");
            return;
        }

        this.Results.Clear();
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

        var progress = new Progress<SearchStatus>(s => this.Status = String.IsNullOrEmpty(s.CurrentDirectory)
            ? $"Scanned {s.Scanned:N0}, found {s.Found:N0}"
            : $"Scanned {s.Scanned:N0}, found {s.Found:N0}  •  {s.CurrentDirectory}");

        var started = DateTime.Now;
        try
        {
            var batch = new List<SearchResultItem>();
            var lastFlush = Environment.TickCount64;
            await foreach (var entry in FileSearcher.SearchAsync(options, progress, cts.Token))
            {
                batch.Add(new SearchResultItem(entry, this.StartPath));
                if (Environment.TickCount64 - lastFlush > 200)
                {
                    lastFlush = Environment.TickCount64;
                    foreach (var b in batch)
                        this.Results.Add(b);
                    batch.Clear();
                }
            }
            foreach (var b in batch)
                this.Results.Add(b);

            this.Status = $"{this.Results.Count:N0} found in {(DateTime.Now - started).TotalSeconds:0.0}s";
        }
        catch (OperationCanceledException)
        {
            this.Status = $"Stopped - {this.Results.Count:N0} found";
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
