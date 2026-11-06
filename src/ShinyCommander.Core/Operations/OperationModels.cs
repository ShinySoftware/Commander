namespace ShinyCommander.Core.Operations;

public enum FileOperationKind
{
    Copy,
    Move,
    Delete,
    Pack,
    Extract
}

public readonly record struct OperationProgress(
    FileOperationKind Kind,
    string CurrentItem,
    long BytesDone,
    long BytesTotal,
    int ItemsDone,
    int ItemsTotal
)
{
    public double Fraction => this.BytesTotal > 0
        ? Math.Clamp((double)this.BytesDone / this.BytesTotal, 0, 1)
        : this.ItemsTotal > 0 ? Math.Clamp((double)this.ItemsDone / this.ItemsTotal, 0, 1) : 0;
}

public enum ConflictAction
{
    Overwrite,
    OverwriteAll,
    OverwriteAllOlder,
    Skip,
    SkipAll,
    KeepBoth,
    Cancel
}

public sealed record FileConflict(
    string SourcePath,
    long SourceSize,
    DateTime SourceModified,
    string TargetPath,
    long TargetSize,
    DateTime TargetModified
);

public delegate Task<ConflictAction> ConflictResolver(FileConflict conflict, CancellationToken ct);

public sealed record OperationResult(int Processed, int Skipped, IReadOnlyList<string> Errors)
{
    public static OperationResult Empty { get; } = new(0, 0, []);
    public bool HasErrors => this.Errors.Count > 0;
}

/// <summary>
/// Where a copy/move goes. <see cref="RenameTo"/> is only used when a single source item is being
/// copied/moved - it's how "F6 rename" and "copy as" work, exactly like TC's target text box.
/// </summary>
public sealed record OperationTarget(string Directory, string? RenameTo = null);
