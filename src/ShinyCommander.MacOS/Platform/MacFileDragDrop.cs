using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Shiny;
using ShinyCommander.Core.Operations;
using ShinyCommander.Services;

namespace ShinyCommander;

/// <summary>
/// The modifier keys of the last click. A MAUI tap fires only once the double-click interval has passed (the row also
/// has a double-tap), by when the event being handled is something else and the key may be up - so the keys are taken
/// from the mouse events themselves as they go by.
/// </summary>
[Singleton]
sealed class MacKeyModifiers : IKeyModifiers
{
    // a click's keys stay its own for about as long as AppKit waits for a second click, then the keyboard's own state
    static readonly TimeSpan ClickMemory = TimeSpan.FromSeconds(2);

    NSEventModifierMask lastClickFlags;
    long lastClickAt;

    // lives as long as the app (a singleton), so the monitor is never removed
    public MacKeyModifiers() => NSApplication.SharedApplication.InvokeOnMainThread(this.StartMonitor);

    void StartMonitor() => NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.LeftMouseDown | NSEventMask.LeftMouseUp, this.OnMouseEvent);

    public ModifierKeys Current
    {
        get
        {
            var current = NSApplication.SharedApplication.CurrentEvent;
            var flags = current?.Type is NSEventType.LeftMouseDown or NSEventType.LeftMouseUp
                ? current.ModifierFlags
                : Environment.TickCount64 - this.lastClickAt < ClickMemory.TotalMilliseconds ? this.lastClickFlags : NSEvent.CurrentModifierFlags;
            var result = ModifierKeys.None;
            if (flags.HasFlag(NSEventModifierMask.ShiftKeyMask))
                result |= ModifierKeys.Shift;
            if (flags.HasFlag(NSEventModifierMask.CommandKeyMask))
                result |= ModifierKeys.Primary;
            if (flags.HasFlag(NSEventModifierMask.AlternateKeyMask))
                result |= ModifierKeys.Alt;
            return result;
        }
    }

    NSEvent OnMouseEvent(NSEvent e)
    {
        this.lastClickFlags = e.ModifierFlags;
        this.lastClickAt = Environment.TickCount64;
        return e;
    }
}

/// <summary>
/// File drag and drop on AppKit: a drag session of file URLs out of the panels (Finder, the Desktop, the Dock's
/// Trash, the other panel), and a drop destination over the whole window for files coming in.
/// </summary>
[Singleton]
sealed class MacFileDragDrop : IFileDragDrop
{
    public IDisposable? Attach(Window window, IFileDragDropHandler handler)
        => window.Handler?.PlatformView is NSWindow native ? new MacDragDropWindow(native, handler) : null;

    public bool Contains(VisualElement element, Point windowPoint)
    {
        if (element.Handler?.PlatformView is not NSView view || view.Window == null || view.IsHiddenOrHasHiddenAncestor)
            return false;

        // inside the view, and inside what a scroll view leaves showing of it - a row scrolled out of sight never
        // matches. Both: MAUI's views don't clip, so on their own their visible rect runs past their bounds
        var local = view.ConvertPointFromView(new CGPoint(windowPoint.X, windowPoint.Y), null);
        return view.Bounds.Contains(local) && view.VisibleRect().Contains(local);
    }
}

/// <summary>One window's drag and drop: its drop view, its mouse monitor and the drag in flight.</summary>
sealed class MacDragDropWindow : IDisposable
{
    internal const string FileUrlType = "public.file-url";

    // a press has to travel this far before it becomes a drag - a click wobbles a point or two
    const double DragThreshold = 4;

    static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(1);

    readonly NSWindow native;
    readonly IFileDragDropHandler handler;
    readonly List<NSObject> observers = [];
    readonly NSObject? mouseMonitor;
    DragDropView? view;
    long lastCheck;
    bool disposed;

    // the press a drag may start from; null once it's been tried (or there was none)
    NSEvent? pressed;
    MacDragSource? source;

