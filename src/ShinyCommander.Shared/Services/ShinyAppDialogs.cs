using Shiny.Maui.Controls;
using Shiny.Maui.Controls.Dialogs;
using Shiny.Maui.Controls.Toast;

namespace ShinyCommander.Services;

public class ShinyAppDialogs(IDialogService dialogs, IToaster toaster, IMainThread mainThread) : IAppDialogs
{
    public event EventHandler? DialogClosed;

    async Task<T> Run<T>(Func<Task<T>> show)
    {
        try
        {
            return await mainThread.InvokeOnMainThreadAsync(show);
        }
        finally
        {
            mainThread.BeginInvokeOnMainThread(() => this.DialogClosed?.Invoke(this, EventArgs.Empty));
        }
    }

    public Task Alert(string title, string message)
        => this.Run(async () =>
        {
            await dialogs.Alert(title, message, "OK");
            return true;
        });

    public Task<bool> Confirm(string title, string message, string okText = "OK", string cancelText = "Cancel")
        => this.Run(() => dialogs.Confirm(title, message, okText, cancelText));

    public Task<string?> Prompt(string title, string message, string? initialValue = null, string okText = "OK", string? placeholder = null)
        => this.Run(async () =>
        {
            var task = dialogs.Prompt(title, message, placeholder, okText, "Cancel", initialValue, configure: c => c.MaxWidth = 620);
            this.FocusDialogEntry();
            var result = await task;
            return result.Ok ? result.Value : null;
        });

    /// <summary>
    /// The dialog's text field doesn't always take first responder on the desktop backends (the command
    /// line keeps it), which also means Return never reaches it. Find the field and focus it.
    /// </summary>
    void FocusDialogEntry()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), () =>
        {
            var page = Shell.Current?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null)
                return;

            var entry = Descendants(page)
                .OfType<Entry>()
                .LastOrDefault(e => Ancestors(e).Any(a => a.GetType().Name == "DialogView"));

            if (entry != null)
            {
                entry.Focus();
                entry.CursorPosition = 0;
                entry.SelectionLength = entry.Text?.Length ?? 0;
            }
        });
    }

    static IEnumerable<Element> Ancestors(Element e)
    {
        for (var p = e.Parent; p != null; p = p.Parent)
            yield return p;
    }

    static IEnumerable<Element> Descendants(Element root)
    {
        foreach (var child in ((IElementController)root).LogicalChildren)
        {
            yield return child;
            foreach (var d in Descendants(child))
                yield return d;
        }
        if (root is IVisualTreeElement vte)
        {
            foreach (var child in vte.GetVisualChildren().OfType<Element>())
            {
                if (((IElementController)root).LogicalChildren.Contains(child))
                    continue;
                yield return child;
                foreach (var d in Descendants(child))
                    yield return d;
            }
        }
    }

    public Task<string?> Choose(string title, IReadOnlyList<string> options, string? cancelText = "Cancel", string? destructive = null)
        => this.Run(() => dialogs.ActionSheet(title, options, cancelText, destructive));

    public Task Toast(string message, ToastKind kind = ToastKind.Info)
        => mainThread.InvokeOnMainThreadAsync(async () =>
        {
            IDisposable _ = kind switch
            {
                ToastKind.Success => await toaster.SuccessAsync(message),
                ToastKind.Warning => await toaster.WarningAsync(message),
                ToastKind.Error => await toaster.DangerAsync(message),
                _ => await toaster.InfoAsync(message)
            };
        });
}
