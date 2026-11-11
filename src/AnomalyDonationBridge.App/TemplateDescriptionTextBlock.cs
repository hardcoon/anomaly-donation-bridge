using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AnomalyDonationBridge.App;

/// <summary>Plain text paragraphs. Only Mutants V2 has an accented installation note; no markup is executed.</summary>
public sealed class TemplateDescriptionTextBlock : TextBlock
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(TemplateDescriptionTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (sender, _) => ((TemplateDescriptionTextBlock)sender).UpdateInlines()));

    public static readonly DependencyProperty HandlerIdProperty = DependencyProperty.Register(
        nameof(HandlerId), typeof(string), typeof(TemplateDescriptionTextBlock),
        new FrameworkPropertyMetadata(string.Empty,
            (sender, _) => ((TemplateDescriptionTextBlock)sender).UpdateInlines()));

    public string HandlerId
    {
        get => (string)GetValue(HandlerIdProperty);
        set => SetValue(HandlerIdProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public TemplateDescriptionTextBlock() => TextWrapping = TextWrapping.Wrap;

    private void UpdateInlines()
    {
        Inlines.Clear();
        var lines = (Description ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) Inlines.Add(new LineBreak());
            var run = new Run(lines[index]);
            if (HandlerId == "hardcoon.adb.mutant_companion_v2" &&
                lines[index].TrimStart().StartsWith("Установка:", StringComparison.Ordinal))
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentForegroundBrush");
                run.FontWeight = FontWeights.SemiBold;
            }
            Inlines.Add(run);
        }
    }
}
