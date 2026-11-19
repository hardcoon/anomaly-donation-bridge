using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class CardPresentationUiChecks
{
    public static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var task = CheckAsync(output);
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task CheckAsync(string output)
    {
        await using var runtime = new BridgeRuntime(new AppPaths(Path.Combine(output, "isolated-data")));
        runtime.Settings.Profiles.Add(new DonationProfile { Id = "card-test", Name = "Card test" });
        runtime.Settings.ActiveProfileId = "card-test";
        var game = Path.Combine(output, "fixture-game");
        var scripts = Path.Combine(game, "gamedata", "scripts");
        Directory.CreateDirectory(scripts);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "effects/gamedata/scripts"), "*.script"))
            File.Copy(file, Path.Combine(scripts, Path.GetFileName(file)), overwrite: true);
        runtime.Settings.GamePath = game;
        runtime.RefreshActionManifest();
        Check(runtime.Presets.Count == 73, "The card fixture must use all shipped presets.");
        runtime.Settings.Actions = runtime.Presets.Take(24).Select((preset, index) =>
        {
            var action = preset.CloneAsNewInstance();
            action.Triggers = [new TriggerRule { Amount = index < 3 ? 1 : index + 10, Currency = "RUB" }];
            return action;
        }).ToList();
        runtime.Settings.UserPresets = runtime.Presets.Take(8).Select(preset => preset.CloneAsNewInstance()).ToList();
        var model = new MainViewModel(runtime) { ShowTemplateDescriptions = false };
        model.RefreshFromSettings();
        var group = model.ActiveBrowser.VisibleEntries.Single(entry => entry.IsGroup).Group!;
        group.IsExpanded = true;
        var window = new MainWindow(model, runtime) { Width = 1488, Height = 860 };
        var root = (FrameworkElement)window.Content;
        void Layout()
        {
            var size = new Size(window.Width - 16, window.Height - 40);
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        }
        async Task Settle()
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            Task pending;
            do { pending = model.TemplateSearchCompletion; await pending; }
            while (!ReferenceEquals(pending, model.TemplateSearchCompletion));
            Layout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        await Settle();
        var active = model.ActiveActions.ToArray();
        var users = model.UserPresets.ToArray();
        var presets = model.Presets.ToArray();
        var entries = model.ActiveBrowser.VisibleEntries.ToArray();
        var children = group.RenderedActions.ToArray();
        var draft = model.Presets.First();
        draft.CooldownSeconds = 77;
        active[0].LastResult = "Executed: fixture";
        var counts = new Dictionary<NotifyCollectionChangedAction, int>();
        foreach (INotifyCollectionChanged collection in new INotifyCollectionChanged[]
        {
            model.ActiveActions, model.UserPresets, model.Presets, model.ActiveBrowser.VisibleEntries,
            model.UserPresetBrowser.VisibleEntries, model.PresetBrowser.VisibleEntries,
            (INotifyCollectionChanged)model.ActiveBrowser.VisibleActions,
            (INotifyCollectionChanged)model.UserPresetBrowser.VisibleActions,
            (INotifyCollectionChanged)model.PresetBrowser.VisibleActions, group.RenderedActions
        }) collection.CollectionChanged += (_, change) => counts[change.Action] = counts.GetValueOrDefault(change.Action) + 1;

        foreach (var width in new[] { 1488d, 640d })
        foreach (var section in new[] { 0, 1, 2 })
        {
            window.Width = width;
            model.SelectedSectionIndex = section;
            await Settle();
            var list = width > 720
                ? (ItemsControl)window.FindName(section switch { 0 => "ActiveActionsList", 1 => "UserPresetsList", _ => "PresetsList" })
                : (ItemsControl)((CompactMainView)window.FindName("CompactShell")).FindName("SmallActionsList");
            var containers = list.Items.Cast<object>().Select(item => (Item: item, Container: list.ItemContainerGenerator.ContainerFromItem(item)))
                .Where(item => item.Container is not null).ToArray();
            Check(containers.Length > 0, "Card containers were not realized.");
            if (width == 640 && section == 2) Check(containers.Length < list.Items.Count, "Compact rows are not virtualized.");
            counts.Clear();
            runtime.Settings.GlobalCooldownSeconds++;
            await runtime.SaveSettingsAsync();
            await Settle();
            AssertStable();
            Check(counts.Values.Sum() == 0, "Saving a global setting changed a card collection.");
            Check(containers.All(item => ReferenceEquals(item.Container, list.ItemContainerGenerator.ContainerFromItem(item.Item))),
                "Saving recreated existing WPF card containers.");
        }
        window.Width = 1488;
        model.SelectedSectionIndex = 0;
        await Settle(); counts.Clear();
        var edited = active[0].Model.Clone(); edited.CooldownSeconds = 21;
        await runtime.SaveActionParametersAsync(edited.Id, edited);
        await Settle(); AssertStable();
        Check(ReferenceEquals(active[0].Model, runtime.Settings.Actions[0]) && active[0].CooldownSeconds == 21,
            "An edited card did not bind to its current persisted model.");
        Check(counts.Values.Sum() == 0, "Editing parameters replaced card/group collections.");
        await runtime.SetGroupStrictSequenceAsync(group.Key, true);
        await runtime.SetGroupDelayAsync(group.Key, 7);
        await Settle(); AssertStable();
        Check(group.StrictSequence && group.GroupDelaySeconds == 7 && counts.Values.Sum() == 0,
            "Sequence preferences rebuilt their group or did not refresh its controls.");

        var catalogEntries = new[] { new CatalogEntry { SectionId = "fixture_item", Kind = CatalogEntryKind.Item,
            DisplayName = "Fixture item", IsAvailable = true } };
        var catalog = new CatalogSnapshot(new CatalogRevision("card-live"), CatalogContentHash.Compute(catalogEntries), catalogEntries);
        typeof(BridgeRuntime).GetField("_catalogState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, new CatalogRuntimeState(CapabilitySet.Empty, catalog, true, "Fresh fixture catalog"));
        model.RefreshFromSettings();
        await Settle(); AssertStable();
        Check(counts.Values.Sum() == 0, "Receiving a catalogue reset existing card collections.");
        Check(model.Presets.Where(card => card.RequiresCatalog).All(card => card.Model.CatalogRevision == catalog.Revision),
            "Stable cards retained a stale catalogue revision.");

        foreach (var section in new[] { 2, 1, 0, 2, 0 })
        {
            model.SelectedSectionIndex = section;
            await Settle(); AssertStable();
        }
        Check(counts.Values.Sum() == 0, "Returning to a settled tab reinserted all its cards.");

        var added = runtime.Settings.Actions.Last().CloneAsNewInstance();
        added.Triggers = [new TriggerRule { Amount = 9999, Currency = "RUB" }];
        runtime.Settings.Actions.Add(added);
        model.RefreshFromSettings(); await Settle();
        Check(model.ActiveActions.Take(active.Length).SequenceEqual(active) && model.ActiveActions.Last().Id == added.Id,
            "Adding a template replaced its neighbours.");
        runtime.Settings.Actions.Remove(added);
        model.RefreshFromSettings();
        Check(!model.ActiveBrowser.VisibleActions.Cast<ActionViewModel>().Any(card => card.Id == added.Id),
            "A removed action remained clickable until the asynchronous refresh.");
        await Settle(); AssertStable();
        Check(!counts.ContainsKey(NotifyCollectionChangedAction.Reset) && !counts.ContainsKey(NotifyCollectionChangedAction.Replace),
            "Adding/removing a card reset or replaced surviving items.");
        var result = new { cardContainersPreserved = true, settings = true, edit = true, catalogue = true,
            groups = true, tabs = true, membership = true, presets = presets.Length, active = active.Length, events = counts };
        File.WriteAllText(Path.Combine(output, "card-stability.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS card presentation: actual WPF containers, settings, parameter edits, catalogue refresh, group controls, tab returns and selective add/remove; no Reset/Replace.");
        window.Close();

        void AssertStable()
        {
            Check(model.ActiveActions.SequenceEqual(active) && model.UserPresets.SequenceEqual(users) && model.Presets.SequenceEqual(presets),
                "A notification replaced stable card view models.");
            Check(model.ActiveBrowser.VisibleEntries.SequenceEqual(entries) && ReferenceEquals(group, entries.Single(entry => entry.IsGroup).Group) &&
                group.IsExpanded && group.RenderedActions.SequenceEqual(children), "An unchanged sequence lost its group/children or expanded state.");
            Check(draft.CooldownSeconds == 77 && active[0].LastResult == "Executed: fixture", "Refreshing lost a draft or the latest effect result.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
