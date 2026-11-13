#if DEBUG && SHINYCOMMANDER_DEVFLOW
using System.Reflection;
using System.Text.Json;
using ICommand = System.Windows.Input.ICommand;
using Microsoft.Maui.DevFlow.Agent.Core;
using Shiny.Controls.Keyboard;

namespace ShinyCommander.Testing;

/// <summary>
/// DevFlow agent tools for the UI test suite, under the <c>org.shiny.commander</c> extension namespace:
/// <c>maui devflow extensions call org.shiny.commander &lt;tool&gt; '{json}'</c>.
/// <para>
/// DevFlow can tap and fill, but it cannot press keys - and ShinyCommander is a keyboard program. <c>key</c> feeds
/// real key strokes into Shiny's shortcut engine (the same entry point the AppKit/GTK key sources use), so a test
/// exercises the actual keymap: gesture parsing, CanExecute hand-back to the command line, TextInput rules.
/// Every mutating tool waits for the app to settle (listings loaded, operations finished) and returns the state.
/// </para>
/// </summary>
public static class UiTestHooks
{
    const string Namespace = "org.shiny.commander";

    public static void Register(AgentOptions options)
    {
        if (!UiTestMode.IsEnabled)
            return;

        var ext = options.RegisterExtension(Namespace, "ShinyCommander UI test hooks", 1);
        Map(ext, "state", "Panels, cursor, marks, dialogs and page - the whole observable app state", (_, _) => Task.FromResult<object>(true));
        Map(ext, "diag", "Turn the expensive state diagnostics (native view counts) on or off: { on = true }", (_, a) =>
        {
            diagnostics = !(a.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.False);
            return Task.FromResult<object>(true);
        });
        Map(ext, "key", "Press a gesture through Shiny's shortcut engine: { gesture, textFocused = true }", Key);
        Map(ext, "type", "Replace the command line text: { text }", TypeText);
        Map(ext, "navigate", "Navigate a panel: { side = active, path }", Navigate);
        Map(ext, "cursor", "Activate a panel and put its cursor on an item: { side = active, name }", Cursor);
        Map(ext, "mark", "Mark items by name in the active panel: { names: [] }", Mark);
        Map(ext, "command", "Execute a MainViewModel command by name (without the Command suffix): { name, parameter }", Command);
        Map(ext, "answer", "Queue dialog answers: { answers: [] }", Answer);
        Map(ext, "reset", "Known state for the next test: { left, right }", Reset);
        Map(ext, "hold_job", "Start a background job that runs until stopped (or { seconds = 30 }) - Settle doesn't wait for it", HoldJob);
        Map(ext, "job", "Act on a job in the jobs pane, newest first: { index = 0, action = stop|goto|dismiss|errors|clear }", JobAction);
        Map(ext, "close_window", "Press the main window's close button (macOS) - the quit guard may stop it", CloseWindow);
        Map(ext, "culture", "Switch the UI language live: { name = \"fr\" } - text already on screen stays; new pages and dialogs use it. Reset goes back to en", SetCulture);
        Map(ext, "use_trash", "Deletes go to the (test) trash until reset: { on = true }", (_, a) =>
        {
            Service<AppSettings>().UseTrash = !(a.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.False);
            return Task.FromResult<object>(true);
        });
        Map(ext, "click_at", "A real mouse click (macOS): mouse down/up NSEvents through the window at page point { x, y } - AppKit hit-testing and all. Returns the native view under it (hit) and the MAUI elements behind it (element)", ClickAt);
        Map(ext, "real_dialogs", "Show the real dialogs instead of answering from the queue: { on = true }", RealDialogs);
        ext.MapTool("state_now", "The state without waiting for the app to settle (while a real dialog is open)", "POST", "state_now",
            async _ => HttpResponse.Json(await MainThread.InvokeOnMainThreadAsync(() => new { result = (object)true, state = Snapshot(MainViewModel.Current!) })));
    }

