using ShinyCommander.Core.Database;

namespace ShinyCommander.Services.Editors;

/// <summary>SQLite databases: browse, edit, insert and delete rows, and run SQL.</summary>
public sealed class SqliteFileEditor(INavigator navigator) : IFileEditor
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { "db", "sqlite", "sqlite3", "db3" };

    public int Order => 100;

    // a .db is as often something else (Thumbs.db) - only one with SQLite's header is a database
    public bool CanEdit(string path) => Extensions.Contains(Path.GetExtension(path).TrimStart('.')) && SqliteDatabase.IsSqliteFile(path);

    public Task Open(string path) => navigator.NavigateTo<DatabaseViewModel>(vm => vm.FilePath = path);
}
