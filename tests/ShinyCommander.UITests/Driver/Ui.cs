using Microsoft.Maui.DevFlow.Driver;

namespace ShinyCommander.UITests.Driver;

/// <summary>
/// The real UI through DevFlow: find elements in the MAUI visual tree and tap / fill / send keys to them the way a
/// user would. Use it for what the hooks deliberately skip - mouse clicks, ribbon and function bar buttons, the path
/// box, real dialogs.
/// </summary>
public sealed class Ui(AgentClient client)
{
    public AgentClient Client { get; } = client;

    public async Task<IReadOnlyList<ElementInfo>> All()
    {
        var list = new List<ElementInfo>();
        void Walk(IEnumerable<ElementInfo>? items)
        {
            foreach (var e in items ?? [])
            {
                list.Add(e);
                Walk(e.Children);
            }
        }
        Walk(await this.Client.GetTreeAsync());
        return list;
    }

    public async Task<ElementInfo> ById(string automationId, TimeSpan? timeout = null)
        => await this.WaitFor(all => all.FirstOrDefault(x => x.AutomationId == automationId && x.IsVisible), $"element '{automationId}'", timeout);

    /// <summary>The first visible element with exactly this text, optionally inside the element with <paramref name="within"/> as AutomationId.</summary>
    public async Task<ElementInfo> ByText(string text, string? within = null, string? type = null, TimeSpan? timeout = null)
        => await this.WaitFor(all =>
        {
            // "within" is an AutomationId, or "type:Name" for the first visible element of that type (e.g. type:DialogView)
            var scope = within == null ? null
                : within.StartsWith("type:", StringComparison.Ordinal) ? all.FirstOrDefault(x => x.Type == within[5..] && x.IsVisible)
                : all.FirstOrDefault(x => x.AutomationId == within);
            if (within != null && scope == null)
                return null;
            var byId = all.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            return all.FirstOrDefault(x =>
                x.IsVisible
                && x.Text == text
                && (type == null || x.Type == type)
                && (scope == null || IsInside(x, scope.Id, byId)));
        }, $"'{text}'{(within == null ? "" : " in " + within)}", timeout);

    /// <summary>The nearest ancestor that has a gesture recognizer - a row, a tile, a key cap, a ribbon button.</summary>
    public async Task<ElementInfo> Tappable(ElementInfo element)
    {
        var all = await this.All();
        var byId = all.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var start = byId.GetValueOrDefault(element.Id) ?? element;

        // a button (or a label inside one) - tap the button itself; it has no recognizer, it *is* the tap target
        for (var c = start; c != null; c = c.ParentId != null ? byId.GetValueOrDefault(c.ParentId) : null)
        {
            if (c.Type is "Button" or "ImageButton" or "ShinyButton" or "RadioButton" or "CheckBox" or "Switch")
                return c;
            if (c.Gestures is { Count: > 0 })
                break;
        }
        for (var c = start; c != null; c = c.ParentId != null ? byId.GetValueOrDefault(c.ParentId) : null)
        {
            if (c.Gestures is { Count: > 0 })
                return c;
        }
        return element;
    }

    public async Task Tap(ElementInfo element)
    {
        var result = await this.Client.TapResultAsync(element.Id, null, null);
        if (!result.Success)
            throw new ShouldAssertException($"Tapping {element.Type} '{element.Text ?? element.AutomationId}' failed: {result.Error}");
    }

    public async Task Tap(string automationId) => await this.Tap(await this.ById(automationId));

    /// <summary>Taps the row/tile/button that shows <paramref name="text"/>.</summary>
    public async Task Click(string text, string? within = null) => await this.Tap(await this.Tappable(await this.ByText(text, within)));

