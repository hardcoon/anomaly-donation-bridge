using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

// Opt-in renderer: never initializes IPC, accounts, donation processing or real settings.
internal static class BrowserViewUiChecks
{
    public static int Run(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var application = new AnomalyDonationBridge.App.App(startRuntime: false);
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(false);
            var bindingErrors = new StringWriter();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var paths = new AppPaths(Path.Combine(outputDirectory, "isolated-data-" + Guid.NewGuid().ToString("N")));
            var runtime = new BridgeRuntime(paths);
            var profile = new DonationProfile { Name = "Изолированная проверка интерфейса" };
            runtime.Settings.Profiles.Add(profile);
            runtime.Settings.ActiveProfileId = profile.Id;
            var model = new MainViewModel(runtime);
            var presets = DefaultActionCatalog.CreatePresets().Take(8).ToArray();
            for (var index = 0; index < presets.Length; index++)
            {
                model.Presets.Add(new ActionViewModel(presets[index].Clone(), isPreset: true)
                    { LastResult = "Выполнено: проверка истории" });
                var active = presets[index].CloneAsNewInstance();
                active.Triggers.Clear();
                active.Triggers.Add(new TriggerRule { Amount = index + 1, Currency = "RUB" });
                model.ActiveActions.Add(new ActionViewModel(active, displayOrdinal: index + 1));
                model.UserPresets.Add(new ActionViewModel(active.CloneAsNewInstance(), isPreset: true,
                    isUserPreset: true, displayOrdinal: index + 1));
            }
            var window = new MainWindow(model, runtime);
            var root = (FrameworkElement)window.FindName("WideShell");
            var browsers = new[] { model.ActiveBrowser, model.UserPresetBrowser, model.PresetBrowser };
            Layout(root, 1388);
            var tabs = Find<TabControl>(root).First();
            CheckTemplateSearch(window, root, model, tabs, outputDirectory);
            for (var section = 0; section < browsers.Length; section++)
            {
                tabs.SelectedIndex = section;
                foreach (var width in new[] { 1028d, 1388d, 1868d })
                {
                    foreach (var option in ActionBrowserViewModel.ViewOptions)
                    {
                        Layout(root, width);
                        var selector = Find<ComboBox>(root).Single(combo => AutomationProperties.GetName(combo) == "Вид шаблонов");
                        Check(selector.Items.Cast<BrowserViewOption>().SequenceEqual(ActionBrowserViewModel.ViewOptions), "Wrong view options.");
                        selector.SetCurrentValue(Selector.SelectedItemProperty, option);
                        Layout(root, width);
                        Check(browsers[section].SelectedView == option, "Selecting a view did not update the browser.");
                        Check(!Find<Slider>(root).Any(), "The old density slider is still present.");
                        var cards = Find<ActionCardPanel>(root).ToArray();
                        if (option.Columns == -1)
                        {
                            Check(cards.Length == 0, "Compact list still uses a grid.");
                            var list = (ItemsControl)window.FindName(section switch
                            { 0 => "ActiveActionsList", 1 => "UserPresetsList", _ => "PresetsList" });
                            Check(Find<Button>(list).Count(button => Equals(button.Content, "Настроить")) == presets.Length,
                                "Compact rows lost template controls.");
                        }
                        else
                        {
                            var panel = cards.Single();
                            Check(panel.Columns == option.Columns, "Grid does not match the selected view.");
                            if (option.Columns > 0)
                                Check(panel.Children.Cast<UIElement>().Take(option.Columns)
                                    .Select(child => Math.Round(child.TranslatePoint(new Point(), panel).X)).Distinct().Count() == option.Columns,
                                    "Grid did not render the requested number of columns.");
                            foreach (FrameworkElement card in panel.Children) CheckControlsInBounds(card);
                        }
                        CheckControlsInBounds(root);
                        CheckActionToolbars(root);
                        Check(!Find<TextBlock>(root).Any(text => text.Text.StartsWith("Результат:", StringComparison.Ordinal) ||
                            text.Text.Contains("Ещё не запускалось", StringComparison.Ordinal) ||
                            text.Text.Contains("Выполнено: проверка истории", StringComparison.Ordinal)),
                            "Execution results are still displayed on template cards.");
                        if (width == 1388 && section == 2 && option.Columns is -1 or 5)
                            Save(root, Path.Combine(outputDirectory, $"templates-{option.Columns}.png"));
                    }
                }
                browsers[section].SelectedView = ActionBrowserViewModel.ViewOptions[3]; // two columns
                Layout(root, 1388);
                var descriptions = Find<TemplateDescriptionTextBlock>(root).Where(text => text.DataContext is ActionViewModel action &&
                    text.Description == action.Description && text.FontSize == 12).ToArray();
                Check(descriptions.Length == presets.Length && descriptions.All(text => text.Visibility == Visibility.Visible),
                    "Descriptions are missing by default.");
                var summaries = Find<ItemsControl>(root).Where(control =>
                    BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path?.Path == "ParameterSummaryItems").ToArray();
                Check(summaries.Length == presets.Length && summaries.All(control => control.Visibility == Visibility.Visible),
                    "Selected settings are missing with descriptions enabled.");
                Check(summaries.Any(control => control.Items.Count > 0), "Summary visibility test has no selected settings.");
                var initialHeight = Find<ActionCardPanel>(root).Single().Children[0].DesiredSize.Height;
                var toggle = Find<CheckBox>(root).Single(check => Equals(check.Content, "Описание"));
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Layout(root, 1388);
                Check(!model.ShowTemplateDescriptions && descriptions.All(text => text.Visibility == Visibility.Collapsed),
                    "Description toggle did not collapse the text.");
                Check(summaries.All(control => control.Visibility == Visibility.Collapsed),
                    "Description toggle left selected settings visible on cards.");
                Check(Find<ActionCardPanel>(root).Single().Children[0].DesiredSize.Height < initialHeight,
                    "Hiding descriptions left their empty space.");
                if (section == 2) Save(root, Path.Combine(outputDirectory, "templates-no-description.png"));
                tabs.SelectedIndex = (section + 1) % browsers.Length;
                Layout(root, 1388);
                Check(Find<CheckBox>(root).Single(check => Equals(check.Content, "Описание")).IsChecked == false,
                    "Description visibility was not shared between sections.");
                Check(Find<ItemsControl>(root).Where(control =>
                    BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path?.Path == "ParameterSummaryItems")
                    .All(control => control.Visibility == Visibility.Collapsed),
                    "Selected settings became visible after switching sections with descriptions disabled.");
                model.ShowTemplateDescriptions = true;
                tabs.SelectedIndex = section;
                Layout(root, 1388);
                Check(Find<TemplateDescriptionTextBlock>(root).All(text => text.Visibility == Visibility.Visible) &&
                    Find<ItemsControl>(root).Where(control =>
                        BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path?.Path == "ParameterSummaryItems")
                        .All(control => control.Visibility == Visibility.Visible),
                    "Re-enabling descriptions did not restore descriptions and selected settings.");
            }
            ThemeManager.Apply(true);
            Layout(root, 1388);
            Save(root, Path.Combine(outputDirectory, "templates-light.png"));
            CheckCheckboxFocus(outputDirectory);
            CheckSequenceControls(root, model, tabs, outputDirectory);
            Console.WriteLine("PASS stable centered checkmark and real keyboard-focus highlight in both themes.");
            runtime.SaveUiPreferencesAsync(runtime.UiPreferences).GetAwaiter().GetResult();
            Check(!File.Exists(paths.SettingsFile), "UI preferences modified donation settings.");
            Check(string.IsNullOrWhiteSpace(bindingErrors.ToString()), "WPF binding errors: " + bindingErrors);
            window.Close();
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("PASS unified views, 1–5 columns, compact rows, shared description/settings visibility, no result rows, narrow/wide layouts and themes.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckSequenceControls(FrameworkElement root, MainViewModel model, TabControl tabs, string output)
    {
        tabs.SelectedIndex = 0;
        model.ActiveBrowser.SearchText = string.Empty; model.ActiveBrowser.SelectCategory("all");
        foreach (var action in model.ActiveActions.Take(3)) action.ReplaceTriggers([new TriggerRule { Amount = 31, Currency = "RUB" }]);
        model.ActiveBrowser.VisibleEntries.Single(entry => entry.IsGroup).Group!.IsExpanded = true;
        foreach (var light in new[] { false, true })
        {
            ThemeManager.Apply(light);
            foreach (var columns in new[] { -1, 0, 1, 2, 3, 4, 5 })
            {
                var compact = columns == -1;
                model.ActiveBrowser.SelectedView = ActionBrowserViewModel.ViewOptions.Single(option => option.Columns == columns);
                foreach (var width in new[] { 1028d, 1388d, 1868d })
                {
                    Layout(root, width);
                    var rows = Find<Border>(root).Where(border => Equals(border.Tag, "SequenceStepRow")).ToList();
                    Check(rows.Count == 3 && rows.All(row => row.AllowDrop), "Sequence drop targets are missing.");
                    Check(rows.Select(row => ((ActionViewModel)row.DataContext).SequenceOrdinal).SequenceEqual([1, 2, 3]), "Wrong visible sequence numbers.");
                    Check(Find<CheckBox>(root).Count(box => Equals(box.Content, "Выполнять строго по порядку")) == 1, "Strict sequence control is missing or duplicated.");
                    foreach (var row in rows)
                    {
                        var handles = Find<Border>(row).Where(border => System.Windows.Automation.AutomationProperties.GetName(border) == "Переместить шаг").ToList();
                        Check(handles.Count == 1 && handles[0].Cursor == System.Windows.Input.Cursors.SizeAll && handles[0].ActualWidth >= 28,
                            "Sequence grip is missing or too small.");
                        Check(handles[0].TranslatePoint(new Point(), row).X >= 0 && row.ActualWidth <= root.ActualWidth, "Sequence controls overflow their row.");
                        var size = row.RenderSize;
                        if (!compact)
                        {
                            Check(row.Name == "SequenceCardDropTarget" && row.Background is not null && handles[0].ActualWidth >= row.ActualWidth - 2,
                                "Grid drop surface/header is not fully hit-testable.");
                            Check(!MainWindow.SequenceDropAfter(size, new Point(1, size.Height - 1), true) &&
                                  MainWindow.SequenceDropAfter(size, new Point(size.Width - 1, 1), true),
                                "Grid insertion must follow left/right, not the mouse's height.");
                        }
                        else Check(!MainWindow.SequenceDropAfter(size, new Point(size.Width - 1, 1), false) &&
                                   MainWindow.SequenceDropAfter(size, new Point(1, size.Height - 1), false),
                            "Compact list lost its vertical insertion direction.");
                    }
                    try { CheckActionToolbars(root); }
                    catch (InvalidOperationException exception)
                    {
                        Save(root, Path.Combine(output, "toolbar-layout-failure.png"));
                        throw new InvalidOperationException($"Group layout columns={columns}, width={width}, light={light}: {exception.Message}", exception);
                    }
                    if (width == 1388 && columns is -1 or 0 or 3) Save(root, Path.Combine(output, $"sequence-{columns}-{(light ? "light" : "dark")}.png"));
                }
            }
        }
    }

    private static void Layout(FrameworkElement root, double width)
    {
        root.Margin = new Thickness(0);
        root.Width = width;
        root.Height = 782;
        for (var pass = 0; pass < 3; pass++)
        {
            root.Measure(new Size(width, 782));
            root.Arrange(new Rect(0, 0, width, 782));
            root.UpdateLayout();
            root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static void CheckTemplateSearch(MainWindow window, FrameworkElement root, MainViewModel model,
        TabControl tabs, string outputDirectory)
    {
        var source = model.ActiveActions.ToArray();
        var input = (TextBox)tabs.Template.FindName("TemplateSearchTextBox", tabs);
        var open = (Button)tabs.Template.FindName("TemplateSearchButton", tabs);
        var clear = (Button)tabs.Template.FindName("ClearTemplateSearchButton", tabs);
        var search = (Border)tabs.Template.FindName("TemplateSearchSurface", tabs);
        var status = (Border)tabs.Template.FindName("QueueStatusSurface", tabs);
        var content = (ContentPresenter)tabs.Template.FindName("PART_SelectedContentHost", tabs);
        void Queue(string text) => typeof(MainViewModel).GetProperty(nameof(MainViewModel.QueueStatusText))!.SetValue(model, text);
        foreach (var width in new[] { 1000d, 1388d, 1868d })
        {
            tabs.SelectedIndex = 0; model.ClearTemplateSearch(); Queue(string.Empty); Layout(root, width);
            var initialTop = content.TranslatePoint(new Point(), root).Y;
            Check(search.ActualWidth == 270 && input.ActualWidth > 150 && search.Visibility == Visibility.Visible,
                "Search must initially be a visible text field, including with an empty query.");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, width);
            Check(search.ActualWidth == 270, "Focusing search changed its width.");
            var expandedX = search.TranslatePoint(new Point(), root).X;
            Check(content.TranslatePoint(new Point(), root).Y == initialTop, "Focusing search moved the lower toolbar.");
            var query = source[0].DisplayName.ToUpperInvariant();
            input.SetCurrentValue(TextBox.TextProperty, query); Layout(root, width);
            foreach (var section in new[] { 0, 1, 2 })
            {
                tabs.SelectedIndex = section; Layout(root, width);
                var browser = section switch { 0 => model.ActiveBrowser, 1 => model.UserPresetBrowser, _ => model.PresetBrowser };
                Check(model.SelectedSectionIndex == section && browser.SearchText == query && model.TemplateSearchText == query &&
                    browser.VisibleActions.Cast<ActionViewModel>().All(action => action.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
                    !browser.VisibleActions.IsEmpty, "Search query/filter lost when switching template sections.");
            }
            tabs.SelectedIndex = 0; Layout(root, width);
            if (width == 1388) Save(root, Path.Combine(outputDirectory, "search-expanded.png"));
            Queue("Следующий эффект через 00:12 · в очереди: 3"); Layout(root, width);
            Check(search.Visibility == Visibility.Visible && search.ActualWidth == 270 && open.IsEnabled && model.TemplateSearchText == query,
                "Queue status closed or disabled the active search field.");
            var searchRight = search.TranslatePoint(new Point(search.ActualWidth, 0), root).X;
            var statusLeft = status.TranslatePoint(new Point(), root).X;
            Check(searchRight < statusLeft && search.TranslatePoint(new Point(), root).X < expandedX - 100,
                "Countdown overlapped the editable search field.");
            Check(content.TranslatePoint(new Point(), root).Y >= initialTop, "Countdown overlapped the lower toolbar.");
            CheckControlsInBounds(root);
            if (width == 1388) Save(root, Path.Combine(outputDirectory, "search-countdown.png"));
            Queue(string.Empty); Layout(root, width);
            Check(search.Visibility == Visibility.Visible && open.IsEnabled && search.TranslatePoint(new Point(), root).X == expandedX,
                "Countdown completion changed the active search state.");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, width);
            Check(input.Text == query, "Search text disappeared after the countdown.");
            tabs.SelectedIndex = 3; Layout(root, width);
            Check(search.Visibility == Visibility.Collapsed && !model.ShowTemplateSearch && model.TemplateSearchText == query,
                "Template search must not filter History or lose its query there.");
            tabs.SelectedIndex = 0; Layout(root, width);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, width);
            input.SetCurrentValue(TextBox.TextProperty, "no such template 891723"); Layout(root, width);
            Check(model.ActiveBrowser.VisibleActions.IsEmpty && Find<TextBlock>(root).Any(text => text.Visibility == Visibility.Visible && text.Text == "Ничего не найдено"),
                "No-match search has no correct empty state.");
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, width);
            Check(model.TemplateSearchText.Length == 0 && search.Visibility == Visibility.Visible && search.ActualWidth == 270 && input.ActualWidth > 150 &&
                model.ActiveBrowser.VisibleActions.Cast<ActionViewModel>().SequenceEqual(source), "Clearing search hid the field or did not restore all original action instances.");
        }
        Console.WriteLine("PASS always-visible search in all three template sections, clear and queue countdown; no header/content overlap at minimum/wide sizes.");
    }

    private static void CheckCheckboxFocus(string outputDirectory)
    {
        var toggle = new CheckBox { Content = "Описание", IsChecked = true, HorizontalAlignment = HorizontalAlignment.Left };
        var other = new Button { Content = "Снять фокус", HorizontalAlignment = HorizontalAlignment.Left };
        var surface = new StackPanel { Margin = new Thickness(12) };
        surface.Children.Add(toggle);
        surface.Children.Add(other);
        var host = new Window
        {
            Content = surface, Width = 280, Height = 130,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        try
        {
            host.Show();
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                host.UpdateLayout();
                Check(toggle.FocusVisualStyle is null, "The default dotted focus rectangle is still enabled.");
                var box = (Border)toggle.Template.FindName("Box", toggle);
                var glyph = (System.Windows.Shapes.Path)toggle.Template.FindName("Check", toggle);
                foreach (var enabled in new[] { false, true })
                {
                    toggle.IsChecked = enabled;
                    Keyboard.Focus(other);
                    host.UpdateLayout();
                    var before = glyph.TranslatePoint(new Point(), toggle);
                    var size = glyph.RenderSize;
                    Keyboard.Focus(toggle);
                    host.UpdateLayout();
                    Check(toggle.IsKeyboardFocused, "The checkbox did not receive real keyboard focus.");
                    Check(box.BorderThickness == new Thickness(1) && glyph.TranslatePoint(new Point(), toggle) == before && glyph.RenderSize == size,
                        "Focusing the checkbox shifted its glyph or changed border thickness.");
                    var expected = (SolidColorBrush)Application.Current.Resources["BlueBrush"];
                    Check(box.BorderBrush is SolidColorBrush brush && brush.Color == expected.Color,
                        "Keyboard focus lost its visible theme highlight.");
                    if (enabled)
                    {
                        var origin = glyph.TranslatePoint(new Point(), box);
                        Check(Math.Abs(origin.X + glyph.ActualWidth / 2 - box.ActualWidth / 2) <= 0.5 &&
                              Math.Abs(origin.Y + glyph.ActualHeight / 2 - box.ActualHeight / 2) <= 0.5,
                            "The checkmark is not centered.");
                        Save(surface, System.IO.Path.Combine(outputDirectory, light ? "checkbox-focused-light.png" : "checkbox-focused-dark.png"));
                    }
                }
            }
        }
        finally { host.Close(); }
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }

    private static void CheckControlsInBounds(FrameworkElement root)
    {
        foreach (var control in Find<Control>(root).Where(control => control is Button or ComboBox or CheckBox or ListBoxItem &&
            control.Visibility == Visibility.Visible && control.ActualWidth > 0))
        {
            var x = control.TranslatePoint(new Point(), root).X;
            Check(x >= -1 && x + control.ActualWidth <= root.ActualWidth + 1,
                $"Control '{(control as ContentControl)?.Content ?? control.GetType().Name}' overflows: {x} + {control.ActualWidth} / {root.ActualWidth}.");
        }
    }

    private static void CheckActionToolbars(FrameworkElement root)
    {
        var toolbars = Find<CardToolbarPanel>(root).Where(panel => panel.Visibility == Visibility.Visible && panel.ActualWidth > 0).ToArray();
        Check(toolbars.Length > 0, "No action toolbars were exercised by the layout check.");
        foreach (var toolbar in toolbars)
        {
            var actions = toolbar.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible).ToArray();
            var labelled = actions.Where(button => ButtonVisual.GetGlyph(button).Length > 0).ToArray();
            Check(labelled.Select(ButtonVisual.GetShowLabel).Distinct().Count() <= 1, "Only part of an action row lost its labels.");
            double? previousRight = null;
            foreach (var button in actions)
            {
                var point = button.TranslatePoint(new Point(), toolbar);
                Check(Math.Abs(point.Y - actions[0].TranslatePoint(new Point(), toolbar).Y) <= 4,
                    "Actions wrapped to a second line.");
                Check(point.X >= -1 && point.X + button.ActualWidth <= toolbar.ActualWidth + 1,
                    $"Action '{button.Content}' leaves the toolbar at {toolbar.ActualWidth}.");
                if (previousRight is { } right) Check(point.X >= right + 5, "Adjacent actions overlap or lose their gap.");
                previousRight = point.X + button.ActualWidth;
                if (ButtonVisual.GetGlyph(button).Length == 0) continue;
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) && button.ToolTip is not null,
                    "Icon-only action lost its name/tooltip.");
                var icon = Find<TextBlock>(button).Single(block => Equals(block.Tag, "ButtonIcon"));
                var label = Find<TextBlock>(button).Single(block => Equals(block.Tag, "ButtonLabel"));
                if (Equals(button.Content, "+ Сумма")) Check(label.Text == "Сумма", "The amount action displays a duplicated plus sign.");
                Check(icon.Foreground.ToString() == button.Foreground.ToString(), "Icon no longer follows the text/theme color.");
                Check((label.Visibility == Visibility.Visible) == ButtonVisual.GetShowLabel(button), "Label state is stale after resizing.");
                foreach (var text in new[] { icon, label }.Where(block => block.Visibility == Visibility.Visible))
                {
                    var origin = text.TranslatePoint(new Point(), button);
                    Check(origin.X >= -1 && origin.X + text.ActualWidth <= button.ActualWidth + 1,
                        $"Icon or label '{text.Text}' is clipped inside '{button.Content}': x={origin.X}, text width={text.ActualWidth}, button width={button.ActualWidth}, toolbar width={toolbar.ActualWidth}, labels={ButtonVisual.GetShowLabel(button)}, item={(button.DataContext as ActionViewModel)?.DisplayName}, rootY={toolbar.TranslatePoint(new Point(), root).Y}, label layout={text.IsMeasureValid}/{text.IsArrangeValid}, button layout={button.IsMeasureValid}/{button.IsArrangeValid}, toolbar layout={toolbar.IsMeasureValid}/{toolbar.IsArrangeValid}.");
                }
            }
        }
    }

    private static void Save(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
