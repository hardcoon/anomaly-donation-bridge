using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class BtrSourcesUiChecks
{
    public static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var effects = Path.Combine(AppContext.BaseDirectory, "effects");
            var game = Path.Combine(output, "isolated-game");
            Directory.CreateDirectory(Path.Combine(game, "gamedata", "scripts"));
            const string script = "anomaly_donation_bridge_effect_hardcoon_adb_btr_companion.script";
            File.Copy(Path.Combine(effects, "gamedata", "scripts", script), Path.Combine(game, "gamedata", "scripts", script), true);
            var preset = new FilePresetLoader(effects).Load(game).Presets.Single(p => p.HandlerId == "hardcoon.adb.btr_companion");
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(output, "isolated-data")));
            var main = new MainWindow(new MainViewModel(runtime), runtime);
            var template = (DataTemplate)main.FindResource("ActionEditorTemplate");
            foreach (var width in new[] { 760d, 980d })
            {
                var opened = new List<TemplateSourceLink>();
                var vm = new ActionEditorViewModel(new(preset), _ => Task.CompletedTask, openSource: opened.Add);
                var editor = new ActionEditorWindow(vm, template);
                var root = (Grid)editor.Content;
                root.Margin = new Thickness(0);
                root.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
                root.Measure(new Size(width, 740)); root.Arrange(new Rect(0, 0, width, 740));
                root.UpdateLayout(); root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var legacy = Find<CheckBox>(root).Single(c =>
                    c.DataContext is ParameterValueViewModel p && p.Definition.Key == "legacy_api");
                if (legacy.IsChecked != false || vm.Draft.Parameters.Single(p => p.Definition.Key == "legacy_api").DisplayName != "Legacy api управление")
                    throw new InvalidOperationException("Legacy control checkbox is missing or enabled by default.");
                legacy.IsChecked = true;
                if (vm.Draft.Parameters.Single(p => p.Definition.Key == "legacy_api").Value != "true")
                    throw new InvalidOperationException("Legacy checkbox does not update the template parameter.");
                legacy.IsChecked = false;
                var labels = Find<TextBlock>(root).Where(t => t.Inlines.OfType<Hyperlink>().Any()).ToList();
                if (labels.Count != 3) throw new InvalidOperationException("BTR source links missing.");
                foreach (var label in labels)
                {
                    var bounds = label.TransformToAncestor(root).TransformBounds(new Rect(label.RenderSize));
                    if (bounds.Right > root.ActualWidth || bounds.Top < 0 || bounds.Bottom > 370)
                        throw new InvalidOperationException("BTR source link is clipped or displaced.");
                    var link = label.Inlines.OfType<Hyperlink>().Single();
                    link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                }
                if (!opened.SequenceEqual(vm.SourceLinks)) throw new InvalidOperationException("Hyperlink clicks did not open their own source.");
                foreach (var link in opened)
                    if (!File.Exists(AddonPackageManager.ResolveTemplateSource(link.RelativePath)))
                        throw new InvalidOperationException("Packaged source is missing.");
                var image = new RenderTargetBitmap((int)width, 740, 96, 96, PixelFormats.Pbgra32);
                image.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(output, $"btr-sources-{width}.png"))) png.Save(file);
                editor.Close();
            }
            main.Close(); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("PASS real BTR editor: relative links, clicks, packaged paths and two widths; no IPC.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}
