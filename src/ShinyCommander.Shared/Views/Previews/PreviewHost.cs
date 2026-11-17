namespace ShinyCommander.Views;

/// <summary>
/// Wraps a preview plugin's view with what has to be let go when the panel moves on - a player to stop, an Office
/// document holding its file open. The preview panel disposes the view it replaces.
/// </summary>
public sealed class PreviewHost : ContentView, IDisposable
{
    Action? release;

    public PreviewHost(View content, Action release)
    {
        this.Content = content;
        this.release = release;
    }

    public void Dispose()
    {
        var release = Interlocked.Exchange(ref this.release, null);
        try
        {
            release?.Invoke();
        }
        catch
        {
            // already torn down with its handler - nothing left to free
        }
    }
}