    static void Map(AgentExtension ext, string name, string description, Func<MainViewModel, JsonElement, Task<object>> action)
        => ext.MapTool(name, description, "POST", name, async request =>
        {
            try
            {
                using var doc = JsonDocument.Parse(String.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                var args = doc.RootElement.Clone();
                var result = await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var main = MainViewModel.Current ?? throw new InvalidOperationException("MainViewModel not created yet");
                    var r = await action(main, args);
                    await Settle(main);
                    return new { result = r, state = Snapshot(main) };
                });
                return HttpResponse.Json(result);
            }
            catch (Exception ex)
            {
                return HttpResponse.Error($"{name}: {ex.GetType().Name}: {ex.Message}", 500, details: ex.ToString());
            }
        });

    static T Service<T>() where T : notnull => IPlatformApplication.Current!.Services.GetRequiredService<T>();

    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static PanelViewModel Panel(MainViewModel main, JsonElement args) => Str(args, "side")?.ToLowerInvariant() switch
    {
        "left" => main.Left,
        "right" => main.Right,
        _ => main.ActivePanel
    };

    /// <summary>Waits until both listings are loaded, no operation is running and the quick view has loaded.</summary>
    static async Task Settle(MainViewModel main, int timeoutMs = 20_000)
    {
        await Task.Delay(30);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var busy = (main.IsOperationRunning && !OnlyHeldJobs(main))
                || main.IsCommandRunning
                || main.Left.ActiveTab?.IsLoading == true
                || main.Right.ActiveTab?.IsLoading == true
                || main.Left.QuickView.IsLoading
                || main.Right.QuickView.IsLoading;
            if (!busy)
                break;
            await Task.Delay(25);
        }
        await Task.Delay(30); // let dispatched follow-ups (cursor scroll, quick view debounce) run
    }

    const string HoldTitle = "Test hold";

    // a hold_job runs until a test stops it - waiting for it would only time out
    static bool OnlyHeldJobs(MainViewModel main)
    {
        var running = main.Jobs.Jobs.Where(x => x.IsRunning).ToList();
        return running.Count > 0 && running.Count == main.Jobs.PendingCount && running.All(x => x.Title == HoldTitle);
    }

    // --- tools ----------------------------------------------------------------------------------

    static KeyboardShortcutEngine Engine
        => (KeyboardShortcutEngine)Type.GetType("Shiny.Maui.Controls.KeyboardShortcutManager, Shiny.Maui.Controls", true)!
            .GetProperty("Engine", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    static async Task<object> Key(MainViewModel main, JsonElement args)
    {
        var gesture = KeyGesture.Parse(Str(args, "gesture") ?? throw new ArgumentException("gesture is required"));
        // the command line normally has focus in ShinyCommander, so strokes arrive "while typing" unless told otherwise
        var textFocused = !(args.TryGetProperty("textFocused", out var tf) && tf.ValueKind == JsonValueKind.False);
        var engine = Engine;
        var window = Application.Current?.Windows.FirstOrDefault();
        var handled = false;

        foreach (var chord in gesture.Chords)
        {
            var mods = chord.Modifiers.Resolve(engine.Platform);
            var (code, ch) = chord.Kind switch
            {
                KeyKind.Letter => ("Key" + chord.Key, mods.HasFlag(KeyModifiers.Shift) ? chord.Key : chord.Key.ToLowerInvariant()),
                KeyKind.Digit => ("Digit" + chord.Key, chord.Key),
                KeyKind.Character => ((string?)null, chord.Key),
                _ => (chord.Key, chord.Key == "Space" ? " " : (string?)null)
            };
            var down = new KeyStroke { Code = code, Character = ch, Modifiers = mods, IsTextInputFocused = textFocused };
            handled |= engine.Process(down, window);
            engine.Process(down with { IsKeyUp = true }, window);
            await Task.Delay(10);
        }

        // a shortcut's command may be async (copy, navigate) - give it a moment to start before settling
        await Task.Delay(60);
        return new { handled };
    }

    static Task<object> TypeText(MainViewModel main, JsonElement args)
    {
        main.CommandText = Str(args, "text") ?? String.Empty;
        return Task.FromResult<object>(true);
    }

    static async Task<object> Navigate(MainViewModel main, JsonElement args)
    {
        var panel = Panel(main, args);
        await panel.NavigateAsync(Str(args, "path") ?? throw new ArgumentException("path is required"));
        return true;
    }

    static Task<object> Cursor(MainViewModel main, JsonElement args)
    {
        var panel = Panel(main, args);
        main.Activate(panel);
        var tab = panel.ActiveTab ?? throw new InvalidOperationException("panel has no tab");
        var name = Str(args, "name") ?? throw new ArgumentException("name is required");
        var item = tab.Items.FirstOrDefault(x => x.Entry.Name == name) ?? throw new ArgumentException($"'{name}' is not in {tab.Path}");
        tab.SetCursor(item);
        return Task.FromResult<object>(true);
    }

    static Task<object> Mark(MainViewModel main, JsonElement args)
    {
        var tab = main.ActivePanel.ActiveTab ?? throw new InvalidOperationException("panel has no tab");
        foreach (var n in args.GetProperty("names").EnumerateArray())
        {
            var item = tab.Items.FirstOrDefault(x => x.Entry.Name == n.GetString()) ?? throw new ArgumentException($"'{n.GetString()}' is not in {tab.Path}");
            if (!item.IsMarked)
                tab.ToggleMark(item);
        }
        return Task.FromResult<object>(true);
    }

    static async Task<object> Command(MainViewModel main, JsonElement args)
    {
        var name = Str(args, "name") ?? throw new ArgumentException("name is required");
        var parameter = Str(args, "parameter");
        var target = name.StartsWith("panel.", StringComparison.Ordinal) ? (object)main.ActivePanel : main;
        var prop = target.GetType().GetProperty(name.Replace("panel.", String.Empty) + "Command") ?? throw new ArgumentException($"no command '{name}'");
        var command = (ICommand)prop.GetValue(target)!;
        if (!command.CanExecute(parameter))
            return new { executed = false };

        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand async)
            await async.ExecuteAsync(parameter);
        else
            command.Execute(parameter);
        return new { executed = true };
    }

    static Task<object> HoldJob(MainViewModel main, JsonElement args)
    {
        var seconds = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("seconds", out var s) ? s.GetInt32() : 30;
        main.Jobs.Start(HoldTitle, FileOperationKind.Copy, null, async (progress, ct) =>
        {
            for (var i = 0; i < seconds * 10; i++)
            {
                await Task.Delay(100, ct);
                progress.Report(new OperationProgress(FileOperationKind.Copy, "held", i, seconds * 10, 0, 1));
            }
            return new OperationResult(1, 0, []);
        });
        return Task.FromResult<object>(true);
    }

    static async Task<object> JobAction(MainViewModel main, JsonElement args)
    {
        var action = Str(args, "action") ?? throw new ArgumentException("action is required");
        if (action == "clear")
        {
            main.ClearCompletedJobsCommand.Execute(null);
            return true;
        }

        var index = args.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
        var job = main.Jobs.Jobs.ElementAtOrDefault(index) ?? throw new ArgumentException($"no job {index} (there are {main.Jobs.Jobs.Count})");
        switch (action)
        {
            case "stop":
                main.StopJobCommand.Execute(job);
                await job.Completion;
                break;
            case "goto":
                await main.GoToJobCommand.ExecuteAsync(job);
                break;
            case "dismiss":
                main.DismissJobCommand.Execute(job);
                break;
            case "errors":
                await main.ShowJobErrorsCommand.ExecuteAsync(job);
                break;
            default:
                throw new ArgumentException($"unknown action '{action}'");
        }
        return true;
    }

    static async Task<object> CloseWindow(MainViewModel main, JsonElement args)
    {
#if MACOS_APPKIT
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is AppKit.NSWindow ns)
        {
            // what the red button does: ask the delegate, close if it agrees
            var closing = ns.Delegate?.WindowShouldClose(ns) ?? true;
            await Task.Delay(100); // the guard's question is asked asynchronously
            return new { closing };
        }
