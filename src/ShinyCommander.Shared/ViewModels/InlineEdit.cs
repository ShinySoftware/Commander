namespace ShinyCommander.ViewModels;

/// <summary>
/// A name being edited in place (F2 on a row or tile, "Rename tab…"): the view puts an editor over the name and
/// commits or cancels it; whoever started it awaits <see cref="Result"/>.
/// </summary>
public sealed class InlineEdit(object target, string text, int selectionLength)
{
    readonly TaskCompletionSource<string?> result = new();

    /// <summary>The row's <see cref="FileItemViewModel"/> or a <see cref="PanelTabViewModel"/>.</summary>
    public object Target => target;

    public string Text => text;

    /// <summary>How much of <see cref="Text"/> starts selected - a file's name without its extension.</summary>
    public int SelectionLength => selectionLength;

    /// <summary>The new text, or null when cancelled.</summary>
    public Task<string?> Result => this.result.Task;

    /// <summary>Set by the view that put an editor up - nobody did, and the caller falls back to a prompt.</summary>
    public bool IsShown { get; set; }

    public bool IsEnded => this.result.Task.IsCompleted;

    /// <summary>Raised once, when committed or cancelled - the view takes its editor down.</summary>
    public event EventHandler? Ended;

    public void Commit(string? value)
    {
        if (this.result.TrySetResult(value))
            this.Ended?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel() => this.Commit(null);
}
