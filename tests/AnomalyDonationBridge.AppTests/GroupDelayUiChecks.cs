using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class GroupDelayUiChecks
{
    // Uses the actual WPF layouts and an isolated profile. No runtime/IPC is started.
    public static int Run(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var errors = new StringWriter();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var paths = new AppPaths(Path.Combine(outputDirectory, "isolated-" + Guid.NewGuid().ToString("N")));
            var runtime = new BridgeRuntime(paths);
            var profile = new DonationProfile { Name = "Group delay UI test" };
            runtime.Settings.Profiles.Add(profile); runtime.Settings.ActiveProfileId = profile.Id;
            runtime.Settings.Actions = profile.Actions;
            foreach (var id in new[] { "a", "b" })
                runtime.Settings.Actions.Add(new()
                {
                    Id = id, HandlerId = "change_money", DisplayName = id == "a" ? "Сменить имя героя" : "Выдать предметы в инвентарь",
                    IsActive = true, StrictSequence = true, GroupDelaySeconds = 2,
                    Triggers = [new() { Amount = 31, Currency = "RUB" }]
                });
            var model = new MainViewModel(runtime);
            foreach (var action in runtime.Settings.Actions) model.ActiveActions.Add(new(action));
            model.ActiveBrowser.VisibleEntries.Single().Group!.IsExpanded = true;
            var window = new MainWindow(model, runtime);
            var root = (FrameworkElement)window.Content;
            foreach (var light in new[] { false, true })
            foreach (var layout in new[] { (1440d, 860d, 0), (1080d, 680d, -1), (800d, 560d, 0), (640d, 520d, 0) })
            {
                ThemeManager.Apply(light, "yellow");
                model.ActiveBrowser.SelectedView = ActionBrowserViewModel.ViewOptions.Single(option => option.Columns == layout.Item3);
                Layout(window, layout.Item1, layout.Item2);
                var input = FindInput(root);
                Check(input.Text == "2", "Group pause is missing in a layout.");
                foreach (var element in Find<FrameworkElement>(root).Where(item => Displayed(item, root) &&
                    (item is TextBox || item is TextBlock text && text.Text.StartsWith("Задержка между"))))
                {
                    var position = element.TranslatePoint(new Point(), root);
                    Check(position.X >= 0 && position.X + element.ActualWidth <= root.ActualWidth + 1,
                        "Group pause label or input overflows the window.");
                }
                var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(outputDirectory, $"group-delay-{layout.Item1}-{layout.Item3}-{(light ? "light" : "dark")}.png"));
                encoder.Save(file);
            }
            var editor = FindInput(root); editor.Text = "7";
            editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, editor, null)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5) &&
                   (!File.Exists(paths.SettingsFile) || !model.Notice.Contains("7 сек.")))
            { Pump(); Thread.Sleep(10); }
            Check(runtime.Settings.Actions.All(action => action.GroupDelaySeconds == 7), "Editing did not update the entire group.");
            var loaded = new JsonSettingsStore(paths).LoadAsync().GetAwaiter().GetResult();
            Check(loaded.Actions.All(action => action.GroupDelaySeconds == 7), "Automatic saving lost the group delay.");
            Layout(window, 640, 520); var invalid = FindInput(root); invalid.Text = "-2";
            invalid.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, invalid, null)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Check(invalid.Text == "7" && runtime.Settings.Actions.All(action => action.GroupDelaySeconds == 7),
                "An invalid edit replaced the saved group pause.");
            Pump(); Check(errors.ToString().Length == 0, "WPF binding error: " + errors);
            window.Close(); app.Shutdown();
            Console.WriteLine("PASS group pause: wide cards/list, 800/640 px, both themes, autosave and invalid input");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static TextBox FindInput(FrameworkElement root) => Find<TextBox>(root).Single(input =>
        Displayed(input, root) && AutomationProperties.GetName(input) == "Задержка между срабатываниями эффектов");
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Layout(MainWindow window, double width, double height)
    {
        window.Width = width; window.Height = height;
        var root = (FrameworkElement)window.Content; root.Width = width - 16; root.Height = height - 40;
        for (var pass = 0; pass < 3; pass++)
        { Pump(); root.Measure(new(root.Width, root.Height)); root.Arrange(new(0, 0, root.Width, root.Height)); root.UpdateLayout(); }
    }
    private static bool Displayed(FrameworkElement element, FrameworkElement root)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        { if (current is UIElement { Visibility: not Visibility.Visible }) return false; if (ReferenceEquals(current, root)) return true; }
        return false;
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
