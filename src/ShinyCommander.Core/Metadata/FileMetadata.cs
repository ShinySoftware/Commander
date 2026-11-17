namespace ShinyCommander.Core.Metadata;

/// <summary>What a file's custom columns can show (<see cref="FileMetadataReader"/>). A field the file doesn't have stays null.</summary>
public sealed record FileMetadata
{
    public static readonly FileMetadata Empty = new();

    // pictures and video
    public int? Width { get; init; }
    public int? Height { get; init; }

    // audio and video
    public TimeSpan? Duration { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }

    /// <summary>Audio bitrate, kbit/s.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Hz.</summary>
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }

    // photos (EXIF)
    public DateTime? DateTaken { get; init; }
    public string? Camera { get; init; }
    public string? Lens { get; init; }
    public double? FNumber { get; init; }

    /// <summary>Seconds.</summary>
    public double? ExposureTime { get; init; }
    public int? Iso { get; init; }

    /// <summary>Millimetres.</summary>
    public double? FocalLength { get; init; }

    /// <summary>EXIF orientation, 1-8.</summary>
    public int? Orientation { get; init; }
    public bool? HasLocation { get; init; }

    // tags (music) and document properties
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public int? Track { get; init; }
    public int? Year { get; init; }

    // documents
    /// <summary>Pages of a PDF or Word document, slides of a presentation.</summary>
    public int? Pages { get; init; }
    public int? Words { get; init; }
    public string? Author { get; init; }

    /// <summary>Lines of a text file.</summary>
    public int? Lines { get; init; }

    // archives
    public int? FileCount { get; init; }
    public long? UnpackedSize { get; init; }

    // apps and executables
    public string? Version { get; init; }
    public string? Company { get; init; }
}
