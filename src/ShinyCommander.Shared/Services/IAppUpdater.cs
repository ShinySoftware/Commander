namespace ShinyCommander.Services;

/// <summary>
/// Installs a new release. Optional - a head registers one only where releases ship (macOS), and
/// without one the app never checks for updates.
/// </summary>
public interface IAppUpdater
{
    /// <summary>The release asset this head installs, for a version: ShinyCommander-1.2.0-macos-arm64.zip.</summary>
    string AssetName(string version);

    /// <summary>
    /// Downloads, checks and installs the release, then quits so it can start again as the new version.
    /// Throws, with a message fit to show, when it can't.
    /// </summary>
    /// <param name="progress">The download, from 0 to 1.</param>
    Task InstallAsync(AppRelease release, IProgress<double> progress, CancellationToken cancellationToken);
}
