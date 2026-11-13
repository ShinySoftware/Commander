using Shiny.Maui.Controls.Cells;

namespace ShinyCommander.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        this.InitializeComponent();

        // only macOS installs its own updates - elsewhere there is nothing to check with
        if (IPlatformApplication.Current?.Services.GetService<UpdateService>() is not { IsSupported: true })
            this.AboutSection.Cells.Remove(this.UpdateCell);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // RadioCell applies a section's SelectedValue only to cells that already exist; the binding lands
        // before the cells are built, so re-apply it once the table is up.
        this.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
        {
            foreach (var section in new BindableObject[] { this.LanguageSection, this.ThemeSection, this.FontSection })
            {
                var value = RadioCell.GetSelectedValue(section);
                RadioCell.SetSelectedValue(section, null);
                RadioCell.SetSelectedValue(section, value);
            }
        });
    }
}
