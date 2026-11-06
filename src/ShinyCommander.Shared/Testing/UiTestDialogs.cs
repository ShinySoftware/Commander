#if DEBUG
using System.Collections.Concurrent;

namespace ShinyCommander.Testing;

public sealed record DialogRecord(string Kind, string Title, string? Message, string? Initial, IReadOnlyList<string>? Options, string? Answer);

/// <summary>
/// UI test mode's dialogs: every alert/confirm/prompt/choose/toast is recorded, and answered from a queue the test
/// fills before it triggers the dialog. An unanswered question is cancelled (and recorded as such) rather than left
/// waiting on a modal nobody will click.
/// <para>Answers: <c>&lt;default&gt;</c> accepts a prompt's initial value, <c>&lt;cancel&gt;</c> cancels, <c>yes</c>/<c>no</c>
/// for confirms, an option's text (or a unique prefix of it) for choices, anything else is typed into a prompt.</para>
/// </summary>
public sealed class UiTestDialogs(ShinyAppDialogs real) : IAppDialogs
{
    /// <summary>Let the real (Shiny) dialogs show, so a test can drive them through DevFlow like a user would.</summary>
    public bool UseRealDialogs { get; set; }

    public const string Default = "<default>";
    public const string Cancel = "<cancel>";

    readonly ConcurrentQueue<string> answers = new();
    readonly List<DialogRecord> log = [];

    public event EventHandler? DialogClosed
    {
        add
        {
            this.closed += value;
            real.DialogClosed += value;
        }
        remove
        {
            this.closed -= value;
            real.DialogClosed -= value;
        }
    }
    EventHandler? closed;

    public void Enqueue(IEnumerable<string> values)
    {
        foreach (var v in values)
            this.answers.Enqueue(v);
    }

    public IReadOnlyList<DialogRecord> Log
    {
        get
        {
            lock (this.log)
                return this.log.ToList();
        }
    }

    public int PendingAnswers => this.answers.Count;

    public void Reset()
    {
        this.UseRealDialogs = false;
        this.answers.Clear();
        lock (this.log)
            this.log.Clear();
    }

    string? Next() => this.answers.TryDequeue(out var a) ? a : null;

    T Record<T>(string kind, string title, string? message, string? initial, IReadOnlyList<string>? options, string? answer, T result)
    {
        lock (this.log)
            this.log.Add(new DialogRecord(kind, title, message, initial, options, answer));
        this.closed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public Task Alert(string title, string message)
        => this.UseRealDialogs ? this.Real("alert", title, message, async () => { await real.Alert(title, message); return true; }) : Task.FromResult(this.Record("alert", title, message, null, null, null, true));

    public Task<bool> Confirm(string title, string message, string okText = "OK", string cancelText = "Cancel")
    {
        if (this.UseRealDialogs)
            return this.Real("confirm", title, message, () => real.Confirm(title, message, okText, cancelText));
        var a = this.Next();
        var yes = a is not null && (a.Equals("yes", StringComparison.OrdinalIgnoreCase) || a.Equals("ok", StringComparison.OrdinalIgnoreCase) || a.Equals("true", StringComparison.OrdinalIgnoreCase) || a.Equals(okText, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(this.Record("confirm", title, message, null, [okText, cancelText], a, yes));
    }

    public Task<string?> Prompt(string title, string message, string? initialValue = null, string okText = "OK", string? placeholder = null)
    {
        if (this.UseRealDialogs)
            return this.Real("prompt", title, message, () => real.Prompt(title, message, initialValue, okText, placeholder));
        var a = this.Next();
        var value = a switch
        {
            null or Cancel => null,
            Default => initialValue,
            _ => a
        };
        return Task.FromResult(this.Record("prompt", title, message, initialValue, null, a, value));
    }

    public Task<string?> Choose(string title, IReadOnlyList<string> options, string? cancelText = "Cancel", string? destructive = null)
    {
        if (this.UseRealDialogs)
            return this.Real("choose", title, null, () => real.Choose(title, options, cancelText, destructive));
        var a = this.Next();
        string? pick = null;
        if (a is not null && a != Cancel)
        {
            pick = options.FirstOrDefault(o => o == a)
                ?? options.FirstOrDefault(o => o.Trim() == a.Trim())
                ?? options.SingleOrDefault(o => o.Trim().StartsWith(a.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? options.SingleOrDefault(o => o.Contains(a.Trim(), StringComparison.OrdinalIgnoreCase));
            if (pick == null && a == cancelText)
                pick = cancelText;
        }
        return Task.FromResult(this.Record("choose", title, null, null, options, a, pick));
    }

    public Task Toast(string message, ToastKind kind = ToastKind.Info)
        => this.UseRealDialogs ? this.Real("toast", kind.ToString(), message, async () => { await real.Toast(message, kind); return true; }) : Task.FromResult(this.Record("toast", kind.ToString(), message, null, null, null, true));

    /// <summary>Records the dialog as shown, then its real answer once the user (a DevFlow tap) gives one.</summary>
    async Task<T> Real<T>(string kind, string title, string? message, Func<Task<T>> show)
    {
        var index = 0;
        lock (this.log)
        {
            index = this.log.Count;
            this.log.Add(new DialogRecord(kind, title, message, null, null, "<showing>"));
        }
        var result = await show();
        lock (this.log)
            this.log[index] = this.log[index] with { Answer = result?.ToString() ?? "<null>" };
        return result;
    }
}
#endif
