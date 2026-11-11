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
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class WanderingTraderTests
{
    private static ActionDefinition Load(string game)
    {
        var effects = Path.Combine(AppContext.BaseDirectory, "effects");
        var target = Path.Combine(game, "gamedata", "scripts"); Directory.CreateDirectory(target);
        const string file = "anomaly_donation_bridge_effect_hardcoon_adb_wandering_trader.script";
        File.Copy(Path.Combine(effects, "gamedata", "scripts", file), Path.Combine(target, file), true);
        return new FilePresetLoader(effects).Load(game).Presets.Single(p => p.HandlerId == "hardcoon.adb.wandering_trader");
    }

    private static CatalogSnapshot Catalog()
    {
        CatalogEntry[] entries = [
            new() { SectionId = "sim_default_stalker_1", Kind = CatalogEntryKind.Npc, DisplayName = "Одиночка", Category = "stalker", IsAvailable = true },
            new() { SectionId = "sim_default_killer_1", Kind = CatalogEntryKind.Npc, DisplayName = "Наёмник", Category = "killer", IsAvailable = true },
            new() { SectionId = "sim_story_npc", Kind = CatalogEntryKind.Npc, DisplayName = "Недоступный NPC", IsAvailable = false, QuarantineReason = QuarantineReason.Blacklisted },
            new() { SectionId = "adb_visual_old", Kind = CatalogEntryKind.NpcVisual, DisplayName = "OGF из старого кэша", IsAvailable = true }
        ];
        return new(new CatalogRevision("trader-visuals"), CatalogContentHash.Compute(entries), entries);
    }

    public static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var preset = Load(Path.Combine(temporary.Path, "game")); var catalog = Catalog();
        DynamicCatalogBinding.Apply(catalog, [preset]);
        Check(preset.Parameters["visuals"] is "sim_default_stalker_1" or "sim_default_killer_1", "Initial model must select one safe NPC, not the entire catalogue.");
        var selection = preset.ParameterDefinitions.Single(p => p.Key == "visuals");
        Check(selection.CatalogKind == CatalogEntryKind.Npc && selection.Options.Count == 2 && !selection.Options.Contains("adb_visual_old"), "Trader must reuse the safe companion/boss catalogue, including with an old cache.");
        preset.Parameters["visuals"] = "sim_default_stalker_1,sim_default_killer_1";
        var source = new ActionViewModel(preset);
        var editor = new ActionEditorViewModel(source, draft => { source.ApplyEditorDraft(draft); return Task.CompletedTask; });
        Check(editor.Draft.TraderStockRows.Count == 8 && editor.Draft.TraderStockRows.All(r => r.Enabled.BooleanValue), "Eight categories must be enabled initially.");
        Check(editor.Draft.RegularParameters.Count() == 5, "Stock parameters are duplicated outside their controls.");
        Check(preset.Parameters["duration_seconds"] == "180" && preset.Parameters["spawn_radius"] == "150" && preset.Parameters["discount_percent"] == "80", "Trader defaults differ from approved settings.");
        var ammo = editor.Draft.TraderStockRows[1]; ammo.Count = 50; ammo.Enabled.BooleanValue = false;
        editor.Draft.TraderStockRows[0].Count = 20;
        Check(await editor.SaveAsync(), "Trader settings did not save: " + editor.Error);
        var saved = JsonSerializer.Deserialize<ActionDefinition>(JsonSerializer.Serialize(source.Model, JsonDefaults.Options), JsonDefaults.Options)!;
        DynamicCatalogBinding.Apply(catalog, [saved]);
        Check(saved.Parameters["ammo_count"] == "50" && saved.Parameters["ammo_enabled"] == "false" && saved.Parameters["weapons_count"] == "20" && saved.Parameters["visuals"] == "sim_default_stalker_1,sim_default_killer_1", "Category/count/model choices did not survive reload.");
        foreach (var key in new[] { "weapons", "ammo", "helmets", "armor", "artifacts", "medicine", "food", "devices" }) saved.Parameters[key + "_enabled"] = "false";
        Check(saved.Validate().Any(e => e.Contains("Ассортимент")), "An empty store was accepted.");
        saved.Parameters["food_enabled"] = "true"; saved.Parameters["duration_seconds"] = "0"; saved.Parameters["spawn_radius"] = "0";
        Check(saved.Validate().Count == 0, "Unlimited lifetime or nearby spawn rejected.");
        saved.Parameters["food_count"] = "31";
        Check(saved.Validate().Count > 0, "Category quantity limit bypassed.");
        var visual = preset.ParameterDefinitions.Single(p => p.Key == "visuals");
        Check(visual.Validate("sim_story_npc") is not null && visual.Validate("adb_visual_old") is not null && visual.Validate("") is not null, "Invalid/empty NPC selection accepted.");
    }

    public static int Render(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var preset = Load(Path.Combine(output, "isolated-game"));
            var catalog = Catalog(); DynamicCatalogBinding.Apply(catalog, [preset]);
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(output, "isolated-data")));
            var main = new MainWindow(new MainViewModel(runtime), runtime);
            var template = (DataTemplate)main.FindResource("ActionEditorTemplate");
            foreach (var width in new[] { 760d, 980d })
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                var source = new ActionViewModel(preset.Clone(), isPreset: true, catalogState: new CatalogRuntimeState(CapabilitySet.Empty, catalog, false, "Каталог игры"));
                var model = new ActionEditorViewModel(source, _ => Task.CompletedTask);
                var editor = new ActionEditorWindow(model, template);
                var root = (Grid)editor.Content; root.Margin = new Thickness(20);
                root.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
                void Layout() { root.Measure(new Size(width, 740)); root.Arrange(new Rect(0, 0, width, 740)); root.UpdateLayout(); }
                Layout();
                var sliders = Find<Slider>(root).ToArray();
                Check(sliders.Length == 8, "Actual trader editor does not show eight sliders.");
                var rows = model.Draft.TraderStockRows;
                sliders[1].Value = 42;
                Check(rows[1].Count == 42, "Actual slider did not update quantity.");
                rows[1].Enabled.BooleanValue = false; Layout();
                Check(!sliders[1].IsEnabled, "Disabled category slider is still active.");
                rows[1].Enabled.BooleanValue = true; sliders[1].Value = 10;
                var scroll = Find<ScrollViewer>(root).First();
                foreach (var bottom in new[] { false, true })
                {
                    if (bottom) scroll.ScrollToEnd(); else scroll.ScrollToHome(); Layout();
                    foreach (var slider in sliders)
                    {
                        var bounds = slider.TransformToAncestor(root).TransformBounds(new Rect(slider.RenderSize));
                        Check(bounds.Left >= 0 && bounds.Right <= width, "Stock slider clips horizontally.");
                    }
                    var image = new RenderTargetBitmap((int)width, 740, 96, 96, PixelFormats.Pbgra32); image.Render(root);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(output, $"trader-{width}-{(light ? "light" : "dark")}-{(bottom ? "stock" : "models")}.png")); png.Save(file);
                }
                editor.Close();
            }
            main.Close(); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("PASS actual trader editor: themed sliders/toggles/counts, two widths, dark/light, no game IPC."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Find<T>(child)) yield return nested; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
