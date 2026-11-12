using ShinyCommander.Core.Models;

namespace ShinyCommander.Views;

public partial class FilePanelView : ContentView
{
    PanelViewModel? panel;

    const double ThumbTileWidth = 128;

    bool toolbarStale;
    Page? hostPage;

    public FilePanelView()
    {
        this.InitializeComponent();
        this.Loaded += (_, _) =>
        {
            if (this.hostPage != null)
                return;
            Element? e = this;
            while (e != null && e is not Page)
                e = e.Parent;
            this.hostPage = e as Page;
            if (this.hostPage != null)
                this.hostPage.Appearing += (_, _) => this.RemeasureToolbarIfStale();
        };
    }

    void RemeasureToolbarIfStale()
    {
        if (!this.toolbarStale || this.hostPage is not { IsLoaded: true })
            return;

        this.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
        {
            this.toolbarStale = false;
            ((IView)this.ToolbarStrip).InvalidateMeasure();
            (this.ToolbarStrip.Parent as IView)?.InvalidateMeasure();
        });
    }

    PanelViewModel? Panel => this.BindingContext as PanelViewModel;

    MainViewModel? Main
    {
        get
        {
            Element? e = this;
            while (e != null && e is not Page)
                e = e.Parent;
            return (e as Page)?.BindingContext as MainViewModel;
        }
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (this.panel != null)
        {
            this.panel.ScrollRequested -= this.OnScrollRequested;
            this.panel.PropertyChanged -= this.OnPanelPropertyChanged;
        }
        this.panel = this.Panel;
        this.WatchTab(this.panel?.ActiveTab);
        this.UpdateListSources();
        if (this.panel != null)
        {
            // stable ids for DevFlow / UI automation
            var side = this.panel.Side.ToString();
            SetId(this, side + "Panel");
            SetId(this.FolderTree, side + "Tree");
            SetId(this.TreeSplitter, side + "TreeSplitter");
            SetId(this.ThumbList, side + "Thumbnails");
            SetId(this.FileList, side + "Files");
            SetId(this.PathEntry, side + "Path");
            SetId(this.PathLabel, side + "PathLabel");
            SetId(this.FilterEntry, side + "Filter");

            this.panel.ScrollRequested += this.OnScrollRequested;
            this.panel.PropertyChanged += this.OnPanelPropertyChanged;
        }
    }

    void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PanelViewModel.ShowQuickFilter) when this.panel?.ShowQuickFilter == true:
                this.Dispatcher.Dispatch(() => this.FilterEntry.Focus());
                break;

            case nameof(PanelViewModel.ViewMode):
                this.UpdateListSources();
                this.UpdateThumbnailSpan(this.ThumbList.Width);
                break;

            case nameof(PanelViewModel.IsQuickView):
                this.UpdateListSources();
                break;

            case nameof(PanelViewModel.ToolbarItems):
                // changed from the Customize page, while this page is covered: on AppKit the path row doesn't
                // re-measure for the new buttons until something asks it to, so ask once the page is back
                this.toolbarStale = true;
                this.RemeasureToolbarIfStale();
                break;

            case nameof(PanelViewModel.Path):
            case nameof(PanelViewModel.ShowTree) when this.panel?.ShowTree == true:
                this.RevealInTree(this.panel?.Path);
                break;

            case nameof(PanelViewModel.ActiveTab):
                this.WatchTab(this.panel?.ActiveTab);
                this.UpdateListSources();
                break;

        }
    }

    void OnScrollRequested(object? sender, int index)
    {
        if (index < 0)
            return;

        // let the CollectionView pick up a freshly swapped ItemsSource first
        this.Dispatcher.Dispatch(() =>
        {
            try
            {
                var count = this.panel?.ActiveTab?.Items.Count ?? 0;
                if (index >= count || this.panel == null || this.panel.IsQuickView)
                    return;

                var list = this.panel.IsThumbnails ? this.ThumbList : this.FileList;
                // already on screen? then leave the list alone - a ScrollTo makes it recompute its visible rows
                var (first, last, source) = this.visibleRange;
                if (ReferenceEquals(source, list) && last > first && index > first && index < last)
                    return;
                list.ScrollTo(index, position: ScrollToPosition.MakeVisible, animate: false);
            }
            catch
            {
                // ScrollTo can race a collection swap on some backends
            }
        });
    }

    PanelTabViewModel? watchedTab;

    void WatchTab(PanelTabViewModel? tab)
    {
        if (this.watchedTab != null)
            this.watchedTab.PropertyChanged -= this.OnTabPropertyChanged;
        this.watchedTab = tab;
        if (tab != null)
            tab.PropertyChanged += this.OnTabPropertyChanged;
    }

    void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelTabViewModel.Items))
            this.UpdateListSources();
    }

    /// <summary>Only the list that's showing has items; the other is emptied so it holds no cells.</summary>
    void UpdateListSources()
    {
        this.visibleRange = (-1, -1, null);
        var items = this.panel?.ActiveTab?.Items;
        var details = this.panel is { ShowDetailsList: true } ? items : null;
        var thumbs = this.panel is { ShowThumbnailList: true } ? items : null;
        if (!ReferenceEquals(this.FileList.ItemsSource, details))
            this.FileList.ItemsSource = details;
        if (!ReferenceEquals(this.ThumbList.ItemsSource, thumbs))
            this.ThumbList.ItemsSource = thumbs;
    }

    // AutomationId can only be set once, and the binding context can change more than once
    static void SetId(Element element, string id)
    {
        if (element.AutomationId == null)
            element.AutomationId = id;
    }

    (int First, int Last, object? Source) visibleRange = (-1, -1, null);

    void OnListScrolled(object? sender, ItemsViewScrolledEventArgs e)
        => this.visibleRange = (e.FirstVisibleItemIndex, e.LastVisibleItemIndex, sender);

    void OnFileListSizeChanged(object? sender, EventArgs e)
    {
        if (this.panel != null && this.FileList.Width > 0)
            this.panel.Columns.AvailableWidth = this.FileList.Width;
    }

    void OnThumbListSizeChanged(object? sender, EventArgs e) => this.UpdateThumbnailSpan(this.ThumbList.Width);

    void UpdateThumbnailSpan(double width)
    {
        if (width <= 0)
            return;

        var span = Math.Max(1, (int)(width / ThumbTileWidth));
        if (this.ThumbLayout.Span != span)
            this.ThumbLayout.Span = span;
        if (this.panel != null)
            this.panel.ThumbnailSpan = span;
    }

    double splitterStart;

    void OnTreeSplitterPan(object? sender, PanUpdatedEventArgs e)
    {
        if (this.panel == null)
            return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                this.splitterStart = this.panel.TreeWidth;
                break;

            case GestureStatus.Running:
                var max = Math.Max(160, this.Width * 0.7);
                this.panel.TreeWidth = Math.Clamp(this.splitterStart + e.TotalX, 120, max);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                this.panel.SaveTreeWidth();
                break;
        }
    }

    CancellationTokenSource? revealCts;

    /// <summary>The panel's tree follows the panel: expand down to its folder, highlight it and scroll it into view.</summary>
    async void RevealInTree(string? path)
    {
        if (this.panel is not { ShowTree: true } p || String.IsNullOrEmpty(path))
            return;

        this.revealCts?.Cancel();
        var cts = this.revealCts = new CancellationTokenSource();
        try
        {
            var row = await p.Tree.RevealAsync(path, cts.Token);
            if (row == null || cts.IsCancellationRequested)
                return;

            p.TreePath = row.Path;
            this.Dispatcher.Dispatch(() =>
            {
                try { this.FolderTree.ScrollTo(row, position: ScrollToPosition.MakeVisible, animate: false); } catch { }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShinyCommander] tree reveal failed: {ex}");
        }
    }

    void OnTreeRowTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is FolderRow row)
        {
            p.Tree.Selected = row;
            this.Main?.Activate(p);
            _ = p.NavigateAsync(row.Path);
            this.Main?.RequestCommandLineFocus();
        }
    }

    void OnTreeChevronTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is FolderRow row)
            _ = p.Tree.ToggleAsync(row);
    }

    void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is FileItemViewModel item)
            this.Main?.ClickItem(p, item, false);
    }

    void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is FileItemViewModel item)
            this.Main?.ClickItem(p, item, true);
    }

    void OnRowRightTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is FileItemViewModel item)
            this.Main?.RightClickItem(p, item);
    }

    void OnTabTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is PanelTabViewModel tab)
        {
            this.Main?.Activate(p);
            p.ActiveTab = tab;
        }
    }

    void OnToolbarItemClicked(object? sender, EventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is PaneToolbarItem item)
            this.Main?.RunToolbarCommand(p, item.Command.Key);
    }

    void OnToolbarRightTapped(object? sender, TappedEventArgs e) => this.Main?.CustomizeToolbarCommand.Execute(null);

    void OnDriveTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is DriveEntry drive)
        {
            this.Main?.Activate(p);
            _ = p.NavigateAsync(drive.RootPath);
        }
    }


    void OnPathLabelTapped(object? sender, TappedEventArgs e)
    {
        this.PathLabelBox.IsVisible = false;
        this.PathEntry.Opacity = 1;
        this.PathEntry.Focus();
        this.PathEntry.CursorPosition = this.PathEntry.Text?.Length ?? 0;
    }

    void OnPathFocused(object? sender, FocusEventArgs e)
    {
        this.PathLabelBox.IsVisible = false;
        this.PathEntry.Opacity = 1;
        if (this.Main is { } main && this.Panel is { } p)
        {
            main.Activate(p);
            main.IsPathEditing = true;
        }
    }

    void OnPathUnfocused(object? sender, FocusEventArgs e)
    {
        this.PathLabelBox.IsVisible = true;
        this.PathEntry.Opacity = 0;
        if (this.Main is { } main)
            main.IsPathEditing = false;

        // restore the real path if the user wandered off mid-edit
        if (this.Panel is { } p)
            p.PathText = p.Path;
    }

    void OnPathCompleted(object? sender, EventArgs e)
    {
        if (this.Panel is { } p)
        {
            var text = p.PathText;
            this.PathEntry.Unfocus();
            this.PathLabelBox.IsVisible = true;
            this.PathEntry.Opacity = 0;
            _ = p.NavigateAsync(text);
            this.Main?.RequestCommandLineFocus();
        }
    }

    void OnFilterFocused(object? sender, FocusEventArgs e)
    {
        if (this.Main is { } main)
            main.IsFilterEditing = true;
    }

    void OnFilterUnfocused(object? sender, FocusEventArgs e)
    {
        if (this.Main is { } main)
            main.IsFilterEditing = false;
    }
}
