using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.App.ViewModels;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AnomalyDonationBridge.App.Services;

public static class SelfTestRunner
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var actions = DefaultActionCatalog.Create();
            var match = RuleMatcher.Match(new DonationEvent { Id = "self", Amount = 1m, Currency = "RUB", CreatedAt = DateTimeOffset.UtcNow }, actions);
            if (match?.Action.HandlerId != "add_radiation") throw new InvalidOperationException("Rule matcher self-test failed.");

            var command = new GameCommand
            {
                CommandId = "self",
                DonationId = "тест",
                ActionId = "add_radiation",
                AmountMinor = 100,
                Currency = "RUB",
                Parameters = new() { ["percent"] = "25" }
            };
            var fields = PipeProtocol.Command(command).Split('\t');
            if (PipeProtocol.PercentDecode(fields[2]) != "тест") throw new InvalidOperationException("Protocol self-test failed.");

            var responsiveWidth = new ResponsiveItemWidthConverter { MinimumColumnWidth = 750 };
            if (responsiveWidth.CalculateItemWidth(1_499) != 1_499 ||
                responsiveWidth.CalculateItemWidth(1_500) != 750 ||
                responsiveWidth.CalculateItemWidth(1_840) != 920 ||
                responsiveWidth.CalculateItemWidth(3_000) != 1_500 ||
                !double.IsNaN(responsiveWidth.CalculateItemWidth(double.NaN)))
            {
                throw new InvalidOperationException("Responsive preset column self-test failed.");
            }

            var directory = CreateTestDirectory("self-test");
            try
            {
                var paths = new AppPaths(directory);
                var store = new JsonSettingsStore(paths);
                await store.SaveAsync(new AppSettings());
                _ = await store.LoadAsync();
                if (OperatingSystem.IsWindows())
                {
                    var secrets = new DpapiSecretStore(paths);
                    await secrets.SaveAsync(new SecretBundle { ClientSecret = "self-test" });
                    if ((await secrets.LoadAsync()).ClientSecret != "self-test") throw new InvalidOperationException("DPAPI self-test failed.");
                }

                const string fatalTestSecret = "fatal-self-test-secret";
                if (!AnomalyDonationBridge.App.App.TryWriteFatalLog(
                        "Self-test",
                        new InvalidOperationException($"access_token={fatalTestSecret}"),
                        paths))
                {
                    throw new InvalidOperationException("Fatal UI logging self-test failed to write.");
                }
                var fatalLog = await File.ReadAllTextAsync(paths.LogFile);
                if (!fatalLog.Contains("\tFATAL\t", StringComparison.Ordinal) ||
                    !fatalLog.Contains("access_token=[REDACTED]", StringComparison.Ordinal) ||
                    fatalLog.Contains(fatalTestSecret, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Fatal UI logging self-test failed to redact sensitive data.");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            return 0;
        }
        catch { return 1; }
    }

    public static async Task<int> RunUiSmokeAsync()
    {
        MainWindow? mainWindow = null;
        SettingsWindow? settingsWindow = null;
        TriggerEditorWindow? triggerEditorWindow = null;
        string? directory = null;
        try
        {
            directory = CreateTestDirectory("ui-smoke");
            try
            {
                var paths = new AppPaths(directory);
                var gamePath = Path.Combine(directory, "game");
                var sourceEffects = Path.Combine(AppContext.BaseDirectory, "effects", "gamedata", "scripts");
                var installedEffects = Path.Combine(gamePath, "gamedata", "scripts");
                Directory.CreateDirectory(installedEffects);
                foreach (var source in Directory.EnumerateFiles(sourceEffects, "*.script", SearchOption.TopDirectoryOnly))
                {
                    File.Copy(source, Path.Combine(installedEffects, Path.GetFileName(source)), true);
                }
                await new JsonSettingsStore(paths).SaveAsync(new AppSettings { GamePath = gamePath });

                await using var runtime = new BridgeRuntime(paths);
                var viewModel = new MainViewModel(runtime);
                await viewModel.InitializeAsync();
                ThemeManager.Apply(viewModel.IsLightTheme, viewModel.ThemeColor);
                mainWindow = new MainWindow(viewModel, runtime)
                {
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowState = WindowState.Minimized,
                    Opacity = 0
                };
                mainWindow.Show();
                ValidateNativeSystemFrame(mainWindow);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (viewModel.CanArm)
                {
                    throw new InvalidOperationException("Donation processing must stay disabled before OAuth is configured.");
                }
                var expectedPresetCount = Directory.EnumerateFiles(sourceEffects,
                    "anomaly_donation_bridge_effect_*.script", SearchOption.TopDirectoryOnly).Count();
                if (viewModel.Presets.Count != expectedPresetCount ||
                    viewModel.Presets.Select(item => item.HandlerId).Distinct(StringComparer.Ordinal).Count() != expectedPresetCount ||
                    viewModel.Presets.Any(item => item.HandlerId == "spawn_dogs_3"))
                {
                    throw new InvalidOperationException("Reusable preset catalog is not unique.");
                }
                ValidateMainTabs(mainWindow);

                var knownIds = runtime.Settings.Actions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                var positiveDraft = viewModel.Presets.Single(item => item.HandlerId == "add_radiation").Model;
                positiveDraft.Parameters["percent"] = "25";
                positiveDraft.Triggers.Add(new TriggerRule { Amount = 40m, Currency = "RUB" });
                await runtime.AddActionInstanceAsync(positiveDraft);
                var firstAdded = runtime.Settings.Actions.Single(item => !knownIds.Contains(item.Id));
                knownIds.Add(firstAdded.Id);

                var negativeDraft = viewModel.Presets.Single(item => item.HandlerId == "add_radiation").Model;
                negativeDraft.Parameters["percent"] = "-25";
                negativeDraft.Triggers.Clear();
                negativeDraft.Triggers.Add(new TriggerRule { Amount = 41m, Currency = "RUB" });
                await runtime.AddActionInstanceAsync(negativeDraft);
                var secondAdded = runtime.Settings.Actions.Single(item => !knownIds.Contains(item.Id));
                if (firstAdded.Id == secondAdded.Id || firstAdded.HandlerId != "add_radiation" ||
                    secondAdded.HandlerId != "add_radiation" ||
                    firstAdded.Parameters["percent"] != "25" || secondAdded.Parameters["percent"] != "-25" ||
                    viewModel.Presets.Count != expectedPresetCount)
                {
                    throw new InvalidOperationException("Preset instances are not independent.");
                }

                var rulesFile = Path.Combine(directory, "preset-roundtrip.json");
                await runtime.ExportRulesAsync(rulesFile);
                await runtime.DeleteActionInstanceAsync(firstAdded.Id);
                await runtime.DeleteActionInstanceAsync(secondAdded.Id);
                await runtime.ImportRulesAsync(rulesFile);
                firstAdded = runtime.Settings.Actions.Single(item => item.Id == firstAdded.Id);
                secondAdded = runtime.Settings.Actions.Single(item => item.Id == secondAdded.Id);
                if (firstAdded.Parameters["percent"] != "25" ||
                    secondAdded.Parameters["percent"] != "-25")
                {
                    throw new InvalidOperationException("Preset export/import roundtrip lost instance parameters.");
                }
                await runtime.DeleteActionInstanceAsync(firstAdded.Id);
                await runtime.DeleteActionInstanceAsync(secondAdded.Id);

                settingsWindow = new SettingsWindow(new SettingsViewModel(runtime))
                {
                    Owner = mainWindow,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32_000,
                    Top = -32_000,
                    Opacity = 0
                };
                settingsWindow.Show();
                settingsWindow.UpdateLayout();
                ValidateNativeSystemFrame(settingsWindow);
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                await Dispatcher.Yield(DispatcherPriority.Render);
                ValidateWritableTargetBindings(settingsWindow);

                triggerEditorWindow = new TriggerEditorWindow(
                    new TriggerEditorViewModel(null, runtime.Settings.ObservedCurrencies))
                {
                    Owner = mainWindow,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32_000,
                    Top = -32_000,
                    Opacity = 0
                };
                triggerEditorWindow.Show();
                triggerEditorWindow.UpdateLayout();
                ValidateNativeSystemFrame(triggerEditorWindow);
                await Dispatcher.Yield(DispatcherPriority.Render);
                ValidateWritableTargetBindings(triggerEditorWindow);
                ValidateThemedControlTemplates(expectLight: false);
                ValidateThemePalette(mainWindow, expectLight: false);

                await viewModel.SetLightThemeAsync(true);
                mainWindow.UpdateLayout();
                settingsWindow.UpdateLayout();
                triggerEditorWindow.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                ValidateThemedControlTemplates(expectLight: true);
                ValidateThemePalette(mainWindow, expectLight: true);

                await viewModel.SetLightThemeAsync(false);
                mainWindow.UpdateLayout();
                settingsWindow.UpdateLayout();
                triggerEditorWindow.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                ValidateThemedControlTemplates(expectLight: false);
                ValidateThemePalette(mainWindow, expectLight: false);

                // Give the app-first named-pipe wait loop time to remain idle. Waiting
                // for the game is a normal state and must not terminate the process.
                await Task.Delay(250);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            finally
            {
                triggerEditorWindow?.Close();
                settingsWindow?.Close();
                mainWindow?.Close();
                if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            return 0;
        }
        catch (Exception exception)
        {
            var diagnosticPath = Environment.GetEnvironmentVariable("ANOMALY_DONATION_BRIDGE_UI_SMOKE_ERROR_FILE");
            if (!string.IsNullOrWhiteSpace(diagnosticPath))
            {
                try { File.WriteAllText(diagnosticPath, exception.ToString()); }
                catch { }
            }
            return 1;
        }
    }

    private static string CreateTestDirectory(string suiteName)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("ADB_TEST_ROOT");
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppPaths.CreateDefault().RootDirectory, "self-tests")
            : Path.GetFullPath(configuredRoot);
        var directory = Path.Combine(root, suiteName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ValidateWritableTargetBindings(DependencyObject root)
    {
        foreach (var target in EnumerateVisualTree(root))
        {
            if (target is TextBox textBox)
            {
                ValidateWritableTargetBinding(textBox, TextBox.TextProperty);
            }
            if (target is Selector selector)
            {
                ValidateWritableTargetBinding(selector, Selector.SelectedItemProperty);
                ValidateWritableTargetBinding(selector, Selector.SelectedValueProperty);
            }
            if (target is ToggleButton toggleButton)
            {
                ValidateWritableTargetBinding(toggleButton, ToggleButton.IsCheckedProperty);
            }
        }
    }

    private static void ValidateNativeSystemFrame(Window window)
    {
        if (window.WindowStyle == WindowStyle.None)
        {
            throw new InvalidOperationException($"{window.GetType().Name} must retain the native Windows frame.");
        }
        if (OperatingSystem.IsWindows() && new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{window.GetType().Name} did not create a native window handle.");
        }
    }

    private static void ValidateThemedControlTemplates(bool expectLight)
    {
        var scrollBarStyle = Application.Current.TryFindResource(typeof(ScrollBar)) as Style
            ?? throw new InvalidOperationException("The application ScrollBar style is missing.");
        foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
        {
            var scrollBar = new ScrollBar
            {
                Style = scrollBarStyle,
                Orientation = orientation,
                Minimum = 2,
                Maximum = 100,
                Value = 30,
                ViewportSize = 12
            };
            scrollBar.ApplyTemplate();
            var track = scrollBar.Template.FindName("PART_Track", scrollBar) as Track
                ?? throw new InvalidOperationException($"The {orientation} ScrollBar template has no PART_Track.");
            if (track.Orientation != orientation || track.Minimum != 2 || track.Maximum != 100 ||
                track.Value != 30 || track.ViewportSize != 12)
            {
                throw new InvalidOperationException($"The {orientation} ScrollBar track is not bound to its owner.");
            }
            track.Value = 45;
            if (scrollBar.Value != 45)
            {
                throw new InvalidOperationException($"The {orientation} ScrollBar track does not update its owner.");
            }
        }

        var comboBoxStyle = Application.Current.TryFindResource(typeof(ComboBox)) as Style
            ?? throw new InvalidOperationException("The application ComboBox style is missing.");
        var comboBox = new ComboBox
        {
            Style = comboBoxStyle,
            IsEditable = true,
            ItemsSource = new[] { "RUB", "USD" },
            Text = "RUB"
        };
        comboBox.ApplyTemplate();
        if (comboBox.ItemsPanel?.LoadContent() is not VirtualizingStackPanel)
        {
            throw new InvalidOperationException("The ComboBox item list is not virtualized.");
        }
        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is not TextBox editableTextBox ||
            editableTextBox.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("The editable ComboBox text field is unavailable.");
        }
        if (comboBox.Template.FindName("PART_Popup", comboBox) is not Popup ||
            comboBox.Template.FindName("DropDownBorder", comboBox) is not Border dropDownBorder ||
            dropDownBorder.Background is not SolidColorBrush popupBrush ||
            Application.Current.TryFindResource("PanelBrush") is not SolidColorBrush panelBrush ||
            popupBrush.Color != panelBrush.Color)
        {
            throw new InvalidOperationException("The themed ComboBox popup template is unavailable.");
        }
        if (expectLight != ThemeManager.IsLightTheme)
        {
            throw new InvalidOperationException("The requested ComboBox theme was not applied.");
        }
    }

    private static void ValidateThemePalette(MainWindow mainWindow, bool expectLight)
    {
        var background = ThemeColor("BackgroundBrush");
        var text = ThemeColor("TextBrush");
        var even = ThemeColor("ActionRowEvenBrush");
        if (expectLight)
        {
            if (background.R < 220 || background.G < 220 || background.B < 220 ||
                text.R > 80 || text.G > 80 || text.B > 80)
            {
                throw new InvalidOperationException(
                    $"The light palette does not provide a light surface with dark text: background={background}, text={text}.");
            }
        }
        else if (background.R > 60 || background.G > 60 || background.B > 60 ||
                 text.R < 200 || text.G < 200 || text.B < 200)
        {
            throw new InvalidOperationException("The dark palette does not provide a graphite surface with light text.");
        }

        if (mainWindow.Background is not SolidColorBrush windowBackground || windowBackground.Color != background)
        {
            throw new InvalidOperationException("The open main window did not update to the selected theme.");
        }
        if (mainWindow.FindName("ActiveActionsList") is not ItemsControl active || active.AlternationCount != 2 ||
            mainWindow.FindName("PresetsList") is not ItemsControl presets || presets.AlternationCount != 2)
        {
            throw new InvalidOperationException("Active actions and presets must use alternating row containers.");
        }

        var tabs = EnumerateVisualTree(mainWindow).OfType<TabControl>().First();
        tabs.SelectedIndex = 2;
        mainWindow.UpdateLayout();
        presets.UpdateLayout();
        if (presets.ItemContainerGenerator.ContainerFromIndex(0) is not ContentPresenter firstContainer ||
            presets.ItemContainerGenerator.ContainerFromIndex(1) is not ContentPresenter secondContainer ||
            ItemsControl.GetAlternationIndex(firstContainer) != 0 ||
            ItemsControl.GetAlternationIndex(secondContainer) != 1 ||
            mainWindow.TryFindResource("ActionRowCardStyle") is not Style rowStyle)
        {
            throw new InvalidOperationException("Alternating preset row containers were not generated.");
        }

        var firstRow = EnumerateVisualTree(firstContainer).OfType<Border>()
            .FirstOrDefault(border => ReferenceEquals(border.Style, rowStyle));
        var secondRow = EnumerateVisualTree(secondContainer).OfType<Border>()
            .FirstOrDefault(border => ReferenceEquals(border.Style, rowStyle));
        if (firstRow?.Background is not SolidColorBrush firstBrush || firstBrush.Color != even ||
            secondRow?.Background is not SolidColorBrush secondBrush || secondBrush.Color != even)
        {
            throw new InvalidOperationException("Preset cards do not render the shared PDA surface in the selected theme.");
        }
    }

    private static Color ThemeColor(string resourceKey) =>
        Application.Current.TryFindResource(resourceKey) is SolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"Theme color {resourceKey} is unavailable.");

    private static void ValidateMainTabs(MainWindow mainWindow)
    {
        mainWindow.UpdateLayout();
        var tabs = EnumerateVisualTree(mainWindow).OfType<TabControl>().FirstOrDefault()
            ?? throw new InvalidOperationException("The main tab control is missing.");
        if (tabs.Items.Count != 4)
        {
            throw new InvalidOperationException("The main window must expose Active, User Presets, Presets and History tabs.");
        }
        for (var index = 0; index < tabs.Items.Count; index++)
        {
            tabs.SelectedIndex = index;
            mainWindow.UpdateLayout();
        }
        if (!EnumerateVisualTree(mainWindow).OfType<DataGrid>().Any())
        {
            throw new InvalidOperationException("The History tab has no event grid.");
        }
    }

    private static void ValidateWritableTargetBinding(FrameworkElement target, DependencyProperty targetProperty)
    {
        var binding = BindingOperations.GetBinding(target, targetProperty);
        if (binding is null) return;

        var mode = binding.Mode;
        if (mode == BindingMode.Default)
        {
            var metadata = targetProperty.GetMetadata(target.GetType());
            mode = metadata is FrameworkPropertyMetadata { BindsTwoWayByDefault: true }
                ? BindingMode.TwoWay
                : BindingMode.OneWay;
        }
        if (mode is not (BindingMode.TwoWay or BindingMode.OneWayToSource)) return;

        var path = binding.Path?.Path;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('.') || path.Contains('[')) return;
        var source = binding.Source ?? target.DataContext;
        var property = source?.GetType().GetProperty(path, BindingFlags.Instance | BindingFlags.Public);
        if (property is { CanWrite: false })
        {
            throw new InvalidOperationException(
                $"{target.GetType().Name}.{targetProperty.Name} uses {mode} for read-only {source!.GetType().Name}.{path}.");
        }
    }

    private static IEnumerable<DependencyObject> EnumerateVisualTree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in EnumerateVisualTree(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }
}
