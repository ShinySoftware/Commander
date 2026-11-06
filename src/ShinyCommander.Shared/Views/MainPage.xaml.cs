namespace ShinyCommander.Views;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        this.InitializeComponent();
        this.FolderTree.ChildrenLoader = item => FolderNode.LoadChildrenAsync(item, false);
        this.FolderTree.HasChildrenSelector = _ => true;
        this.FolderTree.ItemsSource = FolderNode.Roots().ToList();
#if MACOS_APPKIT
        // Shiny's Ribbon builds its tab bodies after its native view exists, which the AppKit head never
        // lays out - use the ribbon-styled bar generated from the same command table (tools/ribbon.py).
        ((Grid)this.MainRibbon.Parent).Children.Remove(this.MainRibbon);
        this.AppKitRibbon.IsVisible = true;
#endif
    }

    MainViewModel? ViewModel => this.BindingContext as MainViewModel;

    void OnTreeItemSelected(object? sender, Shiny.Maui.Controls.Tree.TreeItemEventArgs e)
    {
        if (this.ViewModel is { } vm && e.Item is FolderNode node)
        {
            _ = vm.ActivePanel.NavigateAsync(node.Path);
            vm.RequestCommandLineFocus();
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
