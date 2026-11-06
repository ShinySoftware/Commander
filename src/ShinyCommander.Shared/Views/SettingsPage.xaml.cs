using Shiny.Maui.Controls.Cells;

namespace ShinyCommander.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage() => this.InitializeComponent();

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // RadioCell applies a section's SelectedValue only to cells that already exist; the binding lands
        // before the cells are built, so re-apply it once the table is up.
        this.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
        {
            foreach (var section in new BindableObject[] { this.ThemeSection, this.FontSection })
            {
                var value = RadioCell.GetSelectedValue(section);
                RadioCell.SetSelectedValue(section, null);
                RadioCell.SetSelectedValue(section, value);
            }
        });
    }
}
