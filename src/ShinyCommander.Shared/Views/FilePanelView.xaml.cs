using ShinyCommander.Core.Models;
using Shiny.Maui.Controls.Tree;

namespace ShinyCommander.Views;

public partial class FilePanelView : ContentView
{
    PanelViewModel? panel;

    public FilePanelView()
    {
        this.InitializeComponent();
        this.FolderTree.ChildrenLoader = item => FolderNode.LoadChildrenAsync(item, false);
        this.FolderTree.HasChildrenSelector = _ => true;
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
        if (this.panel != null)
        {
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

            case nameof(PanelViewModel.ShowTree) when this.panel?.ShowTree == true && this.FolderTree.ItemsSource == null:
                this.FolderTree.ItemsSource = FolderNode.Roots().ToList();
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
                if (index < count)
                    this.FileList.ScrollTo(index, position: ScrollToPosition.MakeVisible, animate: false);
            }
            catch
            {
                // ScrollTo can race a collection swap on some backends
            }
        });
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

    void OnTreeItemSelected(object? sender, TreeItemEventArgs e)
    {
        if (this.Panel is { } p && e.Item is FolderNode node)
        {
            this.Main?.Activate(p);
            _ = p.NavigateAsync(node.Path);
        }
    }

    void OnPathFocused(object? sender, FocusEventArgs e)
    {
        if (this.Main is { } main && this.Panel is { } p)
        {
            main.Activate(p);
            main.IsPathEditing = true;
        }
    }

    void OnPathUnfocused(object? sender, FocusEventArgs e)
    {
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
