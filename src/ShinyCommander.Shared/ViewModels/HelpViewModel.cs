namespace ShinyCommander.ViewModels;

[ShellMap<HelpPage>("Help")]
public partial class HelpViewModel(INavigator navigator) : ObservableObject
{
    [ShellProperty(required: false)]
    [ObservableProperty]
    public partial string Markdown { get; set; } = String.Empty;

    [RelayCommand]
    Task Close() => navigator.GoBack();
}
