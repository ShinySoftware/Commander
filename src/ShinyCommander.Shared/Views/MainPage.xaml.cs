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

    /// <summary>
    /// The panels alone, or with the preview right of them or under them. A closed preview and its splitter leave the
    /// tree: on AppKit a hidden view with gesture recognizers still takes clicks meant for the panels under it.
    /// </summary>
    void ArrangePreview(PreviewPaneViewModel preview)
    {
        var grid = this.WorkArea;
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        if (!preview.IsOpen)
        {
            grid.Children.Remove(this.PreviewSplitter);
            grid.Children.Remove(this.PreviewPane);
            return;
        }

        if (!grid.Children.Contains(this.PreviewPane))
        {
            grid.Children.Add(this.PreviewSplitter);
            grid.Children.Add(this.PreviewPane);
        }
        this.PreviewSplitter.IsVisible = true;
        this.PreviewPane.IsVisible = true;

        var bottom = preview.IsBottom;
        if (bottom)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(SplitterSize)));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(preview.Size)));
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(SplitterSize)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(preview.Size)));
        }
        foreach (var (view, index) in new View[] { this.PanelsGrid, this.PreviewSplitter, this.PreviewPane }.Select((x, i) => (x, i)))
        {
            Grid.SetRow(view, bottom ? index : 0);
            Grid.SetColumn(view, bottom ? 0 : index);
        }
        this.PreviewGrip.WidthRequest = bottom ? 28 : 3;
        this.PreviewGrip.HeightRequest = bottom ? 3 : 28;
    }

    const double SplitterSize = 8;
    double previewSplitterStart;

    void OnPreviewSplitterPan(object? sender, PanUpdatedEventArgs e)
    {
        if (this.ViewModel?.Preview is not { } preview)
            return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                this.previewSplitterStart = preview.Size;
                break;

            case GestureStatus.Running:
                // dragging toward the panels grows the preview; the panels keep at least a usable strip
                var room = preview.IsBottom ? this.WorkArea.Height : this.WorkArea.Width;
                var max = Math.Max(PreviewPaneViewModel.MinSize, room - 240);
                preview.Size = Math.Clamp(this.previewSplitterStart - (preview.IsBottom ? e.TotalY : e.TotalX), PreviewPaneViewModel.MinSize, max);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                preview.SaveSize();
                break;
        }
    }

    void OnPreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PreviewPaneViewModel preview)
            return;

        switch (e.PropertyName)
        {
            case nameof(PreviewPaneViewModel.IsOpen):
            case nameof(PreviewPaneViewModel.Position):
                this.ArrangePreview(preview);
                break;

            // while dragging: just the one definition, not a rebuild
            case nameof(PreviewPaneViewModel.Size) when preview.IsOpen:
                if (preview.IsBottom && this.WorkArea.RowDefinitions.Count == 3)
                    this.WorkArea.RowDefinitions[2].Height = new GridLength(preview.Size);
                else if (!preview.IsBottom && this.WorkArea.ColumnDefinitions.Count == 3)
                    this.WorkArea.ColumnDefinitions[2].Width = new GridLength(preview.Size);
                break;
        }
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
            vm.Preview.PropertyChanged -= this.OnPreviewPropertyChanged;
            vm.Preview.PropertyChanged += this.OnPreviewPropertyChanged;
            this.ArrangePanels(vm.StackPanels);
            this.ArrangePreview(vm.Preview);
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

    // TC keeps typing going to the command line; arrow/enter/F-keys are claimed by shortcuts first. Not while a name is
    // edited in place: taking the keyboard from its editor would end the edit (AppKit completes a field that loses it)
    void OnFocusCommandLine(object? sender, EventArgs e)
        => this.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
        {
            if (this.ViewModel?.IsInlineEditing == true)
                return;

            this.CommandEntry.Focus();
            // put the caret at the end
            this.CommandEntry.CursorPosition = this.CommandEntry.Text?.Length ?? 0;
        });
}
