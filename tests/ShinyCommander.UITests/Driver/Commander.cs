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

    public Task<AppState> Mark(params string[] names) => this.Call("mark", new { names });

    /// <summary>Runs a MainViewModel command (name without "Command"; "panel.X" for the active panel's) - for ribbon-only actions.</summary>
    public Task<AppState> Command(string name, string? parameter = null) => this.Call("command", new { name, parameter });

    /// <summary>
    /// Queues answers for the next dialogs, in order. <c>&lt;default&gt;</c> accepts a prompt's suggestion,
    /// <c>&lt;cancel&gt;</c> cancels, <c>yes</c>/<c>no</c> for confirms, an option's text for choices, anything else is typed.
    /// </summary>
    public Task<AppState> Answer(params string[] answers) => this.Call("answer", new { answers });

    public Task<AppState> Reset(string left, string right) => this.Call("reset", new { left, right });

    /// <summary>Real (Shiny) dialogs from here on, for this test - driven with DevFlow taps/fills. Reset turns it off.</summary>
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
