using System.ComponentModel;
using Shiny.Controls.Diagramming;

namespace ShinyCommander.Views;

/// <summary>A table in the schema diagram. The diagram hands its node template the node; the card shows the node's table.</summary>
public partial class DiagramTableCard : ContentView
{
    DiagramNode? node;

    public DiagramTableCard() => this.InitializeComponent();

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (this.node != null)
            this.node.PropertyChanged -= this.OnNodeChanged;

        this.node = this.BindingContext as DiagramNode;
        this.Card.BindingContext = this.node?.Item;
        if (this.node != null)
            this.node.PropertyChanged += this.OnNodeChanged;
        this.ShowSelection();
    }

    void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagramNode.IsSelected))
            this.ShowSelection();
    }

    // the diagram's own selection outline is part of the node it no longer draws
    void ShowSelection()
    {
        if (this.node?.IsSelected == true)
        {
            this.Frame.Stroke = (Color)Application.Current!.Resources["Accent"];
            this.Frame.StrokeThickness = 2;
        }
        else
        {
            this.Frame.ClearValue(Border.StrokeProperty);
            this.Frame.ClearValue(Border.StrokeThicknessProperty);
        }
    }
}
