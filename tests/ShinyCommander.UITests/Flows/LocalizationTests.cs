using ShinyCommander.UITests.Driver;

namespace ShinyCommander.UITests.Flows;

/// <summary>
/// French and Spanish reach the screen: the satellite assemblies load, the view models' dialogs and job titles and
/// the pages' {l:Tr} text come out translated. (The app starts in English in test mode; the culture hook switches it
/// live, which affects dialogs and pages opened afterwards.)
/// </summary>
public class LocalizationTests(AppFixture fixture) : UiTest(fixture)
{
    [Fact]
    public async Task Delete_asks_in_french()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));
        await this.App.Culture("fr");
        await this.App.Cursor("a.txt");
        await this.App.Answer("yes");

        var s = await this.App.Key("Shift+F8");

        var confirm = s.Dialogs.First(d => d.Kind == "confirm");
        confirm.Title.ShouldBe("Supprimer définitivement");
        confirm.Message.ShouldBe("Supprimer définitivement « a.txt » ? Cette action est irréversible.");
        this.LeftTree.Exists("a.txt").ShouldBeFalse();
    }

    [Fact]
    public async Task A_copy_job_is_titled_and_summarized_in_spanish()
    {
        await this.Start(l => l.File("a.txt").File("b.txt"));
        await this.App.Culture("es");
        await this.App.Mark("a.txt", "b.txt");
        await this.App.Answer("<default>");

        var s = await this.App.Key("F5");

        s.Dialogs.First(d => d.Kind == "prompt").Title.ShouldBe("Copiar 2 elementos");
        var job = s.Jobs.ShouldHaveSingleItem();
        job.Title.ShouldBe("Copiar 2 elementos");
        job.Subtitle.ShouldStartWith("2 elemento(s) en ");
        s.Dialogs.ShouldContain(d => d.Kind == "toast" && d.Message == "Copiar 2 elementos: listo (2 elemento(s))");
    }

    [Fact]
    public async Task The_settings_page_opens_in_spanish()
    {
        await this.Start();
        await this.App.Culture("es");

        await this.App.Command("Settings");

        await this.Ui.ByText("Mostrar archivos ocultos");
        await this.Ui.ByText("Español");
    }

    [Fact]
    public async Task Quitting_with_a_job_running_asks_in_french()
    {
        await this.Start();
        await this.App.Culture("fr");
        await this.App.HoldJob();
        await this.App.Answer("no");

        var s = await this.App.Command("Exit");

        s.Dialogs.ShouldContain(d => d.Kind == "confirm" && d.Title == "Tâches en cours");
        await this.App.Job("stop");
    }
}
