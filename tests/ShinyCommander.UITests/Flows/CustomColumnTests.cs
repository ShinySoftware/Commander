using System.Diagnostics;
using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>Custom columns: a folder that's mostly one kind of file gets its column set (music: duration, artist...).</summary>
public class CustomColumnTests(AppFixture fixture) : UiTest(fixture)
{
    // a silent PCM wav - its length is in the header, so it's all a duration column needs
    static byte[] Wav(double seconds, int sampleRate = 8000)
    {
        var data = (int)(sampleRate * seconds) * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data);
        w.Write(new byte[data]);
        return ms.ToArray();
    }

    static Action<TestTree> Music => t => t
        .Bytes("long.wav", Wav(125))
        .Bytes("short.wav", Wav(1))
        .Bytes("middle.wav", Wav(3))
        .Bytes("cover.png", TestTree.Png1x1);

    static string Duration(AppState s, string name)
    {
        var item = s.Left.Items.Single(x => x.Name == name);
        return item.Custom![s.Left.CustomColumns!.IndexOf("duration")];
    }

    [Fact]
    public async Task A_music_folder_shows_durations_and_sorts_by_them()
    {
        await this.Start(Music);
        var s = await this.App.WaitFor(x => x.Left.ColumnSet == "music" && !x.Left.IsReadingMetadata, "the music column set");
        s.Left.CustomColumns.ShouldBe(["duration", "artist", "album", "track"]);
        s.Left.CustomHeaders.ShouldContain("Duration");
        Duration(s, "long.wav").ShouldBe("2:05");
        Duration(s, "short.wav").ShouldBe("0:01");
        // the cover isn't audio - blank, not read
        Duration(s, "cover.png").ShouldBe("");

        await this.Ui.Tap("ColumnHeader_duration");
        s = await this.App.WaitFor(x => x.Left.Sort == "custom:duration", "sorted by duration");
        s.Left.Items.Where(x => x.Name.EndsWith(".wav")).Select(x => x.Name).ShouldBe(["short.wav", "middle.wav", "long.wav"]);
        s.Left.CustomHeaders.ShouldContain("Duration ▲");
        // no duration goes last, whichever way it's sorted
        s.Left.Items[^1].Name.ShouldBe("cover.png");

        await this.Ui.Tap("ColumnHeader_duration");
        s = await this.App.WaitFor(x => x.Left.Sort == "custom:duration desc", "sorted longest first");
        s.Left.Items.Where(x => x.Name.EndsWith(".wav")).Select(x => x.Name).ShouldBe(["long.wav", "middle.wav", "short.wav"]);
        s.Left.Items[^1].Name.ShouldBe("cover.png");
    }

    [Fact]
    public async Task A_mixed_folder_has_no_column_set_and_the_sort_waits_for_one()
    {
        await this.Start(Music, r => r.Dir("mixed").File("mixed/a.txt").Bytes("mixed/b.png", TestTree.Png1x1).Bytes("mixed/c.wav", Wav(1)));
        await this.App.WaitFor(x => x.Left.ColumnSet == "music", "the music column set");
        await this.Ui.Tap("ColumnHeader_duration");

        // a third each of text, pictures and audio: no set wins - and with no duration column it's by name
        var s = await this.App.Navigate(Path.Combine(this.RightTree.Path, "mixed"), "left");
        s = await this.App.WaitFor(x => x.Left.Path.EndsWith("mixed") && x.Left.ColumnSet == null, "no column set");
        s.Left.CustomHeaders.ShouldBeEmpty();
        s.Left.Items.Skip(1).Select(x => x.Name).ShouldBe(["a.txt", "b.png", "c.wav"]);

        // back in the music folder it's sorted by duration again
        s = await this.App.Navigate(this.LeftTree.Path, "left");
        s = await this.App.WaitFor(x => x.Left.ColumnSet == "music" && !x.Left.IsReadingMetadata, "the music folder");
        s.Left.Items.Where(x => x.Name.EndsWith(".wav")).Select(x => x.Name).ShouldBe(["short.wav", "middle.wav", "long.wav"]);
    }

    [Fact]
    public async Task The_column_chooser_adds_a_set_column_and_settings_turns_the_set_off()
    {
        await this.Start(Music);
        await this.App.WaitFor(x => x.Left.ColumnSet == "music", "the music column set");

        await this.App.Answer("Sample rate (Music)", "Done");
        await this.App.Command("ChooseColumns");
        var s = await this.App.WaitFor(x => x.Left.CustomColumns!.Contains("samplerate") && !x.Left.IsReadingMetadata, "the sample rate column");
        s.Left.CustomColumns![^1].ShouldBe("samplerate");
        s.Left.Items.Single(x => x.Name == "short.wav").Custom![^1].ShouldBe("8 kHz");

        await this.App.Command("Settings");
        await this.App.WaitFor(x => x.Page == "SettingsPage", "the settings page");
        await this.Ui.Tap("SettingsColumnSet_music");
        await this.App.Key("Escape");
        s = await this.App.WaitFor(x => x.Page == "MainPage" && x.Left.ColumnSet == null, "the music set off");
        s.Left.CustomHeaders.ShouldBeEmpty();
    }

    /// <summary>The music and photo column sets on real files, for a look - only with run.sh --screens (needs ffmpeg).</summary>
    [Fact]
    public async Task Screens_music_and_photo_column_sets()
    {
        if (Environment.GetEnvironmentVariable("SC_UITEST_SCREENS") is not ("1" or "true") || !File.Exists("/opt/homebrew/bin/ffmpeg"))
            return;

        await this.Start(l => l.Dir("music"), r => r.Dir("photos"));
        var songs = new (string File, string Title, string Artist, string Album, int Track, int Seconds)[]
        {
            ("01 Morning.mp3", "Morning", "The Shinies", "Open Panels", 1, 187),
            ("02 Two Panes.mp3", "Two Panes", "The Shinies", "Open Panels", 2, 242),
            ("03 Quick View.mp3", "Quick View", "The Shinies", "Open Panels", 3, 95),
            ("04 F5 Copy.mp3", "F5 Copy", "The Shinies", "Open Panels", 4, 311),
            ("05 Long Night.mp3", "Long Night", "Marta Keys", "After Hours", 1, 3725)
        };
        foreach (var song in songs)
        {
            Run("/opt/homebrew/bin/ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", $"anullsrc=r=44100:cl=stereo", "-t", song.Seconds.ToString(),
                "-c:a", "libmp3lame", "-b:a", "32k", "-metadata", $"title={song.Title}", "-metadata", $"artist={song.Artist}",
                "-metadata", $"album={song.Album}", "-metadata", $"track={song.Track}", this.LeftTree["music/" + song.File]);
        }
        File.Copy("/System/Library/Sounds/Glass.aiff", this.LeftTree["music/Glass.aiff"]);
        foreach (var name in new[] { "iMac Blue", "iMac Green", "Mac Purple", "Radial Sky Blue" })
            File.Copy($"/System/Library/Desktop Pictures/{name}.heic", this.RightTree[$"photos/{name}.heic"]);
        File.Copy("/System/Library/Wallpapers/.default/DefaultAerial.jpg", this.RightTree["photos/DefaultAerial.jpg"]);

        await this.App.Navigate(this.LeftTree["music"], "left");
        await this.App.Navigate(this.RightTree["photos"], "right");
        await this.App.WaitFor(x => x.Left.ColumnSet == "music" && x.Right.ColumnSet == "photos" && !x.Left.IsReadingMetadata && !x.Right.IsReadingMetadata, "both column sets");
        await this.Ui.Tap("ColumnHeader_duration");
        await this.Shot("custom-columns");

        // stacked: each panel gets the window's width, so more of the set fits
        await this.App.Command("TogglePanelLayout");
        await this.Shot("custom-columns-stacked");
        await this.App.Command("TogglePanelLayout");

        await this.App.Command("Settings");
        await this.App.WaitFor(x => x.Page == "SettingsPage", "the settings page");
        var cell = await this.Ui.ById("SettingsColumnSet_text");
        await this.Fixture.Client.ScrollAsync(cell.Id, animated: false);
        await this.Shot("custom-columns-settings");
        await this.App.Key("Escape");
    }

    static void Run(string file, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardError = true })!;
        p.WaitForExit();
        p.ExitCode.ShouldBe(0, p.StandardError.ReadToEnd());
    }

    async Task Shot(string name)
    {
        await Task.Delay(600);
        var current = (await this.App.StateNow()).Page;
        var page = (await this.Ui.All()).LastOrDefault(x => x.Type == current && x.Bounds is { Width: > 0 });
        page.ShouldNotBeNull();
        var png = await this.Fixture.Client.ScreenshotAsync(elementId: page!.Id);
        var dir = Path.Combine(TestConfig.ArtifactDir, "screens");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, name + ".png"), png!);
    }
}