    public MacDragDropWindow(NSWindow native, IFileDragDropHandler handler)
    {
        this.native = native;
        this.handler = handler;
        this.EnsureInstalled();

        // MAUI can swap the content view (a page change); put the drop view back on top when it does
        this.observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidUpdateNotification, this.OnWindowUpdated, native));
        this.observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidBecomeKeyNotification, this.OnWindowBecameKey, native));
        this.mouseMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(
            NSEventMask.LeftMouseDown | NSEventMask.LeftMouseDragged | NSEventMask.LeftMouseUp,
            this.OnMouseEvent
        );
    }

    void OnWindowUpdated(NSNotification _)
    {
        var now = Environment.TickCount64;
        if (now - this.lastCheck < RecheckInterval.TotalMilliseconds)
            return;
        this.lastCheck = now;
        this.EnsureInstalled();
    }

    void OnWindowBecameKey(NSNotification _) => this.EnsureInstalled();

    /// <summary>
    /// The drop view becomes the window's content view and MAUI's content its subview: AppKit finds a drop's
    /// destination by hit testing and walking up the superviews, so it has to be above every view in the window.
    /// </summary>
    void EnsureInstalled()
    {
        if (this.disposed || this.native.ContentView is DragDropView)
            return;

        var content = this.native.ContentView;
        if (content == null)
            return;

        var drop = new DragDropView(this) { Frame = content.Frame, AutoresizesSubviews = true };
        this.native.ContentView = drop;
        content.Frame = drop.Bounds;
        content.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        drop.AddSubview(content);
        drop.RegisterForDraggedTypes([FileUrlType]);
        this.view = drop;
    }

    // --- dragging out ---------------------------------------------------------------------------

    NSEvent OnMouseEvent(NSEvent e)
    {
        if (e.Window != this.native)
            return e;

        switch (e.Type)
        {
            case NSEventType.LeftMouseDown:
                this.pressed = e;
                break;

            case NSEventType.LeftMouseUp:
                this.pressed = null;
                break;

            case NSEventType.LeftMouseDragged when this.pressed is { } down && Distance(down.LocationInWindow, e.LocationInWindow) >= DragThreshold:
                // one try per press: over anything but a file row the press stays with whatever it was on
                this.pressed = null;
                var started = this.BeginDrag(down.LocationInWindow, e);
                return started ? null! : e; // null swallows the event: the drag session has the mouse now
        }
        return e;
    }

    bool BeginDrag(CGPoint at, NSEvent e)
    {
        var paths = this.handler.GetDragSource(new Point(at.X, at.Y));
        if (paths.Count == 0 || this.view is not { } drop)
            return false;

        var origin = drop.ConvertPointFromView(at, null);
        var items = paths
            .Select((path, i) =>
            {
                var item = new NSPasteboardItem();
                item.SetStringForType(NSUrl.FromFilename(path).AbsoluteString!, FileUrlType);

                var icon = NSWorkspace.SharedWorkspace.IconForFile(path);
                icon.Size = new CGSize(32, 32);
                var dragging = new NSDraggingItem(item);
                // fanned out a little under the pointer; AppKit groups them into a pile once it's moving
                dragging.SetDraggingFrame(new CGRect(origin.X - 16 + i * 3, origin.Y - 16 - i * 3, 32, 32), icon);
                return dragging;
            })
            .ToArray();

        this.source = new MacDragSource(this, paths);
        var session = drop.BeginDraggingSession(items, e, this.source);
        session.AnimatesToStartingPositionsOnCancelOrFail = true;
        session.DraggingFormation = NSDraggingFormation.Pile;
        return true;
    }

    internal void DragSourceEnded(MacDragSource ended, NSDragOperation operation)
    {
        if (ReferenceEquals(this.source, ended))
            this.source = null;

        // a drop on one of our panels has been handled already, as a job of ours
        if (ended.DroppedInside)
            return;

        // Finder moves (or copies) the files itself; Trash is a move too
        var performed = operation.HasFlag(NSDragOperation.Move) || operation.HasFlag(NSDragOperation.Generic) || operation.HasFlag(NSDragOperation.Delete)
            ? DragOperations.Move
            : operation.HasFlag(NSDragOperation.Copy) ? DragOperations.Copy : DragOperations.None;
        this.handler.DragSourceEnded(ended.Paths, performed);
    }

    // --- dropping in ----------------------------------------------------------------------------

    internal NSDragOperation DraggingUpdated(INSDraggingInfo info)
    {
        var paths = ReadFiles(info);
        if (paths.Count == 0)
            return NSDragOperation.None;

        var mask = info.DraggingSourceOperationMask;
        var op = this.handler.DragOver(Location(info), paths, Allowed(mask));
        return ToNative(op, mask);
    }

    internal void DraggingExited() => this.handler.DragExited();

    internal bool PerformDragOperation(INSDraggingInfo info)
    {
        var paths = ReadFiles(info);
        if (paths.Count == 0)
            return false;

        if (info.DraggingSource is MacDragSource ours)
            ours.DroppedInside = true;
        return this.handler.Drop(Location(info), paths, Allowed(info.DraggingSourceOperationMask));
    }

    static Point Location(INSDraggingInfo info) => new(info.DraggingLocation.X, info.DraggingLocation.Y);

    // the source's mask after AppKit applied the keys held: ⌥ leaves Copy, ⌘ leaves Generic (a move)
    static DragOperations Allowed(NSDragOperation mask)
    {
        var allowed = DragOperations.None;
        if (mask.HasFlag(NSDragOperation.Copy))
            allowed |= DragOperations.Copy;
        if (mask.HasFlag(NSDragOperation.Move) || mask.HasFlag(NSDragOperation.Generic))
            allowed |= DragOperations.Move;
        return allowed;
    }

    // the badge AppKit draws: none for a move, + for a copy - and it has to be one the source offered
    static NSDragOperation ToNative(DragOperations op, NSDragOperation mask) => op switch
    {
        DragOperations.Copy => NSDragOperation.Copy,
        DragOperations.Move => mask.HasFlag(NSDragOperation.Move) ? NSDragOperation.Move : NSDragOperation.Generic,
        _ => NSDragOperation.None
    };

    static IReadOnlyList<string> ReadFiles(INSDraggingInfo info)
    {
        var items = info.DraggingPasteboard?.PasteboardItems ?? [];
        return items
            .Select(x => x.GetStringForType(FileUrlType))
            .Where(x => !String.IsNullOrEmpty(x))
            .Select(x => NSUrl.FromString(x!)?.FilePathUrl?.Path)
            .Where(x => !String.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToList();
    }

    static double Distance(CGPoint a, CGPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        foreach (var observer in this.observers)
            NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        this.observers.Clear();
        if (this.mouseMonitor != null)
            NSEvent.RemoveMonitor(this.mouseMonitor);
        this.view?.UnregisterDraggedTypes();
        this.view = null;
    }
}

