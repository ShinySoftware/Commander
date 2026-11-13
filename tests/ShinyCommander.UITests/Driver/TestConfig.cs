namespace ShinyCommander.UITests.Driver;

/// <summary>Paths and switches for the suite. Everything can be overridden with environment variables (run.sh sets them).</summary>
public static class TestConfig
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SuiteDir => Path.Combine(RepoRoot, "tests", "ShinyCommander.UITests");

    public static string AppProject => Path.Combine(RepoRoot, "src", "ShinyCommander.MacOS", "ShinyCommander.MacOS.csproj");

    /// <summary>The suite's own build of the app (run.sh / <see cref="AppFixture.BuildApp"/>), apart from src/…/bin so it never fights a dev build.</summary>
    public static string AppBundle => Env("SC_UITEST_APP") ?? Path.Combine(SuiteDir, ".app-build", "bin", "ShinyCommander.app");

    public static string AppExecutable => Path.Combine(AppBundle, "Contents", "MacOS", "ShinyCommander");

    /// <summary>Remembers the app the suite launched (pid + agent port + launch time) so the next run can attach to it.</summary>
    public static string StateFile => Path.Combine(SuiteDir, ".uitest-app.json");

    public static string ArtifactDir => Path.Combine(SuiteDir, "artifacts");

    /// <summary>Per-test file trees live here (wiped at the start of each run).</summary>
    public static string WorkDir => Path.Combine(ArtifactDir, "work");

    /// <summary>Where the app's test trash puts "trashed" items (the real Trash is never used).</summary>
    public static string TrashDir => Path.Combine(ArtifactDir, "trash");

    /// <summary>Build the app before the run (run.sh does it itself, so it leaves this off).</summary>
    public static bool BuildBeforeRun => Flag("SC_UITEST_BUILD");

    /// <summary>
    /// Attach to the app a previous run left running instead of launching a new one - the difference between a
    /// one-test run taking ~2s and ~15s. Ignored (a fresh launch happens) when the app was rebuilt since.
    /// Default on.
    /// </summary>
    public static bool ReuseApp => !Flag("SC_UITEST_FRESH");

    /// <summary>Leave the app running when the run ends so the next run can attach. Default on.</summary>
    public static bool KeepApp => !Flag("SC_UITEST_QUIT");

    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    static bool Flag(string name) => Env(name) is "1" or "true" or "TRUE" or "yes";

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ShinyCommander.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (ShinyCommander.slnx) above " + AppContext.BaseDirectory);
    }
}
