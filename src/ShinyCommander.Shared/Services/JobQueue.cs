namespace ShinyCommander.Services;

/// <summary>
/// The background processor: copy/move/delete/pack/unpack run here so the panels stay usable while they work.
/// Jobs run side by side; each can be stopped, and finished ones stay listed (with "Go to") until cleared.
/// </summary>
[Singleton]
public partial class JobQueue : ObservableObject
{
    // jobs whose follow-up (refresh, toast) hasn't finished yet - IsBusy stays true until then
    int pending;

    /// <summary>Newest first.</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public int RunningCount => this.Jobs.Count(x => x.IsRunning);
    public bool HasJobs => this.Jobs.Count > 0;
    public bool IsEmpty => this.Jobs.Count == 0;
    public bool HasFinished => this.Jobs.Any(x => x.IsFinished);
    public bool IsBusy => this.pending > 0;

    /// <summary>Jobs started and not yet completely finished (follow-up included).</summary>
    public int PendingCount => this.pending;

    /// <summary>The command line's jobs chip: "2 running" while working, then "3 done".</summary>
    public string Summary
    {
        get
        {
            var running = this.RunningCount;
            if (running > 0)
                return L.Jobs_RunningFormat(running);

            var problems = this.Jobs.Count(x => x.IsProblem);
            return problems > 0 ? L.Jobs_FailedFormat(problems) : L.Jobs_DoneFormat(this.Jobs.Count);
        }
    }

    public event EventHandler<JobViewModel>? Started;

    /// <summary>
    /// Starts <paramref name="work"/> in the background. <paramref name="finished"/> runs on the UI thread once it
    /// ends (however it ends) and before <see cref="IsBusy"/> clears.
    /// </summary>
    public JobViewModel Start(
        string title,
        FileOperationKind kind,
        JobDestination? destination,
        Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult>> work,
        Func<JobViewModel, Task>? finished = null
    )
    {
        var job = new JobViewModel(title, kind, destination);
        job.PropertyChanged += this.OnJobPropertyChanged;
        this.Jobs.Insert(0, job);
        this.pending++;
        this.Notify();
        this.Started?.Invoke(this, job);
        _ = this.Run(job, work, finished);
        return job;
    }

    async Task Run(JobViewModel job, Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult>> work, Func<JobViewModel, Task>? finished)
    {
        var lastUpdate = 0L;
        // created on the UI thread, so reports arrive there
        var progress = new Progress<OperationProgress>(p =>
        {
            // throttle UI updates
            var now = Environment.TickCount64;
            if (now - lastUpdate < 50 && p.ItemsDone < p.ItemsTotal)
                return;
            lastUpdate = now;
            if (job.IsRunning)
                job.Report(p);
        });

        try
        {
            job.Finish(await work(progress, job.Token));
        }
        catch (OperationCanceledException)
        {
            job.Cancelled();
        }
        catch (Exception ex)
        {
            job.Fail(ex.Message);
        }

        try
        {
            if (finished != null)
                await finished(job);
        }
        catch
        {
            // a follow-up failing (a refresh, a toast) doesn't change what the job did
        }
        finally
        {
            this.pending--;
            this.Notify();
        }
    }

    void OnJobPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobViewModel.State))
            this.Notify();
    }

    public void Remove(JobViewModel job)
    {
        if (job.IsRunning)
            return;

        job.PropertyChanged -= this.OnJobPropertyChanged;
        this.Jobs.Remove(job);
        this.Notify();
    }

    public void ClearFinished()
    {
        foreach (var job in this.Jobs.Where(x => x.IsFinished).ToList())
            this.Remove(job);
    }

    public void StopAll()
    {
        foreach (var job in this.Jobs)
            job.Stop();
    }

    /// <summary>Stops everything and waits (up to <paramref name="timeout"/>) for the jobs to clean up after themselves.</summary>
    public async Task StopAllAndWait(TimeSpan timeout)
    {
        var running = this.Jobs.Where(x => x.IsRunning).Select(x => x.Completion).ToList();
        this.StopAll();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(timeout));
    }

    void Notify()
    {
        this.OnPropertyChanged(nameof(this.RunningCount));
        this.OnPropertyChanged(nameof(this.HasJobs));
        this.OnPropertyChanged(nameof(this.IsEmpty));
        this.OnPropertyChanged(nameof(this.HasFinished));
        this.OnPropertyChanged(nameof(this.IsBusy));
        this.OnPropertyChanged(nameof(this.Summary));
    }
}
