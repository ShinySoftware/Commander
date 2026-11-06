namespace ShinyCommander.Views;

public partial class ViewerPage : ContentPage
{
    public ViewerPage() => this.InitializeComponent();

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (this.BindingContext is ViewerViewModel vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewerViewModel.WordWrap))
                    this.ApplyWrap(vm.WordWrap);
            };
            this.ApplyWrap(vm.WordWrap);
        }
    }

    void ApplyWrap(bool wrap) => this.TextLabel.LineBreakMode = wrap ? LineBreakMode.WordWrap : LineBreakMode.NoWrap;
}
