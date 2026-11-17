using ShinyCommander.Core.Database;

namespace ShinyCommander.Services.Previewers;

/// <summary>SQLite databases: their tables and views, and the first rows of each - read only; F4 edits.</summary>
public sealed class SqliteFilePreviewer : IFilePreviewer
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { "db", "sqlite", "sqlite3", "db3" };

    public string Key => "sqlite";
    public string Title => L.Preview_Sqlite;
    public int Order => 100;

    public bool Handles(string fileName) => Extensions.Contains(Path.GetExtension(fileName).TrimStart('.'));

    // a .db is as often something else (Thumbs.db)
    public bool CanPreview(string path) => SqliteDatabase.IsSqliteFile(path);

    public async Task<View> CreateView(string path, CancellationToken ct)
    {
        var view = await MainThread.InvokeOnMainThreadAsync(() => new SqlitePreviewView(path));
        await view.LoadAsync(ct);
        return view;
    }
}
