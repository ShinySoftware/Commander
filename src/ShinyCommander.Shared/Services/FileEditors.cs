using Microsoft.Extensions.DependencyInjection;

namespace ShinyCommander.Services;

/// <summary>
/// F4 for one kind of file - a page that understands the format (image metadata, a SQLite database) instead of the
/// text editor. Register one with <see cref="FileEditorExtensions.AddFileEditor{T}"/>; the first editor (lowest
/// <see cref="Order"/>) whose <see cref="CanEdit"/> says yes gets the file, and the text editor takes the rest.
/// </summary>
public interface IFileEditor
{
    /// <summary>Lower goes first. The text editor is <see cref="Int32.MaxValue"/>, so it only gets what nothing else claimed.</summary>
    int Order { get; }

    /// <summary>
    /// Whether this editor handles the file - by extension, and where an extension isn't proof (.db), by its header.
    /// The path is always on disk: files in archives and on servers are copied out first.
    /// </summary>
    bool CanEdit(string path);

    /// <summary>Opens the editor. Saves go to <paramref name="path"/>, and from there back into an archive or up to a server.</summary>
    Task Open(string path);
}

/// <summary>The registered <see cref="IFileEditor"/>s, in the order they're asked.</summary>
public sealed class FileEditors(IEnumerable<IFileEditor> editors)
{
    readonly IReadOnlyList<IFileEditor> ordered = editors.OrderBy(x => x.Order).ToList();

    public IFileEditor For(string path) => this.ordered.First(x => x.CanEdit(path));

    public Task Open(string path) => this.For(path).Open(path);
}

public static class FileEditorExtensions
{
    public static IServiceCollection AddFileEditor<T>(this IServiceCollection services) where T : class, IFileEditor
    {
        services.AddSingleton<IFileEditor, T>();
        return services;
    }
}
