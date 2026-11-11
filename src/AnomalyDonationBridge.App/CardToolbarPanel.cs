using System.Windows;
using System.Windows.Controls;

namespace AnomalyDonationBridge.App;

/// <summary>One row of evenly spaced actions; labels collapse together when the row gets too narrow.</summary>
public sealed class CardToolbarPanel : Panel
{
    private const double Gap = 6;
    private UIElement[] VisibleChildren() => InternalChildren.Cast<UIElement>()
        .Where(child => child.Visibility != Visibility.Collapsed).ToArray();

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren();
        if (children.Length == 0) return new Size();
        var natural = Math.Max(0, children.Length - 1) * Gap;
        foreach (var child in children)
        {
            if (child is Button button && ButtonVisual.GetGlyph(button).Length > 0)
                natural += ButtonVisual.LabelledWidth(button);
            else
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                natural += child.DesiredSize.Width;
            }
        }
        // Measure the selected content only after deciding whether all labels fit.
        var showLabels = !double.IsFinite(availableSize.Width) || natural <= availableSize.Width;
        foreach (var button in children.OfType<Button>().Where(button => ButtonVisual.GetGlyph(button).Length > 0))
            ButtonVisual.SetShowLabel(button, showLabels);
        foreach (var child in children) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = children.Sum(child => child.DesiredSize.Width) + (children.Length - 1) * Gap;
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : desired,
            children.Max(child => child.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren();
        var flexible = children.Count(child => child is Button button && ButtonVisual.GetGlyph(button).Length > 0 && double.IsNaN(button.Width));
        var spare = Math.Max(0, finalSize.Width - children.Sum(child => child.DesiredSize.Width) - Math.Max(0, children.Length - 1) * Gap);
        double x = 0;
        foreach (var child in children)
        {
            var grow = child is Button button && ButtonVisual.GetGlyph(button).Length > 0 && double.IsNaN(button.Width);
            var width = child.DesiredSize.Width + (grow && flexible > 0 ? spare / flexible : 0);
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + Gap;
        }
        return finalSize;
    }
}
