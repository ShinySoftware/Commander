using System.IO.Compression;
using System.Text;
using ShinyCommander.Core.Metadata;
using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Tests;

public class FileMetadataReaderTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    static FileMetadata Read(string path) => FileMetadataReader.Read(FileEntry.FromInfo(new FileInfo(path)));

    static FileMetadata ReadDir(string path) => FileMetadataReader.Read(FileEntry.FromInfo(new DirectoryInfo(path)));

    [Fact]
    public void Video_has_duration_size_codecs_and_title()
    {
        var m = Read(Fixture("clip.mp4"));
        m.Duration!.Value.TotalSeconds.ShouldBe(2, 0.15);
        m.Width.ShouldBe(64);
        m.Height.ShouldBe(48);
        m.VideoCodec.ShouldNotBeNullOrEmpty();
        m.AudioCodec.ShouldNotBeNullOrEmpty();
        m.Title.ShouldBe("Test clip");
    }

    [Fact]
    public void Music_has_duration_tags_and_format()
    {
        var m = Read(Fixture("song.mp3"));
        m.Duration!.Value.TotalSeconds.ShouldBe(3, 0.15);
        m.Title.ShouldBe("Song");
        m.Artist.ShouldBe("Band");
        m.Album.ShouldBe("Record");
        m.Genre.ShouldBe("Rock");
        m.Track.ShouldBe(7);
        m.Year.ShouldBe(2024);
        m.Bitrate.ShouldBe(64);
        m.SampleRate.ShouldBe(44100);
        m.Channels.ShouldBe(2);
        m.Width.ShouldBeNull();
    }

    // lame writes the Info frame at 64 kbit/s in a 32 kbit/s file; timed by that frame it came out half as long
    [Fact]
    public void Low_bitrate_mp3_is_timed_by_its_frame_count() => Read(Fixture("low.mp3")).ShouldSatisfyAllConditions(
        m => m.Duration!.Value.TotalSeconds.ShouldBe(6, 0.1),
        m => m.Bitrate.ShouldBe(32)
    );

    [Fact]
    public void Wav_duration_comes_from_its_header()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "tone.wav");
        File.WriteAllBytes(path, Wav(8000, 1, TimeSpan.FromSeconds(1.5)));

        var m = Read(path);
        m.Duration!.Value.TotalSeconds.ShouldBe(1.5, 0.01);
        m.SampleRate.ShouldBe(8000);
        m.Channels.ShouldBe(1);
    }

    [Fact]
    public void Photo_has_size_camera_date_and_location()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "photo.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String(TinyJpeg));
        ImageMetadata.Write(path, new ImageMetadataValues
        {
            Make = "Canon",
            Model = "Canon EOS R5",
            DateTaken = new DateTime(2025, 7, 14, 18, 30, 0),
            Latitude = 46.2,
            Longitude = 6.15
        });

        var m = Read(path);
        m.Width.ShouldBe(8);
        m.Height.ShouldBe(8);
        m.Camera.ShouldBe("Canon EOS R5");
        m.DateTaken.ShouldBe(new DateTime(2025, 7, 14, 18, 30, 0));
        m.HasLocation.ShouldBe(true);
    }

    [Theory]
    [InlineData("Canon", "Canon EOS R5", "Canon EOS R5")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6", "NIKON Z 6")]
    [InlineData("Apple", "iPhone 16 Pro", "Apple iPhone 16 Pro")]
    [InlineData(null, "X100V", "X100V")]
    [InlineData("FUJIFILM", null, "FUJIFILM")]
    public void Camera_name_drops_the_repeated_make(string? make, string? model, string expected)
        => FileMetadataReader.Camera(make, model).ShouldBe(expected);

    [Fact]
    public void Heic_size_is_the_primary_picture() => Read(Fixture("pic.heic")).ShouldSatisfyAllConditions(
        m => m.Width.ShouldBe(160),
        m => m.Height.ShouldBe(90)
    );

    [Fact]
    public void Webp_size_comes_from_its_header()
    {
        using var dir = new TempDir();

        // extended (VP8X): 24-bit width-1 and height-1
        var extended = WebP("VP8X", [0, 0, 0, 0, 0x2B, 0x01, 0x00, 0xC7, 0x00, 0x00]);
        var lossless = WebP("VP8L", [0x2F, .. BitConverter.GetBytes((uint)(299 | 199 << 14))]);
        File.WriteAllBytes(Path.Combine(dir.Path, "a.webp"), extended);
        File.WriteAllBytes(Path.Combine(dir.Path, "b.webp"), lossless);

        Read(Path.Combine(dir.Path, "a.webp")).ShouldSatisfyAllConditions(m => m.Width.ShouldBe(300), m => m.Height.ShouldBe(200));
        Read(Path.Combine(dir.Path, "b.webp")).ShouldSatisfyAllConditions(m => m.Width.ShouldBe(300), m => m.Height.ShouldBe(200));
    }

    [Fact]
    public void Pdf_pages_come_from_the_page_tree() => Read(Fixture("doc.pdf")).Pages.ShouldBe(3);

    [Fact]
    public void Pdf_in_an_object_stream_still_has_pages_author_and_title()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "packed.pdf");
        // the page tree and the info dictionary compressed in one object stream (PDF 1.5); the title UTF-16 in hex
        var objects = "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 5 >> "
            + "<< /Type /Page /Parent 2 0 R >> "
            + "<< /Author (Ada \\(L\\)) /Title <FEFF004E006F007400E9> /Producer (test) >>";
        using (var output = new MemoryStream())
        {
            var head = Encoding.Latin1.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /ObjStm /N 3 /First 0 /Filter /FlateDecode >>\nstream\n");
            output.Write(head);
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(Encoding.Latin1.GetBytes(objects));
            output.Write(Encoding.Latin1.GetBytes("\nendstream\nendobj\n%%EOF\n"));
            File.WriteAllBytes(path, output.ToArray());
        }

        var m = Read(path);
        m.Pages.ShouldBe(5);
        m.Author.ShouldBe("Ada (L)");
        m.Title.ShouldBe("Noté");
    }

    [Fact]
    public void Office_documents_have_pages_words_and_author()
    {
        using var dir = new TempDir();
        var docx = Office(dir, "report.docx", "<Pages>12</Pages><Words>3400</Words>", "Grace", "Quarterly report");
        var pptx = Office(dir, "talk.pptx", "<Slides>9</Slides>", "Linus", null);

        Read(docx).ShouldSatisfyAllConditions(
            m => m.Pages.ShouldBe(12),
            m => m.Words.ShouldBe(3400),
            m => m.Author.ShouldBe("Grace"),
            m => m.Title.ShouldBe("Quarterly report")
        );
        Read(pptx).ShouldSatisfyAllConditions(m => m.Pages.ShouldBe(9), m => m.Author.ShouldBe("Linus"));
    }

    [Fact]
    public void Text_lines_count_the_last_line_with_or_without_a_newline()
    {
        using var dir = new TempDir();
        Read(dir.File("a.txt", "one\ntwo\nthree\n")).Lines.ShouldBe(3);
        Read(dir.File("b.cs", "one\r\ntwo\r\nthree")).Lines.ShouldBe(3);
        Read(dir.File("c.md", "")).Lines.ShouldBe(0);
        Read(dir.File("d.json", "x\0y")).Lines.ShouldBeNull();
    }

    [Fact]
    public void Zip_counts_its_files_and_their_unpacked_size()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "bundle.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("folder/");
            Entry(zip, "folder/a.txt", new string('a', 1000));
            Entry(zip, "b.txt", new string('b', 500));
            Entry(zip, "c.txt", "c");
        }

        var m = Read(path);
        m.FileCount.ShouldBe(3);
        m.UnpackedSize.ShouldBe(1501);
    }

    [Fact]
    public void App_bundle_version_reads_a_binary_info_plist()
    {
        using var dir = new TempDir();
        var app = dir.Dir("Demo.app/Contents");
        File.Copy(Fixture("Info.bplist"), Path.Combine(app, "Info.plist"));

        var m = ReadDir(Path.Combine(dir.Path, "Demo.app"));
        m.Version.ShouldBe("4.2.1");
        m.Company.ShouldBe("© 2026 Shiny");
    }

    [Fact]
    public void App_bundle_version_reads_an_xml_info_plist()
    {
        using var dir = new TempDir();
        dir.File("Demo.app/Contents/Info.plist", """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
                <key>CFBundleVersion</key><string>812</string>
                <key>LSUIElement</key><true/>
            </dict></plist>
            """);

        ReadDir(Path.Combine(dir.Path, "Demo.app")).Version.ShouldBe("812");
    }

    [Fact]
    public void A_broken_file_reads_as_empty_and_never_throws()
    {
        using var dir = new TempDir();
        Read(dir.File("fake.mp3", "not music")).ShouldBe(FileMetadata.Empty);
        Read(dir.File("fake.pdf", "not a pdf")).ShouldBe(FileMetadata.Empty);
        Read(dir.File("fake.docx", "not a zip")).ShouldBe(FileMetadata.Empty);
        Read(dir.File("fake.heic", "nothing")).ShouldBe(FileMetadata.Empty);
    }

    [Theory]
    [InlineData("a.JPG", FileKind.Image)]
    [InlineData("a.heic", FileKind.Image)]
    [InlineData("a.mkv", FileKind.Video)]
    [InlineData("a.flac", FileKind.Audio)]
    [InlineData("a.pdf", FileKind.Document)]
    [InlineData("a.cs", FileKind.Text)]
    [InlineData("a.zip", FileKind.Archive)]
    [InlineData("a.tar.gz", FileKind.Other)]
    [InlineData("a.exe", FileKind.App)]
    [InlineData("a.bin", FileKind.Other)]
    public void Kinds_are_picked_by_name(string name, FileKind kind)
        => FileKinds.Of(new FileEntry { Name = name, FullPath = "/x/" + name }).ShouldBe(kind);

    [Fact]
    public void Only_app_bundles_are_folders_with_a_kind()
    {
        FileKinds.Of(new FileEntry { Name = "Safari.app", FullPath = "/Applications/Safari.app", IsDirectory = true }).ShouldBe(FileKind.App);
        FileKinds.Of(new FileEntry { Name = "photos.jpg", FullPath = "/x/photos.jpg", IsDirectory = true }).ShouldBe(FileKind.Other);
    }

    // --- builders ---------------------------------------------------------------------------

    // an 8x8 JPEG (as in ImageMetadataTests)
    const string TinyJpeg = "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAACKADAAQAAAABAAAACAAAAAD/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9sAQwACAgICAgIDAgIDBQMDAwUGBQUFBQYIBgYGBgYICggICAgICAoKCgoKCgoKDAwMDAwMDg4ODg4PDw8PDw8PDw8P/9sAQwECAgIEBAQHBAQHEAsJCxAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ/90ABAAB/9oADAMBAAIRAxEAPwDwL48/FLStTt7vSrmOzE6TNa/u3b7Ujo2G3JhlA2kMD1KnIGcgfJP27TP+fl/1/wDjFaXxh/5KJrn/AGF5P/RMdcJXlUpVYU4RhVmkkvtP/M+hxFWHtZuVKLd+sUz/2Q==";

    static byte[] Wav(int sampleRate, short channels, TimeSpan duration)
    {
        var dataLength = (int)(sampleRate * duration.TotalSeconds) * channels * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataLength);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataLength);
        w.Write(new byte[dataLength]);
        return ms.ToArray();
    }

    static byte[] WebP(string chunk, byte[] payload)
    {
        var body = new byte[Math.Max(payload.Length, 18)];
        payload.CopyTo(body, 0);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(4 + 8 + body.Length);
        w.Write("WEBP"u8);
        w.Write(Encoding.ASCII.GetBytes(chunk));
        w.Write(body.Length);
        w.Write(body);
        return ms.ToArray();
    }

    static string Office(TempDir dir, string name, string appProperties, string? creator, string? title)
    {
        var path = Path.Combine(dir.Path, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(zip, "docProps/app.xml", $"""<?xml version="1.0"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">{appProperties}</Properties>""");
        Entry(zip, "docProps/core.xml", $"""<?xml version="1.0"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/">{(title == null ? "" : $"<dc:title>{title}</dc:title>")}<dc:creator>{creator}</dc:creator></cp:coreProperties>""");
        return path;
    }

    static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
