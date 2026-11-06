using ShinyCommander.Core.Models;

namespace ShinyCommander.Views;

public partial class FilePanelView : ContentView
{
    PanelViewModel? panel;

    const double ThumbTileWidth = 128;

    public FilePanelView()
    {
        this.InitializeComponent();
        this.FolderTree.ChildrenLoader = item => FolderNode.LoadChildrenAsync(item, false);
        this.FolderTree.HasChildrenSelector = _ => true;
        this.FolderTree.ItemsSource = FolderNode.Roots().ToList();
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
            SetId(this.ViewModeButton, side + "ViewMode");
            SetId(this.TreeButton, side + "TreeToggle");
            SetId(this.FolderTree, side + "Tree");
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

    void OnTreeItemSelected(object? sender, Shiny.Maui.Controls.Tree.TreeItemEventArgs e)
    {
        if (this.Panel is { } p && e.Item is FolderNode node)
        {
            this.Main?.Activate(p);
            _ = p.NavigateAsync(node.Path);
            this.Main?.RequestCommandLineFocus();
        }
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
        this.PathEntry.Focus();
        this.PathEntry.CursorPosition = this.PathEntry.Text?.Length ?? 0;
    }

    void OnPathFocused(object? sender, FocusEventArgs e)
    {
        this.PathLabelBox.IsVisible = false;
        if (this.Main is { } main && this.Panel is { } p)
        {
            main.Activate(p);
            main.IsPathEditing = true;
        }
    }

    void OnPathUnfocused(object? sender, FocusEventArgs e)
    {
        this.PathLabelBox.IsVisible = true;
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
