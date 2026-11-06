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

    /// <summary>Side by side (two columns) or stacked (two rows).</summary>
    void ArrangePanels(bool stacked)
    {
        var grid = this.PanelsGrid;
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        if (stacked)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        }
        Grid.SetRow(this.LeftPanel, 0);
        Grid.SetColumn(this.LeftPanel, 0);
        Grid.SetRow(this.RightPanel, stacked ? 1 : 0);
        Grid.SetColumn(this.RightPanel, stacked ? 0 : 1);
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (this.ViewModel is { } vm)
        {
            vm.FocusCommandLineRequested -= this.OnFocusCommandLine;
            vm.FocusCommandLineRequested += this.OnFocusCommandLine;
            vm.PropertyChanged -= this.OnViewModelPropertyChanged;
            vm.PropertyChanged += this.OnViewModelPropertyChanged;
            this.ArrangePanels(vm.StackPanels);
        }
    }


    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StackPanels) && this.ViewModel is { } vm)
            this.ArrangePanels(vm.StackPanels);
        else if (e.PropertyName == nameof(MainViewModel.CommandOutput))
            this.Dispatcher.Dispatch(async () =>
            {
                // scroll so the last line shows - ScrollToAsync(0, Double.MaxValue) overshoots on AppKit and leaves the
                // pane looking empty
                try
                {
                    await Task.Yield();
                    var bottom = Math.Max(0, this.OutputLabel.Height - this.OutputScroll.Height + this.OutputScroll.Padding.VerticalThickness);
                    await this.OutputScroll.ScrollToAsync(0, bottom, false);
                }
                catch { }
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
