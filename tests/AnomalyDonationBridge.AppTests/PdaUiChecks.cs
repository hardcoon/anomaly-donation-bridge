using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
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

// Opt-in, disconnected WPF rendering: no game pipe, real account, or user settings are initialized.
internal static class PdaUiChecks
{
    public static int RunPriceEditor(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var application = new AnomalyDonationBridge.App.App(startRuntime: false);
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                CheckPriceEditor(outputDirectory, light ? "-light" : "");
            }
            Console.WriteLine("PASS price editor: gear alignment at minimum/default widths, currency checkboxes and dark/light rendering (disconnected).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckPriceEditor(string outputDirectory, string suffix = "")
    {
        var triggerWindow = new TriggerEditorWindow(new TriggerEditorViewModel(null, ["RUB", "USD", "EUR"]));
        var triggerRoot = (FrameworkElement)triggerWindow.Content;
        foreach (var width in new[] { 504d, 550d })
        {
            Layout(triggerRoot, width, 700);
            var calculateButton = (Button)triggerWindow.FindName("CalculatePricesButton");
            var currencyButton = (Button)triggerWindow.FindName("CurrencySettingsButton");
            Check(calculateButton.TranslatePoint(new Point(calculateButton.ActualWidth, 0), triggerRoot).X <
                    currencyButton.TranslatePoint(new Point(), triggerRoot).X &&
                    Math.Abs(calculateButton.ActualHeight - currencyButton.ActualHeight) < 1,
                "The currency gear overlaps the calculation button or has a mismatched height.");
            CheckHorizontalBounds(triggerRoot);
        }
        Save(triggerRoot, Path.Combine(outputDirectory, "pda-price-editor" + suffix + ".png"));
        var currencyWindow = new CurrencyCalculationSettingsWindow(["RUB", "USD", "EUR"])
        {
            Left = -32000, Top = -32000, Opacity = 0, ShowActivated = false
        };
        // SizeToContent must be measured by its native Window, rather than
        // manually arranging a child that an unshown Window can rearrange.
        currencyWindow.Show();currencyWindow.UpdateLayout();
        currencyWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        var currencyRoot = (FrameworkElement)currencyWindow.Content;
        Check(Find<CheckBox>(currencyRoot).Count() == 8 &&
            currencyWindow.Options.Count(option => option.IsSelected) == 3,
            "The currency settings window lost its eight themed checkboxes or selected values.");
        CheckHorizontalBounds(currencyRoot);
        var apply = Find<Button>(currencyRoot).Single(button => Equals(button.Content, "Применить"));
        Check(apply.TranslatePoint(new Point(0, apply.ActualHeight), currencyRoot).Y <= currencyRoot.ActualHeight,
            "The auto-sized currency window clipped its Apply button.");
        Save(currencyRoot, Path.Combine(outputDirectory, "pda-currency-settings" + suffix + ".png"));
        currencyWindow.Close();
    }

    public static int Run(string outputDirectory, bool updatedUiOnly = false)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var application = new AnomalyDonationBridge.App.App(startRuntime: false);
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(false);
            var longListScrollBar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Minimum = 0,
                Maximum = 9990,
                ViewportSize = 10,
                Style = (Style)application.FindResource(typeof(ScrollBar))
            };
            longListScrollBar.ApplyTemplate();
            Layout(longListScrollBar, 16, 400);
            var longListThumb = Find<Thumb>(longListScrollBar).Single();
            Check(longListThumb.ActualHeight >= 60,
                $"Long-list scrollbar thumb is still too small: {longListThumb.ActualHeight:0.##} px.");
            Save(longListScrollBar, Path.Combine(outputDirectory, "pda-long-list-scrollbar.png"));
            var trace = new StringWriter();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(trace));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(outputDirectory, "isolated-unused-data")));
            var fixture = new Fixture();
            var window = new MainWindow(new MainViewModel(runtime), runtime) { DataContext = fixture };
            var root = (FrameworkElement)window.Content;

            Layout(root, 1328, 782);
            var bridgeUpdate = Find<Button>(root).Single(button => button.Name == "HeaderBridgeUpdateButton");
            Check(bridgeUpdate.Visibility == Visibility.Collapsed, "Header update button occupies space without an update.");
            fixture.ShowBridgeUpdate = true;
            foreach (var width in new[] { 972d, 1328d, 1868d })
            {
                Layout(root, width, 782);
                var origin = bridgeUpdate.TranslatePoint(new Point(), root);
                Check(IsEffectivelyVisible(bridgeUpdate) && Math.Abs(origin.X + bridgeUpdate.ActualWidth / 2 - root.ActualWidth / 2) < 1,
                    $"Header update button is not centred at width {width}: x={origin.X}, button={bridgeUpdate.ActualWidth}, root={root.ActualWidth}, visible={IsEffectivelyVisible(bridgeUpdate)}");
                var profiles = Find<Button>(root).Single(button => Equals(button.Content, "Профили"));
                Check(origin.X + bridgeUpdate.ActualWidth < profiles.TranslatePoint(new Point(), root).X,
                    "Header update overlaps Profiles at width " + width);
                CheckHorizontalBounds(root);
            }
            Layout(root, 1328, 782);
            Save(root, Path.Combine(outputDirectory, "pda-header-update.png"));
            fixture.ShowBridgeUpdate = false;
            Layout(root, 1328, 782);
            Check(bridgeUpdate.Visibility == Visibility.Collapsed, "Header did not collapse after update notification cleared.");
            if (updatedUiOnly)
            {
                CheckBurerEditor(window, outputDirectory);
                var bindingErrors = trace.ToString();
                Check(string.IsNullOrEmpty(bindingErrors), "Binding errors in updated UI: " + bindingErrors);
                Console.WriteLine("Header update visibility/centering and two-row burer editor rendering passed (disconnected).");
                return 0;
            }
            Check(Find<ActionCardPanel>(root).First().ActualWidth > 1052, "Wide layout should use two columns.");
            var activeCategoryOrigin = Find<ListBox>(root)
                .Single(list => list.Items.Count == ActionBrowserViewModel.Categories.Count && list.Items.Cast<object>().All(item => item is ActionCategory))
                .TranslatePoint(new Point(), root);
            var first = fixture.ActiveActions[0];
            var priceButtons = Find<Button>(root).Where(button => button.DataContext is TriggerRuleViewModel trigger &&
                ReferenceEquals(trigger.Owner, first) && ReferenceEquals(button.Command, fixture.EditTriggerCommand)).ToArray();
            Check(priceButtons.Length == 7, "Not all seven independent price buttons are rendered.");
            Check(priceButtons.All(button => button.ActualHeight >= 32), "Price hit targets are too short.");
            Check(priceButtons.Select(button => button.TranslatePoint(new Point(), root).Y).Distinct().Count() >= 2,
                "Seven prices should wrap at ordinary two-column card width.");
            CheckHorizontalBounds(root);
            Save(root, Path.Combine(outputDirectory, "pda-dark-1380.png"));

            var original = first.Model.Triggers.Select(rule => rule.Clone()).ToArray();
            Click(priceButtons[1]);
            Check(first.Triggers[1].Model.Amount == 177 && first.Triggers.Where((_, index) => index != 1)
                .Select(rule => rule.Model.Amount).SequenceEqual(original.Where((_, index) => index != 1).Select(rule => rule.Amount)),
                "Editing one price changed its siblings.");
            var remove = Find<Button>(root).Single(button => ReferenceEquals(button.DataContext, first.Triggers[2]) &&
                ReferenceEquals(button.Command, fixture.DeleteTriggerCommand));
            Click(remove);
            Layout(root, 1328, 782);
            Check(first.Triggers.Count == 6 && first.PriceItems.Count == 7, "Deleting a price lost the add button.");
            var add = Find<Button>(root).Single(button => ReferenceEquals(button.DataContext, first) &&
                ReferenceEquals(button.Command, fixture.AddTriggerCommand));
            Click(add);
            Layout(root, 1328, 782);
            Check(first.Triggers.Count == 7 && first.Triggers[^1].Model.Currency == "EUR", "Inline price add did not reach its owning action.");

            ActionDefinition? savedDraft = null;
            var editor = new ActionEditorViewModel(first, draft => { savedDraft = draft.Model.Clone(); return Task.CompletedTask; });
            var editorWindow = new ActionEditorWindow(editor, (DataTemplate)window.Resources["ActionEditorTemplate"]);
            Check(editorWindow.WindowState == WindowState.Normal && editorWindow.Width == 980,
                "The action editor no longer opens as the widened non-maximized modal.");
            var editorRoot = (FrameworkElement)editorWindow.Content;
            Layout(editorRoot, 760, 650);
            var selectors = Find<CheckBox>(editorRoot).Where(box => box.DataContext is CatalogOptionViewModel option && option.IsAvailable).Take(3).ToArray();
            Check(selectors.Length == 3, "The popup catalog checkboxes were not rendered.");
            foreach (var selector in selectors) selector.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            var radius = Find<TextBox>(editorRoot).Single(box => box.DataContext is ParameterValueViewModel parameter &&
                ReferenceEquals(editor.Draft.Parameters.First(item => item.Definition.Key == "radius_meters"), parameter) &&
                BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.ParentBinding.Path.Path == "Value");
            radius.SetCurrentValue(TextBox.TextProperty, "");
            radius.SetCurrentValue(TextBox.TextProperty, "42");
            var cooldown = Find<TextBox>(editorRoot).Single(box => ReferenceEquals(box.DataContext, editor.Draft));
            cooldown.SetCurrentValue(TextBox.TextProperty, "17");
            Check(first.Model.Parameters["radius_meters"] != "42" && first.Model.CooldownSeconds != 17,
                "Popup edits escaped into the live action before Save.");
            Check(editor.SaveAsync().GetAwaiter().GetResult(), "Popup save failed: " + editor.Error);
            Check(savedDraft is { CooldownSeconds: 17 } saved && saved.Parameters["radius_meters"] == "42" &&
                selectors.All(box => box.DataContext is CatalogOptionViewModel { IsSelected: false } option &&
                    !saved.Parameters["squad_sections"].Split(',').Contains(option.SectionId, StringComparer.OrdinalIgnoreCase)),
                "Several checkbox and numeric edits were not committed together.");
            Layout(editorRoot, 760, 650);
            CheckHorizontalBounds(editorRoot);
            Save(editorRoot, Path.Combine(outputDirectory, "pda-settings-popup.png"));
            CheckBurerEditor(window, outputDirectory);
            Check(Find<Button>(editorRoot).Any(button => button.IsCancel && Equals(button.Content, "Отмена")) &&
                Find<Button>(editorRoot).Any(button => Equals(button.Content, "Сохранить и закрыть")), "Popup footer actions are missing.");

            var unavailableOption = new CatalogOptionViewModel(new CatalogEntry
            {
                SectionId = "qa_unavailable_item",
                Kind = CatalogEntryKind.Item,
                DisplayName = "Недоступный предмет",
                IsAvailable = false,
                QuarantineReason = QuarantineReason.Blacklisted
            }, false, null);
            var unavailablePreview = new ContentControl
            {
                Content = unavailableOption,
                ContentTemplate = (DataTemplate)window.Resources["CompactCatalogOptionTemplate"]
            };
            Layout(unavailablePreview, 360, 34);
            var unavailableText = Find<TextBlock>(unavailablePreview).Single();
            var unavailableSurface = Find<Border>(unavailablePreview).Single(border => ReferenceEquals(border.DataContext, unavailableOption));
            Check(unavailableText.Foreground is SolidColorBrush warningText &&
                warningText.Color == ((SolidColorBrush)Application.Current.Resources["DangerTextBrush"]).Color &&
                unavailableSurface.Background is SolidColorBrush warningSurface && warningSurface.Color.A == 0,
                "Unavailable catalog entries must use pale red text without a colored background.");
            Save(unavailablePreview, Path.Combine(outputDirectory, "pda-unavailable-catalog-option.png"));

            var npcAction = fixture.Presets.Single(action => action.HandlerId == "spawn_npcs");
            var npcEditor = new ActionEditorViewModel(npcAction, _ => Task.CompletedTask);
            var npcWindow = new ActionEditorWindow(npcEditor, (DataTemplate)window.Resources["ActionEditorTemplate"]);
            var npcRoot = (FrameworkElement)npcWindow.Content;
            Layout(npcRoot, 930, 650);
            var npcRow = npcEditor.Draft.SpawnGroupRows.Single();
            var npcSearch = Find<TextBox>(npcRoot).Single(box => ReferenceEquals(box.DataContext, npcRow) &&
                BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.ParentBinding.Path.Path == "Picker.CatalogSearch");
            var npcCategory = Find<ComboBox>(npcRoot).Single(combo => ReferenceEquals(combo.DataContext, npcRow) &&
                BindingOperations.GetBindingExpression(combo, Selector.SelectedItemProperty)?.ParentBinding.Path.Path == "Picker.SelectedCatalogCategory");
            var npcPicker = Find<ComboBox>(npcRoot).Single(combo => ReferenceEquals(combo.DataContext, npcRow) &&
                BindingOperations.GetBindingExpression(combo, ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "Picker.CatalogOptionsView");
            var npcCount = Find<TextBox>(npcRoot).Single(box => ReferenceEquals(box.DataContext, npcRow) &&
                BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.ParentBinding.Path.Path == "CountText");
            var npcDelete = Find<Button>(npcRoot).Single(button => ReferenceEquals(button.DataContext, npcRow) && Equals(button.Content, "Удалить"));
            var npcControls = new FrameworkElement[] { npcCategory, npcSearch, npcPicker, npcCount, npcDelete };
            var npcOrigins = npcControls.Select(control => control.TranslatePoint(new Point(), npcRoot)).ToArray();
            npcSearch.ApplyTemplate();
            var searchContentHost = (ScrollViewer?)npcSearch.Template.FindName("PART_ContentHost", npcSearch);
            var searchPlaceholder = (TextBlock?)npcSearch.Template.FindName("Placeholder", npcSearch);
            Check(npcSearch.Padding.Left == 4 && searchContentHost?.Margin.Left == 4 && searchPlaceholder?.Margin.Left == 4 && npcCount.Padding.Left == 10,
                "Compact search caret, watermark and text no longer share the intended tight left inset.");
            Check(npcOrigins.Zip(npcOrigins.Skip(1), (left, right) => left.X < right.X).All(result => result) &&
                npcOrigins.Max(point => point.Y) - npcOrigins.Min(point => point.Y) < 2 &&
                npcControls.All(control => Math.Abs(control.ActualHeight - 34) < 0.5) && npcCategory.ActualWidth >= 155,
                $"NPC category, search, selection, count and delete are not one aligned compact row: " +
                $"origins={string.Join(";", npcOrigins.Select(point => $"{point.X:F1},{point.Y:F1}"))}; " +
                $"heights={string.Join(",", npcControls.Select(control => control.ActualHeight.ToString("F1")))}; categoryWidth={npcCategory.ActualWidth:F1}.");
            Check(npcPicker.Items.Count == 12 && npcPicker.MaxDropDownHeight >= 400,
                "The NPC selector lost its long scrollable dropdown.");
            npcSearch.SetCurrentValue(TextBox.TextProperty, "qa_npc_12");
            Check(npcPicker.Items.Count == 1, "NPC search did not filter the compact selector.");
            npcSearch.SetCurrentValue(TextBox.TextProperty, string.Empty);
            Layout(npcRoot, 930, 650);
            Check(Equals(npcPicker.SelectedValue, "qa_npc_1"),
                "Clearing compact search did not restore the selected NPC.");
            npcEditor.Draft.AddSpawnGroupCommand.Execute(null);
            Layout(npcRoot, 930, 650);
            Check(npcEditor.Draft.SpawnGroupRows.Count == 2 &&
                npcEditor.Draft.SpawnGroupRows.All(row => Find<Border>(npcRoot)
                    .Where(border => ReferenceEquals(border.DataContext, row)).Max(border => border.ActualHeight) < 100),
                "Multiple NPC groups do not remain short rows.");
            Check(Find<ComboBox>(npcRoot).Where(combo => ReferenceEquals(combo.DataContext, npcRow) &&
                    BindingOperations.GetBindingExpression(combo, Selector.SelectedValueProperty)?.ParentBinding.Path.Path == "Picker.Value")
                .Any(combo => Equals(combo.SelectedValue, "qa_npc_1")),
                "Adding another group blanked the existing NPC selection.");
            CheckHorizontalBounds(npcRoot);
            Save(npcRoot, Path.Combine(outputDirectory, "pda-spawn-npc-editor.png"));

            var mutantAction = fixture.Presets.Single(action => action.HandlerId == "spawn_mutants");
            var mutantEditor = new ActionEditorViewModel(mutantAction, _ => Task.CompletedTask);
            var mutantWindow = new ActionEditorWindow(mutantEditor, (DataTemplate)window.Resources["ActionEditorTemplate"]);
            var mutantRoot = (FrameworkElement)mutantWindow.Content;
            Layout(mutantRoot, 930, 650);
            var mutantRow = mutantEditor.Draft.SpawnGroupRows.Single();
            Check(Find<ComboBox>(mutantRoot).Any(combo => ReferenceEquals(combo.DataContext, mutantRow) &&
                    BindingOperations.GetBindingExpression(combo, ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "Picker.CatalogOptionsView") &&
                Find<TextBox>(mutantRoot).Any(box => ReferenceEquals(box.DataContext, mutantRow) &&
                    BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.ParentBinding.Path.Path == "Picker.CatalogSearch"),
                "The mutant editor did not receive the shared compact row layout.");

            var itemAction = fixture.Presets.Single(action => action.HandlerId == "spawn_items");
            var itemEditor = new ActionEditorViewModel(itemAction, _ => Task.CompletedTask);
            var itemWindow = new ActionEditorWindow(itemEditor, (DataTemplate)window.Resources["ActionEditorTemplate"]);
            var itemRoot = (FrameworkElement)itemWindow.Content;
            Layout(itemRoot, 930, 650);
            var itemRow = itemEditor.Draft.ItemSpawnRows.Single();
            var itemSearch = Find<TextBox>(itemRoot).Single(box => ReferenceEquals(box.DataContext, itemRow) &&
                BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.ParentBinding.Path.Path == "Picker.CatalogSearch");
            var itemCategory = Find<ComboBox>(itemRoot).Single(combo => ReferenceEquals(combo.DataContext, itemRow) &&
                BindingOperations.GetBindingExpression(combo, Selector.SelectedItemProperty)?.ParentBinding.Path.Path == "Picker.SelectedCatalogCategory");
            var itemPicker = Find<ComboBox>(itemRoot).Single(combo => ReferenceEquals(combo.DataContext, itemRow) &&
                BindingOperations.GetBindingExpression(combo, ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "Picker.CatalogOptionsView");
            var itemCount = Find<TextBox>(itemRoot).Single(box => ReferenceEquals(box.DataContext, itemRow) &&
                BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.ParentBinding.Path.Path == "CountText");
            var itemDelete = Find<Button>(itemRoot).Single(button => ReferenceEquals(button.DataContext, itemRow) && Equals(button.Content, "Удалить"));
            var itemControls = new FrameworkElement[] { itemCategory, itemSearch, itemPicker, itemCount, itemDelete };
            var itemOrigins = itemControls.Select(control => control.TranslatePoint(new Point(), itemRoot)).ToArray();
            Check(itemOrigins.Zip(itemOrigins.Skip(1), (left, right) => left.X < right.X).All(result => result) &&
                itemOrigins.Max(point => point.Y) - itemOrigins.Min(point => point.Y) < 2 &&
                itemControls.All(control => Math.Abs(control.ActualHeight - 34) < 0.5) && itemCategory.ActualWidth >= 155,
                $"Item category, search, selection, count and delete are not one aligned compact row: " +
                $"origins={string.Join(";", itemOrigins.Select(point => $"{point.X:F1},{point.Y:F1}"))}; " +
                $"heights={string.Join(",", itemControls.Select(control => control.ActualHeight.ToString("F1")))}; categoryWidth={itemCategory.ActualWidth:F1}.");
            itemEditor.Draft.AddItemSpawnCommand.Execute(null);
            Layout(itemRoot, 930, 650);
            Check(itemEditor.Draft.ItemSpawnRows.Count == 2 &&
                itemEditor.Draft.ItemSpawnRows.All(row => Find<Border>(itemRoot)
                    .Where(border => ReferenceEquals(border.DataContext, row)).Max(border => border.ActualHeight) < 100),
                "Multiple items do not remain short rows.");
            CheckHorizontalBounds(itemRoot);
            Save(itemRoot, Path.Combine(outputDirectory, "pda-spawn-items-editor.png"));

            var more = Find<Button>(root).First(button => ReferenceEquals(button.DataContext, first) && button.ContextMenu is not null);
            var menu = more.ContextMenu!;
            LayoutMenu(menu, more);
            Check(menu.Items.OfType<MenuItem>().Any(item => ReferenceEquals(item.Command, fixture.CodeCommand) && ReferenceEquals(item.CommandParameter, first)) &&
                menu.Items.OfType<MenuItem>().Any(item => ReferenceEquals(item.Command, fixture.DeleteActiveCommand) && ReferenceEquals(item.CommandParameter, first)),
                "The detached card menu lost its command or owning action binding.");
            foreach (var lightTheme in new[] { false, true })
            {
                ThemeManager.Apply(lightTheme);
                var themedMenu = menu;
                var themedTarget = more;
                if (lightTheme)
                {
                    // This renderer never opens native popups. A detached, never-opened
                    // ContextMenu has no resource-tree invalidation on theme changes;
                    // instantiate the real card template in the target palette instead.
                    var themedWindow = new MainWindow(new MainViewModel(runtime), runtime) { DataContext = fixture };
                    var themedRoot = (FrameworkElement)themedWindow.Content;
                    Layout(themedRoot, 1328, 782);
                    themedTarget = Find<Button>(themedRoot).First(button => ReferenceEquals(button.DataContext, first) && button.ContextMenu is not null);
                    themedMenu = themedTarget.ContextMenu!;
                }
                LayoutMenu(themedMenu, themedTarget);
                CheckMenuSurface(themedMenu, expectedRows: 4, lightTheme);
                Save(themedMenu, Path.Combine(outputDirectory, lightTheme ? "pda-card-menu-light.png" : "pda-card-menu-dark.png"));
            }
            ThemeManager.Apply(false);
            var codeItem = menu.Items.OfType<MenuItem>().First();
            codeItem.IsEnabled = false;
            LayoutMenu(menu, more);
            Check(codeItem.Opacity < 1, "Disabled card menu command is not visually distinct.");
            codeItem.ClearValue(UIElement.IsEnabledProperty);

            fixture.ActiveBrowser.Columns = 1;
            Layout(root, 1028, 782);
            var panel = Find<ActionCardPanel>(root).First();
            Check(panel.Children[1].TranslatePoint(new Point(), panel).X == 0, "Active one-column layout did not preserve order.");
            var priceGroupContainer = panel.Children.Cast<FrameworkElement>()
                .Single(child => child.DataContext is ActiveActionListEntryViewModel { IsGroup: true });
            Check(Math.Abs(priceGroupContainer.ActualWidth - panel.ActualWidth) < 1,
                "An equal-price group did not span the full active-list width.");
            CheckHorizontalBounds(root);
            Save(root, Path.Combine(outputDirectory, "pda-dark-1080.png"));
            foreach (var width in new[] { 1028d, 1868d })
            {
                for (var columns = 1; columns <= 4; columns++)
                {
                    fixture.ActiveBrowser.Columns = columns;
                    Layout(root, width, 922);
                    var activeGrid = Find<ActionCardPanel>(root).Single();
                    var xPositions = activeGrid.Children.Cast<UIElement>().Take(4)
                        .Select(child => Math.Round(child.TranslatePoint(new Point(), activeGrid).X)).Distinct().Count();
                    Check(xPositions == columns, $"Active grid requested {columns} columns but rendered {xPositions} at {width}.");
                    Check(((BrowserViewOption)Find<ComboBox>(root).Single().SelectedItem).Columns == columns, "Active selector lost its density binding.");
                    Check(fixture.PresetBrowser.Columns == 2 && !fixture.PresetBrowser.AutomaticColumns,
                        "Changing the Active view changed preset layout preferences.");
                    CheckHorizontalBounds(root);
                    foreach (FrameworkElement card in activeGrid.Children) CheckHorizontalBounds(card);
                    if (columns == 4) Save(root, Path.Combine(outputDirectory, $"pda-active-four-columns-{width + 52}.png"));
                }
            }
            var activeView = Find<ComboBox>(root).Single();
            activeView.SetCurrentValue(Selector.SelectedItemProperty, ActionBrowserViewModel.ViewOptions.Single(option => option.Columns == 3));
            Check(fixture.ActiveBrowser.Columns == 3, "Active selector input did not update its own view model.");
            activeView.SetCurrentValue(Selector.SelectedItemProperty, ActionBrowserViewModel.ViewOptions.Single(option => option.Columns == 4));
            Check(fixture.ActiveBrowser.Columns == 4, "Active selector did not update the layout.");
            fixture.ActiveBrowser.AutomaticColumns = true;
            Layout(root, 1868, 922);
            Check(((BrowserViewOption)Find<ComboBox>(root).Single().SelectedItem).Columns == 0 && Find<ActionCardPanel>(root).Single().Columns == 0,
                "Active Auto mode failed to select automatic layout.");
            fixture.ActiveBrowser.AutomaticColumns = false;
            fixture.ActiveBrowser.Columns = 2;
            Layout(root, 1868, 922);
            CheckHorizontalBounds(root);
            Save(root, Path.Combine(outputDirectory, "pda-dark-1920.png"));
            fixture.Armed = false;
            ThemeManager.Apply(true);
            Layout(root, 1328, 782);
            Save(root, Path.Combine(outputDirectory, "pda-light-stopped-1380.png"));
            ThemeManager.Apply(false);
            Find<TabControl>(root).First().SelectedIndex = 2;
            Layout(root, 1328, 782);
            CheckHorizontalBounds(root);
            var presetCategoryOrigin = Find<ListBox>(root)
                .Single(list => list.Items.Count == ActionBrowserViewModel.Categories.Count && list.Items.Cast<object>().All(item => item is ActionCategory))
                .TranslatePoint(new Point(), root);
            Check(Math.Abs(activeCategoryOrigin.X - presetCategoryOrigin.X) < 0.5 && Math.Abs(activeCategoryOrigin.Y - presetCategoryOrigin.Y) < 0.5,
                $"Category row jumps between Active and Presets: active={activeCategoryOrigin}, presets={presetCategoryOrigin}.");
            Check(!Find<Button>(root).Any(button => IsEffectivelyVisible(button) &&
                    ReferenceEquals(button.Command, fixture.AddTriggerCommand) &&
                    button.CommandParameter is ActionViewModel { IsBuiltInPreset: true }) &&
                !Find<Button>(root).Any(button => IsEffectivelyVisible(button) &&
                    ReferenceEquals(button.Command, fixture.AddFromPresetCommand) &&
                    button.CommandParameter is ActionViewModel { IsBuiltInPreset: true }) &&
                Find<Button>(root).Any(button => Equals(button.Content, "В пользовательский шаблон") &&
                    IsEffectivelyVisible(button) &&
                    ReferenceEquals(button.Command, fixture.SaveAsUserPresetCommand) &&
                    button.CommandParameter is ActionViewModel { IsBuiltInPreset: true }),
                "Built-in presets must have no price editor or direct activation and must save to user presets instead.");
            Save(root, Path.Combine(outputDirectory, "pda-presets-1380.png"));
            var presetMore = Find<Button>(root).First(button => button.DataContext is ActionViewModel { IsPreset: true } && button.ContextMenu is not null);
            LayoutMenu(presetMore.ContextMenu!, presetMore);
            CheckMenuSurface(presetMore.ContextMenu!, expectedRows: 2);
            Save(presetMore.ContextMenu!, Path.Combine(outputDirectory, "pda-preset-menu-dark.png"));

            foreach (var width in new[] { 1028d, 1868d })
            {
                for (var columns = 1; columns <= 4; columns++)
                {
                    fixture.PresetBrowser.Columns = columns;
                    Layout(root, width, 922);
                    var grid = Find<ActionCardPanel>(root).Single();
                    var xPositions = grid.Children.Cast<UIElement>().Take(4).Select(child => Math.Round(child.TranslatePoint(new Point(), grid).X)).Distinct().Count();
                    Check(xPositions == columns, $"Manual {columns}-column layout at width {width} rendered {xPositions} columns.");
                    CheckHorizontalBounds(root);
                    foreach (FrameworkElement card in grid.Children) CheckHorizontalBounds(card);
                    var densityView = Find<ComboBox>(root).Single();
                    Check(((BrowserViewOption)densityView.SelectedItem).Columns == columns, "View selector lost its binding.");
                    if (columns == 4) Save(root, Path.Combine(outputDirectory, $"pda-four-columns-{width + 52}.png"));
                }
            }
            var presetViewSelector = Find<ComboBox>(root).Single();
            presetViewSelector.SetCurrentValue(Selector.SelectedItemProperty, ActionBrowserViewModel.ViewOptions.Single(option => option.Columns == 3));
            Check(fixture.PresetBrowser.Columns == 3, "Selecting a grid view did not update the view model.");
            Find<TabControl>(root).First().SelectedIndex = 1;
            Layout(root, 1328, 782);
            Check(Find<Button>(root).Any(button => IsEffectivelyVisible(button) &&
                    ReferenceEquals(button.Command, fixture.AddTriggerCommand) &&
                    button.CommandParameter is ActionViewModel { IsUserPreset: true }) &&
                Find<Button>(root).Any(button => Equals(button.Content, "В активные") &&
                    IsEffectivelyVisible(button) &&
                    ReferenceEquals(button.Command, fixture.AddFromPresetCommand) &&
                    button.CommandParameter is ActionViewModel { IsUserPreset: true }),
                "User presets must own price editing and activation.");
            Check(!Find<Button>(root).Any(button =>
                    IsEffectivelyVisible(button) && Equals(button.Content, "Удиви меня")),
                "The retired Surprise Me button is still visible.");
            Save(root, Path.Combine(outputDirectory, "pda-user-preset-pipeline.png"));
            Find<TabControl>(root).First().SelectedIndex = 2;
            Layout(root, 1868, 922);
            fixture.PresetBrowser.SelectCategory("npc_mutants");
            Layout(root, 1868, 922);
            Check(Find<ActionCardPanel>(root).Single().Children.Count == 6, "NPC/mutant filter did not show the expected templates.");
            Save(root, Path.Combine(outputDirectory, "pda-npc-category.png"));
            fixture.PresetBrowser.SelectCategory("simulation");
            Layout(root, 1868, 922);
            Check(Find<ActionCardPanel>(root).Single().Children.Count == 4, "Simulation filter did not show four squad presets.");
            Save(root, Path.Combine(outputDirectory, "pda-simulation-category.png"));
            fixture.PresetBrowser.SelectCategory("world");
            Layout(root, 1868, 922);
            Check(Find<ActionCardPanel>(root).Single().Children.Count == 9, "World category is incorrect.");
            fixture.PresetBrowser.SelectCategory("player_state");
            Layout(root, 1868, 922);
            Check(Find<ActionCardPanel>(root).Single().Children.Count == 13, "Player state category is incorrect.");
            Check(Find<TextBlock>(root).Any(text => text.Text == "Все") &&
                Find<TextBlock>(root).Any(text => text.Text == "Состояния игрока") &&
                Find<TextBlock>(root).Any(text => text.Text == "Инвентарь"),
                "The All filter or one of the specific one-line categories was not rendered.");
            fixture.PresetBrowser.SelectCategory("all");
            Layout(root, 1868, 922);
            Check(Find<ActionCardPanel>(root).Single().Children.Count == 41, "The restored All category does not show every template.");
            Save(root, Path.Combine(outputDirectory, "pda-player-categories.png"));
            fixture.ActiveBrowser.SelectCategory("inventory");
            Find<TabControl>(root).First().SelectedIndex = 0;
            Layout(root, 1328, 782);
            Check(fixture.ActiveBrowser.VisibleActions.Cast<ActionViewModel>().Count() == 3 &&
                fixture.ActiveBrowser.VisibleActions.Cast<ActionViewModel>().All(action => ActionBrowserViewModel.CategoryFor(action.HandlerId) == "inventory"),
                "Active inventory category is not filtering independently.");
            Find<TabControl>(root).First().SelectedIndex = 3;
            Layout(root, 1868, 922);
            Check(Find<TextBlock>(root).Any(text => text.Text == "!") && Find<TextBlock>(root).Any(text => text.Text == "✓"),
                "History status markers were not rendered.");
            Save(root, Path.Combine(outputDirectory, "pda-history-colors.png"));
            ThemeManager.Apply(true);
            Layout(root, 1868, 922);
            Save(root, Path.Combine(outputDirectory, "pda-history-colors-light.png"));
            ThemeManager.Apply(false);

            CheckPriceEditor(outputDirectory);

            var settingsWindow = new SettingsWindow(new SettingsViewModel(runtime));
            var settingsRoot = (FrameworkElement)settingsWindow.Content;
            var settingsTabControl = Find<TabControl>(settingsRoot).First();
            var topSettingsTabs = settingsTabControl.Items.Cast<TabItem>().ToList();
            settingsTabControl.SelectedIndex = 0;
            Layout(settingsRoot, 940, 650);
            Check(topSettingsTabs.Select(tab => Math.Round(tab.TranslatePoint(new Point(), settingsRoot).Y, 1)).Distinct().Count() == 1,
                "The default settings width still wraps the final top-level tab onto a second row.");
            CheckHorizontalBounds(settingsRoot);
            Save(settingsRoot, Path.Combine(outputDirectory, "pda-unified-install-settings.png"));
            settingsTabControl.SelectedIndex = 1;
            Layout(settingsRoot, 940, 650);
            CheckHorizontalBounds(settingsRoot);
            Check(Find<TextBlock>(settingsRoot).Any(text => text.Text == "Создайте собственное OAuth-приложение"),
                "The DonationAlerts integration settings tab is incomplete.");
            Save(settingsRoot, Path.Combine(outputDirectory, "pda-integration-settings.png"));
            var integrationTabControl = Find<TabControl>(settingsRoot)
                .Single(control => !ReferenceEquals(control, settingsTabControl));
            Check(integrationTabControl.Items.Cast<TabItem>().Select(tab => tab.Header as string)
                    .SequenceEqual(["DonationAlerts", "DonateX"]),
                "The integration tabs are missing DonationAlerts or DonateX.");
            integrationTabControl.SelectedIndex = 1;
            Layout(settingsRoot, 940, 650);
            CheckHorizontalBounds(settingsRoot);
            Check(Find<TextBlock>(settingsRoot).Any(text => text.Text == "Подключение DonateX") &&
                    Find<PasswordBox>(settingsRoot).Any(box => box.Name == "DonateXTokenBox") &&
                    Find<Button>(settingsRoot).Any(button => Equals(button.Content, "Подключить")),
                "The DonateX connection settings are incomplete.");
            Save(settingsRoot, Path.Combine(outputDirectory, "pda-donatx-settings.png"));
            settingsTabControl.SelectedIndex = 2;
            Layout(settingsRoot, 940, 650);
            CheckHorizontalBounds(settingsRoot);
            Check(Find<TextBlock>(settingsRoot).Any(text => text.Text == "Исключения из игрового каталога"),
                "The exclusions settings tab is incomplete.");
            Save(settingsRoot, Path.Combine(outputDirectory, "pda-exclusions-settings.png"));
            settingsTabControl.SelectedIndex = 4;
            Layout(settingsRoot, 940, 650);
            var settingsTabs = topSettingsTabs;
            Check(settingsTabs.Select(tab => tab.Header as string).SequenceEqual(
                    ["Игра и редактор", "Интеграция", "Исключения", "Очередь", "Общие настройки", "Горячие клавиши", "О программе"]) &&
                Find<TextBlock>(settingsRoot).Any(text => text.Text == "Скрывать недоступные секции") &&
                Find<TextBlock>(settingsRoot).Any(text => text.Text == "Имя анонимного донатчика") &&
                Find<TextBlock>(settingsRoot).Any(text => text.Text == "Работа в системном трее") &&
                Find<TextBlock>(settingsRoot).Any(text => text.Text == "Правила и действия") &&
                Find<TextBlock>(settingsRoot).Any(text => text.Text == "Данные программы"),
                "Settings tab order is wrong or general settings are incomplete.");
            CheckHorizontalBounds(settingsRoot);
            Save(settingsRoot, Path.Combine(outputDirectory, "pda-general-settings.png"));
            settingsTabControl.SelectedIndex = 5;
            Layout(settingsRoot, 940, 650);
            Check(Find<TextBlock>(settingsRoot).Any(text => text.Text == "Глобальные сочетания Windows") &&
                    Find<TextBlock>(settingsRoot).Any(text => text.Text == "Повторить последний донат"),
                "The hotkey settings tab is incomplete.");
            Save(settingsRoot, Path.Combine(outputDirectory, "pda-hotkey-settings.png"));
            settingsTabControl.SelectedIndex = 6;
            Layout(settingsRoot, 940, 650);
            Check(Find<TextBlock>(settingsRoot).Any(text => text.Text == "Поддержать автора") &&
                    Find<TextBlock>(settingsRoot).Any(text => text.Text == "2200 3961 2613 9183") &&
                    Find<Button>(settingsRoot).Any(button => Equals(button.Content, "Проверить обновление")),
                "The About tab lost its update check or T-Bank support section.");
            Save(settingsRoot, Path.Combine(outputDirectory, "pda-about-settings.png"));
            var errors = trace.ToString();
            File.WriteAllText(Path.Combine(outputDirectory, "binding-errors.txt"), errors);
            Check(errors.Length == 0, "WPF binding errors: " + errors);
            Console.WriteLine("PASS PDA independent Active/Preset sliders (1–4 columns), category filters, 7-price wrapping, transactional popup, themed card menus without system gutter, contextual commands and history colors in both themes.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CheckBurerEditor(MainWindow window, string outputDirectory)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "effects", "gamedata", "scripts",
            "anomaly_donation_bridge_effect_hardcoon_adb_burer_form.script");
        var json = File.ReadAllText(source).Split("ADB_PRESET_V1", 2)[1].Split("ADB_PRESET_END", 2)[0];
        var definition = System.Text.Json.JsonSerializer.Deserialize<ActionDefinition>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            })!;
        var editor = new ActionEditorViewModel(new ActionViewModel(definition), _ => Task.CompletedTask);
        var editorWindow = new ActionEditorWindow(editor, (DataTemplate)window.Resources["ActionEditorTemplate"]);
        var root = (FrameworkElement)editorWindow.Content;
        foreach (var width in new[] { 760d, 930d })
        {
            Layout(root, width, 720);
            var bindings = Find<TextBox>(root).Where(box => box.IsReadOnly &&
                box.DataContext is ParameterValueViewModel { AllowsMouseKey: true }).ToArray();
            var expectedKeys = definition.ParameterDefinitions.Count(parameter => parameter.Kind == ParameterKind.GameInputKey);
            Check(expectedKeys >= 4 && bindings.Length == expectedKeys,
                "Burer editor lost an ability/camera key field.");
            var positions = bindings.Select(box => box.TranslatePoint(new Point(), root)).ToArray();
            for (var index = 0; index < positions.Length; index++)
                Check(Math.Abs(positions[index].X - positions[index % 2].X) < 1 &&
                    (index % 2 == 0 || Math.Abs(positions[index].Y - positions[index - 1].Y) < 1) &&
                    (index < 2 || positions[index].Y > positions[index - 2].Y),
                    "Burer ability/camera fields must wrap into two columns at width " + width);
            CheckHorizontalBounds(root);
        }
        var scroll = Find<ScrollViewer>(root).First();
        scroll.ScrollToBottom();
        Layout(root, 930, 720);
        Save(root, Path.Combine(outputDirectory, "pda-burer-editor-two-rows.png"));
    }

    private static void LayoutMenu(ContextMenu menu, Button target)
    {
        menu.PlacementTarget = target;
        BindingOperations.GetBindingExpression(menu, FrameworkElement.DataContextProperty)?.UpdateTarget();
        menu.ApplyTemplate();
        menu.Measure(new Size(320, double.PositiveInfinity));
        menu.Arrange(new Rect(new Point(), menu.DesiredSize));
        menu.UpdateLayout();
        menu.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        foreach (var item in menu.Items.OfType<MenuItem>())
            BindingOperations.GetBindingExpression(item, MenuItem.CommandProperty)?.UpdateTarget();
    }

    private static void CheckMenuSurface(ContextMenu menu, int expectedRows, bool lightTheme = false)
    {
        var renderedSurface = menu.Template.FindName("MenuSurface", menu) as Border;
        var expectedSurface = (Color)ColorConverter.ConvertFromString(lightTheme ? "#FAFAF6" : "#222523");
        Check(menu.Template.FindName("MenuSurface", menu) is Border surface &&
            surface.Background is SolidColorBrush brush && brush.Color == expectedSurface,
            $"Context menu surface mismatch: style={menu.Style is not null}; surface={renderedSurface is not null}; menu={menu.Background}; rendered={renderedSurface?.Background}; expected={expectedSurface}.");
        var rows = menu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible).ToArray();
        Check(rows.Length == expectedRows && rows.All(item => item.ActualHeight >= 36),
            $"Menu row count or hit target height changed: expected={expectedRows}, actual={rows.Length}, heights={string.Join(",", rows.Select(item => item.ActualHeight))}.");
        foreach (var row in rows)
        {
            var border = (Border)row.Template.FindName("MenuRow", row);
            var header = (FrameworkElement)border.Child;
            var origin = header.TranslatePoint(new Point(), menu);
            Check(origin.X is >= 12 and <= 20, "A system icon gutter or inconsistent text inset remains.");
            Check(row.ActualWidth >= menu.ActualWidth - 12, "Menu items no longer fill the menu surface.");
            var foreground = (SolidColorBrush)row.Foreground;
            var expected = (SolidColorBrush)Application.Current.Resources[
                Equals(row.Header, "Удалить из активных") ? "BadBrush" : "TextBrush"];
            Check(foreground.Color == expected.Color, "Menu command text lost its normal/danger theme color.");
        }
    }

    private static void Click(Button button)
    {
        Check(button.Command is not null && button.Command.CanExecute(button.CommandParameter), "Command is unavailable.");
        button.Command!.Execute(button.CommandParameter);
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Margin = new Thickness(0);
        root.Width = width;
        root.Height = height;
        for (var pass = 0; pass < 3; pass++)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }

    private static void CheckHorizontalBounds(FrameworkElement root)
    {
        foreach (var button in Find<Button>(root).Where(button => button.ActualWidth > 0 && button.Visibility == Visibility.Visible))
        {
            var point = button.TranslatePoint(new Point(), root);
            Check(point.X >= -1 && point.X + button.ActualWidth <= root.ActualWidth + 1,
                $"Button '{button.Content}' overflows horizontally: {point.X} + {button.ActualWidth} / {root.ActualWidth}.");
        }
    }

    private static void Save(FrameworkElement root, string path)
    {
        var width = (int)Math.Ceiling(root.ActualWidth) + 52;
        var height = (int)Math.Ceiling(root.ActualHeight) + 36;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var background = Application.Current?.Resources["BackgroundBrush"] as Brush ?? Brushes.Black;
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(26, 18, root.ActualWidth, root.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool IsEffectivelyVisible(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    public sealed class Fixture : ObservableObject
    {
        private bool _armed = true;
        private bool _showBridgeUpdate;
        public bool ShowBridgeUpdate { get => _showBridgeUpdate; set => SetProperty(ref _showBridgeUpdate, value); }
        public string BridgeUpdateButtonText => "Обновить эффекты и аддон";
        public string BridgeUpdateHint => "Доступны обновления локального аддона и эффектов";
        public ICommand InstallBridgeCommand { get; } = new RelayCommand(() => { });
        public bool ShowTemplateDescriptions { get; set; } = true;
        public Fixture()
        {
            var entries = Enum.GetValues<CatalogEntryKind>().SelectMany(kind => Enumerable.Range(1, 12).Select(index => new CatalogEntry
            {
                SectionId = "qa_" + kind.ToString().ToLowerInvariant() + "_" + index,
                Kind = kind,
                DisplayName = kind == CatalogEntryKind.StalkerSquad ? new[] { "Сталкеры", "Долг", "Свобода", "Бандиты" }[(index - 1) % 4] + " · отряд " + index : "Вариант " + index,
                Category = "Тестовый каталог",
                Faction = "stalker",
                Strength = CatalogStrength.Normal,
                IsAvailable = true
            }));
            var catalog = new CatalogSnapshot(new CatalogRevision("pda-preview-1"), new string('a', 64), entries);
            var state = new CatalogRuntimeState(CapabilitySet.Empty, catalog, true, "Живой каталог");
            var presets = DefaultActionCatalog.CreatePresets();
            foreach (var (handler, count) in new[]
            {
                ("spawn_random_hostile_squad", 7), ("spawn_random_mutant_pack", 3), ("spawn_npcs", 1), ("spawn_mutants", 1),
                ("change_money", 1), ("spawn_items", 4), ("spawn_anomaly", 2), ("use_random_consumable", 2)
            })
            {
                var action = new ActionViewModel(presets.Single(preset => preset.HandlerId == handler).Clone(), catalogState: state, displayOrdinal: ActiveActions.Count + 1);
                foreach (var parameter in action.Parameters.Where(parameter => parameter.IsDynamicCatalogChoice))
                    parameter.Value = parameter.IsDynamicCatalogMultiChoice
                        ? string.Join(",", parameter.CatalogOptions.Select(option => option.SectionId))
                        : parameter.CatalogOptions.First().SectionId;
                for (var index = 0; index < count; index++) action.AddTrigger(new TriggerRule
                {
                    Amount = index < 3 ? 99 + index * 50 : index - 2,
                    Currency = index < 3 ? "RUB" : "USD",
                    Comparator = index == 6 ? TriggerComparator.GreaterOrEqual : TriggerComparator.Exact
                });
                ActiveActions.Add(action);
            }
            foreach (var source in presets.Where(preset => preset.HandlerId is "change_health" or "get_drunk"))
            {
                var grouped = source.CloneAsNewInstance();
                grouped.Triggers.Clear();
                grouped.Triggers.Add(new TriggerRule { Amount = 987654.32m, Currency = "XAF" });
                ActiveActions.Add(new ActionViewModel(grouped, catalogState: state, displayOrdinal: ActiveActions.Count + 1));
            }
            foreach (var preset in presets) Presets.Add(new ActionViewModel(preset, isPreset: true, catalogState: state));
            var userPreset = presets.Single(preset => preset.HandlerId == "change_money").CloneAsNewInstance();
            userPreset.DisplayName = "Пользовательские деньги";
            userPreset.HasCustomDisplayName = true;
            userPreset.Triggers.Clear();
            userPreset.Triggers.Add(new TriggerRule { Amount = 100, Currency = "RUB" });
            UserPresets.Add(new ActionViewModel(userPreset, isPreset: true, isUserPreset: true, catalogState: state));
            ActiveBrowser = new ActionBrowserViewModel(ActiveActions, groupEqualPrices: true);
            UserPresetBrowser = new ActionBrowserViewModel(UserPresets);
            PresetBrowser = new ActionBrowserViewModel(Presets);
            var historyTime = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
            foreach (var status in Enum.GetValues<DispatchState>())
                History.Add(new EventHistoryEntry { DonationCreatedAt = historyTime, Donor = "Локальный тест", Amount = 100, Currency = "RUB", ActionName = "Случайный сквад NPC", State = status, Detail = "Пример статуса: " + FriendlyValueConverter.Display(status) });
            EditTriggerCommand = new RelayCommand(parameter => { var trigger = (TriggerRuleViewModel)parameter!; trigger.Model.Amount = 177; trigger.Refresh(); });
            DeleteTriggerCommand = new RelayCommand(parameter => { var trigger = (TriggerRuleViewModel)parameter!; trigger.Owner.RemoveTrigger(trigger); });
            AddTriggerCommand = new RelayCommand(parameter => ((ActionViewModel)parameter!).AddTrigger(new TriggerRule { Amount = 2, Currency = "EUR" }));
            SaveActionCommand = new RelayCommand(parameter =>
            {
                var action = (ActionViewModel)parameter!;
                Check(action.TryCommitPendingEdits(out var error), error);
                Saved = action.Model.Clone();
            });
        }
        public ActionBrowserViewModel ActiveBrowser { get; }
        public ActionBrowserViewModel UserPresetBrowser { get; }
        public ActionBrowserViewModel PresetBrowser { get; }
        public ActionDefinition? Saved { get; private set; }
        public ObservableCollection<ActionViewModel> ActiveActions { get; } = [];
        public ObservableCollection<ActionViewModel> UserPresets { get; } = [];
        public ObservableCollection<ActionViewModel> Presets { get; } = [];
        public ObservableCollection<EventHistoryEntry> History { get; } = [];
        public EventHistoryEntry? SelectedHistory { get; set; }
        public bool Armed { get => _armed; set { SetProperty(ref _armed, value); RaisePropertyChanged(nameof(ProcessingStatus)); } }
        public bool CanArm => true;
        public bool IsLightTheme { get; set; }
        public bool CanRefreshCatalog => true;
        public bool IsCatalogSynchronizing => false;
        public bool IsCatalogProgressIndeterminate => false;
        public double CatalogProgressPercent => 100;
        public string CatalogProgressText => "Каталог загружен";
        public string DonationStatus => "DonationAlerts: streamer · DonateX: streamer";
        public string GameStatus => "Готова · addon 1.0.22";
        public string CatalogStatus => "Anomaly 1.5.3 · каталог готов · 3996 секций";
        public string ExtensionStatus => "Пользовательские расширения не установлены";
        public string ArmHint => "Обработка событий";
        public string ProcessingStatus => (Armed ? "Эффекты донатов включены" : "Эффекты донатов отключены") + "\nГолоса YouTube обрабатываются";
        public string Notice => "Изолированная проверка интерфейса · игровые команды не отправляются";
        public string DataDirectory => "Тестовые данные";
        public string RefreshEventsButtonText => "Обновить события";
        public string QueueStatusText => "Следующий эффект через 00:04 · в очереди: 2";
        public ICommand EditTriggerCommand { get; }
        public ICommand DeleteTriggerCommand { get; }
        public ICommand AddTriggerCommand { get; }
        public ICommand SaveActionCommand { get; }
        public ICommand CodeCommand { get; } = new RelayCommand(() => { });
        public ICommand DeleteActiveCommand { get; } = new RelayCommand(() => { });
        public ICommand TestCommand { get; } = new RelayCommand(() => { });
        public ICommand TestPriceGroupCommand { get; } = new RelayCommand(() => { });
        public ICommand AddPriceGroupTriggerCommand { get; } = new RelayCommand(() => { });
        public ICommand RenamePriceGroupCommand { get; } = new RelayCommand(() => { });
        public ICommand AddFromPresetCommand { get; } = new RelayCommand(() => { });
        public ICommand SaveAsUserPresetCommand { get; } = new RelayCommand(() => { });
        public ICommand ExportActivePresetsCommand { get; } = new RelayCommand(() => { });
        public ICommand SortActiveByPriceCommand { get; } = new RelayCommand(() => { });
        public ICommand RefreshCatalogCommand { get; } = new RelayCommand(() => { });
        public ICommand RefreshEventsCommand { get; } = new RelayCommand(() => { });
        public ICommand ClearQueueCommand { get; } = new RelayCommand(() => { });
        public ICommand RetryCommand { get; } = new RelayCommand(() => { });
        public ICommand RemoveQueuedCommand { get; } = new RelayCommand(_ => { }, parameter => parameter is EventHistoryEntry { State: DispatchState.Queued });
    }
}
