using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class NeedsTemplateTests
{
    private static ActionDefinition Load(string game)
    {
        var effects = Path.Combine(AppContext.BaseDirectory, "effects");
        var target = Path.Combine(game, "gamedata", "scripts"); Directory.CreateDirectory(target);
        const string file = "anomaly_donation_bridge_effect_change_needs.script";
        File.Copy(Path.Combine(effects, "gamedata", "scripts", file), Path.Combine(target, file), true);
        return new FilePresetLoader(effects).Load(game).Presets.Single(p => p.HandlerId == "change_needs");
    }

    public static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var preset = Load(Path.Combine(temporary.Path, "game"));
        var old = preset.Clone(); old.Parameters.Remove("wait_if_insufficient");
        old.ParameterDefinitions.RemoveAll(p => p.Key == "wait_if_insufficient");
        old.Parameters["hunger_percent"] = "-15"; old.Parameters["thirst_percent"] = "0";
        new ActionManifestLoader().ApplyPresetsToInstances([old], [preset]);
        Check(old.Parameters["wait_if_insufficient"] == "true" && old.Parameters["hunger_percent"] == "-15",
            "An existing saved template did not receive the default enabled checkbox or lost its original percentage.");
        var source = new ActionViewModel(old);
        var editor = new ActionEditorViewModel(source, draft => { source.ApplyEditorDraft(draft); return Task.CompletedTask; });
        var toggle = editor.Draft.Parameters.Single(p => p.Definition.Key == "wait_if_insufficient");
        Check(toggle.IsBooleanChoice && toggle.BooleanValue, "Needs waiting is not a checked checkbox.");
        toggle.BooleanValue = false;
        Check(await editor.SaveAsync(), "Unable to save disabled needs waiting: " + editor.Error);
        var saved = JsonSerializer.Deserialize<ActionDefinition>(JsonSerializer.Serialize(source.Model, JsonDefaults.Options), JsonDefaults.Options)!;
        new ActionManifestLoader().ApplyPresetsToInstances([saved], [preset]);
        Check(saved.Parameters["wait_if_insufficient"] == "false" && saved.Parameters["hunger_percent"] == "-15",
            "Disabled checkbox was lost during save/reload/schema refresh.");
    }

    public static int Render(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false); app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var preset = Load(Path.Combine(output, "isolated-game"));
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(output, "isolated-data")));
            var main = new MainWindow(new MainViewModel(runtime), runtime);
            var template = (DataTemplate)main.FindResource("ActionEditorTemplate");
            foreach (var width in new[] { 760d, 980d })
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                var model = new ActionEditorViewModel(new(preset.Clone()), _ => Task.CompletedTask);
                var editor = new ActionEditorWindow(model, template);
                var root = (Grid)editor.Content; root.Margin = new Thickness(0);
                root.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
                root.Measure(new Size(width, 650)); root.Arrange(new Rect(0, 0, width, 650)); root.UpdateLayout();
                var checkbox = Find<CheckBox>(root).Single(c => c.DataContext is ParameterValueViewModel p && p.Definition.Key == "wait_if_insufficient");
                Check(checkbox.IsChecked == true && checkbox.IsEnabled, "Actual needs checkbox is not enabled by default.");
                checkbox.IsChecked = false;
                Check(!model.Draft.Parameters.Single(p => p.Definition.Key == "wait_if_insufficient").BooleanValue,
                    "Actual checkbox cannot be switched off.");
                checkbox.IsChecked = true;
                var bounds = checkbox.TransformToAncestor(root).TransformBounds(new Rect(checkbox.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= width && bounds.Bottom < 600, "Needs checkbox clips the editor/footer.");
                var image = new RenderTargetBitmap((int)width, 650, 96, 96, PixelFormats.Pbgra32); image.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(output, $"needs-{width}-{(light ? "light" : "dark")}.png"))) png.Save(file);
                editor.Close();
            }
            main.Close(); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("PASS actual needs editor: checkbox enabled by default, toggles correctly, two widths and both themes. No IPC/game actions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Find<T>(child)) yield return nested; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
