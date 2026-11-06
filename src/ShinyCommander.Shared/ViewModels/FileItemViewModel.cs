namespace ShinyCommander.ViewModels;

public enum CursorState
{
    None,
    Active,
    Inactive
}

public partial class FileItemViewModel(FileEntry entry, bool friendlySizes) : ObservableObject
{
    public FileEntry Entry { get; } = entry;

    public string Icon => FileIcons.For(this.Entry);
    public string MotionIcon => FileIcons.Motion(this.Entry).Icon;
    public Color IconColor => this.Cursor == CursorState.Active ? Colors.White : Color.FromArgb(FileIcons.Motion(this.Entry).Color);

    /// <summary>The icon under the active cursor gently animates.</summary>
    public bool IsActiveCursor => this.Cursor == CursorState.Active;
    public string Name => this.Entry.IsDirectory || this.Entry.IsParentLink ? this.Entry.Name : this.Entry.NameWithoutExtension;
    public string Extension => this.Entry.Extension;
    public string DateText => this.Entry.IsParentLink || this.Entry.Modified == DateTime.MinValue ? String.Empty : this.Entry.Modified.ToString("yyyy-MM-dd HH:mm");
    public string AttributeText => this.Entry.AttributeText;
    public bool IsDirectory => this.Entry.IsDirectory;
    public bool IsHidden => this.Entry.IsHidden;

    /// <summary>Relative path shown in branch view (Ctrl+B) where the list contains files from many folders.</summary>
    public string? Location { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long? CalculatedSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowState))]
    public partial bool IsMarked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowState), nameof(IsActiveCursor), nameof(IconColor))]
    public partial CursorState Cursor { get; set; }

    /// <summary>One mutually exclusive value for the row's DataTriggers (cursor x marked).</summary>
    public string RowState => (this.Cursor, this.IsMarked) switch
    {
        (CursorState.Active, true) => "ActiveMarked",
        (CursorState.Active, false) => "Active",
        (CursorState.Inactive, true) => "InactiveMarked",
        (CursorState.Inactive, false) => "Inactive",
        (_, true) => "Marked",
        _ => "Normal"
    };

    public long EffectiveSize => this.Entry.IsDirectory ? this.CalculatedSize ?? 0 : this.Entry.Size;

    public string SizeText
    {
        get
        {
            if (this.Entry.IsParentLink)
                return "<UP>";

            if (this.Entry.IsDirectory)
                return this.CalculatedSize is { } calc ? SizeFormatter.Format(calc, friendlySizes) : "<DIR>";

            return SizeFormatter.Format(this.Entry.Size, friendlySizes);
        }
    }
}
