using System.ComponentModel;
using ShinyCommander.Share;

namespace ShinyCommander.Services;

/// <summary>Whether the computer is kept from sleeping while work is running (Settings → Keep awake).</summary>
public enum KeepAwakePolicy
{
    /// <summary>The app does nothing - the operating system sleeps as it normally would.</summary>
    PlatformDefault,

    /// <summary>Kept awake while work runs, but only on mains power - unplugging lets it sleep again.</summary>
    PluggedIn,

    /// <summary>Kept awake while work runs, on battery too.</summary>
    Always
}

/// <summary>
/// Keeps the computer awake (Amphetamine-style) while anything is working: a job in the queue (copy, move, delete,
/// pack...) or a share's transfers (as the Sharing window's own keep-awake choice asks - <see cref="SharingService.WantsKeepAwake"/>).
/// <see cref="Policy"/> decides whether that applies at all, and on battery. The power source is checked again every
/// <see cref="PowerCheckInterval"/> while there's work, so unplugging mid-copy lets the machine sleep when the policy
/// says so. The only holder of the app's <see cref="SleepInhibitor"/>.
/// </summary>
[Singleton]
public partial class KeepAwakeService : ObservableObject, IDisposable
{
    public static readonly TimeSpan PowerCheckInterval = TimeSpan.FromSeconds(30);

    readonly AppSettings settings;
    readonly JobQueue jobs;
    readonly SharingService sharing;
    readonly TimeProvider time;
    readonly SleepInhibitor sleep = new();
    readonly Lock gate = new();
    ITimer? powerTimer;
    // read on the UI thread, used from the timer's thread too
    volatile bool busy;
    bool disposed;

    public KeepAwakeService(AppSettings settings, JobQueue jobs, SharingService sharing, TimeProvider time)
    {
        this.settings = settings;
        this.jobs = jobs;
        this.sharing = sharing;
        this.time = time;
        this.HasBattery = PowerSource.Read().HasBattery;
        jobs.PropertyChanged += this.OnJobsChanged;
        sharing.Changed += this.OnSharingChanged;
        this.Refresh();
    }

    /// <summary>The machine has a battery (or UPS) - without one "on battery too" means nothing, so Settings hides it.</summary>
    public bool HasBattery { get; }

    /// <summary>Defaults to <see cref="KeepAwakePolicy.PluggedIn"/>.</summary>
    public KeepAwakePolicy Policy
    {
        get => Enum.TryParse<KeepAwakePolicy>(this.settings.KeepAwake, out var policy) ? policy : KeepAwakePolicy.PluggedIn;
        set
        {
            if (value == this.Policy)
                return;

            this.settings.KeepAwake = value.ToString();
            this.OnPropertyChanged();
            this.Apply();
        }
    }

    void OnJobsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobQueue.RunningCount))
            this.Refresh();
    }

    void OnSharingChanged(object? sender, EventArgs e) => this.Refresh();

    /// <summary>Reads what's running (UI thread) and applies the policy.</summary>
    void Refresh()
    {
        this.busy = this.jobs.RunningCount > 0 || this.sharing.WantsKeepAwake;
        this.Apply();
    }

    void OnPowerTimer(object? state) => this.Apply();

    void Apply()
    {
        lock (this.gate)
        {
            if (this.disposed)
                return;

            var busy = this.busy;
            var policy = this.Policy;
            var hold = busy && policy switch
            {
                KeepAwakePolicy.Always => true,
                KeepAwakePolicy.PluggedIn => !PowerSource.Read().OnBattery,
                _ => false
            };

            this.sleep.Set(hold, "ShinyCommander is working");

            // only "plugged in" depends on the power source - watch it while there's work to hold for
            var watch = busy && policy == KeepAwakePolicy.PluggedIn;
            if (watch && this.powerTimer == null)
            {
                this.powerTimer = this.time.CreateTimer(this.OnPowerTimer, null, PowerCheckInterval, PowerCheckInterval);
            }
            else if (!watch && this.powerTimer != null)
            {
                this.powerTimer.Dispose();
                this.powerTimer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
                return;

            this.disposed = true;
            this.powerTimer?.Dispose();
            this.powerTimer = null;
        }
        this.jobs.PropertyChanged -= this.OnJobsChanged;
        this.sharing.Changed -= this.OnSharingChanged;
        this.sleep.Dispose();
    }
}
