using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace AnomalyDonationBridge.App;

/// <summary>Decorates existing buttons without replacing their commands, content or accessible names.</summary>
public static class ButtonVisual
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(ButtonVisual), new PropertyMetadata("", GlyphChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(ButtonVisual), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.RegisterAttached(
        "ShowLabel", typeof(bool), typeof(ButtonVisual), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure, ShowLabelChanged));

    public static string GetGlyph(DependencyObject value) => (string)value.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject value, string glyph) => value.SetValue(GlyphProperty, glyph);
    public static string GetLabel(DependencyObject value) => (string)value.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject value, string label) => value.SetValue(LabelProperty, label);
    public static bool GetShowLabel(DependencyObject value) => (bool)value.GetValue(ShowLabelProperty);
    public static void SetShowLabel(DependencyObject value, bool visible) => value.SetValue(ShowLabelProperty, visible);

    private static void GlyphChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Button button || string.IsNullOrEmpty(args.NewValue as string)) return;
        UpdateContentTemplate(button);
        if (button.ReadLocalValue(LabelProperty) == DependencyProperty.UnsetValue)
            BindingOperations.SetBinding(button, LabelProperty, new Binding(nameof(Button.Content)) { Source = button, Converter = new LabelConverter() });
        if (button.ReadLocalValue(AutomationProperties.NameProperty) == DependencyProperty.UnsetValue)
            BindingOperations.SetBinding(button, AutomationProperties.NameProperty, new Binding(nameof(Button.Content)) { Source = button });
        if (button.ReadLocalValue(FrameworkElement.ToolTipProperty) == DependencyProperty.UnsetValue)
            BindingOperations.SetBinding(button, FrameworkElement.ToolTipProperty, new Binding(nameof(Button.Content)) { Source = button });
        ToolTipService.SetShowOnDisabled(button, true);
    }

    private static void ShowLabelChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is Button button && GetGlyph(button).Length > 0) UpdateContentTemplate(button);
    }

    private static void UpdateContentTemplate(Button button) => button.SetResourceReference(
        ContentControl.ContentTemplateProperty, GetShowLabel(button) ? "IconButtonContentTemplate" : "IconOnlyButtonContentTemplate");

    internal static double LabelledWidth(Button button)
    {
        var text = new FormattedText(GetLabel(button), CultureInfo.CurrentUICulture, button.FlowDirection,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
            button.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(button).PixelsPerDip)
        { TextAlignment = TextAlignment.Left };
        // Measure independently of the currently hidden label, so growing the window restores it.
        return Math.Max(button.MinWidth, Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 14 + 6 +
            button.Padding.Left + button.Padding.Right + 2) + button.Margin.Left + button.Margin.Right;
    }

    private sealed class LabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string text && text == "+ Сумма" ? "Сумма" : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
