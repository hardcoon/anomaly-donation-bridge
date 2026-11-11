using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class ActorBoostResetUiChecks
{
    public static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var game = Path.Combine(output, "isolated-game");
            var scripts = Path.Combine(game, "gamedata", "scripts"); Directory.CreateDirectory(scripts);
            var effects = Path.Combine(AppContext.BaseDirectory, "effects");
            const string file = "anomaly_donation_bridge_effect_actor_boost.script";
            File.Copy(Path.Combine(effects, "gamedata", "scripts", file), Path.Combine(scripts, file), true);
            var preset = new FilePresetLoader(effects).Load(game).Presets.Single(p => p.HandlerId == "actor_boost");
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(output, "isolated-data")));
            var main = new MainWindow(new MainViewModel(runtime), runtime);
            var template = (DataTemplate)main.FindResource("ActionEditorTemplate");
            foreach (var width in new[] { 760d, 980d })
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                var calls = 0;
                var vm = new ActionEditorViewModel(new(preset), _ => Task.CompletedTask,
                    () => { calls++; return Task.FromResult("Параметры сброшены к базовым. Сохраните игру."); });
                var editor = new ActionEditorWindow(vm, template);
                var root = (Grid)editor.Content;
                // Capture only the client content; an unshown Window's visual
                // offset would otherwise clip the root's outer margin.
                root.Margin = new Thickness(0);
                root.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
                Layout(root, width);
                var button = Find<Button>(root).Single(b => Equals(b.Content, "Сбросить параметры к базовым"));
                Check(button.IsVisible || button.Visibility == Visibility.Visible, "The real editor recovery button is hidden.");
                Check(button.IsEnabled, "The real editor recovery action is disabled.");
                var rect = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Check(rect.Left >= 0 && rect.Right <= root.ActualWidth && rect.Bottom < root.ActualHeight - 45,
                    "Recovery button clips or overlaps the editor footer.");
                Capture(root, Path.Combine(output, $"actor-boost-reset-{width}-{(light ? "light" : "dark")}.png"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(root, width);
                Check(calls == 1 && vm.ResetStatus.Contains("Сохраните игру") && !vm.IsBusy,
                    "The real reset button click did not reach its isolated action or display acknowledgement.");
                Check(Find<TextBlock>(root).Any(t => t.Text == vm.ResetStatus), "Acknowledgement is missing from the editor.");
                Capture(root, Path.Combine(output, $"actor-boost-reset-done-{width}-{(light ? "light" : "dark")}.png"));
                editor.Close();
            }
            main.Close(); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("PASS real actor-boost reset editor: two widths, both themes, click acknowledgement, no IPC or game actions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Layout(FrameworkElement root, double width)
    {
        root.Measure(new Size(width, 740)); root.Arrange(new Rect(0, 0, width, 740));
        root.UpdateLayout(); root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
    private static void Capture(FrameworkElement root, string path)
    {
        var image = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
