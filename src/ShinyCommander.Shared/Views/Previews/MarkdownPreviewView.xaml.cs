namespace ShinyCommander.Views;

/// <summary>Rendered Markdown (MarkdownFilePreviewer).</summary>
public partial class MarkdownPreviewView : ContentView
{
    public static readonly BindableProperty MarkdownProperty = BindableProperty.Create(nameof(Markdown), typeof(string), typeof(MarkdownPreviewView), String.Empty);

    public MarkdownPreviewView() => this.InitializeComponent();

    public string Markdown
    {
        get => (string)this.GetValue(MarkdownProperty);
        set => this.SetValue(MarkdownProperty, value);
    }
}
