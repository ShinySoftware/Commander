namespace ShinyCommander.Services;

/// <summary>Modifier keys held right now - MAUI's tap events don't say.</summary>
[Flags]
public enum ModifierKeys
{
    None = 0,
    Shift = 1,
    /// <summary>⌘ on a Mac, Ctrl elsewhere.</summary>
    Primary = 2,
    Alt = 4
}

/// <summary>Optional - a head registers one; without it a click never extends the selection.</summary>
public interface IKeyModifiers
{
    /// <summary>Called on the UI thread from a click handler, so it reads the event being handled.</summary>
    ModifierKeys Current { get; }
}

/// <summary>
/// Native file drag and drop for a window: files dragged out of a panel (to Finder, the Desktop, a folder, the other
/// panel) and files dragged in from other apps. Optional - a head registers one where it's implemented.
/// </summary>
/// <remarks>
/// Window points are the platform's own coordinates and only ever go back to <see cref="Contains"/>, which is how the
/// shared code finds what is under the pointer without knowing them.
/// </remarks>
public interface IFileDragDrop
{
    /// <summary>Starts handling drags in the window. Dispose to stop.</summary>
    IDisposable? Attach(Window window, IFileDragDropHandler handler);

    /// <summary>Whether the window point is on the element, as it shows on screen (clipped by any scroller).</summary>
    bool Contains(VisualElement element, Point windowPoint);
}

/// <summary>The app's side of <see cref="IFileDragDrop"/>. Every call comes on the UI thread.</summary>
public interface IFileDragDropHandler
{
    /// <summary>The mouse went down at the point and was dragged: the files to drag from there, or none for no drag.</summary>
    IReadOnlyList<string> GetDragSource(Point windowPoint);

    /// <summary>A drag of ours ended somewhere outside the app - Finder may have moved the files away.</summary>
    void DragSourceEnded(IReadOnlyList<string> paths, DragOperations performed);

    /// <summary>Files are over the point: what a drop there would do (None refuses it).</summary>
    DragOperations DragOver(Point windowPoint, IReadOnlyList<string> paths, DragOperations allowed);

    /// <summary>The drag left the window, or was dropped and is done.</summary>
    void DragExited();

    /// <summary>The files were dropped at the point. False when nothing was done.</summary>
    bool Drop(Point windowPoint, IReadOnlyList<string> paths, DragOperations allowed);
}
