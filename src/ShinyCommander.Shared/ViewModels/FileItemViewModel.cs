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
    public Color IconColor => ThemeColors.Get(this.Cursor == CursorState.Active ? "Accent" : FileIcons.Motion(this.Entry).Color);

    /// <summary>The icon under the active cursor gently animates.</summary>
    public bool IsActiveCursor => this.Cursor == CursorState.Active;

    ImageSource? systemIcon;
    bool systemIconLoaded;

    /// <summary>The system's icon for the row (SystemIcons) - null shows the motion icon instead.</summary>
    public ImageSource? SystemIcon
    {
        get
        {
            if (!this.systemIconLoaded)
            {
                this.systemIcon = SystemIcons.Get(this.Entry, 16);
                this.systemIconLoaded = true;
            }
            return this.systemIcon;
        }
    }
    public bool HasSystemIcon => this.SystemIcon != null;
    public bool ShowMotionIcon => !this.HasSystemIcon;

    /// <summary>A hidden motion icon doesn't animate.</summary>
    public bool IsMotionPlaying => this.IsActiveCursor && this.ShowMotionIcon;
    public string Name => this.Entry.IsDirectory || this.Entry.IsParentLink ? this.Entry.Name : this.Entry.NameWithoutExtension;
    public string Extension => this.Entry.Extension;
    public string DateText => this.Entry.IsParentLink || this.Entry.Modified == DateTime.MinValue ? String.Empty : this.Entry.Modified.ToString("yyyy-MM-dd HH:mm");
    public string CreatedText => this.Entry.IsParentLink || this.Entry.Created == DateTime.MinValue ? String.Empty : this.Entry.Created.ToString("yyyy-MM-dd HH:mm");

    /// <summary>Shared column visibility - the row's ColumnDefinitions bind their widths to it.</summary>
    public required ColumnLayout Columns { get; init; }

    /// <summary>The full file name - thumbnails show it under the picture.</summary>
    public string FullName => this.Entry.Name;

    public bool HasThumbnail => !this.Entry.IsDirectory && !this.Entry.IsInArchive && FileIcons.IsImage(this.Entry.Extension);

    ImageSource? thumbnail;
    ImageSource? systemTile;
    bool systemTileLoaded;

    /// <summary>Thumbnail view: local images load lazily as the cell binds; everything else shows its system or motion icon.</summary>
    public ImageSource? Thumbnail => this.HasThumbnail ? this.thumbnail ??= ImageSource.FromFile(this.Entry.FullPath) : null;

    public ImageSource? SystemTile
    {
        get
        {
            if (!this.systemTileLoaded)
            {
                this.systemTile = this.HasThumbnail ? null : SystemIcons.Get(this.Entry, 54);
                this.systemTileLoaded = true;
            }
            return this.systemTile;
        }
    }
    public bool HasSystemTile => this.SystemTile != null;
    public bool ShowIconTile => !this.HasThumbnail && !this.HasSystemTile;
    public bool IsTilePlaying => this.IsActiveCursor && this.ShowIconTile;
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
    [NotifyPropertyChangedFor(nameof(RowState), nameof(IsActiveCursor), nameof(IsMotionPlaying), nameof(IsTilePlaying), nameof(IconColor))]
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
