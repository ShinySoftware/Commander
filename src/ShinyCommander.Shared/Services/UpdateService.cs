using System.Reflection;
using System.Text.Json;

namespace ShinyCommander.Services;

/// <summary>
/// New releases, from the repo's GitHub releases (public, no token needed). Checked at launch and once an
/// hour after (the app can stay open for days), or on demand from Settings.
/// </summary>
/// <remarks>
/// Prereleases count. A release is only offered when it carries this head's download (see
/// <see cref="IAppUpdater.AssetName"/>), so a release still being built isn't. Without an
/// <see cref="IAppUpdater"/> (Windows, Linux) nothing is ever checked.
/// </remarks>
[Singleton]
public sealed class UpdateService
{
    const string ReleasesUrl = "https://api.github.com/repos/ShinySoftware/Commander/releases?per_page=20";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    readonly AppSettings settings;
    readonly IAppUpdater? updater;
    readonly IDispatcher dispatcher;
    readonly TimeProvider time;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    ITimer? timer;
    /// <summary>The check in flight, so a click on Check for Updates during the hourly one waits on it.</summary>
    Task<bool>? checking;
    /// <summary>The release last announced through <see cref="Found"/>, so an hourly check doesn't ask twice.</summary>
    string? announced;

    public UpdateService(AppSettings settings, IEnumerable<IAppUpdater> updaters, IDispatcher dispatcher, TimeProvider time)
    {
        this.settings = settings;
        this.updater = updaters.FirstOrDefault();
        this.dispatcher = dispatcher;
        this.time = time;
        // GitHub's API turns away requests without one.
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("ShinyCommander");
        this.http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public bool IsSupported => this.updater != null;

    /// <summary>Opens the release notes and Install in their window. Set by the app, which owns the windows.</summary>
    public Action<AppRelease>? ShowRelease { get; set; }

    /// <summary>
    /// The running version. A release build is stamped with its whole tag (1.2.0-beta2) as the
    /// informational version; the bundle's own version can only hold the numbers.
    /// </summary>
    public string CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } v
            ? v.Split('+')[0]
            : AppInfo.VersionString;

    /// <summary>A newer release than this one, or null.</summary>
    public AppRelease? Available { get; private set; }

    public bool IsInstalling { get; private set; }

    /// <summary><see cref="Available"/> or <see cref="IsInstalling"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>A newer release turned up that the user hasn't said not to show again. Raised once per release.</summary>
    public event EventHandler<AppRelease>? Found;

    /// <summary>Checks now, then once an hour. Does nothing where there's no updater.</summary>
    public void Start()
    {
        if (this.updater == null || this.timer != null)
            return;
        this.timer = this.time.CreateTimer(_ => this.dispatcher.Dispatch(() => _ = this.CheckAsync()), null, TimeSpan.Zero, CheckEvery);
    }

    /// <summary>"Don't show again": no more asking about this release. A newer one asks again.</summary>
    public void Skip(AppRelease release) => this.settings.SkippedUpdate = release.Version;

    /// <summary>Asks GitHub for a newer release, updating <see cref="Available"/>.</summary>
    /// <returns>False when GitHub couldn't be reached (offline, rate limited), or there's nothing to check with.</returns>
    public Task<bool> CheckAsync()
    {
        if (this.updater == null || this.IsInstalling)
            return Task.FromResult(false);

        if (this.checking is { IsCompleted: false } running)
            return running;
        return this.checking = this.CheckCoreAsync(this.updater);
    }

    async Task<bool> CheckCoreAsync(IAppUpdater updater)
    {
        try
        {
            var newest = await this.FetchNewestAsync(updater).ConfigureAwait(true);
            var available = newest != null && ReleaseVersion.Compare(newest.Version, this.CurrentVersion) > 0 ? newest : null;
            if (available?.Version != this.Available?.Version)
            {
                this.Available = available;
                this.Changed?.Invoke(this, EventArgs.Empty);
            }

            if (available != null && available.Version != this.announced && available.Version != this.settings.SkippedUpdate)
            {
                this.announced = available.Version;
                this.Found?.Invoke(this, available);
            }
            return true;
        }
        catch
        {
            // Offline, rate limited or GitHub having a moment: the next hour's check will do.
            return false;
        }
    }

    /// <summary>Installs <see cref="Available"/>. On success the app quits and restarts as the new version.</summary>
    /// <exception cref="Exception">The install failed; the message is fit to show.</exception>
    public async Task InstallAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        if (this.updater == null || this.Available is not { } release || this.IsInstalling)
            return;

        this.IsInstalling = true;
        this.Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await this.updater.InstallAsync(release, progress, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            this.IsInstalling = false;
            this.Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    async Task<AppRelease?> FetchNewestAsync(IAppUpdater updater)
    {
        using var doc = JsonDocument.Parse(await this.http.GetStringAsync(ReleasesUrl).ConfigureAwait(true));

        AppRelease? newest = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean() || r.GetProperty("tag_name").GetString() is not { } tag)
                continue;

            var version = tag.TrimStart('v', 'V');
            var assetName = updater.AssetName(version);
            var download = r.GetProperty("assets").EnumerateArray()
                .Where(a => a.GetProperty("name").GetString() == assetName)
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault();
            if (download == null || ReleaseVersion.Compare(version, "0") == null)
                continue;

            if (newest == null || ReleaseVersion.Compare(version, newest.Version) > 0)
            {
                newest = new AppRelease(
                    version,
                    r.GetProperty("name").GetString() is { Length: > 0 } name ? name : $"ShinyCommander {version}",
                    r.GetProperty("body").GetString() ?? "",
                    r.GetProperty("published_at").GetDateTimeOffset(),
                    r.GetProperty("prerelease").GetBoolean(),
                    new Uri(r.GetProperty("html_url").GetString()!),
                    new Uri(download)
                );
            }
        }
        return newest;
    }
}
