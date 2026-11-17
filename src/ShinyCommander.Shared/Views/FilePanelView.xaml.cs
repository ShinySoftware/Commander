using ShinyCommander.Core.Models;

namespace ShinyCommander.Views;

public partial class FilePanelView : ContentView
{
    PanelViewModel? panel;

    const double ThumbTileWidth = 128;

    bool toolbarStale;
    Page? hostPage;

    // the panel's root grid - the breadcrumb dropdown's layer joins it only while open
    readonly Grid crumbMenuHost;

    // the tab whose menu is open
    PanelTabViewModel? menuTab;

    public FilePanelView()
    {
        this.InitializeComponent();

        // even hidden, the layer's tap recognizers take real clicks meant for what's under it on AppKit (the up arrow
        // stopped working) - so it's only in the tree while the dropdown shows
        this.crumbMenuHost = (Grid)this.CrumbMenuLayer.Parent;
        this.crumbMenuHost.Children.Remove(this.CrumbMenuLayer);
        this.PanelRoot.Children.Remove(this.TabMenuLayer);

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
            this.panel.Tabs.CollectionChanged -= this.OnTabsChanged;
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
            SetId(this.PathEditButton, side + "PathEdit");
            SetId(this.PathCrumbStrip, side + "PathCrumbs");
            SetId(this.CrumbMenu, side + "CrumbMenu");
            SetId(this.TabStrip, side + "Tabs");
            SetId(this.NewTabButton, side + "NewTab");
            SetId(this.TabMenu, side + "TabMenu");
            SetId(this.TabMenuBackdrop, side + "TabMenuBackdrop");
            SetId(this.FilterEntry, side + "Filter");
            SetId(this.BackButton, side + "Back");
            SetId(this.ForwardButton, side + "Forward");
            SetId(this.UpButton, side + "Up");

            this.panel.ScrollRequested += this.OnScrollRequested;
            this.panel.PropertyChanged += this.OnPanelPropertyChanged;
            this.panel.Tabs.CollectionChanged += this.OnTabsChanged;
        }
    }

    // on AppKit the tab row keeps the width of a closed tab (the + stays where it was) until it's asked to measure again
    void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
            return;

        this.Dispatcher.Dispatch(() =>
        {
            ((IView)this.TabStrip).InvalidateMeasure();
            (this.TabStrip.Parent as IView)?.InvalidateMeasure();
        });
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

            case nameof(PanelViewModel.ToolbarItems):
                // changed from the Customize page, while this page is covered: on AppKit the path row doesn't
                // re-measure for the new buttons until something asks it to, so ask once the page is back
                this.toolbarStale = true;
                this.RemeasureToolbarIfStale();
                break;

            case nameof(PanelViewModel.IsActive) when this.panel?.IsActive == false:
                this.CloseCrumbMenu();
                this.CloseTabMenu();
                break;

            case nameof(PanelViewModel.PathCrumbs):
                this.CloseCrumbMenu();
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
                if (index >= count || this.panel == null)
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
        var details = this.panel is { IsDetails: true } ? items : null;
        var thumbs = this.panel is { IsThumbnails: true } ? items : null;
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

    void OnTabRightTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is not { } p || sender is not VisualElement part || part.BindingContext is not PanelTabViewModel tab)
            return;

        this.Main?.Activate(p);
        this.menuTab = tab;
        this.TabMenu.BindingContext = tab;
        this.RefreshTabMenu();

        // under the tab that was right-clicked (the first Border up from its icon or name), kept inside the panel
        var anchor = part;
        for (var parent = part.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is Border border)
            {
                anchor = border;
                break;
            }
        }
        var at = PositionIn(anchor, this.PanelRoot);
        var left = Math.Max(4, Math.Min(at.X, this.PanelRoot.Width - this.TabMenu.WidthRequest - 4));
        this.TabMenu.Margin = new Thickness(left, at.Y + anchor.Height + 2, 0, 0);
        if (!this.PanelRoot.Children.Contains(this.TabMenuLayer))
            this.PanelRoot.Children.Add(this.TabMenuLayer);
    }

    // the parts of the menu that depend on the tab's state - rebuilt as colours and icons are picked
    void RefreshTabMenu()
    {
        if (this.menuTab is not { } tab || this.Panel is not { } p)
            return;

        // the first tab is locked in place for good - nothing to lock or unlock
        this.TabMenuLock.Text = tab.IsLocked ? L.Tab_Unlock : L.Tab_Lock;
        this.TabMenuLock.IsVisible = !tab.IsFirst;
        var closable = tab.CanClose && p.Tabs.Count > 1;
        this.TabMenuClose.IsVisible = closable;
        this.TabMenuCloseLine.IsVisible = closable;
        BindableLayout.SetItemsSource(this.TabColorSwatches, TabColors.Choices(tab.ColorKey));
        BindableLayout.SetItemsSource(this.TabIconChoices, TabIcons.Choices(tab.IconKey, tab.AutomaticIcon));
    }

    void CloseTabMenu()
    {
        this.PanelRoot.Children.Remove(this.TabMenuLayer);
        this.menuTab = null;
    }

    void OnTabMenuDismiss(object? sender, TappedEventArgs e) => this.CloseTabMenu();

    void OnTabMenuLockTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && this.menuTab is { } tab)
            p.SetTabLocked(tab, !tab.IsLocked);
        this.CloseTabMenu();
    }

    void OnTabMenuRenameTapped(object? sender, TappedEventArgs e)
    {
        var tab = this.menuTab;
        this.CloseTabMenu();
        if (this.Panel is { } p && tab != null)
            _ = this.Main?.RenameTab(p, tab);
    }

    void OnTabMenuCloseTapped(object? sender, TappedEventArgs e)
    {
        var tab = this.menuTab;
        this.CloseTabMenu();
        if (this.Panel is { } p && tab != null)
            p.CloseTab(tab);
    }

    void OnTabColorTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && this.menuTab is { } tab && (sender as BindableObject)?.BindingContext is TabColorChoice choice)
        {
            p.EditTab(tab, x => x.ColorKey = choice.Key);
            this.RefreshTabMenu();
        }
    }

    void OnTabIconTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && this.menuTab is { } tab && (sender as BindableObject)?.BindingContext is TabIconChoice choice)
        {
            p.EditTab(tab, x => x.IconKey = choice.Key);
            this.RefreshTabMenu();
        }
    }

    void OnTabLockTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is PanelTabViewModel tab)
            p.SetTabLocked(tab, false);
    }

    void OnTabCloseTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is { } p && (sender as BindableObject)?.BindingContext is PanelTabViewModel tab)
            p.CloseTab(tab);
    }

    void OnNewTabClicked(object? sender, EventArgs e)
    {
        if (this.Panel is { } p)
        {
            this.Main?.Activate(p);
            _ = p.AddTabAsync(p.Path);
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


    void OnPathLabelTapped(object? sender, EventArgs e)
    {
        this.PathLabelBox.IsVisible = false;
        this.PathEntry.Opacity = 1;
        this.PathEntry.Focus();
        this.PathEntry.CursorPosition = this.PathEntry.Text?.Length ?? 0;
    }

    void OnCrumbTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is not { } p || (sender as BindableObject)?.BindingContext is not PathCrumb crumb)
            return;

        // the folder you're already in has nowhere to go - edit the path instead
        if (crumb.IsCurrent)
        {
            this.OnPathLabelTapped(sender, e);
            return;
        }
        this.Main?.Activate(p);
        _ = p.NavigateAsync(crumb.Path);
    }

    // deep paths overflow: keep the folder you're in, at the end of the trail, in view. On a size change rather than
    // when the crumbs change - until the new ones are laid out there's nothing new to scroll to
    void OnCrumbsSizeChanged(object? sender, EventArgs e)
    {
#if MACOS_APPKIT
        // "Never" isn't honoured when we scroll the crumbs ourselves - AppKit shows its scroller over them. Turned off
        // here, each time, as the handler puts it back when it maps the ScrollView again
        if (this.PathCrumbScroll.Handler?.PlatformView is AppKit.NSScrollView native)
            native.HasHorizontalScroller = false;
#endif
        _ = this.PathCrumbScroll.ScrollToAsync(Math.Max(0, this.PathCrumbStrip.Width - this.PathCrumbScroll.Width), 0, false);
    }

    async void OnCrumbChevronTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is not { } p || sender is not Label chevron || chevron.BindingContext is not PathCrumb crumb)
            return;

        this.Main?.Activate(p);
        var folders = await p.ListSubfoldersAsync(crumb.Path);
        this.CrumbMenuList.ItemsSource = folders;
        this.CrumbMenuList.IsVisible = folders.Count > 0;
        this.CrumbMenuEmpty.IsVisible = folders.Count == 0;

        // sized up front: a CollectionView left to measure itself inside a Start-aligned box comes out 0 tall on AppKit
        const double rowHeight = 24;
        this.CrumbMenuList.HeightRequest = Math.Min(folders.Count, 14) * rowHeight;

        // measured against the panel, not the layer - out of the tree, the layer has no size yet
        var host = this.crumbMenuHost;
        var at = PositionIn(chevron, host);
        var left = Math.Max(4, Math.Min(at.X, host.Width - this.CrumbMenu.WidthRequest - 4));
        this.CrumbMenu.Margin = new Thickness(left, at.Y + chevron.Height + 2, 0, 0);
        if (!host.Children.Contains(this.CrumbMenuLayer))
            host.Children.Add(this.CrumbMenuLayer);
    }

    void OnCrumbFolderTapped(object? sender, TappedEventArgs e)
    {
        if (this.Panel is not { } p || (sender as BindableObject)?.BindingContext is not PathCrumbFolder folder)
            return;

        this.CloseCrumbMenu();
        _ = p.NavigateAsync(folder.Path);
    }

    void OnCrumbFolderPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement row)
            row.BackgroundColor = ThemeColors.Get("CursorInactive");
    }

    void OnCrumbFolderPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement row)
            row.BackgroundColor = Colors.Transparent;
    }

    void OnCrumbMenuDismiss(object? sender, TappedEventArgs e) => this.CloseCrumbMenu();

    void CloseCrumbMenu()
    {
        this.crumbMenuHost.Children.Remove(this.CrumbMenuLayer);
        this.CrumbMenuList.ItemsSource = null;
    }

    /// <summary>Where <paramref name="element"/> sits inside <paramref name="ancestor"/>, allowing for scrolled ScrollViews on the way up.</summary>
    static Point PositionIn(VisualElement element, VisualElement ancestor)
    {
        double x = 0, y = 0;
        for (var e = element as Element; e != null && e != ancestor; e = e.Parent)
        {
            if (e is VisualElement v)
            {
                x += v.X + v.TranslationX;
                y += v.Y + v.TranslationY;
            }
            if (e is ScrollView scroll)
            {
                x -= scroll.ScrollX;
                y -= scroll.ScrollY;
            }
        }
        return new Point(x, y);
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
