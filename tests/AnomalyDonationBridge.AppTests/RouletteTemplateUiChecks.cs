using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

// Render the actual editor template without IPC, providers or real settings.
internal static class RouletteTemplateUiChecks
{
    public static int Run(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var game = Path.Combine(outputDirectory, "isolated-game");
            var gameScripts = Path.Combine(game, "gamedata", "scripts");
            Directory.CreateDirectory(gameScripts);
            var effects = Path.Combine(AppContext.BaseDirectory, "effects");
            foreach (var file in new[] { "anomaly_donation_bridge_effect_hardcoon_adb_spin_roulette.script", "anomaly_donation_bridge_effect_damage_equipment.script", "anomaly_donation_bridge_effect_hardcoon_adb_helicopter_companion.script", "anomaly_donation_bridge_effect_hardcoon_adb_equipment_ammo.script", "anomaly_donation_bridge_effect_spawn_named_companion.script", "anomaly_donation_bridge_effect_spawn_mutants.script", "anomaly_donation_bridge_effect_spawn_npcs.script", "anomaly_donation_bridge_effect_spawn_random_mutants.script", "anomaly_donation_bridge_effect_spawn_random_npcs.script" })
                File.Copy(Path.Combine(effects, "gamedata", "scripts", file), Path.Combine(gameScripts, file), true);
            var presets = new FilePresetLoader(effects).Load(game).Presets;
            File.Copy(Path.Combine(effects, "gamedata", "scripts", "anomaly_donation_bridge_effect_hardcoon_adb_special_companion.script"),
                Path.Combine(gameScripts, "anomaly_donation_bridge_effect_hardcoon_adb_special_companion.script"), true);
            presets = new FilePresetLoader(effects).Load(game).Presets;
            var specialEntries = new[]
            {
                new CatalogEntry { SectionId = "special_hip", Kind = CatalogEntryKind.NpcVisual, DisplayName = @"Хип — actors\mnp_npc_remeik\girl.ogf", Category = "Особые персонажи", IsAvailable = true },
                new CatalogEntry { SectionId = "special_barman", Kind = CatalogEntryKind.NpcVisual, DisplayName = @"Бармен — actors\barman\barman.ogf", Category = "Особые персонажи", IsAvailable = true },
                new CatalogEntry { SectionId = "special_sidor", Kind = CatalogEntryKind.NpcVisual, DisplayName = @"Сидорович — actors\stalker_trader\stalker_trader_1.ogf", Category = "Особые персонажи", IsAvailable = true }
            };
            var specialCatalog = new CatalogSnapshot(new CatalogRevision("special-ui"), CatalogContentHash.Compute(specialEntries), specialEntries);
            DynamicCatalogBinding.Apply(specialCatalog, [presets.Single(p => p.HandlerId == "hardcoon.adb.special_companion")]);
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(outputDirectory, "isolated-data")));
            var profile = new DonationProfile { Name = "Изолированная проверка" };
            runtime.Settings.Profiles.Add(profile); runtime.Settings.ActiveProfileId = profile.Id;
            var main = new MainWindow(new MainViewModel(runtime), runtime);
            var template = (DataTemplate)main.FindResource("ActionEditorTemplate");
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                foreach (var id in new[] { "hardcoon.adb.special_companion", "hardcoon.adb.spin_roulette", "damage_equipment", "hardcoon.adb.helicopter_companion", "hardcoon.adb.equipment_ammo", "spawn_named_companion", "spawn_mutants", "spawn_npcs", "spawn_random_mutants", "spawn_random_npcs" })
                {
                    var model = id == "hardcoon.adb.special_companion"
                        ? new ActionViewModel(presets.Single(p => p.HandlerId == id), isPreset: true,
                            catalogState: new CatalogRuntimeState(CapabilitySet.Empty, specialCatalog, false, "Каталог игры"))
                        : new ActionViewModel(presets.Single(p => p.HandlerId == id));
                    var draft = new ActionEditorViewModel(model, _ => Task.CompletedTask);
                    var editor = new ActionEditorWindow(draft, template);
                    var root = (FrameworkElement)editor.Content;
                    Layout(root);
                    if (id is "spawn_mutants" or "spawn_npcs" or "spawn_random_mutants" or "spawn_random_npcs")
                    {
                        var method = draft.Draft.Parameters.Single(p => p.Definition.Key == "spawn_point_method");
                        var picker = Find<ComboBox>(root).Single(control => control.DataContext == method && control.SelectedValuePath == "Value" &&
                            BindingOperations.GetBindingExpression(control, ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "OptionEntries");
                        var hint = Find<TextBlock>(root).Single(control => control.DataContext == method &&
                            BindingOperations.GetBindingExpression(control, TextBlock.TextProperty)?.ParentBinding.Path.Path == "SpawnPointMethodHint");
                        Check(method.Value == "boss_sample" && hint.Visibility == Visibility.Visible && hint.Text.Contains("не ближе 10 м"),
                            "Direct spawn editor lost the boss default or its visible explanation.");
                        var hints = new HashSet<string>();
                        foreach (var option in method.OptionEntries)
                        {
                            picker.SetCurrentValue(Selector.SelectedValueProperty, option.Value); Layout(root);
                            Check(method.Value == option.Value && !string.IsNullOrWhiteSpace(hint.Text) && hint.Text == method.SpawnPointMethodHint,
                                "Spawn picker does not update its value and live explanation.");
                            hints.Add(hint.Text);
                        }
                        Check(hints.Count == 5, "Spawn methods need five distinct explanations.");
                        picker.SetCurrentValue(Selector.SelectedValueProperty, "boss_sample");
                        var radius = draft.Draft.Parameters.Single(p => p.Definition.Key == "radius_meters");
                        radius.Value = "500"; Layout(root);
                        Check(radius.Definition.Validate(radius.Value) is null, "Actual spawn editor rejects 500 m.");
                        Save(root, Path.Combine(outputDirectory, $"{id}-{(light ? "light" : "dark")}.png"));
                        editor.Close();
                        continue;
                    }
                    if (id is "spawn_named_companion" or "hardcoon.adb.special_companion")
                    {
                        var damage = draft.Draft.Parameters.Single(p => p.Definition.Key == "incoming_damage_percent");
                        Check(damage.Value == "100", "Named companion must default to ordinary damage in the real editor.");
                        damage.Value = "10"; Layout(root);
                        Check(damage.Definition.Validate(damage.Value) is null, "NPC damage percent rejected by the real editor.");
                        Save(root, Path.Combine(outputDirectory, $"{id}-{(light ? "light" : "dark")}.png"));
                        editor.Close();
                        continue;
                    }
                    if (id == "hardcoon.adb.equipment_ammo")
                    {
                        foreach (var (switchKey, initial) in new[] { ("primary", true), ("secondary", true), ("hands_only", false) })
                        {
                            var toggle = Find<CheckBox>(root).Single(control =>
                                control.DataContext is ParameterValueViewModel p && p.Definition.Key == switchKey);
                            Check(toggle.IsChecked == initial, "Equipment ammo checkbox default changed: " + switchKey);
                            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, !initial); Layout(root);
                            Check(draft.Draft.Parameters.Single(p => p.Definition.Key == switchKey).Value ==
                                (!initial ? "true" : "false"), "Equipment ammo checkbox did not update the actual draft.");
                            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, initial); Layout(root);
                        }
                        var count = draft.Draft.Parameters.Single(p => p.Definition.Key == "ammo_packs");
                        count.Value = "50"; Layout(root);
                        Check(count.Definition.Validate(count.Value) is null, "Equipment ammo pack count rejected by actual editor.");
                        Save(root, Path.Combine(outputDirectory, $"equipment-ammo-{(light ? "light" : "dark")}.png"));
                        editor.Close();
                        continue;
                    }
                    if (id == "hardcoon.adb.helicopter_companion")
                    {
                        foreach (var (switchKey, initial) in new[] { ("use_rockets", false), ("engine_sound", true) })
                        {
                            var toggle = Find<CheckBox>(root).Single(control =>
                                control.DataContext is ParameterValueViewModel p && p.Definition.Key == switchKey);
                            Check(toggle.IsChecked == initial, "Support helicopter checkbox default changed: " + switchKey);
                            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, !initial); Layout(root);
                            Check(draft.Draft.Parameters.Single(p => p.Definition.Key == switchKey).Value ==
                                (!initial ? "true" : "false"), "Support checkbox did not update the actual editor draft.");
                            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, initial); Layout(root);
                        }
                        var duration = draft.Draft.Parameters.Single(p => p.Definition.Key == "duration_seconds");
                        duration.Value = "99999999"; Layout(root);
                        Check(duration.Definition.Validate(duration.Value) is null, "Large duration is rejected by the actual editor.");
                        Save(root, Path.Combine(outputDirectory, $"helicopter-template-{(light ? "light" : "dark")}.png"));
                        editor.Close();
                        continue;
                    }
                    var key = id == "damage_equipment" ? "target" : "risk_mode";
                    var combo = Find<ComboBox>(root).Single(control =>
                        BindingOperations.GetBindingExpression(control, ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "OptionEntries" &&
                        control.DataContext is ParameterValueViewModel p && p.Definition.Key == key);
                    Check(combo.ItemsSource is not null && combo.SelectedValuePath == "Value" &&
                        BindingOperations.IsDataBound(combo, Selector.SelectedValueProperty), "Real flat-choice editor lost value binding.");
                    var expected = id == "damage_equipment" ? new[] { "броню", "шлем", "броню и шлем" } : new[] { "Low и High", "Low", "High" };
                    var options = combo.Items.Cast<ItemOptionViewModel>().ToArray();
                    Check(options.Select(option => option.Label).SequenceEqual(expected), "Real dropdown captions ignored per-template labels or regressed equipment fallback.");
                    foreach (var option in options)
                    {
                        var row = (TextBlock)combo.ItemTemplate.LoadContent(); row.DataContext = option;
                        row.Measure(new Size(300, 40)); row.Arrange(new Rect(0, 0, 300, 40));
                        row.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        Check(row.Text == option.Label, "Dropdown item template rendered the wrong caption.");
                        combo.SetCurrentValue(Selector.SelectedValueProperty, option.Value); Layout(root);
                        Check(draft.Draft.Parameters.Single(p => p.Definition.Key == key).Value == option.Value,
                            "Changing a labelled choice did not preserve its protocol value.");
                    }
                    if (id != "damage_equipment")
                    {
                        var pause = Find<CheckBox>(root).Single(control =>
                            control.DataContext is ParameterValueViewModel p && p.Definition.Key == "pause_game");
                        var pauseHint = Find<TextBlock>(root).Single(control =>
                            control.DataContext is ParameterValueViewModel p && p.Definition.Key == "pause_game" &&
                            control.Text.StartsWith("При включённой паузе курсор неподвижен.", StringComparison.Ordinal));
                        Check(pause.IsChecked == false, "Roulette pause must default to off in the real editor.");
                        Check(pauseHint.Visibility == Visibility.Collapsed, "Pause hint must stay hidden while the option is off.");
                        pause.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Layout(root);
                        Check(pauseHint.Visibility == Visibility.Visible && pauseHint.Text.Contains("Пробел или назначенная клавиша прыжка"),
                            "Enabling pause must reveal accurate keyboard controls under the checkbox.");
                        Check(draft.Draft.Parameters.Single(p => p.Definition.Key == "pause_game").Value == "true",
                            "Roulette pause checkbox did not update the editor draft.");
                        combo.SetCurrentValue(Selector.SelectedValueProperty, "both"); Layout(root);
                        var description = Find<TemplateDescriptionTextBlock>(root).Single();
                        Check(!description.Description.Contains("Установка:") && description.Inlines.OfType<Run>().All(run =>
                            run.ReadLocalValue(TextElement.ForegroundProperty) == DependencyProperty.UnsetValue && run.FontWeight == FontWeights.Normal),
                            "Roulette editor still has an accented installation note.");
                        Save(root, Path.Combine(outputDirectory, $"roulette-template-{(light ? "light" : "dark")}.png"));
                    }
                    editor.Close();
                }
            }
            main.Close();
            Console.WriteLine("PASS real editors: roulette labels/equipment fallback; support helicopter checkboxes/defaults/bindings and 99,999,999s input; both themes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(932, 680)); root.Arrange(new Rect(0, 0, 932, 680)); root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Save(FrameworkElement root, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
