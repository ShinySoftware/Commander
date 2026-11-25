namespace ShinyCommander.Views;

/// <summary>
/// Files dragged out of the panels and dropped into them. A drag starts on a row and carries the selection (the marked
/// rows, or the one dragged when it isn't marked); a drop goes into the folder row under the pointer, or else the
/// panel's folder - moved within a volume, copied onto another (⌥ copies, ⌘ moves), like Finder.
/// </summary>
public sealed class PanelDragDrop(IFileDragDrop platform, MainViewModel main, FilePanelView left, FilePanelView right) : IFileDragDropHandler
{
    // what's lit up under a drag
    FileItemViewModel? litRow;
    PanelViewModel? litPanel;

    record DropTarget(PanelViewModel Panel, FileItemViewModel? Row, string Directory, bool OnDisk);

#if DEBUG
    /// <summary>The live handler, for the UI test hooks (Testing/UiTestHooks.cs).</summary>
    public static PanelDragDrop? Current { get; private set; }

    public PanelDragDrop Register()
    {
        Current = this;
        return this;
    }
#endif

    public IReadOnlyList<string> GetDragSource(Point windowPoint)
    {
        var view = this.PanelAt(windowPoint);
        var panel = view?.BindingContext as PanelViewModel;
        var tab = panel?.ActiveTab;
        // only real files can leave the app - not entries inside an archive or on a server
        if (view == null || panel == null || tab == null || tab.IsArchive || tab.IsRemote)
            return [];

        var row = view.RowAt(x => platform.Contains(x, windowPoint));
        if (row == null || row.Entry.IsParentLink)
            return [];

        // Finder: dragging a selected row takes the selection, an unselected one is selected first and goes alone
        main.Activate(panel);
        if (!row.IsMarked)
            tab.SelectOnly(row);
        return tab.SelectedEntries.Select(x => x.FullPath).ToList();
    }

    public void DragSourceEnded(IReadOnlyList<string> paths, DragOperations performed)
    {
        if (performed != DragOperations.None)
            main.DraggedOut(paths);
    }

    public DragOperations DragOver(Point windowPoint, IReadOnlyList<string> paths, DragOperations allowed)
    {
        var target = this.TargetAt(windowPoint);
        var op = target == null ? DragOperations.None : DropRules.Choose(paths, target.Directory, allowed, target.OnDisk);
        this.Light(op == DragOperations.None ? null : target);
        return op;
    }

    public void DragExited() => this.Light(null);

    public bool Drop(Point windowPoint, IReadOnlyList<string> paths, DragOperations allowed)
    {
        var target = this.TargetAt(windowPoint);
        this.Light(null);
        var op = target == null ? DragOperations.None : DropRules.Choose(paths, target.Directory, allowed, target.OnDisk);
        return op != DragOperations.None && main.DropFiles(paths, target!.Directory, op == DragOperations.Move);
    }

    FilePanelView? PanelAt(Point windowPoint)
        => new[] { left, right }.FirstOrDefault(x => x.BindingContext is PanelViewModel && platform.Contains(x, windowPoint));

    // a folder row (".." included) takes the drop; anywhere else on the panel drops into the panel's folder
    DropTarget? TargetAt(Point windowPoint)
    {
        var view = this.PanelAt(windowPoint);
        if (view?.BindingContext is not PanelViewModel { ActiveTab: { } tab } panel)
            return null;

        var onDisk = !tab.IsArchive && !tab.IsRemote;
        var row = view.RowAt(x => platform.Contains(x, windowPoint));
        return row is { Entry.IsDirectory: true }
            ? new DropTarget(panel, row, row.Entry.FullPath, onDisk)
            : new DropTarget(panel, null, tab.Path, onDisk);
    }

    void Light(DropTarget? target)
    {
        var row = target?.Row;
        if (!ReferenceEquals(this.litRow, row))
        {
            if (this.litRow != null)
                this.litRow.IsDropTarget = false;
            if (row != null)
                row.IsDropTarget = true;
            this.litRow = row;
        }

        // the panel is outlined when the drop goes into its own folder rather than a row's
        var panel = target is { Row: null } ? target.Panel : null;
        if (!ReferenceEquals(this.litPanel, panel))
        {
            if (this.litPanel != null)
                this.litPanel.IsDropTarget = false;
            if (panel != null)
                panel.IsDropTarget = true;
            this.litPanel = panel;
        }
    }
}