    /// <summary>Taps a button in the open (Shiny) dialog and waits for the dialog to animate away.</summary>
    public async Task ClickInDialog(string text)
    {
        await this.Click(text, "type:DialogView");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && await this.Exists(x => x.Type == "DialogView" && x.IsVisible && x.Bounds is { Width: > 0 }))
            await Task.Delay(100);
    }

    public async Task DoubleClick(string text, string? within = null)
    {
        var target = await this.Tappable(await this.ByText(text, within));
        var result = await this.Client.GestureDetailedAsync("doubletap", target.Id);
        if (!result.Success)
            throw new ShouldAssertException($"Double-tapping '{text}' failed: {result.Error}");
    }

    public async Task Fill(ElementInfo entry, string text)
    {
        if (!await this.Client.FillAsync(entry.Id, text))
            throw new ShouldAssertException($"Could not fill {entry.Type} {entry.AutomationId ?? entry.Id}");
    }

    /// <summary>Sends a key to an element (e.g. Enter to complete an entry).</summary>
    public async Task Press(ElementInfo element, string key)
    {
        if (!await this.Client.KeyAsync(key, element.Id))
            throw new ShouldAssertException($"Could not press {key} on {element.Type} {element.AutomationId ?? element.Id}");
    }

    /// <summary>An element's centre in page coordinates - the tree's bounds are relative to each parent.</summary>
    public async Task<(double X, double Y)> Center(ElementInfo element)
    {
        var all = await this.All();
        var byId = all.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        double x = 0, y = 0;
        for (var e = element; e != null; e = e.ParentId != null && byId.TryGetValue(e.ParentId, out var p) ? p : null)
        {
            if (e.Bounds is { X: >= 0, Y: >= 0 } b)
            {
                x += b.X;
                y += b.Y;
            }
        }
        return (x + element.Bounds!.Width / 2, y + element.Bounds.Height / 2);
    }

    public async Task<bool> Exists(Func<ElementInfo, bool> predicate) => (await this.All()).Any(predicate);

    /// <summary>
    /// Why <paramref name="element"/> can't be seen, or null when it can: it needs a size, and its window rectangle
    /// must survive clipping by every ancestor (a zero-height scroll view hides children that have sizes of their own).
    /// </summary>
    public async Task<string?> WhyHidden(ElementInfo element)
    {
        var all = await this.All();
        var byId = all.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var e = byId.GetValueOrDefault(element.Id) ?? element;
        if (!e.IsVisible)
            return "IsVisible is false";
        if (e.WindowBounds is not { Width: > 1, Height: > 1 } r)
            return $"no size ({e.WindowBounds?.Width}x{e.WindowBounds?.Height})";

        double x1 = r.X, y1 = r.Y, x2 = r.X + r.Width, y2 = r.Y + r.Height;
        for (var p = e.ParentId != null ? byId.GetValueOrDefault(e.ParentId) : null; p != null; p = p.ParentId != null ? byId.GetValueOrDefault(p.ParentId) : null)
        {
            if (p.Type.StartsWith("Shell", StringComparison.Ordinal) || p.Type is "Window" or "FlyoutItem" or "Tab")
                continue; // shell containers report hidden / no geometry while their page shows
            if (!p.IsVisible)
                return $"ancestor {p.Type} is not visible";
            if (p.WindowBounds is not { } pb || pb.Width < 0 || pb.Height < 0)
                continue; // no geometry reported (shell items etc.)
            x1 = Math.Max(x1, pb.X); y1 = Math.Max(y1, pb.Y);
            x2 = Math.Min(x2, pb.X + pb.Width); y2 = Math.Min(y2, pb.Y + pb.Height);
            if (x2 - x1 < 1 || y2 - y1 < 1)
                return $"clipped away by ancestor {p.Type} {p.AutomationId} ({pb.X},{pb.Y} {pb.Width}x{pb.Height})";
        }
        return null;
    }

    public async Task ShouldBeVisible(ElementInfo element, string what)
    {
        if (await this.WhyHidden(element) is { } why)
            throw new ShouldAssertException($"{what} can't be seen: {why}");
    }

    async Task<ElementInfo> WaitFor(Func<IReadOnlyList<ElementInfo>, ElementInfo?> find, string what, TimeSpan? timeout)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var all = await this.All();
            if (find(all) is { } found)
                return found;
            if (DateTime.UtcNow > deadline)
                throw new ShouldAssertException($"Could not find {what} in the visual tree");
            await Task.Delay(150);
        }
    }

    static bool IsInside(ElementInfo e, string ancestorId, Dictionary<string, ElementInfo> byId)
    {
        var current = e.ParentId;
        while (current != null)
        {
            if (current == ancestorId)
                return true;
            current = byId.GetValueOrDefault(current)?.ParentId;
        }
        return false;
    }
}
