#if DEBUG
using Shiny.Extensions.Stores;

namespace ShinyCommander.Testing;

/// <summary>
/// Debug-only switches for the UI test suite (tests/ShinyCommander.UITests). Nothing here does anything unless the
/// app was started with <c>SHINYCOMMANDER_UITEST=1</c> - which only the test fixture does.
/// <list type="bullet">
/// <item>Settings live in memory: a test run never reads or writes the developer's real preferences.</item>
/// <item>The panels start where the fixture says (<c>SHINYCOMMANDER_LEFT</c> / <c>SHINYCOMMANDER_RIGHT</c>).</item>
/// <item>Dialogs are answered from a script (<see cref="UiTestDialogs"/>), so no test ever blocks on a modal.</item>
/// </list>
/// </summary>
public static class UiTestMode
{
    public static bool IsEnabled { get; } = Environment.GetEnvironmentVariable("SHINYCOMMANDER_UITEST") == "1";

    public static void Configure(MauiAppBuilder builder)
    {
        if (!IsEnabled)
            return;

        var store = new MemoryKeyValueStore();
        if (Environment.GetEnvironmentVariable("SHINYCOMMANDER_LEFT") is { Length: > 0 } left)
            store.Set("LeftTabs", left);
        if (Environment.GetEnvironmentVariable("SHINYCOMMANDER_RIGHT") is { Length: > 0 } right)
            store.Set("RightTabs", right);
        Shiny.Stores.Register(StoreKeys.Default, store);

        builder.Services.AddSingleton<ShinyAppDialogs>();
        builder.Services.AddSingleton<UiTestDialogs>();
        builder.Services.AddSingleton<IAppDialogs>(sp => sp.GetRequiredService<UiTestDialogs>());
        builder.Services.AddSingleton<ITrashService, UiTestTrash>();
    }

    /// <summary>A trash that never touches the developer's real one: "trashed" items move into SHINYCOMMANDER_TRASH.</summary>
    sealed class UiTestTrash : ITrashService
    {
        static readonly string Dir = Environment.GetEnvironmentVariable("SHINYCOMMANDER_TRASH") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "shinycommander-uitest-trash");

        public bool TryMoveToTrash(string path)
        {
            var target = Path.Combine(Dir, Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Dir);
            if (Directory.Exists(path))
                Directory.Move(path, target);
            else
                File.Move(path, target);
            return true;
        }
    }
}
#endif
