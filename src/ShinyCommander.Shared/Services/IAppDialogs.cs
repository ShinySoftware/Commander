namespace ShinyCommander.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// The app's dialog surface. The default implementation is Shiny's owned, animated IDialogService/IToaster;
/// a head can register its own (e.g. native NSAlert) where the in-app overlay is not available.
/// </summary>
public interface IAppDialogs
{
    /// <summary>Raised after any dialog closes - the main page uses it to put focus back in the command line.</summary>
    event EventHandler? DialogClosed;

    Task Alert(string title, string message);
    Task<bool> Confirm(string title, string message, string okText = "OK", string cancelText = "Cancel");
    Task<string?> Prompt(string title, string message, string? initialValue = null, string okText = "OK", string? placeholder = null);
    /// <summary>A prompt whose text is masked - passwords. Implementations without a masked field fall back to <see cref="Prompt"/>.</summary>
    Task<string?> PromptSecret(string title, string message, string okText = "OK") => this.Prompt(title, message, null, okText);

    Task<string?> Choose(string title, IReadOnlyList<string> options, string? cancelText = "Cancel", string? destructive = null);
    Task Toast(string message, ToastKind kind = ToastKind.Info);
}
