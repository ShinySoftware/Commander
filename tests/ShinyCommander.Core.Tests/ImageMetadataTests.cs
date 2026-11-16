using ShinyCommander.Core.Metadata;

namespace ShinyCommander.Core.Tests;

public class ImageMetadataTests
{
    // an 8x8 JPEG with no EXIF of its own
    const string TinyJpeg = "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAACKADAAQAAAABAAAACAAAAAD/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9sAQwACAgICAgIDAgIDBQMDAwUGBQUFBQYIBgYGBgYICggICAgICAoKCgoKCgoKDAwMDAwMDg4ODg4PDw8PDw8PDw8P/9sAQwECAgIEBAQHBAQHEAsJCxAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ/90ABAAB/9oADAMBAAIRAxEAPwDwL48/FLStTt7vSrmOzE6TNa/u3b7Ujo2G3JhlA2kMD1KnIGcgfJP27TP+fl/1/wDjFaXxh/5KJrn/AGF5P/RMdcJXlUpVYU4RhVmkkvtP/M+hxFWHtZuVKLd+sUz/2Q==";

    static string Jpeg(TempDir dir, string name = "photo.jpg")
    {
        var path = Path.Combine(dir.Path, name);
        File.WriteAllBytes(path, Convert.FromBase64String(TinyJpeg));
        return path;
    }

    // everything from the start-of-scan marker on is the compressed picture
    static byte[] ScanData(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xDA)
                return bytes[i..];
        }
        throw new InvalidOperationException("no SOS marker");
    }

    [Fact]
    public void Only_real_jpegs_can_be_edited()
    {
        using var dir = new TempDir();
        ImageMetadata.CanEdit(Jpeg(dir)).ShouldBeTrue();
        ImageMetadata.CanEdit(dir.File("fake.jpg", "not a picture")).ShouldBeFalse();
        ImageMetadata.CanEdit(Jpeg(dir, "photo.png")).ShouldBeFalse();
    }

    [Fact]
    public void Written_fields_read_back_and_the_picture_is_untouched()
    {
        using var dir = new TempDir();
        var path = Jpeg(dir);
        var pixels = ScanData(path);

        ImageMetadata.Write(path, new ImageMetadataValues
        {
            Title = "Lac Léman",
            Description = "Sunset over the lake",
            Artist = "Allan",
            Copyright = "© 2026",
            Keywords = "lake;sunset",
            Comment = "a comment",
            Make = "Canon",
            Model = "EOS R5",
            DateTaken = new DateTime(2024, 6, 1, 20, 15, 30),
            Latitude = 46.452,
            Longitude = -6.6
        });

        var v = ImageMetadata.Read(path).Values;
        v.Title.ShouldBe("Lac Léman");
        v.Description.ShouldBe("Sunset over the lake");
        v.Artist.ShouldBe("Allan");
        v.Copyright.ShouldBe("© 2026");
        v.Keywords.ShouldBe("lake;sunset");
        v.Comment.ShouldBe("a comment");
        v.Make.ShouldBe("Canon");
        v.Model.ShouldBe("EOS R5");
        v.DateTaken.ShouldBe(new DateTime(2024, 6, 1, 20, 15, 30));
        v.Latitude!.Value.ShouldBe(46.452, 0.0001);
        v.Longitude!.Value.ShouldBe(-6.6, 0.0001);

        ScanData(path).ShouldBe(pixels);
    }

    [Fact]
    public void Writing_twice_replaces_instead_of_duplicating()
    {
        using var dir = new TempDir();
        var path = Jpeg(dir);
        ImageMetadata.Write(path, new ImageMetadataValues { Artist = "First" });
        ImageMetadata.Write(path, new ImageMetadataValues { Artist = "Second" });

        var snapshot = ImageMetadata.Read(path);
        snapshot.Values.Artist.ShouldBe("Second");
        snapshot.Tags.Count(x => x.Name == "Artist").ShouldBe(1);
    }

    [Fact]
    public void Clearing_a_field_removes_the_tag()
    {
        using var dir = new TempDir();
        var path = Jpeg(dir);
        ImageMetadata.Write(path, new ImageMetadataValues { Artist = "Allan", Copyright = "me" });
        ImageMetadata.Write(path, new ImageMetadataValues { Artist = "Allan" });

        var snapshot = ImageMetadata.Read(path);
        snapshot.Values.Copyright.ShouldBeNull();
        snapshot.Tags.ShouldNotContain(x => x.Name == "Copyright");
    }

    [Fact]
    public void Remove_location_keeps_the_rest()
    {
        using var dir = new TempDir();
        var path = Jpeg(dir);
        ImageMetadata.Write(path, new ImageMetadataValues { Artist = "Allan", Latitude = 10, Longitude = 20 });

        ImageMetadata.RemoveLocation(path);

        var snapshot = ImageMetadata.Read(path);
        snapshot.Values.Latitude.ShouldBeNull();
        snapshot.Values.Artist.ShouldBe("Allan");
        snapshot.Tags.ShouldNotContain(x => x.Group == "GPS");
    }

    [Fact]
    public void Remove_all_leaves_a_valid_jpeg_with_no_exif()
    {
        using var dir = new TempDir();
        var path = Jpeg(dir);
        var pixels = ScanData(path);
        ImageMetadata.Write(path, new ImageMetadataValues { Artist = "Allan", DateTaken = DateTime.Today, Latitude = 1, Longitude = 2 });

        ImageMetadata.RemoveAll(path);

        ImageMetadata.CanEdit(path).ShouldBeTrue();
        ImageMetadata.Read(path).Tags.ShouldBeEmpty();
        ScanData(path).ShouldBe(pixels);
    }

    [Fact]
    public void An_edited_copy_gets_the_originals_metadata_back_without_the_stale_parts()
    {
        using var dir = new TempDir();
        var original = Jpeg(dir, "original.jpg");
        ImageMetadata.Write(original, new ImageMetadataValues { Artist = "Allan", Title = "Lake", DateTaken = new DateTime(2024, 6, 1), Latitude = 46, Longitude = 6 });
        var edited = Jpeg(dir, "edited.jpg");   // what an encoder writes: the picture, no EXIF
        var pixels = ScanData(edited);

        ImageMetadata.CopyMetadata(original, edited);

        var v = ImageMetadata.Read(edited);
        v.Values.Artist.ShouldBe("Allan");
        v.Values.Title.ShouldBe("Lake");
        v.Values.DateTaken.ShouldBe(new DateTime(2024, 6, 1));
        v.Values.Latitude!.Value.ShouldBe(46, 0.0001);
        v.Tags.ShouldNotContain(x => x.Name == "Orientation" || x.Name.StartsWith("Pixel"));
        ScanData(edited).ShouldBe(pixels);
    }
}
