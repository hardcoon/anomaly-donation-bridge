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
            CheckPixelAlignedIcons();
            Console.WriteLine("PASS theme popup: all 8 palettes, native radio automation/bindings, legacy background, 5% zoom steps in regular and compact layouts, isolated persistence/reload, browser/color races, failure rollback, Escape and live popup resources.");
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
        {
            model.Presets.Add(new ActionViewModel(preset.Clone(), isPreset: true));
            var userPreset = preset.CloneAsNewInstance();
            userPreset.Triggers.Clear();
            model.UserPresets.Add(new ActionViewModel(userPreset, isPreset: true, isUserPreset: true));
        }
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


            var scalePicker = (ComboBox)window.FindName("ThemeScaleSelector");
            var viewport = (FrameworkElement)window.FindName("InterfaceRoot");
            Check(scalePicker.Items.Cast<int>().SequenceEqual(Enumerable.Range(16, 15).Select(value => value * 5)) &&
                Equals(scalePicker.SelectedItem, 100), "Legacy preferences must start at 100% with five-point steps.");

            scalePicker.SetCurrentValue(Selector.SelectedItemProperty, 110);
            await WaitAsync(() => choices.IsEnabled && model.InterfaceScalePercent == 110);
            Layout(root, 1388);
            double MainScale() => (viewport.TranslatePoint(new Point(100, 0), root) -
                viewport.TranslatePoint(new Point(), root)).X / 100;
            Check(Math.Abs(MainScale() - 1.1) < 0.01, "The main layout did not scale to 110%.");
            var popupScale = (surface.PointToScreen(new Point(100, 0)) - surface.PointToScreen(new Point())).X /
                (100 * VisualTreeHelper.GetDpi(surface).DpiScaleX);
            Check(Math.Abs(popupScale - 1.1) < 0.02, $"The theme popup did not scale to 110%: {popupScale:0.###}.");
            tabs.SelectedIndex = 1;
            model.ShowTemplateDescriptions = false;
            Layout(root, 1388);
            var caption = Find<TextBlock>(viewport).First(text => Equals(text.Tag, "ButtonLabel") &&
                text.Text == "В активные" && text.Visibility == Visibility.Visible);
            var captionMode = TextOptions.GetTextFormattingMode(caption);
            var popupMode = TextOptions.GetTextFormattingMode(scalePicker);
            Check(captionMode == TextFormattingMode.Ideal && popupMode == TextFormattingMode.Ideal &&
                TextOptions.GetTextHintingMode(window) == TextHintingMode.Fixed &&
                TextOptions.GetTextHintingMode(surface) == TextHintingMode.Fixed,
                $"Incorrect scaled text rendering: window caption {captionMode}, popup selector {popupMode}.");
            Save(root, Path.Combine(output, "interface-110.png"));
            Save(surface, Path.Combine(output, "theme-scale.png"));
            ThemeManager.Apply(false, "red");
            Layout(root, 1388);
            Save(root, Path.Combine(output, "cards-110-dark.png"));
            ThemeManager.Apply(model.IsLightTheme, model.ThemeColor);
            model.ShowTemplateDescriptions = true;
            tabs.SelectedIndex = 2;

            var dialog = new PresetNameWindow("Имя шаблона", "Новая жизнь") { Owner = window };
            var dialogWidth = dialog.Width;
            InterfaceScaling.ApplyToDialog(dialog, window);
            Check(((FrameworkElement)dialog.Content).LayoutTransform is ScaleTransform { ScaleX: 1.1, ScaleY: 1.1 } &&
                Math.Abs(dialog.Width - Math.Min(dialogWidth * 1.1, InterfaceScaling.GetWorkArea(window).Width)) < 1,
                "A template dialog did not inherit the app zoom.");
            Check(TextOptions.GetTextFormattingMode((FrameworkElement)dialog.Content) == TextFormattingMode.Ideal &&
                TextOptions.GetTextHintingMode((FrameworkElement)dialog.Content) == TextHintingMode.Fixed,
                "The scaled dialog reuses Display metrics.");
            dialog.Close();

            window.Width = 1150;
            await WaitAsync(() => window.IsCompactLayout);
            var compact = (CompactMainView)window.FindName("CompactShell");
            Check(popup.IsOpen && ReferenceEquals(popup.PlacementTarget, compact.ThemeTrigger),
                "The scale picker did not stay anchored as the interface switched to compact mode.");
            scalePicker.SetCurrentValue(Selector.SelectedItemProperty, 150);
            await WaitAsync(() => choices.IsEnabled && model.InterfaceScalePercent == 150);
            Layout(root, 1150);
            Check(window.IsCompactLayout && Math.Abs(MainScale() - 1.5) < 0.01, "150% did not keep responsive compact layout.");
            scalePicker.SetCurrentValue(Selector.SelectedItemProperty, 80);
            await WaitAsync(() => choices.IsEnabled && model.InterfaceScalePercent == 80 && !window.IsCompactLayout);
            Check(popup.IsOpen && ReferenceEquals(popup.PlacementTarget, trigger) && Math.Abs(MainScale() - 0.8) < 0.01,
                "Scaling down did not restore the regular layout and popup anchor.");
            scalePicker.SetCurrentValue(Selector.SelectedItemProperty, 110);
            await WaitAsync(() => choices.IsEnabled && model.InterfaceScalePercent == 110);
            window.Width = 1440;
            await WaitAsync(() => !window.IsCompactLayout);

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
                !reloaded.ShowTemplateDescriptions && reloaded.PresetColumns == 3 && reloaded.InterfaceScalePercent == 110,
                "Reload lost theme, scale, or browser settings.");

            // A blocked UI file must roll back resources and native radio state.
            var blockPath = Path.Combine(paths.RootDirectory, "ui-preferences.json.tmp");
            Directory.CreateDirectory(blockPath);
            Select((RadioButton)window.FindName("ThemeRedChoice"));
            var error = (TextBlock)window.FindName("ThemePopupError");
            await WaitAsync(() => choices.IsEnabled && error.Visibility == Visibility.Visible);
            Check(model.IsGreenTheme && ThemeManager.ColorKey == "green" &&
                ((RadioButton)window.FindName("ThemeGreenChoice")).IsChecked == true,
                "Failed save did not restore the old palette/selection.");
            scalePicker.SetCurrentValue(Selector.SelectedItemProperty, 115);
            await WaitAsync(() => choices.IsEnabled && error.Visibility == Visibility.Visible);
            Check(model.InterfaceScalePercent == 110 && Equals(scalePicker.SelectedItem, 110) &&
                Math.Abs(MainScale() - 1.1) < 0.01, "Failed save did not restore the previous scale.");
            Directory.Delete(blockPath);
            Invoke(trigger);
            await WaitAsync(() => popup.IsOpen && error.Visibility == Visibility.Collapsed);
            Select((RadioButton)window.FindName("ThemeBlueChoice"));
            await WaitAsync(() => choices.IsEnabled && model.IsBlueTheme);
            scalePicker.SetCurrentValue(Selector.SelectedItemProperty, 100);
            await WaitAsync(() => choices.IsEnabled && model.InterfaceScalePercent == 100);
            Check(TextOptions.GetTextFormattingMode(scalePicker) == TextFormattingMode.Display &&
                TextOptions.GetTextFormattingMode(viewport) == TextFormattingMode.Display,
                "The original text metrics were not restored at 100%.");

            var source = PresentationSource.FromVisual(surface) ?? throw new InvalidOperationException("Popup HWND missing.");
            surface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!popup.IsOpen, "Escape did not dismiss the popup.");
            Invoke(trigger);await WaitAsync(() => popup.IsOpen);
            var done = Find<Button>(surface).Single(button => Equals(button.Content, "Готово"));
            Invoke(done);await WaitAsync(() => !popup.IsOpen);
            var neighbors = Find<Button>(root)
                .Where(button => ReferenceEquals(button.Parent, trigger.Parent) && !ReferenceEquals(button, trigger)).ToArray();
            Check(neighbors.Length == 1 && Equals(neighbors[0].Content, "Профили") && neighbors[0].Padding == trigger.Padding,
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


    private static void CheckPixelAlignedIcons()
    {
        foreach (var monitorScale in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var percent in MainViewModel.InterfaceScaleOptions)
        {
            var zoom = percent / 100d;
            var plus = RasterizeIcon("\uE710", zoom, monitorScale);
            var list = RasterizeIcon("\uE8FD", zoom, monitorScale);
            var plusRows = GetInkBounds(plus);
            Check(plusRows.Width == plusRows.Height && plusRows.Width >= 7,
                $"Plus is stretched or missing at {percent}% zoom and {monitorScale:P0} DPI.");
            for (var y = 0; y < plusRows.Height; y++)
            for (var x = 0; x < plusRows.Width; x++)
            {
                bool At(int column, int row) => plus[plusRows.Y + row, plusRows.X + column];
                Check(At(x, y) == At(y, x) && At(x, y) == At(plusRows.Width - 1 - x, y),
                    $"Plus arms are asymmetric at {percent}% zoom and {monitorScale:P0} DPI (pixel {x}, {y}, bounds {plusRows}).");
            }

            var listBounds = GetInkBounds(list);
            var rowRuns = new List<(bool Visible, int Height)>();
            for (var y = listBounds.Y; y < listBounds.Y + listBounds.Height; y++)
            {
                var visible = list[y, listBounds.X + listBounds.Width - 1];
                if (rowRuns.Count > 0 && rowRuns[^1].Visible == visible)
                    rowRuns[^1] = (visible, rowRuns[^1].Height + 1);
                else
                    rowRuns.Add((visible, 1));
            }
            Check(rowRuns.Count == 5 && rowRuns[0] == rowRuns[2] && rowRuns[2] == rowRuns[4] &&
                rowRuns[0].Visible && rowRuns[1] == rowRuns[3] && !rowRuns[1].Visible,
                $"List lost a stroke or equal spacing at {percent}% zoom and {monitorScale:P0} DPI.");
        }

        Console.WriteLine("PASS pixel-symmetric plus and three equal list strokes at every zoom step, on 100–200% monitor DPI.");
    }

    private static bool[,] RasterizeIcon(string glyph, double zoom, double monitorScale)
    {
        var icon = new VectorIcon
        {
            Glyph = glyph, Foreground = Brushes.White, Width = 14, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var root = new Border { Background = Brushes.Black, Width = 36, Height = 36 };
        root.Child = new Border { Child = icon, LayoutTransform = new ScaleTransform(zoom, zoom) };
        VisualTreeHelper.SetRootDpi(root, new DpiScale(monitorScale, monitorScale));
        root.SetValue(VectorIcon.LayoutScaleProperty, zoom);
        root.Measure(new Size(36, 36));
        root.Arrange(new Rect(0, 0, 36, 36));
        root.UpdateLayout();
        var size = (int)Math.Ceiling(36 * monitorScale);
        var bmp = new RenderTargetBitmap(size, size, 96 * monitorScale, 96 * monitorScale, PixelFormats.Pbgra32);
        bmp.Render(root);
        var pixels = new byte[size * size * 4];
        bmp.CopyPixels(pixels, size * 4, 0);
        var result = new bool[size, size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            result[y, x] = pixels[(y * size + x) * 4] >= 235;
        return result;
    }

    private static Int32Rect GetInkBounds(bool[,] pixels)
    {
        var left = pixels.GetLength(1);
        var top = pixels.GetLength(0);
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < pixels.GetLength(0); y++)
        for (var x = 0; x < pixels.GetLength(1); x++)
        {
            if (!pixels[y, x]) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        return right < 0 ? Int32Rect.Empty : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
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
        if (root.DataContext is MainViewModel model) TemplateSearchUiChecks.WaitForSearch(model, root.Dispatcher);
        root.Margin = new Thickness(0);root.Width = width;root.Height = 782;
        root.Measure(new Size(width, 782));root.Arrange(new Rect(0, 0, width, 782));root.UpdateLayout();
    }
    private static void Save(FrameworkElement root, string path)
    {
        var bounds = root.LayoutTransform.TransformBounds(new Rect(root.RenderSize));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
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
