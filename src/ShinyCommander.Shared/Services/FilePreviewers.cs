using Microsoft.Extensions.DependencyInjection;

namespace ShinyCommander.Services;

/// <summary>
/// The preview panel's view of one kind of file - a rendered page, a PDF, a database's tables - instead of its bytes.
/// Register one with <see cref="FilePreviewerExtensions.AddFilePreviewer{T}"/>; the first enabled previewer (lowest
/// <see cref="Order"/>) that takes the file shows it, and the panel's own text / hex view takes the rest.
/// </summary>
public interface IFilePreviewer
{
    /// <summary>Stable id - what Settings remembers when the previewer is turned off.</summary>
    string Key { get; }

    /// <summary>Its name in Settings.</summary>
    string Title { get; }

    /// <summary>Lower goes first.</summary>
    int Order { get; }

    /// <summary>
    /// By name alone - asked first, so a file in an archive or on a server is only copied out when a previewer
    /// could want it.
    /// </summary>
    bool Handles(string fileName);

    /// <summary>The file is on disk now: a last look where the name isn't proof (a .db that isn't SQLite).</summary>
    bool CanPreview(string path) => true;

    /// <summary>The view for the panel. <paramref name="path"/> stays on disk until the panel moves on.</summary>
    Task<View> CreateView(string path, CancellationToken ct);
}

/// <summary>The registered <see cref="IFilePreviewer"/>s, in the order they're asked, less the ones turned off in Settings.</summary>
public sealed class FilePreviewers(IEnumerable<IFilePreviewer> previewers, AppSettings settings)
{
    public IReadOnlyList<IFilePreviewer> All { get; } = previewers.OrderBy(x => x.Order).ToList();

    public bool IsEnabled(IFilePreviewer previewer) => !this.Disabled().Contains(previewer.Key);

    public void SetEnabled(IFilePreviewer previewer, bool enabled)
    {
        var disabled = this.Disabled();
        if (enabled)
            disabled.Remove(previewer.Key);
        else
            disabled.Add(previewer.Key);
        settings.DisabledPreviewers = disabled.Count == 0 ? null : String.Join(',', disabled.Order());
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A previewer was turned on or off - the panel shows the file again.</summary>
    public event EventHandler? Changed;

    /// <summary>The enabled previewers that would take a file by this name.</summary>
    public IReadOnlyList<IFilePreviewer> Candidates(string fileName)
    {
        var disabled = this.Disabled();
        return this.All.Where(x => !disabled.Contains(x.Key) && x.Handles(fileName)).ToList();
    }

    HashSet<string> Disabled() => new(
        (settings.DisabledPreviewers ?? String.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase
    );
}

public static class FilePreviewerExtensions
{
    public static IServiceCollection AddFilePreviewer<T>(this IServiceCollection services) where T : class, IFilePreviewer
    {
        services.AddSingleton<IFilePreviewer, T>();
        return services;
    }
}
