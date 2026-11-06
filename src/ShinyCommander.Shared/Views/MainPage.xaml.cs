namespace ShinyCommander.Views;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        this.InitializeComponent();
#if MACOS_APPKIT
        // Shiny's Ribbon builds its tab bodies after its native view exists, which the AppKit head never
        // lays out - use the ribbon-styled bar generated from the same command table (tools/ribbon.py).
        ((Grid)this.MainRibbon.Parent).Children.Remove(this.MainRibbon);
        this.AppKitRibbon.IsVisible = true;
#endif
    }

    MainViewModel? ViewModel => this.BindingContext as MainViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (this.ViewModel is { } vm)
        {
            vm.FocusCommandLineRequested -= this.OnFocusCommandLine;
            vm.FocusCommandLineRequested += this.OnFocusCommandLine;
            vm.PropertyChanged -= this.OnViewModelPropertyChanged;
            vm.PropertyChanged += this.OnViewModelPropertyChanged;
        }
    }


    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CommandOutput))
            this.Dispatcher.Dispatch(async () =>
            {
                try { await this.OutputScroll.ScrollToAsync(0, Double.MaxValue, false); } catch { }
            });
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.OnFocusCommandLine(this, EventArgs.Empty);
    }

    // TC keeps typing going to the command line; arrow/enter/F-keys are claimed by shortcuts first
    void OnFocusCommandLine(object? sender, EventArgs e)
        => this.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
        {
            this.CommandEntry.Focus();
            // put the caret at the end
            this.CommandEntry.CursorPosition = this.CommandEntry.Text?.Length ?? 0;
        });
}
