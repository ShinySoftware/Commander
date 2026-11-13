namespace ShinyCommander.ViewModels;

public enum JobState
{
    Running,
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled
}

/// <summary>Where a finished job's result lives - "Go to" opens this folder with the cursor on <see cref="FocusName"/>.</summary>
public sealed record JobDestination(string Directory, string? FocusName = null);

/// <summary>One background copy/move/delete/pack/unpack in the jobs pane (<see cref="Services.JobQueue"/>).</summary>
public partial class JobViewModel : ObservableObject
{
    readonly CancellationTokenSource cts = new();
    readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly DateTime started = DateTime.Now;

    public JobViewModel(string title, FileOperationKind kind, JobDestination? destination)
    {
        this.Title = title;
        this.Kind = kind;
        this.Destination = destination;
    }

    public string Title { get; }
    public FileOperationKind Kind { get; }
    public JobDestination? Destination { get; }
    public CancellationToken Token => this.cts.Token;

    /// <summary>Completes once the job has finished, whatever the outcome - it never faults.</summary>
    public Task Completion => this.completion.Task;

    public string Icon => this.Kind switch
    {
        FileOperationKind.Copy => "copy",
        FileOperationKind.Move => "arrow-right",
        FileOperationKind.Delete => "trash",
        FileOperationKind.Pack => "download",
        _ => "upload"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFinished), nameof(CanGoTo), nameof(IsSucceeded), nameof(IsProblem), nameof(StateLabel), nameof(StatePill), nameof(Subtitle))]
    public partial JobState State { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string Detail { get; set; } = L.Job_Preparing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    public partial IReadOnlyList<string> Errors { get; private set; } = [];

    public bool IsRunning => this.State == JobState.Running;
    public bool IsFinished => !this.IsRunning;
    public bool IsSucceeded => this.State == JobState.Completed;
    public bool IsProblem => this.State is JobState.Failed or JobState.CompletedWithErrors;
    public bool HasErrors => this.Errors.Count > 0;
    public bool CanGoTo => this.Destination != null && this.State is JobState.Completed or JobState.CompletedWithErrors;

    public OperationResult? Result { get; private set; }
    public TimeSpan Duration { get; private set; }

    public string StateLabel => this.State switch
    {
        JobState.Running => L.Job_StateRunning,
        JobState.Completed => L.Job_StateDone,
        JobState.CompletedWithErrors => L.Job_StateErrors,
        JobState.Failed => L.Job_StateFailed,
        _ => L.Job_StateStopped
    };

    public Shiny.Maui.Controls.PillType StatePill => this.State switch
    {
        JobState.Running => Shiny.Maui.Controls.PillType.Info,
        JobState.Completed => Shiny.Maui.Controls.PillType.Success,
        JobState.Failed => Shiny.Maui.Controls.PillType.Critical,
        _ => Shiny.Maui.Controls.PillType.Warning
    };

    /// <summary>The line under the title: live progress, then how it went and where things ended up.</summary>
    public string Subtitle => this.State switch
    {
        JobState.Running => this.Detail,
        JobState.Failed => this.Errors.FirstOrDefault() ?? L.Job_StateFailed,
        JobState.Cancelled => L.Job_StoppedPartWay,
        _ => $"{this.Summary}{(this.Destination is { } d ? "  →  " + d.Directory : String.Empty)}"
    };

    string Summary => this.State == JobState.CompletedWithErrors
        ? L.Job_SummaryErrorsFormat(this.Errors.Count, this.Result?.Processed ?? 0, Seconds(this.Duration))
        : this.Result is { Skipped: > 0 } r
            ? L.Job_SummarySkippedFormat(r.Processed, r.Skipped, Seconds(this.Duration))
            : L.Job_SummaryFormat(this.Result?.Processed ?? 0, Seconds(this.Duration));

    static string Seconds(TimeSpan t) => t.TotalSeconds < 10 ? $"{t.TotalSeconds:0.0}s" : t.TotalMinutes < 1 ? $"{t.TotalSeconds:0}s" : $"{(int)t.TotalMinutes}m {t.Seconds}s";

    public void Stop()
    {
        if (this.IsRunning)
            this.cts.Cancel();
    }

    public void Report(OperationProgress p)
    {
        this.Progress = p.Fraction;
        this.Detail = p.BytesTotal > 0
            ? $"{p.CurrentItem}  •  {SizeFormatter.Friendly(p.BytesDone)} / {SizeFormatter.Friendly(p.BytesTotal)}  •  {p.ItemsDone}/{p.ItemsTotal}"
            : $"{p.CurrentItem}  •  {p.ItemsDone}/{p.ItemsTotal}";
    }

    internal void Finish(OperationResult result)
    {
        this.Result = result;
        this.Errors = result.Errors;
        this.Progress = 1;
        this.End(result.HasErrors ? JobState.CompletedWithErrors : JobState.Completed);
    }

    internal void Fail(string error)
    {
        this.Errors = [error];
        this.End(JobState.Failed);
    }

    internal void Cancelled() => this.End(JobState.Cancelled);

    void End(JobState state)
    {
        this.Duration = DateTime.Now - this.started;
        this.State = state;
        this.completion.TrySetResult();
    }
}
