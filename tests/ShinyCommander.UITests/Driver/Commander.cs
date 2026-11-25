using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;

namespace ShinyCommander.UITests.Driver;

/// <summary>
/// The app's test tools (<c>org.shiny.commander</c>, Shared/Testing/UiTestHooks.cs) as typed calls. Every call
/// returns the app state after the app has settled: listings loaded, operations finished, quick view loaded.
/// <para>
/// Keys go through Shiny's real shortcut engine, so <see cref="Key"/> tests the actual keymap - gesture, CanExecute
/// hand-back to the command line and all. Anything a user does with the mouse goes through DevFlow's own taps
/// (<see cref="Client"/>).
/// </para>
/// </summary>
public sealed class Commander(AgentClient client)
{
    const string Base = "/api/v1/ext/org.shiny.commander/";

    public AgentClient Client { get; } = client;

    /// <summary>The <c>result</c> part of the last call (e.g. <c>{ handled }</c> for a key).</summary>
    public JsonElement LastResult { get; private set; }

    async Task<AppState> Call(string tool, object? args = null)
    {
        var parameters = JsonSerializer.SerializeToElement(args ?? new { }, AppState.Json);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var body = await this.Client.CallExtensionToolAsync("POST", Base + tool, parameters);
        if (watch.ElapsedMilliseconds > 1000)
            TestContext.Current.SendDiagnosticMessage($"[uitests] slow call: {tool} {parameters.GetRawText()} took {watch.ElapsedMilliseconds}ms");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        this.LastResult = root.TryGetProperty("result", out var r) ? r.Clone() : default;
        if (!root.TryGetProperty("state", out var state))
            throw new InvalidOperationException($"{tool}: unexpected answer {body}");
        return state.Deserialize<AppState>(AppState.Json)!;
    }

    public Task<AppState> State() => this.Call("state");

    /// <summary>Native view counts in the state (NativeViews / NativeViewTypes) - slow, so off unless asked for.</summary>
    public Task<AppState> Diagnostics(bool on = true) => this.Call("diag", new { on });

    /// <summary>
    /// Presses a gesture (<c>"F5"</c>, <c>"Primary+Shift+K"</c>, <c>"Ctrl+Q"</c>). <paramref name="textFocused"/> mirrors
    /// the app's normal state - the command line has keyboard focus - so keys the command line should keep are
    /// handed back to it exactly as in real use.
    /// </summary>
    public Task<AppState> Key(string gesture, bool textFocused = true) => this.Call("key", new { gesture, textFocused });

    public bool LastKeyHandled => this.LastResult.ValueKind == JsonValueKind.Object && this.LastResult.TryGetProperty("handled", out var h) && h.GetBoolean();

    public async Task<AppState> Keys(params string[] gestures)
    {
        AppState state = null!;
        foreach (var g in gestures)
            state = await this.Key(g);
        return state;
    }

    /// <summary>What typing into the command line leaves there.</summary>
    public Task<AppState> Type(string text) => this.Call("type", new { text });

    public Task<AppState> Navigate(string path, string? side = null) => this.Call("navigate", new { side, path });

    /// <summary>Activates the panel and puts its cursor on <paramref name="name"/> (what clicking a row does).</summary>
    public Task<AppState> Cursor(string name, string? side = null) => this.Call("cursor", new { side, name });

    /// <summary>Types into the in-place name editor (F2, Rename tab…), then presses Enter unless told not to.</summary>
    public Task<AppState> InlineName(string text, bool enter = true) => this.Call("inline_name", new { text, enter });

    /// <summary>Opens a file in its app for real (test mode otherwise only records it); <see cref="LastResult"/> has <c>opened</c>.</summary>
    public Task<AppState> OpenReal(string path) => this.Call("open_real", new { path });

    /// <summary>Motion (line) file icons on or off until reset - the Settings switch.</summary>
    public Task<AppState> MotionIcons(bool on = true) => this.Call("motion_icons", new { on });

    public Task<AppState> Mark(params string[] names) => this.Call("mark", new { names });

    /// <summary>Runs a MainViewModel command (name without "Command"; "panel.X" for the active panel's) - for ribbon-only actions.</summary>
    public Task<AppState> Command(string name, string? parameter = null) => this.Call("command", new { name, parameter });

    /// <summary>
    /// Queues answers for the next dialogs, in order. <c>&lt;default&gt;</c> accepts a prompt's suggestion,
    /// <c>&lt;cancel&gt;</c> cancels, <c>yes</c>/<c>no</c> for confirms, an option's text for choices, anything else is typed.
    /// </summary>
    public Task<AppState> Answer(params string[] answers) => this.Call("answer", new { answers });

    /// <summary>A background job that runs until stopped (or <paramref name="seconds"/> pass); settling doesn't wait for it.</summary>
    public Task<AppState> HoldJob(int seconds = 30) => this.Call("hold_job", new { seconds });

    /// <summary>A jobs pane action on job <paramref name="index"/> (newest first): stop, goto, dismiss, errors - or clear (completed).</summary>
    public Task<AppState> Job(string action, int index = 0) => this.Call("job", new { action, index });

