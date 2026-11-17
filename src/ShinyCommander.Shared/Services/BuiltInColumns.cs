using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using ShinyCommander.Core.Metadata;

namespace ShinyCommander.Services;

/// <summary>The custom columns and column sets that come with the app.</summary>
public static class BuiltInColumns
{
    const FileKind Image = FileKind.Image, Video = FileKind.Video, Audio = FileKind.Audio, Document = FileKind.Document;

    public static IServiceCollection AddBuiltInFileColumns(this IServiceCollection services)
    {
        foreach (var column in Columns())
            services.AddFileColumn(column);

        services
            .AddFileColumnSet(new("photos", () => L.ColumnSet_Photos, [Image], ["dimensions", "taken", "camera"]))
            .AddFileColumnSet(new("video", () => L.ColumnSet_Video, [Video], ["duration", "dimensions", "videocodec"]))
            .AddFileColumnSet(new("music", () => L.ColumnSet_Music, [Audio], ["duration", "artist", "album", "track"]))
            .AddFileColumnSet(new("documents", () => L.ColumnSet_Documents, [Document], ["pages", "author"]))
            .AddFileColumnSet(new("text", () => L.ColumnSet_Text, [FileKind.Text], ["lines"]))
            .AddFileColumnSet(new("archives", () => L.ColumnSet_Archives, [FileKind.Archive], ["files", "unpacked"]))
            .AddFileColumnSet(new("apps", () => L.ColumnSet_Apps, [FileKind.App], ["version", "company"]));
        return services;
    }

    static IEnumerable<FileColumn> Columns() =>
    [
        // pictures and video
        new("dimensions", () => L.Column_Dimensions, 92, true, [Image, Video],
            m => m.Width is { } w && m.Height is { } h ? (long)w * h : null,
            m => m.Width is { } w && m.Height is { } h ? $"{w}×{h}" : null),
        new("duration", () => L.Column_Duration, 68, true, [Video, Audio],
            m => m.Duration,
            m => m.Duration is { } d ? Duration(d) : null),

        // photos
        new("taken", () => L.Column_DateTaken, 122, false, [Image],
            m => m.DateTaken,
            m => m.DateTaken?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        new("camera", () => L.Column_Camera, 140, false, [Image], m => m.Camera),
        new("lens", () => L.Column_Lens, 140, false, [Image], m => m.Lens),
        new("aperture", () => L.Column_Aperture, 60, true, [Image],
            m => m.FNumber,
            m => m.FNumber is { } f ? $"f/{f.ToString("0.#", CultureInfo.CurrentCulture)}" : null),
        new("shutter", () => L.Column_Shutter, 70, true, [Image],
            m => m.ExposureTime,
            m => m.ExposureTime is { } t ? Shutter(t) : null),
        new("iso", () => L.Column_Iso, 56, true, [Image], m => m.Iso),
        new("focal", () => L.Column_FocalLength, 70, true, [Image],
            m => m.FocalLength,
            m => m.FocalLength is { } f ? $"{f.ToString("0.#", CultureInfo.CurrentCulture)} mm" : null),
        new("orientation", () => L.Column_Orientation, 100, false, [Image],
            m => m.Orientation,
            m => m.Orientation is { } o ? Orientation(o) : null),
        new("location", () => L.Column_Location, 64, false, [Image],
            m => m.HasLocation,
            m => m.HasLocation == true ? "✓" : null),

        // video and audio streams
        new("videocodec", () => L.Column_VideoCodec, 110, false, [Video], m => m.VideoCodec),
        new("audiocodec", () => L.Column_AudioCodec, 110, false, [Video, Audio], m => m.AudioCodec),
        new("bitrate", () => L.Column_Bitrate, 80, true, [Audio],
            m => m.Bitrate,
            m => m.Bitrate is { } b ? $"{b} kbps" : null),
        new("samplerate", () => L.Column_SampleRate, 80, true, [Audio],
            m => m.SampleRate,
            m => m.SampleRate is { } r ? $"{(r / 1000.0).ToString("0.#", CultureInfo.CurrentCulture)} kHz" : null),

        // tags
        new("title", () => L.Column_Title, 150, false, [Audio, Video, Document, Image], m => m.Title),
        new("artist", () => L.Column_Artist, 130, false, [Audio], m => m.Artist),
        new("album", () => L.Column_Album, 130, false, [Audio], m => m.Album),
        new("track", () => L.Column_Track, 52, true, [Audio], m => m.Track),
        new("year", () => L.Column_Year, 52, true, [Audio], m => m.Year),
        new("genre", () => L.Column_Genre, 96, false, [Audio], m => m.Genre),

        // documents, text, archives, apps
        new("pages", () => L.Column_Pages, 60, true, [Document], m => m.Pages),
        new("words", () => L.Column_Words, 70, true, [Document],
            m => m.Words,
            m => m.Words?.ToString("N0", CultureInfo.CurrentCulture)),
        new("author", () => L.Column_Author, 130, false, [Document], m => m.Author),
        new("lines", () => L.Column_Lines, 70, true, [FileKind.Text],
            m => m.Lines,
            m => m.Lines?.ToString("N0", CultureInfo.CurrentCulture)),
        new("files", () => L.Column_Files, 64, true, [FileKind.Archive],
            m => m.FileCount,
            m => m.FileCount?.ToString("N0", CultureInfo.CurrentCulture)),
        new("unpacked", () => L.Column_Unpacked, 92, true, [FileKind.Archive],
            m => m.UnpackedSize,
            m => m.UnpackedSize is { } s ? SizeFormatter.Friendly(s) : null),
        new("version", () => L.Column_Version, 96, false, [FileKind.App], m => m.Version),
        new("company", () => L.Column_Company, 160, false, [FileKind.App], m => m.Company)
    ];

    /// <summary>"3:07", or "1:02:45" past an hour.</summary>
    public static string Duration(TimeSpan d) => d.TotalHours >= 1
        ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
        : $"{d.Minutes}:{d.Seconds:00}";

    /// <summary>"1/250" under a second, "2.5s" over.</summary>
    public static string Shutter(double seconds) => seconds >= 0.3
        ? seconds.ToString("0.#", CultureInfo.CurrentCulture) + "s"
        : $"1/{Math.Round(1 / seconds)}";

    static string Orientation(int exif) => exif switch
    {
        1 => L.Orientation_Normal,
        3 => L.Orientation_Rotated180,
        6 => L.Orientation_Rotated90,
        8 => L.Orientation_Rotated270,
        _ => L.Orientation_Mirrored
    };
}
