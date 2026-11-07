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
        => this.Run(() =>
        {
            var task = dialogs.ActionSheet(title, options, cancelText, destructive);
            this.SizeActionSheetOptions();
            return task;
        });

    /// <summary>
    /// The action sheet puts its options in a ScrollView capped with MaximumHeightRequest; the AppKit backend measures
    /// that ScrollView at zero height (maui-labs#830), so the options vanish and only Cancel shows. Give it an
    /// explicit height from its content until the backend fix ships.
    /// </summary>
    void SizeActionSheetOptions() => this.SizeActionSheetOptions(0);

    // the dialog builds asynchronously - keep looking for its options list for up to ~1.5s
    void SizeActionSheetOptions(int attempt)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || attempt > 15)
            return;

        dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(attempt == 0 ? 0 : 100), () =>
        {
            var root = Application.Current?.Windows.FirstOrDefault()?.Page;
            var sized = false;
            if (root != null)
            {
                foreach (var scroll in Descendants(root).OfType<ScrollView>().Where(s => Ancestors(s).Any(a => a.GetType().Name == "DialogView")))
                {
                    if (scroll.Content is not IView content)
                        continue;
                    var size = content.Measure(Math.Max(200, scroll.Width > 0 ? scroll.Width : 460), Double.PositiveInfinity);
                    if (size.Height <= 0)
                        continue;
                    scroll.HeightRequest = Math.Min(scroll.MaximumHeightRequest is > 0 and < Double.PositiveInfinity ? scroll.MaximumHeightRequest : 360, size.Height);
                    sized = true;
                }
            }
            if (!sized)
                this.SizeActionSheetOptions(attempt + 1);
        });
    }

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