#endif
        await Task.CompletedTask;
        throw new NotSupportedException("close_window is macOS only");
    }

    static Task<object> SetCulture(MainViewModel main, JsonElement args)
    {
        Strings.ApplyLanguage(Str(args, "name") ?? "en");
        return Task.FromResult<object>(System.Globalization.CultureInfo.CurrentUICulture.Name);
    }

    static async Task<object> ClickAt(MainViewModel main, JsonElement args)
    {
#if MACOS_APPKIT
        var x = args.GetProperty("x").GetDouble();
        var y = args.GetProperty("y").GetDouble();
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is not AppKit.NSWindow ns || ns.ContentView is not { } content)
            throw new InvalidOperationException("no window");

        // page points are top-down from the content view; window coordinates are bottom-up
        var point = new CoreGraphics.CGPoint(x, content.Frame.Height - y);
        var hit = content.Superview?.HitTest(point);
        var chain = new List<string>();
        for (var v = hit; v != null && chain.Count < 12; v = v.Superview)
            chain.Add(v.GetType().Name);

        // a button's mouseDown tracks the mouse in a loop until mouse-up arrives from the event queue - so queue the up
        // first, then deliver the down. The up is stamped after the down: a click gesture recognizer (what a MAUI tap is on
        // AppKit) ignores an up that predates its down
        var now = NSProcessInfoUptime();
        var down = AppKit.NSEvent.MouseEvent(AppKit.NSEventType.LeftMouseDown, point, 0, now, ns.WindowNumber, null, 0, 1, 1f);
        var up = AppKit.NSEvent.MouseEvent(AppKit.NSEventType.LeftMouseUp, point, 0, now + 0.05, ns.WindowNumber, null, 0, 1, 0f);
        AppKit.NSApplication.SharedApplication.PostEvent(up, false);
        ns.SendEvent(down);
        await Task.Delay(500); // a dialog animates out
        return new { hit = String.Join(" < ", chain), element = HitElement(hit) };
