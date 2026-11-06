namespace ShinyCommander.Core.Models;

public sealed record DriveEntry(
    string Name,
    string RootPath,
    string Label,
    long TotalSize,
    long FreeSpace,
    DriveType DriveType
)
{
    public double UsedRatio => this.TotalSize <= 0 ? 0 : 1d - (double)this.FreeSpace / this.TotalSize;
    public override string ToString() => this.Name;
}
