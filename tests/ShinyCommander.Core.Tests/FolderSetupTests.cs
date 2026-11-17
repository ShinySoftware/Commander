using ShinyCommander.Core.Util;

namespace ShinyCommander.Core.Tests;

public class FolderSetupTests
{
    static FolderSetup Setup(string path, bool subfolders = false) => new() { Path = path, IncludeSubfolders = subfolders };

    [Fact]
    public void A_folder_gets_its_own_setup()
    {
        var music = Setup("/Users/me/Music");
        FolderSetup.Find([Setup("/Users/me"), music], "/Users/me/Music").ShouldBe(music);
        FolderSetup.Find([music], "/Users/me/Music/").ShouldBe(music);
    }

    [Fact]
    public void Folders_without_one_stay_global()
    {
        FolderSetup.Find([Setup("/Users/me/Music")], "/Users/me/Movies").ShouldBeNull();
        // only saved with its subfolders does a setup reach below
        FolderSetup.Find([Setup("/Users/me/Music")], "/Users/me/Music/Live").ShouldBeNull();
    }

    [Fact]
    public void Subfolders_take_the_nearest_setup_above_them()
    {
        var music = Setup("/Users/me/Music", subfolders: true);
        var live = Setup("/Users/me/Music/Live", subfolders: true);
        FolderSetup.Find([music, live], "/Users/me/Music/Albums/2024").ShouldBe(music);
        FolderSetup.Find([music, live], "/Users/me/Music/Live/Paris").ShouldBe(live);
    }

    [Fact]
    public void A_name_that_only_starts_the_same_is_not_below()
        => FolderSetup.Find([Setup("/Users/me/Music", subfolders: true)], "/Users/me/Musicals").ShouldBeNull();

    [Fact]
    public void A_root_with_subfolders_covers_everything()
        => FolderSetup.Find([Setup("/", subfolders: true)], "/usr/local").ShouldNotBeNull();

    [Fact]
    public void Round_trips_as_json()
    {
        var setups = new[]
        {
            new FolderSetup
            {
                Path = "/Users/me/Music",
                IncludeSubfolders = true,
                Columns = ["Size", "Modified"],
                CustomColumns = ["duration", "artist"],
                Sort = SortColumn.Custom,
                SortKey = "duration",
                Descending = true,
                ViewMode = "Details"
            },
            new FolderSetup { Path = "/Users/me/Pictures", ViewMode = "Thumbnails" }
        };

        var back = FolderSetup.Parse(FolderSetup.Format(setups));
        back.Count.ShouldBe(2);
        back[0].SameAs(setups[0]).ShouldBeTrue();
        back[1].SameAs(setups[1]).ShouldBeTrue();
        // null custom columns (follow the column set) stays null - not an empty pick
        back[1].CustomColumns.ShouldBeNull();
    }

    [Fact]
    public void A_damaged_setting_reads_as_none() => FolderSetup.Parse("{not json").ShouldBeEmpty();
}