#else
        await Task.CompletedTask;
        throw new NotSupportedException("click_at is macOS only");
#endif
    }

#if MACOS_APPKIT
    // the MAUI elements behind the native view chain, innermost first - "Type#AutomationId"
    static string HitElement(AppKit.NSView? hit)
    {
        var all = Application.Current?.Windows.FirstOrDefault()?.Page is IVisualTreeElement root
            ? root.GetVisualTreeDescendants().OfType<VisualElement>().Where(x => x.Handler?.PlatformView is AppKit.NSView).ToList()
            : [];
        var chain = new List<string>();
        for (var v = hit; v != null && chain.Count < 8; v = v.Superview)
            chain.AddRange(all.Where(x => ReferenceEquals(x.Handler!.PlatformView, v)).Select(x => x.GetType().Name + "#" + x.AutomationId));
        return String.Join(" < ", chain);
    }

    static double NSProcessInfoUptime() => Foundation.NSProcessInfo.ProcessInfo.SystemUptime;
#endif

    static Task<object> Answer(MainViewModel main, JsonElement args)
    {
        Service<UiTestDialogs>().Enqueue(args.GetProperty("answers").EnumerateArray().Select(x => x.GetString() ?? String.Empty));
        return Task.FromResult<object>(true);
    }

    static Task<object> RealDialogs(MainViewModel main, JsonElement args)
    {
        Service<UiTestDialogs>().UseRealDialogs = !(args.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.False);
        return Task.FromResult<object>(true);
    }

    /// <summary>A real dialog a failed test left open would swallow the next test's keys - press its way out.</summary>
    static async Task DismissDialogs()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var root = Application.Current?.Windows.FirstOrDefault()?.Page as IVisualTreeElement;
            var dialog = root == null ? null : Descendants(root).FirstOrDefault(x => x.GetType().Name == "DialogView" && x is VisualElement { IsVisible: true });
            if (dialog == null)
                return;

            var buttons = Descendants(dialog).OfType<Button>().ToList();
            var button = buttons.FirstOrDefault(b => b.Text is "Cancel" or "No") ?? buttons.LastOrDefault();
            if (button == null)
                return;
            ((IButtonController)button).SendClicked();
            await Task.Delay(400); // dialog animates out
        }
    }

    static IEnumerable<IVisualTreeElement> Descendants(IVisualTreeElement element)
    {
        foreach (var child in element.GetVisualChildren())
        {
            yield return child;
            foreach (var d in Descendants(child))
                yield return d;
        }
    }

    static async Task<object> Reset(MainViewModel main, JsonElement args)
    {
        diagnostics = false;
        await DismissDialogs();
        // back to the main page
        var nav = Shell.Current?.Navigation;
        if (nav != null)
        {
            while (nav.ModalStack.Count > 0)
                await nav.PopModalAsync(false);
            if (nav.NavigationStack.Count > 1)
                await nav.PopToRootAsync(false);
        }

        Strings.ApplyLanguage(Environment.GetEnvironmentVariable("SHINYCOMMANDER_LANG") is { Length: > 0 } lang ? lang : "en");
        var settings = Service<AppSettings>();
        settings.ShowHidden = false;
        settings.ConfirmDelete = true;
        settings.UseTrash = false; // the use_trash tool turns it on - into a test folder, never the real Trash
        settings.ExtractToFolderPerArchive = true;
        settings.FriendlySizes = false;

        var columns = Service<ColumnSet>();
        foreach (var c in Enum.GetValues<PanelColumn>())
            columns.Set(c, c != PanelColumn.Created);

        Service<UiTestDialogs>().Reset();
        main.CommandText = String.Empty;
        main.ShowOutput = false;
        await main.Jobs.StopAllAndWait(TimeSpan.FromSeconds(5));
        main.Jobs.ClearFinished();
        main.ShowJobs = false;
        main.StackPanels = false;
        main.IsPathEditing = false;
        main.IsFilterEditing = false;

        foreach (var (panel, path) in new[] { (main.Left, Str(args, "left")), (main.Right, Str(args, "right")) })
        {
            panel.IsQuickView = false;
            panel.ShowTree = false;
            panel.TreeWidth = 240;
            panel.ShowQuickFilter = false;
            panel.ViewMode = PanelViewMode.Details;
            while (panel.Tabs.Count > 1)
                panel.CloseTab(panel.Tabs[^1]);

            if (panel.ActiveTab is { } tab)
            {
                tab.QuickFilter = String.Empty;
                tab.IsBranchView = false;
                tab.SortDescending = false;
                tab.SortColumn = SortColumn.Name;
                if (path != null)
                    await tab.NavigateAsync(path);
                else
                    await tab.RefreshAsync();
                tab.MarkAll(false);
                tab.SetCursor(0);
            }
        }
        main.Activate(main.Left);
        return true;
    }

    // --- state ----------------------------------------------------------------------------------

    static bool diagnostics;

    static object Snapshot(MainViewModel main)
    {
        var dialogs = Service<UiTestDialogs>();
        var columns = Service<ColumnSet>();
        return new
        {
            page = Shell.Current?.CurrentPage?.GetType().Name,
            activeSide = main.ActivePanel == main.Left ? "left" : "right",
            stackPanels = main.StackPanels,
            commandText = main.CommandText,
            commandOutput = main.CommandOutput.Length > 4000 ? main.CommandOutput[^4000..] : main.CommandOutput,
            showOutput = main.ShowOutput,
            isOperationRunning = main.IsOperationRunning,
            showJobs = main.ShowJobs,
            jobs = main.Jobs.Jobs.Select(j => new
            {
                title = j.Title,
                state = j.State.ToString(),
                subtitle = j.Subtitle,
                canGoTo = j.CanGoTo,
                destination = j.Destination?.Directory,
                errors = j.Errors
            }).ToList(),
            ribbonTab = main.RibbonTab,
            columns = Enum.GetValues<PanelColumn>().Where(columns.IsVisible).Select(x => x.ToString()).ToList(),
            left = PanelState(main.Left),
            right = PanelState(main.Right),
            dialogs = dialogs.Log,
            pendingAnswers = dialogs.PendingAnswers,
            focus = NativeFocus(),
            // counting every NSView is itself slow - only when a test asks for it (the "diag" tool)
            nativeViews = diagnostics ? NativeViewCount() : 0,
            nativeViewTypes = diagnostics ? NativeViewTypes() : null,
            shortcuts = Service<Shiny.Maui.Controls.IKeyboardShortcutService>().GetActiveShortcuts().Count,
            managedMb = GC.GetTotalMemory(false) / 1024 / 1024
        };
    }

    /// <summary>
    /// Which Entry really has the keyboard: the native first responder, not MAUI's IsFocused (which the AppKit
    /// backend doesn't keep up to date). Its AutomationId, "dialog" for a dialog's field, "(other)" or null.
    /// </summary>
    static string? NativeFocus()
    {
#if MACOS_APPKIT
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is not AppKit.NSWindow nsWindow)
            return null;

        var responder = nsWindow.FirstResponder;
        if (responder == null)
            return null;

        // while a field is being edited the window's shared field editor (an NSTextView) is first responder, and
        // the field is its delegate; the backend may also wrap the NSTextField in a container view
        var candidates = new List<AppKit.NSView>();
        if (responder is AppKit.NSView rv)
            candidates.Add(rv);
        if (responder is AppKit.NSTextView tv && tv.Delegate is AppKit.NSView dv)
            candidates.Add(dv);

        if (window.Page is not IVisualTreeElement root)
            return "(other:" + responder.GetType().Name + ")";
        foreach (var entry in Descendants(root).OfType<Entry>())
        {
            if (entry.Handler?.PlatformView is not AppKit.NSView pv)
                continue;
            if (candidates.Any(c => ReferenceEquals(c, pv) || c.IsDescendantOf(pv)))
            {
                for (Element? e = entry; e != null; e = e.Parent)
                {
                    if (e.GetType().Name == "DialogView")
                        return "dialog";
                }
                return entry.AutomationId ?? "(entry)";
            }
        }
        return "(other:" + responder.GetType().Name + ")";
