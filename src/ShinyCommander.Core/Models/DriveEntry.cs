namespace ShinyCommander.Core.Models;

/// <summary>What a drive bar entry points at - a real volume, or a well-known folder that lives on one.</summary>
public enum DriveKind
{
    Volume,
    Removable,
    Network,
    Home,
    Special,
    Cloud,
    /// <summary>A saved server or storage account (Remote.RemoteConnection) - RootPath is its URI root.</summary>
    Remote
}

public sealed record DriveEntry(
    string Name,
    string RootPath,
    string Label,
    long TotalSize,
    long FreeSpace,
    DriveType DriveType,
    DriveKind Kind = DriveKind.Volume,
    string? OpenPath = null
)
{
    /// <summary>Where a click goes - a connection's start folder, otherwise the root.</summary>
    public string Target => this.OpenPath ?? this.RootPath;

    /// <summary>A shortcut to a folder on some other drive (Home, Documents...) - never the "current drive".</summary>
    public bool IsPlace => this.Kind is DriveKind.Home or DriveKind.Special;
    public double UsedRatio => this.TotalSize <= 0 ? 0 : 1d - (double)this.FreeSpace / this.TotalSize;
    public override string ToString() => this.Name;
}
