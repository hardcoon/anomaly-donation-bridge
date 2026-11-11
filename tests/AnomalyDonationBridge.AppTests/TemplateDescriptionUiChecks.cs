using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.AppTests;

// Disconnected renderer of the REAL editor header: no IPC, settings or accounts.
internal static class TemplateDescriptionUiChecks
{
    public static int Run(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "effects/gamedata/scripts/anomaly_donation_bridge_effect_hardcoon_adb_mutant_companion_v2.script"));
            using var preset = JsonDocument.Parse(script.Split("ADB_PRESET_V1", 2)[1].Split("ADB_PRESET_END", 2)[0]);
            var description = preset.RootElement.GetProperty("description").GetString()!;
            var model = new ActionViewModel(new ActionDefinition
            {
                DisplayName = preset.RootElement.GetProperty("displayName").GetString()!,
                Description = description, HandlerId = "hardcoon.adb.mutant_companion_v2"
            });
            foreach (var width in new[] { 760d, 980d })
            {
                var editor = new ActionEditorWindow(new ActionEditorViewModel(model, _ => Task.CompletedTask), new DataTemplate());
                var root = (Grid)editor.Content;
                var header = root.Children.OfType<StackPanel>().Single(panel => Grid.GetRow(panel) == 0);
                header.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
                foreach (var light in new[] { false, true })
                {
                    ThemeManager.Apply(light);
                    root.Measure(new Size(width, 420));
                    root.Arrange(new Rect(0, 0, width, 420));
                    root.UpdateLayout();
                    root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    var text = header.Children.OfType<TemplateDescriptionTextBlock>().Single();
                    Check(text.Description == description && BindingOperations.IsDataBound(text, TemplateDescriptionTextBlock.DescriptionProperty),
                        "Description binding was lost while formatting inlines.");
                    var accent = text.Inlines.OfType<Run>().Single(run => run.Text.StartsWith("Установка:"));
                    Check(((SolidColorBrush)accent.Foreground).Color == ((SolidColorBrush)app.FindResource("AccentForegroundBrush")).Color &&
                        accent.FontWeight == FontWeights.SemiBold && text.Inlines.OfType<LineBreak>().Count() == 4,
                        "Installation does not have a themed gold accent and separate paragraphs.");
                    Check(text.TextWrapping == TextWrapping.Wrap && text.ActualWidth <= header.ActualWidth,
                        "Description overflows a narrow editor.");
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(header.ActualWidth), (int)Math.Ceiling(header.ActualHeight),
                        96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(header);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(outputDirectory, $"description-{width}-{(light ? "light" : "dark")}.png"));
                    encoder.Save(file);
                }
                editor.Close();
            }
            // Rebinding and ordinary descriptions remain literal, with no markup.
            var ordinary = new TemplateDescriptionTextBlock { HandlerId = "hardcoon.adb.spin_roulette", Description = "Установка: обычный текст" };
            Check(ordinary.Inlines.OfType<Run>().All(run => run.FontWeight == FontWeights.Normal &&
                run.ReadLocalValue(TextElement.ForegroundProperty) == DependencyProperty.UnsetValue),
                "Installation emphasis leaked from Mutants V2 into another template.");
            ordinary.HandlerId = "hardcoon.adb.mutant_companion_v2";
            Check(ordinary.Inlines.OfType<Run>().Single().FontWeight == FontWeights.SemiBold,
                "Changing template identity did not restore the V2-only emphasis.");
            var source = new TextBlock { Text = description };
            var target = new TemplateDescriptionTextBlock();
            target.SetBinding(TemplateDescriptionTextBlock.DescriptionProperty, new Binding("Text") { Source = source });
            source.Text = "Обычный текст <b>не HTML</b>";
            target.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Check(target.Description == source.Text && string.Concat(target.Inlines.OfType<Run>().Select(r => r.Text)) == source.Text &&
                target.Inlines.OfType<Run>().Count() == 1 && target.Inlines.OfType<Run>().All(r => r.FontWeight == FontWeights.Normal),
                "Formatting survived rebinding or interpreted user markup.");
            Console.WriteLine("PASS multiline description: real editor, gold installation, two widths, live themes and binding updates.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
