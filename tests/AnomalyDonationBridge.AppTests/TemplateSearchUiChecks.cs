using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

// Explicit performance check with synthetic data and an isolated runtime.
internal static class TemplateSearchUiChecks
{
    internal static void WaitForSearch(MainViewModel model, Dispatcher dispatcher)
    {
        var pending = model.TemplateSearchCompletion;
        if (!pending.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = pending.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame);
        }
        pending.GetAwaiter().GetResult();
    }

    public static int Run(string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var task = MeasureAsync(outputDirectory);
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task MeasureAsync(string outputDirectory)
    {
        var runtime = new BridgeRuntime(new AppPaths(Path.Combine(outputDirectory, "isolated-data")));
        runtime.Settings.Profiles.Add(new DonationProfile { Id = "search-test", Name = "Search test" });
        runtime.Settings.ActiveProfileId = "search-test";
        var model = new MainViewModel(runtime) { ShowTemplateDescriptions = false };
        var effects = Path.Combine(AppContext.BaseDirectory, "effects");
        var game = Path.Combine(outputDirectory, "fixture-game");
        var gameScripts = Path.Combine(game, "gamedata", "scripts");
        Directory.CreateDirectory(gameScripts);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(effects, "gamedata", "scripts"), "*.script"))
            File.Copy(file, Path.Combine(gameScripts, Path.GetFileName(file)), overwrite: true);
        var presets = new FilePresetLoader(effects).Load(game).Presets;
        if (presets.Count != 73) throw new InvalidOperationException("Performance fixture must use the actual shipped templates.");
        for (var index = 0; index < 84; index++)
        {
            var action = presets[index % presets.Count].CloneAsNewInstance();
            action.DisplayName = index == 0 ? "Вертолёт-напарник" : action.DisplayName + " " + index;
            action.Triggers = [new TriggerRule { Amount = index < 3 ? 1 : index < 43 ? 2131 : index * 10, Currency = "RUB" }];
            model.ActiveActions.Add(new ActionViewModel(action, displayOrdinal: index + 1));
        }
        foreach (var preset in presets)
        {
            model.Presets.Add(new ActionViewModel(preset.CloneAsNewInstance(), isPreset: true));
            model.UserPresets.Add(new ActionViewModel(preset.CloneAsNewInstance(), isUserPreset: true));
        }
        foreach (var group in model.ActiveBrowser.VisibleEntries.Where(entry => entry.IsGroup)) group.Group!.IsExpanded = true;
        var window = new MainWindow(model, runtime) { Width = 1488, Height = 860 };
        await model.TemplateSearchCompletion;
        var root = (FrameworkElement)window.Content;
        void Layout()
        {
            var size = new Size(window.Width - 16, window.Height - 40);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
        }
        Layout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var timings = new List<object>();
        foreach (var query in new[] { "вер", "вертолёт", "в", "", "ничего-891723", "" })
        {
            var watch = Stopwatch.StartNew();
            var lastPulse = watch.Elapsed.TotalMilliseconds;
            var maximumGap = 0d;
            var pulses = 0;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            heartbeat.Tick += (_, _) =>
            {
                var at = watch.Elapsed.TotalMilliseconds;
                maximumGap = Math.Max(maximumGap, at - lastPulse);
                lastPulse = at;
                pulses++;
            };
            heartbeat.Start();
            model.TemplateSearchText = query;
            var setterMs = watch.Elapsed.TotalMilliseconds;
            await model.TemplateSearchCompletion;
            Layout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            maximumGap = Math.Max(maximumGap, watch.Elapsed.TotalMilliseconds - lastPulse);
            heartbeat.Stop();
            Check(model.ActiveBrowser.VisibleActions.Cast<ActionViewModel>().Select(action => action.Id).SequenceEqual(
                model.ActiveActions.Where(action => action.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .Select(action => action.Id)), "Search returned stale or incorrect matches.");
            Check(setterMs < 50, "Typing synchronously executed expensive search or rendering.");
            Check(maximumGap < 400, "Search blocked the UI dispatcher.");
            timings.Add(new { query, setterMs, settledMs = watch.Elapsed.TotalMilliseconds,
                maximumGap, pulses,
                matches = model.ActiveBrowser.VisibleActions.Cast<object>().Count() });
        }
        var json = JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDirectory, "timings.json"), json);
        Console.WriteLine(json);
        await CheckTabsAndCategoriesAsync(model, window, Layout, outputDirectory);
        await CheckCancellationAsync(model, runtime);
        await CheckLargeListAsync();
        window.Close();
        await runtime.DisposeAsync();
    }

    private static async Task CheckTabsAndCategoriesAsync(MainViewModel model, MainWindow window,
        Action layout, string outputDirectory)
    {
        var results = new List<object>();
        foreach (var width in new[] { 1488d, 640d })
        {
            window.Width = width;
            model.SelectedSectionIndex = 3;
            await model.TemplateSearchCompletion;
            layout();
            foreach (var section in new[] { 2, 1, 0 })
            {
                await MeasureAsync("tab-" + section, () => model.SelectedSectionIndex = section);
                var browser = section switch { 0 => model.ActiveBrowser, 1 => model.UserPresetBrowser, _ => model.PresetBrowser };
                await MeasureAsync("inventory-" + section, () => browser.SelectCategory("inventory"));
                await MeasureAsync("all-" + section, () => browser.SelectCategory("all"));
            }

            async Task MeasureAsync(string operation, Action change)
            {
                var watch = Stopwatch.StartNew();
                var gap = 0d;
                var previous = 0d;
                var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
                heartbeat.Tick += (_, _) =>
                {
                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    gap = Math.Max(gap, elapsed - previous);
                    previous = elapsed;
                };
                heartbeat.Start();
                change();
                var handlerMs = watch.Elapsed.TotalMilliseconds;
                layout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                var firstFrameMs = watch.Elapsed.TotalMilliseconds;
                await model.TemplateSearchCompletion;
                layout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                gap = Math.Max(gap, watch.Elapsed.TotalMilliseconds - previous);
                heartbeat.Stop();
                Check(!model.IsTemplateBrowserUpdating, "The tab stayed in a loading state.");
                Check(handlerMs < 100 && firstFrameMs < 400 && gap < 400,
                    $"Opening a tab/category blocked the UI: {operation}/{width}, handler={handlerMs:F1}, first={firstFrameMs:F1}, gap={gap:F1}.");
                results.Add(new { width, operation, handlerMs, firstFrameMs, maximumGapMs = gap, totalMs = watch.Elapsed.TotalMilliseconds });
            }
        }
        var pending = new List<Task>();
        foreach (var section in new[] { 1, 2, 0, 1, 2 })
        {
            model.SelectedSectionIndex = section;
            pending.Add(model.TemplateSearchCompletion);
        }
        await Task.WhenAll(pending);
        Check(model.SelectedSectionIndex == 2 && model.PresetBrowser.VisibleActions.Cast<object>().Count() == model.Presets.Count &&
            !model.IsTemplateBrowserUpdating, "Rapid navigation published an incomplete or stale tab.");
        model.SelectedSectionIndex = 0;
        await model.TemplateSearchCompletion;
        window.Width = 1488;
        layout();
        File.WriteAllText(Path.Combine(outputDirectory, "tabs-and-categories.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS opening template tabs and categories at 1488/640 DIP, first-frame/input latency, rapid navigation cancellation.");
    }

    private static async Task CheckCancellationAsync(MainViewModel model, BridgeRuntime runtime)
    {
        var source = model.ActiveActions.ToArray();
        var triggers = source.Select(action => action.Model.Triggers.Select(rule => rule.Amount).ToArray()).ToArray();
        var requests = new List<Task>();
        foreach (var query in new[] { "в", "вер", "", "радиа", "радиация", "891723" })
        {
            model.TemplateSearchText = query;
            requests.Add(model.TemplateSearchCompletion);
        }
        await Task.WhenAll(requests);
        Check(model.ActiveBrowser.VisibleActions.IsEmpty && model.ActiveActions.SequenceEqual(source),
            "A superseded search replaced the latest query or modified the active templates.");
        Check(source.Select((action, index) => action.Model.Triggers.Select(rule => rule.Amount).SequenceEqual(triggers[index])).All(value => value),
            "Searching modified effect prices.");
        Check(model.PresetBrowser.SearchText.Length == 0 && model.UserPresetBrowser.SearchText.Length == 0,
            "Typing rebuilt hidden browser tabs.");

        model.TemplateSearchText = "в";
        await Task.Delay(135);
        var previous = model.TemplateSearchCompletion;
        model.SelectedSectionIndex = 2;
        await Task.WhenAll(previous, model.TemplateSearchCompletion);
        Check(model.PresetBrowser.VisibleActions.Cast<ActionViewModel>()
            .All(action => action.DisplayName.Contains("в", StringComparison.CurrentCultureIgnoreCase)),
            "Changing sections lost the shared query.");
        model.SelectedSectionIndex = 3;
        var history = new EventHistoryEntry { DonationId = "search-history", ActionName = "History during search" };
        model.History.Add(history);
        Check(model.History.Contains(history) && !runtime.Settings.Armed, "Search affected history or donation reception.");

        model.SelectedSectionIndex = 0;
        model.TemplateSearchText = "";
        await Task.Delay(140);
        previous = model.TemplateSearchCompletion;
        model.ActiveBrowser.SelectCategory("inventory");
        await Task.WhenAll(previous, model.TemplateSearchCompletion);
        Check(model.ActiveBrowser.VisibleActions.Cast<ActionViewModel>()
            .All(action => (string.IsNullOrWhiteSpace(action.Model.CategoryId)
                ? ActionBrowserViewModel.CategoryFor(action.HandlerId) : action.Model.CategoryId) == "inventory"),
            "A late result overwrote the new category.");
        model.ActiveBrowser.SelectCategory("all");
        model.TemplateSearchText = "в";
        await Task.Delay(135);
        previous = model.TemplateSearchCompletion;
        model.ActiveActions.Clear();
        var replacement = new ActionViewModel(new ActionDefinition { HandlerId = "add_radiation", DisplayName = "Новый вариант" });
        model.ActiveActions.Add(replacement);
        await Task.WhenAll(previous, model.TemplateSearchCompletion);
        Check(model.ActiveBrowser.VisibleActions.Cast<ActionViewModel>().SequenceEqual([replacement]) &&
            model.ActiveBrowser.VisibleEntries.Single().Action == replacement,
            "A late result restored templates from the previous profile/source.");
        Console.WriteLine("PASS latest-query cancellation, changing tabs/categories/source, original instances and prices, independent history.");
    }

    private static async Task CheckLargeListAsync()
    {
        var actions = new ObservableCollection<ActionViewModel>(Enumerable.Range(0, 10_000).Select(index =>
            new ActionViewModel(new ActionDefinition { HandlerId = "add_radiation", DisplayName = "Эффект " + index },
                displayOrdinal: index + 1)));
        var browser = new ActionBrowserViewModel(actions);
        using var cancellation = new CancellationTokenSource();
        var cancelled = browser.SearchAsync("Эффект 1", cancellation.Token);
        cancellation.Cancel();
        await browser.SearchAsync("Эффект 9999", CancellationToken.None);
        await cancelled;
        Check(browser.VisibleActions.Cast<ActionViewModel>().SequenceEqual([actions[^1]]) &&
            browser.VisibleEntries.Single().Action == actions[^1], "Large-list cancellation published a stale result.");
        await browser.SearchAsync("нет совпадения", CancellationToken.None);
        Check(browser.VisibleActions.IsEmpty && browser.VisibleEntries.Count == 0, "Large-list empty result is incorrect.");
        Console.WriteLine("PASS 10,000-template search and cancellation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