#else
        return null;
#endif
    }

    /// <summary>Every NSView under the window - what a page change detaches and re-attaches.</summary>
    static int NativeViewCount()
    {
#if MACOS_APPKIT
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is not AppKit.NSWindow w || w.ContentView is not { } root)
            return 0;
        static int Count(AppKit.NSView v) => 1 + v.Subviews.Sum(Count);
        return Count(root);
#else
        return 0;
#endif
    }

    static string NativeViewTypes()
    {
#if MACOS_APPKIT
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is not AppKit.NSWindow w || w.ContentView is not { } root)
            return "";
        var counts = new Dictionary<string, int>();
        int constraints = 0;
        var owners = new Dictionary<string, int>();
        var translating = 0;
        void Walk(AppKit.NSView v)
        {
            var n = v.GetType().Name;
            counts[n] = counts.GetValueOrDefault(n) + 1;
            constraints += v.Constraints.Length;
            if (v.TranslatesAutoresizingMaskIntoConstraints)
                translating++;
            foreach (var c in v.Constraints)
            {
                var cn = c.GetType().Name;
                if (cn.Contains("Autoresizing"))
                    continue;
                var key = $"{n}:{cn}";
                owners[key] = owners.GetValueOrDefault(key) + 1;
            }
            foreach (var c in v.Subviews)
                Walk(c);
        }
        Walk(root);
        return $"constraints={constraints}; translating={translating}; real constraint owners: {String.Join(", ", owners.Select(x => $"{x.Key}={x.Value}"))}; " + String.Join(", ", counts.OrderByDescending(x => x.Value).Take(14).Select(x => $"{x.Key}={x.Value}"));
