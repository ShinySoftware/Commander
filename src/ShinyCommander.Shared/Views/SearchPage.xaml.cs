namespace ShinyCommander.Views;

public partial class SearchPage : ContentPage
{
    public SearchPage() => this.InitializeComponent();

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () => this.MaskEntry.Focus());
    }
}
