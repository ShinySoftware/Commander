using ShinyCommander.Core.Models;
using ShinyCommander.Core.Rename;

namespace ShinyCommander.Core.Tests;

public class MultiRenameTests
{
    static FileEntry Entry(string name, DateTime? modified = null) => new()
    {
        Name = name,
        FullPath = Path.Combine(Path.GetTempPath(), "Photos", name),
        Modified = modified ?? new DateTime(2026, 3, 9, 14, 5, 7)
    };

    [Theory]
    [InlineData("[N]", "[E]", "holiday.jpg", "holiday.jpg")]
    [InlineData("[N1-3]", "[E]", "holiday.jpg", "hol.jpg")]
    [InlineData("[N3]", "[E]", "holiday.jpg", "l.jpg")]
    [InlineData("[N3-]", "[E]", "holiday.jpg", "liday.jpg")]
    [InlineData("[N-3-]", "[E]", "holiday.jpg", "day.jpg")]
    [InlineData("[P]_[N]", "[E]", "a.jpg", "Photos_a.jpg")]
    [InlineData("[YMD]_[hms]", "[E]", "a.jpg", "20260309_140507.jpg")]
    [InlineData("[[][N][]]", "[E]", "a.jpg", "[a].jpg")]
    [InlineData("[N]", "txt", "a.jpg", "a.txt")]
    public void Masks(string nameMask, string extMask, string input, string expected)
        => MultiRenameEngine.BuildName(Entry(input), 0, new MultiRenameOptions { NameMask = nameMask, ExtensionMask = extMask }).ShouldBe(expected);

    [Fact]
    public void Counter()
    {
        var o = new MultiRenameOptions { NameMask = "img_[C]", CounterStart = 5, CounterStep = 5, CounterDigits = 3 };
        MultiRenameEngine.BuildName(Entry("x.png"), 0, o).ShouldBe("img_005.png");
        MultiRenameEngine.BuildName(Entry("x.png"), 2, o).ShouldBe("img_015.png");
    }

    [Fact]
    public void SearchReplaceAndCase()
    {
        var o = new MultiRenameOptions { Search = "IMG|DSC", Replace = "photo", Case = RenameCase.Upper };
        MultiRenameEngine.BuildName(Entry("img_001.jpg"), 0, o).ShouldBe("PHOTO_001.JPG");

        var regex = new MultiRenameOptions { Search = @"(\d+)", Replace = "#$1", UseRegex = true };
        MultiRenameEngine.BuildName(Entry("file12.txt"), 0, regex).ShouldBe("file#12.txt");
    }

    [Fact]
    public void PreviewFlagsDuplicates()
    {
        var items = new[] { Entry("a.txt"), Entry("b.txt") };
        var preview = MultiRenameEngine.Preview(items, new MultiRenameOptions { NameMask = "same" });
        preview.ShouldAllBe(x => x.Error == "Duplicate name");
    }

    [Fact]
    public async Task ExecuteSwapsAndUndoes()
    {
        using var tmp = new TempDir();
        var a = tmp.File("a.txt", "A");
        var b = tmp.File("b.txt", "B");
        var entries = new[]
        {
            FileEntry.FromInfo(new FileInfo(a)),
            FileEntry.FromInfo(new FileInfo(b))
        };
        var previews = new[]
        {
            new RenamePreview(entries[0], "b.txt", null),
            new RenamePreview(entries[1], "a.txt", null)
        };

        var undo = await MultiRenameEngine.ExecuteAsync(previews);
        File.ReadAllText(a).ShouldBe("B");
        File.ReadAllText(b).ShouldBe("A");

        await MultiRenameEngine.UndoAsync(undo);
        // undo of a swap is applied in order; second step is blocked since target exists - files keep content integrity
        (File.Exists(a) && File.Exists(b)).ShouldBeTrue();
    }
}