#else
        return "";
#endif
    }

    static object PanelState(PanelViewModel p)
    {
        var tab = p.ActiveTab;
        return new
        {
            isActive = p.IsActive,
            path = p.Path,
            viewMode = p.ViewMode.ToString(),
            showTree = p.ShowTree,
            treePath = p.TreePath,
            treeWidth = p.TreeWidth,
            showQuickFilter = p.ShowQuickFilter,
            isQuickView = p.IsQuickView,
            thumbnailSpan = p.ThumbnailSpan,
            tabs = p.Tabs.Select(t => t.Path).ToList(),
            activeTab = tab == null ? -1 : p.Tabs.IndexOf(tab),
            quickView = new
            {
                title = p.QuickView.Title,
                kind = p.QuickView.Kind?.ToString(),
                info = p.QuickView.Info,
                text = p.QuickView.Text.Length > 500 ? p.QuickView.Text[..500] : p.QuickView.Text,
                hasImage = p.QuickView.Image != null
            },
            cursor = tab?.CursorIndex ?? -1,
            cursorName = tab?.CursorItem?.Entry.Name,
            markedCount = tab?.MarkedCount ?? 0,
            status = tab?.StatusText,
            error = tab?.Error,
            isArchive = tab?.IsArchive ?? false,
            isReadOnlyArchive = tab?.IsReadOnlyArchive ?? false,
            archiveBadge = tab?.ArchiveBadge,
            quickFilter = tab?.QuickFilter,
            isBranchView = tab?.IsBranchView ?? false,
            sort = tab == null ? null : $"{tab.SortColumn}{(tab.SortDescending ? " desc" : String.Empty)}",
            canGoBack = tab?.CanGoBack ?? false,
            items = tab?.Items.Select(i => new
            {
                name = i.Entry.Name,
                dir = i.Entry.IsDirectory,
                parent = i.Entry.IsParentLink,
                marked = i.IsMarked,
                size = i.Entry.Size,
                sizeText = i.SizeText
            }).ToList()
        };
    }
}
#endif
