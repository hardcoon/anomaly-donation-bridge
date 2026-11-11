using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

// Opt-in native WPF test. No InitializeAsync/IPC/provider/game/profile startup.
internal static class ThemePickerUiChecks
{
    public static int Run(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var task = CheckAsync(outputDirectory);
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke((Action)(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            Console.WriteLine("PASS theme popup: all 8 palettes, native radio automation/bindings, legacy background, isolated persistence/reload, browser/color races, failure rollback, Escape and live popup resources.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static async Task CheckAsync(string output)
    {
        var paths = new AppPaths(Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N")));
        paths.EnsureExists();
        var legacyBytes = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":13,\"useLightTheme\":true}");
        await File.WriteAllBytesAsync(paths.SettingsFile, legacyBytes);
        var game = Path.Combine(paths.RootDirectory, "fake-game");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "keep.txt"), "unchanged");
        await using var runtime = new BridgeRuntime(paths);
        runtime.Settings.UseLightTheme = true;
        runtime.Settings.GamePath = game;
        var profile = new DonationProfile { Name = "Изолированная UI-проверка" };
        runtime.Settings.Profiles.Add(profile);
        runtime.Settings.ActiveProfileId = profile.Id;
        var model = new MainViewModel(runtime);
        foreach (var preset in DefaultActionCatalog.CreatePresets().Take(6))
            model.Presets.Add(new ActionViewModel(preset.Clone(), isPreset: true));
        Check(model.IsLightTheme && model.IsYellowTheme, "Legacy light/yellow choice was lost.");
        ThemeManager.Apply(model.IsLightTheme, model.ThemeColor);
        var errors = new StringWriter();
        var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var window = new MainWindow(model, runtime)
        {
            Width = 1440, Height = 860, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0
        };
        var popup = (Popup)window.FindName("ThemePopup");
        popup.Opacity = 0; // Do not flash a separate popup HWND on the user's monitor.
        try
        {
            window.Show();
            var root = (FrameworkElement)window.Content;
            var tabs = Find<TabControl>(root).First();
            tabs.SelectedIndex = 2;
            Layout(root, 1388);
            var trigger = (Button)window.FindName("ThemeButton");
            Invoke(trigger);
            await WaitAsync(() => popup.IsOpen);
            var surface = (FrameworkElement)window.FindName("ThemePopupSurface");
            var choices = (FrameworkElement)window.FindName("ThemeChoices");
            Check(!popup.StaysOpen, "Outside-click dismissal is disabled.");
            Check(((RadioButton)window.FindName("ThemeWhiteChoice")).IsChecked == true, "Legacy background not reflected in popup.");
            foreach (var light in new[] { false, true })
            foreach (var color in new[] { "red", "green", "blue", "yellow" })
            {
                Select((RadioButton)window.FindName(light ? "ThemeWhiteChoice" : "ThemeBlackChoice"));
                await WaitAsync(() => choices.IsEnabled && model.IsLightTheme == light);
                var selected = (RadioButton)window.FindName("Theme" + char.ToUpperInvariant(color[0]) + color[1..] + "Choice");
                Select(selected);
                await WaitAsync(() => choices.IsEnabled && model.ThemeColor == color);
                Layout(root, 1388);
                surface.UpdateLayout();
                var stored = await new JsonUiPreferencesStore(paths).LoadAsync();
                Check(stored.ThemeColor == color && stored.UseLightTheme == light, "Theme did not roundtrip to UI preferences.");
                Check(ThemeManager.ColorKey == color && ThemeManager.IsLightTheme == light,
                    $"Resources do not match saved choice: expected {color}/{light}, actual {ThemeManager.ColorKey}/{ThemeManager.IsLightTheme}; model {model.ThemeColor}/{model.IsLightTheme}.");
                Check(BindingOperations.GetBinding(selected, ToggleButton.IsCheckedProperty)?.Mode == BindingMode.OneWay,
                    "Radio grouping removed the selection binding.");
                Check(selected.IsChecked == true, "Native selected radio did not update.");
                Check(ReferenceEquals(surface.TryFindResource("PanelBrush"), Application.Current.Resources["PanelBrush"]),
                    "Popup resources did not update with the main window.");
                Save(root, Path.Combine(output, color + (light ? "-white.png" : "-black.png")));
                Save(surface, Path.Combine(output, color + (light ? "-white-popup.png" : "-black-popup.png")));
            }

            // Independent queued presentation updates may never undo each other.
            var colorSave = model.SetThemeColorAsync("blue");
            model.ShowTemplateDescriptions = false;
            model.PresetBrowser.SelectedView = ActionBrowserViewModel.ViewOptions[4];
            await colorSave;
            await WaitAsync(() => runtime.UiPreferences.ThemeColor == "blue" &&
                !runtime.UiPreferences.ShowTemplateDescriptions && runtime.UiPreferences.PresetColumns == 3);
            await Task.WhenAll(model.SetThemeColorAsync("green"), model.SetLightThemeAsync(false));
            Check(model.IsGreenTheme && model.IsBlackTheme, "Independent concurrent color/background requests were coupled.");
            var reloaded = await new JsonUiPreferencesStore(paths).LoadAsync();
            Check(reloaded.ThemeColor == "green" && reloaded.UseLightTheme == false &&
                !reloaded.ShowTemplateDescriptions && reloaded.PresetColumns == 3,
                "Reload lost theme or browser settings.");

            // A blocked UI file must roll back resources and native radio state.
            var blockPath = Path.Combine(paths.RootDirectory, "ui-preferences.json.tmp");
            Directory.CreateDirectory(blockPath);
            Select((RadioButton)window.FindName("ThemeRedChoice"));
            var error = (TextBlock)window.FindName("ThemePopupError");
            await WaitAsync(() => choices.IsEnabled && error.Visibility == Visibility.Visible);
            Check(model.IsGreenTheme && ThemeManager.ColorKey == "green" &&
                ((RadioButton)window.FindName("ThemeGreenChoice")).IsChecked == true,
                "Failed save did not restore the old palette/selection.");
            Directory.Delete(blockPath);
            Invoke(trigger);
            await WaitAsync(() => popup.IsOpen && error.Visibility == Visibility.Collapsed);
            Select((RadioButton)window.FindName("ThemeBlueChoice"));
            await WaitAsync(() => choices.IsEnabled && model.IsBlueTheme);

            var source = PresentationSource.FromVisual(surface) ?? throw new InvalidOperationException("Popup HWND missing.");
            surface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!popup.IsOpen, "Escape did not dismiss the popup.");
            Invoke(trigger);await WaitAsync(() => popup.IsOpen);
            var done = Find<Button>(surface).Single(button => Equals(button.Content, "Готово"));
            Invoke(done);await WaitAsync(() => !popup.IsOpen);
            var neighbors = Find<Button>(root)
                .Where(button => Equals(button.Content, "Профили") || Equals(button.Content, "Настройки")).ToArray();
            Check(neighbors.Length == 2 && neighbors.All(button => button.Padding == trigger.Padding),
                "Header buttons have inconsistent padding.");
            foreach (var width in new[] { 1028d, 1388d, 1868d })
            {
                Layout(root, width);
                var buttonPosition = trigger.TranslatePoint(new Point(), root);
                Check(buttonPosition.X >= 0 && buttonPosition.X + trigger.ActualWidth <= root.ActualWidth,
                    "Theme button clips at width " + width);
                Check(neighbors.All(button => Math.Abs(button.ActualHeight - trigger.ActualHeight) < 0.1 &&
                    Math.Abs(button.TranslatePoint(new Point(), root).Y - buttonPosition.Y) < 0.1),
                    "Header button height/alignment differs at width " + width);
            }
            Check(File.ReadAllBytes(paths.SettingsFile).SequenceEqual(legacyBytes), "Theme rewrote donation settings.");
            Check(Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length == 1, "Theme wrote game files.");
            Check(!File.Exists(paths.PendingQueueFile) && !File.Exists(paths.HistoryFile), "Theme changed donation state.");
            Check(string.IsNullOrWhiteSpace(errors.ToString()), "Theme binding errors: " + errors);
        }
        finally
        {
            popup.IsOpen = false;
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            ThemeManager.Apply(false);
        }
    }

    private static void Select(RadioButton button)
    {
        if (button.IsChecked == true) return;
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new RadioButtonAutomationPeer(button);
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
    }
    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static async Task WaitAsync(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow > until) throw new InvalidOperationException("Timed out waiting for theme UI/save.");
            await Task.Delay(10);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void Layout(FrameworkElement root, double width)
    {
        root.Margin = new Thickness(0);root.Width = width;root.Height = 782;
        root.Measure(new Size(width, 782));root.Arrange(new Rect(0, 0, width, 782));root.UpdateLayout();
    }
    private static void Save(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);encoder.Save(output);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

}