/// <summary>A drag of our files - what AppKit asks the source about it, and how it ended.</summary>
sealed class MacDragSource(MacDragDropWindow owner, IReadOnlyList<string> paths) : NSObject, INSDraggingSource
{
    public IReadOnlyList<string> Paths { get; } = paths;

    /// <summary>Set when one of our own panels took the drop.</summary>
    public bool DroppedInside { get; set; }

    // Finder picks a move within a volume and a copy across volumes from these; Trash takes Delete
    [Export("draggingSession:sourceOperationMaskForDraggingContext:")]
    public NSDragOperation SourceOperationMask(NSDraggingSession session, NSDraggingContext context)
        => NSDragOperation.Copy | NSDragOperation.Move | NSDragOperation.Generic
            | (context == NSDraggingContext.OutsideApplication ? NSDragOperation.Delete : NSDragOperation.None);

    [Export("draggingSession:endedAtPoint:operation:")]
    public void Ended(NSDraggingSession session, CGPoint screenPoint, NSDragOperation operation) => owner.DragSourceEnded(this, operation);
}

/// <summary>
/// The window's content view, standing in for MAUI's so it's on the superview chain above every view. The dragging
/// destination methods are exported by selector: the bindings keep them on a separate model class, not NSView.
/// </summary>
sealed class DragDropView(MacDragDropWindow owner) : NSView
{
    public override bool IsOpaque => false;

    [Export("draggingEntered:")]
    public NSDragOperation DraggingEntered(NativeHandle sender) => With(sender, owner.DraggingUpdated, NSDragOperation.None);

    [Export("draggingUpdated:")]
    public NSDragOperation DraggingUpdated(NativeHandle sender) => With(sender, owner.DraggingUpdated, NSDragOperation.None);

    [Export("draggingExited:")]
    public void DraggingExited(NativeHandle sender) => owner.DraggingExited();

    [Export("prepareForDragOperation:")]
    public bool PrepareForDragOperation(NativeHandle sender) => true;

    [Export("performDragOperation:")]
    public bool PerformDragOperation(NativeHandle sender) => With(sender, owner.PerformDragOperation, false);

    [Export("concludeDragOperation:")]
    public void ConcludeDragOperation(NativeHandle sender) => owner.DraggingExited();

    static T With<T>(NativeHandle sender, Func<INSDraggingInfo, T> work, T fallback)
        => Runtime.GetINativeObject<INSDraggingInfo>(sender, false) is { } info ? work(info) : fallback;
}
