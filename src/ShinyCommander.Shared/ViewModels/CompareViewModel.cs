using ShinyCommander.Core.Compare;
using Shiny.Mediator;

namespace ShinyCommander.ViewModels;

public partial class CompareRowViewModel(CompareItem item, bool friendly) : ObservableObject
{
    public CompareItem Item { get; } = item;
    public string RelativePath => this.Item.RelativePath + (this.Item.IsDirectory ? Path.DirectorySeparatorChar.ToString() : String.Empty);
    public string LeftSize => this.Item.Left is { IsDirectory: false } l ? SizeFormatter.Format(l.Size, friendly) : this.Item.Left != null ? "<DIR>" : String.Empty;
    public string RightSize => this.Item.Right is { IsDirectory: false } r ? SizeFormatter.Format(r.Size, friendly) : this.Item.Right != null ? "<DIR>" : String.Empty;
    public string LeftDate => this.Item.Left?.Modified.ToString("yyyy-MM-dd HH:mm") ?? String.Empty;
    public string RightDate => this.Item.Right?.Modified.ToString("yyyy-MM-dd HH:mm") ?? String.Empty;

    public string StateText => this.Item.State switch
    {
        CompareState.Equal => "=",
        CompareState.LeftNewer => L.Compare_StateLeftNewer,
        CompareState.RightNewer => L.Compare_StateRightNewer,
        CompareState.LeftOnly => L.Compare_StateLeftOnly,
        CompareState.RightOnly => L.Compare_StateRightOnly,
        _ => "≠"
    };

    public Shiny.Maui.Controls.PillType StatePill => this.Item.State switch
    {
        CompareState.Equal => Shiny.Maui.Controls.PillType.Success,
        CompareState.LeftOnly or CompareState.RightOnly => Shiny.Maui.Controls.PillType.Info,
        CompareState.Different => Shiny.Maui.Controls.PillType.Critical,
        _ => Shiny.Maui.Controls.PillType.Warning
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    public partial SyncAction Action { get; set; } = item.DefaultAction;

    public string ActionText => this.Action switch
    {
        SyncAction.CopyToRight => "→",
        SyncAction.CopyToLeft => "←",
        _ => "·"
    };

    [RelayCommand]
    void CycleAction()
    {
        // only offer directions that have a source
        var options = new List<SyncAction> { SyncAction.None };
        if (this.Item.Left != null) options.Add(SyncAction.CopyToRight);
        if (this.Item.Right != null) options.Add(SyncAction.CopyToLeft);
        var i = options.IndexOf(this.Action);
        this.Action = options[(i + 1) % options.Count];
    }
}

/// <summary>"Synchronize directories" - compare two trees and copy the differences either way.</summary>
[ShellMap<ComparePage>("Compare")]
public partial class CompareViewModel(FileOperationService ops, AppSettings settings, IAppDialogs dialogs, INavigator navigator, IMediator mediator) : ObservableObject
{
    List<CompareRowViewModel> all = [];
    CancellationTokenSource? cts;

    [ShellProperty]
    [ObservableProperty]
    public partial string LeftPath { get; set; } = String.Empty;

    [ShellProperty]
    [ObservableProperty]
    public partial string RightPath { get; set; } = String.Empty;

    [ObservableProperty]
    public partial bool Recursive { get; set; } = true;

    [ObservableProperty]
    public partial bool CompareContent { get; set; }

    [ObservableProperty]
    public partial bool IgnoreDate { get; set; }

    [ObservableProperty]
    public partial string Mask { get; set; } = "*";

    [ObservableProperty]
    public partial bool ShowEqual { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = L.Compare_PressCompare;

    [ObservableProperty]
    public partial object? SelectedRow { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<CompareRowViewModel> Rows { get; set; } = [];

    partial void OnShowEqualChanged(bool value) => this.ApplyFilter();

    void ApplyFilter()
    {
        this.Rows = new ObservableCollection<CompareRowViewModel>(this.ShowEqual ? this.all : this.all.Where(x => x.Item.State != CompareState.Equal));
        var toRight = this.all.Count(x => x.Action == SyncAction.CopyToRight);
        var toLeft = this.all.Count(x => x.Action == SyncAction.CopyToLeft);
        this.Status = L.Compare_StatusFormat(this.all.Count, this.all.Count(x => x.Item.State == CompareState.Equal), toRight, toLeft);
    }

    [RelayCommand]
    async Task Compare()
    {
        this.cts?.Cancel();
        var cts = this.cts = new CancellationTokenSource();
        this.IsBusy = true;
        try
        {
            var items = await DirectoryComparer.CompareAsync(this.LeftPath, this.RightPath, new CompareOptions
            {
                Recursive = this.Recursive,
                CompareContent = this.CompareContent,
                IgnoreDate = this.IgnoreDate,
                Mask = this.Mask,
                IncludeHidden = settings.ShowHidden
            }, cts.Token);

            this.all = items.Select(x => new CompareRowViewModel(x, settings.FriendlySizes)).ToList();
            this.ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await dialogs.Alert(L.Compare_Compare, ex.Message);
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    [RelayCommand]
    void CycleSelected() => (this.SelectedRow as CompareRowViewModel)?.CycleActionCommand.Execute(null);

    [RelayCommand]
    async Task Synchronize()
    {
        var work = this.all.Where(x => x.Action != SyncAction.None).ToList();
        if (work.Count == 0)
        {
            await dialogs.Toast(L.Compare_NothingToSync);
            return;
        }

        var ok = await dialogs.Confirm(L.Compare_Synchronize, L.Compare_ConfirmSyncFormat(work.Count(x => x.Action == SyncAction.CopyToRight), work.Count(x => x.Action == SyncAction.CopyToLeft)), L.Compare_Synchronize);
        if (!ok)
            return;

        this.IsBusy = true;
        var errors = new List<string>();
        try
        {
            foreach (var row in work)
            {
                var (source, targetRoot) = row.Action == SyncAction.CopyToRight
                    ? (row.Item.Left!, this.RightPath)
                    : (row.Item.Right!, this.LeftPath);

                this.Status = L.Compare_CopyingFormat(row.Item.RelativePath);
                var targetDir = Path.Combine(targetRoot, Path.GetDirectoryName(row.Item.RelativePath) ?? String.Empty);
                var result = await ops.CopyAsync([source], new OperationTarget(targetDir), (_, _) => Task.FromResult(ConflictAction.Overwrite));
                errors.AddRange(result.Errors);
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        finally
        {
            this.IsBusy = false;
        }

        await mediator.Publish(new FileSystemChangedEvent([this.LeftPath, this.RightPath]));
        if (errors.Count > 0)
            await dialogs.Alert(L.Compare_SyncErrors, String.Join("\n", errors.Take(15)));
        else
            await dialogs.Toast(L.Compare_Synchronized, ToastKind.Success);

        await this.Compare();
    }

    [RelayCommand]
    Task Close()
    {
        this.cts?.Cancel();
        return navigator.GoBack();
    }
}