    /// <summary>The main window's close button (macOS). <see cref="LastResult"/> has <c>closing</c>.</summary>
    public Task<AppState> CloseWindow() => this.Call("close_window");

    /// <summary>The UI language from here on ("fr", "es") - dialogs and newly opened pages. Reset goes back to English.</summary>
    public Task<AppState> Culture(string name) => this.Call("culture", new { name });

    /// <summary>Saves a remote connection (memory keychain); <see cref="LastResult"/> is its start path.</summary>
    public Task<AppState> Connection(string protocol, string name, string host, int port, string user, string secret, string start = "/")
        => this.Call("connection", new { protocol, name, host, port, user, secret, start });

    /// <summary>Saves any connection from an anonymous object of the hook's fields (protocol, name, host, port, user, secret, start, keyFile, region, endpoint).</summary>
    public Task<AppState> Connection(object fields) => this.Call("connection", fields);

    public Task<AppState> RemoveConnection(string name) => this.Call("connection", new { remove = name });

    /// <summary>Deletes go to the trash (a test folder, <see cref="TestConfig.TrashDir"/>) until the next reset.</summary>
    public Task<AppState> UseTrash(bool on = true) => this.Call("use_trash", new { on });

    /// <summary>Folder tabs on or off, as the Settings switch does it (reset turns them back on).</summary>
    public Task<AppState> ShowTabs(bool on = true) => this.Call("show_tabs", new { on });

    /// <summary>A preview plugin (IFilePreviewer.Key) on or off, as its Settings switch does it (reset turns them all back on).</summary>
    public Task<AppState> Previewer(string key, bool on = true) => this.Call("previewer", new { key, on });

    /// <summary>Runs JavaScript in the preview panel's web view; the result is a string in <see cref="LastResult"/>.</summary>
    public async Task<string?> PreviewJs(string script)
    {
        await this.Call("preview_js", new { script });
        return this.LastResult.ValueKind == JsonValueKind.String ? this.LastResult.GetString() : this.LastResult.ToString();
    }

    /// <summary>A real mouse click at a page point (macOS): mouse down/up go through the window's event routing, unlike a DevFlow tap. <see cref="LastResult"/> has <c>hit</c>, the native view chain under the point, and <c>element</c>, the MAUI elements behind it.</summary>
    /// <summary>The files a drag starting at the page point would carry (real hit testing; no AppKit drag session).</summary>
    public async Task<List<string>> DragFrom(double x, double y)
    {
        await this.Call("drag_from", new { x, y });
        return this.LastResult.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    /// <summary>Drops files at the page point as a drag from Finder would: "copy", "move" or "none" (refused).</summary>
    public async Task<string> DropAt(double x, double y, IEnumerable<string> paths, string allowed = "copy+move")
    {
        await this.Call("drop_at", new { x, y, paths, allowed });
        return this.LastResult.GetString()!;
    }

    /// <summary>A real AppKit click; <paramref name="modifiers"/> holds keys down for it ("shift", "cmd", "shift+cmd").</summary>
    public Task<AppState> ClickAt(double x, double y, bool secondary = false, int clicks = 1, string? modifiers = null) => this.Call("click_at", new { x, y, secondary, clicks, modifiers });

    /// <summary>What a click at a page point would hit, without clicking (a popup's menu would hold the main thread).</summary>
    public Task<AppState> HitAt(double x, double y) => this.Call("click_at", new { x, y, hitOnly = true });

    public Task<AppState> Reset(string left, string right) => this.Call("reset", new { left, right });

    /// <summary>Real (Shiny) dialogs from here on, for this test - driven with DevFlow taps/fills. Reset turns it off.</summary>
    /// <summary>The shared folders and their addresses on loopback; <paramref name="close"/> stops them all and closes the Sharing window.</summary>
    public async Task<JsonElement> Sharing(bool close = false)
    {
        await this.Call("sharing", new { close });
        return this.LastResult;
    }

    /// <summary>The Sharing window drawn natively (macOS), or null.</summary>
    public async Task<byte[]?> SharingPng()
    {
        await this.Call("sharing_png");
        var text = this.LastResult.ValueKind == JsonValueKind.String ? this.LastResult.GetString() : null;
        return String.IsNullOrEmpty(text) ? null : Convert.FromBase64String(text);
    }

    public Task<AppState> RealDialogs(bool on = true) => this.Call("real_dialogs", new { on });

    /// <summary>The state right now, without waiting for the app to settle (e.g. while a real dialog is open).</summary>
    public Task<AppState> StateNow() => this.Call("state_now");

    /// <summary>Polls the state until <paramref name="condition"/> holds - for things that finish after the call returns.</summary>
    public async Task<AppState> WaitFor(Func<AppState, bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        AppState state;
        do
        {
            state = await this.State();
            if (condition(state))
                return state;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        throw new ShouldAssertException($"Timed out waiting for {what}.\nLeft: {state.Left.Path} [{String.Join(", ", state.Left.Names)}]\nRight: {state.Right.Path} [{String.Join(", ", state.Right.Names)}]\nPage: {state.Page}  Focus: {state.Focus ?? "(none)"}  Active: {state.ActiveSide}\nDialogs: {String.Join(" | ", state.Dialogs)}");
    }
}
