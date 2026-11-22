namespace ShinyCommander.Views;

/// <summary>
/// The Sharing window's page. While it's showing, the figures are re-read twice a second (speeds and time left move on
/// their own) and everything else whenever the server says something changed.
/// </summary>
public partial class SharingPage : ContentPage
{
    readonly SharingService sharing;
    readonly SharingViewModel viewModel;
    IDispatcherTimer? timer;

    public SharingPage(SharingService sharing, SharingViewModel viewModel)
    {
        this.sharing = sharing;
        this.viewModel = viewModel;
        this.BindingContext = viewModel;
        this.InitializeComponent();
    }

    public SharingViewModel ViewModel => this.viewModel;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.sharing.Changed += this.OnSharingChanged;
        this.timer ??= this.Dispatcher.CreateTimer();
        this.timer.Interval = TimeSpan.FromMilliseconds(500);
        this.timer.Tick += this.OnTick;
        this.timer.Start();
        this.viewModel.Sync();
    }

    protected override void OnDisappearing()
    {
        this.sharing.Changed -= this.OnSharingChanged;
        if (this.timer != null)
        {
            this.timer.Stop();
            this.timer.Tick -= this.OnTick;
        }
        base.OnDisappearing();
    }

    void OnSharingChanged(object? sender, EventArgs e) => this.viewModel.Sync();

    void OnTick(object? sender, EventArgs e) => this.viewModel.Tick();
}
