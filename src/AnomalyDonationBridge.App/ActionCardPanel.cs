using System.Windows;
using System.Windows.Controls;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

/// <summary>Independent columns with stable item order and optional explicit density.</summary>
public sealed class ActionCardPanel : Panel
{
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth), typeof(double), typeof(ActionCardPanel),
        new FrameworkPropertyMetadata(520d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double width && double.IsFinite(width) && width > 0);

    public double MinimumColumnWidth
    {
        get => (double)GetValue(MinimumColumnWidthProperty);
        set => SetValue(MinimumColumnWidthProperty, value);
    }

    private const double Gap = 12;

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(ActionCardPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int count && count is >= 0 and <= 5);
    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns), typeof(int), typeof(ActionCardPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int count && count is >= 1 and <= 5);
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public int MaximumColumns { get => (int)GetValue(MaximumColumnsProperty); set => SetValue(MaximumColumnsProperty, value); }
    private int ColumnCount(double width) => Columns > 0 ? Columns :
        Math.Clamp((int)((width + Gap) / (MinimumColumnWidth + Gap)), 1, MaximumColumns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinimumColumnWidth;
        var columns = ColumnCount(width);
        var itemWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        var nextColumn = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (IsFullWidth(child))
            {
                var top = heights.Max();
                child.Measure(new Size(width, double.PositiveInfinity));
                Array.Fill(heights, top + child.DesiredSize.Height + Gap);
                nextColumn = 0;
                continue;
            }

            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            heights[nextColumn] += child.DesiredSize.Height + Gap;
            nextColumn = (nextColumn + 1) % columns;
        }
        return new Size(width, Math.Max(0, heights.Max() - Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount(finalSize.Width);
        var itemWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        var nextColumn = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (IsFullWidth(child))
            {
                var top = heights.Max();
                child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
                Array.Fill(heights, top + child.DesiredSize.Height + Gap);
                nextColumn = 0;
                continue;
            }

            var column = nextColumn;
            child.Arrange(new Rect(column * (itemWidth + Gap), heights[column], itemWidth, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Gap;
            nextColumn = (nextColumn + 1) % columns;
        }
        return finalSize;
    }

    private static bool IsFullWidth(UIElement child) =>
        child is FrameworkElement { DataContext: ActiveActionListEntryViewModel { IsGroup: true } };
}
