using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>Folder setups: a folder remembered with its own columns, sort and view mode; every other folder stays global.</summary>
public class FolderSetupTests(AppFixture fixture) : UiTest(fixture)
{
    static Action<TestTree> Tree => t => t
        .Dir("photos").File("photos/a.txt", "aaaa").File("photos/b.txt", "b")
        .Dir("photos/2024").File("photos/2024/c.txt")
        .Dir("other").File("other/z.txt");

    async Task<AppState> RememberHere(string answer)
    {
        await this.App.Answer(answer);
        return await this.App.Command("SetUpFolder");
    }

    [Fact]
    public async Task A_remembered_folder_keeps_its_view_mode_and_sort_and_others_stay_global()
    {
        await this.Start(Tree);
        await this.App.Navigate(this.LeftTree["photos"], "left");

        var s = await this.RememberHere("Remember for this folder");
        s.Left.Setup.ShouldBe(this.LeftTree["photos"]);
        (await this.Ui.ById("FolderSetupBadge")).ShouldNotBeNull();
        await this.Shot("folder-setup-badge");

        // changed here, they're the folder's
        await this.App.Key("Primary+Shift+F1");
        s = await this.App.Command("panel.Sort", "Size");
        s.Left.ViewMode.ShouldBe("Thumbnails");
        s.Left.Sort.ShouldBe("Size desc");

        // a folder without a setup has the global view and the tab's own sort
        s = await this.App.Navigate(this.LeftTree["other"], "left");
        s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["other"], "the other folder");
        s.Left.Setup.ShouldBeNull();
        s.Left.ViewMode.ShouldBe("Details");
        s.Left.Sort.ShouldBe("Name");

        // and back
        s = await this.App.Navigate(this.LeftTree["photos"], "left");
        s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["photos"], "photos again");
        s.Left.ViewMode.ShouldBe("Thumbnails");
        s.Left.Sort.ShouldBe("Size desc");
        s.Left.Items.Skip(1).Where(x => !x.Dir).Select(x => x.Name).ShouldBe(["a.txt", "b.txt"]);
    }

    [Fact]
    public async Task Columns_chosen_in_a_remembered_folder_are_only_that_folders()
    {
        await this.Start(Tree);
        await this.App.Navigate(this.LeftTree["photos"], "left");
        await this.RememberHere("Remember for this folder");

        await this.App.Answer("Attributes", "Done");
        var s = await this.App.Command("ChooseColumns");
        s.Left.PanelColumns.ShouldNotContain("Attributes");
        // the global choice is untouched
        s.Columns.ShouldContain("Attributes");

        s = await this.App.Navigate(this.LeftTree["other"], "left");
        s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["other"], "the other folder");
        s.Left.PanelColumns.ShouldContain("Attributes");
    }

    [Fact]
    public async Task Subfolders_inherit_a_setup_and_can_get_their_own_or_forget_it()
    {
        await this.Start(Tree);
        await this.App.Navigate(this.LeftTree["photos"], "left");
        var s = await this.RememberHere("Remember for this folder and its subfolders");
        s.Left.SetupSubfolders.ShouldBeTrue();
        await this.App.Key("Primary+Shift+F1");

        s = await this.App.Navigate(this.LeftTree["photos/2024"], "left");
        s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["photos/2024"], "the subfolder");
        s.Left.Setup.ShouldBe(this.LeftTree["photos"]);
        s.Left.ViewMode.ShouldBe("Thumbnails");

        // its own, starting from the inherited one - then changed without touching the parent's
        s = await this.RememberHere("Remember for this folder");
        s.Left.Setup.ShouldBe(this.LeftTree["photos/2024"]);
        s = await this.App.Key("Primary+F2");
        s.Left.ViewMode.ShouldBe("Details");

        s = await this.App.Navigate(this.LeftTree["photos"], "left");
        s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["photos"], "the parent");
        s.Left.ViewMode.ShouldBe("Thumbnails");

        // forgotten: back to global, and the subfolder keeps its own
        s = await this.RememberHere("Forget it");
        s.Left.Setup.ShouldBeNull();
        s.Left.ViewMode.ShouldBe("Details");
        s = await this.App.Navigate(this.LeftTree["photos/2024"], "left");
        s = await this.App.WaitFor(x => x.Left.Path == this.LeftTree["photos/2024"], "the subfolder again");
        s.Left.Setup.ShouldBe(this.LeftTree["photos/2024"]);
    }

    [Fact]
    public async Task Settings_lists_setups_and_forgets_one()
    {
        await this.Start(Tree);
        await this.App.Navigate(this.LeftTree["photos"], "left");
        await this.RememberHere("Remember for this folder");

        await this.App.Command("Settings");
        await this.App.WaitFor(x => x.Page == "SettingsPage", "the settings page");
        // below the column sets - scroll it into view so the table builds the row
        var below = await this.Ui.ById("SettingsColumnSet_apps");
        await this.Fixture.Client.ScrollAsync(below.Id, animated: false);
        var row = await this.Ui.ById("SettingsFolderSetup_photos");
        await this.Fixture.Client.ScrollAsync(row.Id, animated: false);
        await this.Shot("settings-folder-setups");
        await this.App.Answer("yes");
        await this.Ui.Tap("SettingsFolderSetup_photos");
        await this.App.Key("Escape");

        var s = await this.App.WaitFor(x => x.Page == "MainPage" && x.Left.Setup == null, "the setup forgotten");
        s.Left.ViewMode.ShouldBe("Details");
    }

    async Task Shot(string name)
    {
        if (Environment.GetEnvironmentVariable("SC_UITEST_SCREENS") is not ("1" or "true"))
            return;
        await Task.Delay(600);
        if (await this.Fixture.Client.ScreenshotAsync() is { } png)
        {
            var dir = Path.Combine(TestConfig.ArtifactDir, "screens");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, name + ".png"), png);
        }
    }
}
