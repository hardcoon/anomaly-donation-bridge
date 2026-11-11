using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.AppTests;

// Disconnected renderer/integration fixture. No provider, IPC server, user data,
// global hotkeys or on-screen windows are started.
internal static class VotingUiChecks
{
    public static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false); app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var task = CheckAllAsync(output); var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke((Action)(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            Console.WriteLine("PASS native single-active voting, queue policy, live editing, scoped reset/cancellation, deletion to empty, pointer insertion, universal OBS capture identity/lifecycle, compact alpha OBS, profile separation, collapsible settings/conditions/persistence and isolated donation integration."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task CheckAllAsync(string output)
    {
        await CheckAsync(output);
        await CheckProfilesAsync(output);
        await CheckCompactPanelAsync(output);
    }

    private static async Task CheckProfilesAsync(string output)
    {
        using var temporary = new Program.TemporaryDirectory();
        await using var runtime = new BridgeRuntime(new AppPaths(temporary.Path));
        await runtime.InitializeVotingModuleAsync();
        var source = runtime.Settings.ActiveProfileId;
        var original = runtime.Voting.Snapshot.CollectionId;
        using var controller = new VotingObsController(runtime);
        var window = new VotingWindow(runtime, controller);
        var root = (FrameworkElement)window.Content;
        Layout(root, 1310, 860);
        Invoke(Find<Button>(root).First(button => Equals(button.Tag, original)));
        await Wait(() => !Field<bool>(window, "_generalSelected") && !Field<bool>(window, "_busy"));
        Field<CheckBox>(window, "_time").IsChecked = true;
        Field<CheckBox>(window, "_money").IsChecked = false;
        Field<TextBox>(window, "_minutes").Text = "10";
        await window.FlushPendingChangesAsync();
        var dialog = new PresetNameWindow("Название нового профиля", "Новый профиль", true, true);
        var checks = Find<CheckBox>(dialog.Content as FrameworkElement ?? throw new Exception("Missing profile dialog"));
        var copyCheck = checks.Single(check => check.Name == "CopyVotingsBox");
        Check(copyCheck.Visibility == Visibility.Visible && dialog.CopyVotings, "Profile dialog lacks its independent copy-votes checkbox");
        copyCheck.IsChecked = false; Check(!dialog.CopyVotings && dialog.CopyUserPresets, "Copy checkbox changes user preset copying");
        var dialogRoot = (FrameworkElement)dialog.Content;
        dialogRoot.Measure(new Size(430, double.PositiveInfinity)); Layout(dialogRoot, 430, Math.Ceiling(dialogRoot.DesiredSize.Height));
        Save(dialogRoot, Path.Combine(output, "profile-copy-votings.png")); dialog.Close();
        var copiedProfile = await runtime.CreateProfileAsync("Копия голосований", copyVotings: true);
        await Wait(() => Field<string>(window, "_profileId") == copiedProfile.Id);
        var copied = runtime.Voting.CollectionsFor(copiedProfile.Id).Single();
        Check(copied is { MoneyGoal: false, TimeGoal: true, DurationSeconds: 600, IsActive: false }, "Profile copy missed the flushed side settings");
        Check(!Find<Button>(root).Any(button => Equals(button.Tag, original)) && Find<Button>(root).Any(button => Equals(button.Tag, copied.CollectionId)),
            "Voting navigation still exposes another profile's collections");
        var empty = await runtime.CreateProfileAsync("Без голосований", copyVotings: false);
        await Wait(() => Field<string>(window, "_profileId") == empty.Id);
        Check(Find<Button>(Field<StackPanel>(window, "_sidebar")).All(button => button.Tag is null), "Empty profile still shows old votes");
        Invoke(Find<Button>(root).First(button => Equals(button.Content, "+ Добавить")));
        await Wait(() => runtime.Voting.CollectionsFor(empty.Id).Count == 1 && !Field<bool>(window, "_busy"));
        Check(runtime.Voting.CollectionsFor(empty.Id).Single().ProfileId == empty.Id, "Add vote from empty profile wrote into an old profile");
        await runtime.SwitchProfileAsync(source);
        await Wait(() => Field<string>(window, "_profileId") == source);
        Check(Find<Button>(root).Any(button => Equals(button.Tag, original)) && !Find<Button>(root).Any(button => Equals(button.Tag, copied.CollectionId)),
            "Returning to profile did not restore its voting list");
        window.Close(); await Wait(() => Field<bool>(window, "_closed"));
    }

    private static async Task CheckCompactPanelAsync(string output)
    {
        var now = DateTimeOffset.UtcNow;
        var sample = new VotingRound { MoneyGoal = false, TimeGoal = true, DurationSeconds = 90 };
        Check(VotingWindow.CompactConditions(sample, now) == "1:30 (таймер)", "Compact timer must omit disabled amount condition");
        sample.MoneyGoal = true;
        Check(VotingWindow.CompactConditions(sample, now) == "0/1000 рублей ИЛИ 1:30 (таймер)", "Compact OR conditions are incorrect");
        sample.Condition = VotingCondition.All; sample.Currency = "EUR"; sample.Target = 25.50m;
        sample.Options = [new VotingOption { Amount = 3.25m }];
        Check(VotingWindow.CompactConditions(sample, now) == "3,25/25,5 EUR И 1:30 (таймер)", "Compact amount/currency/AND conditions are incorrect");
        sample.TimeGoal = false;
        Check(VotingWindow.CompactConditions(sample, now) == "3,25/25,5 EUR", "Disabled timer leaked into compact conditions");
        sample.MoneyGoal = false; sample.TimeGoal = true; sample.Phase = VotingPhase.Paused; sample.PausedAt = now; sample.Deadline = now.AddSeconds(90);
        Check(VotingWindow.CompactConditions(sample, now.AddMinutes(5)) == "1:30 (таймер)", "Compact paused countdown keeps running");
        sample.Phase = VotingPhase.Collecting; sample.PausedAt = null;
        Check(VotingWindow.CompactConditions(sample, now.AddSeconds(1)) == "1:29 (таймер)", "Compact live countdown does not update");
        foreach (var phase in new[] { VotingPhase.Ready, VotingPhase.Failed, VotingPhase.Drawn })
        {
            sample.Phase = phase;
            Check(VotingWindow.CompactConditions(sample, now) == "0:00 (таймер)", "Compact completed timer is not zero");
        }

        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        await using var runtime = new BridgeRuntime(paths);
        await runtime.InitializeVotingModuleAsync();
        var draft = runtime.Voting.Snapshot;
        draft.Title = "ПОМОЧЬ ИЛИ ПОМЕШАТЬ?"; draft.MoneyGoal = false; draft.TimeGoal = true; draft.DurationSeconds = 90;
        draft.Options = [new VotingOption { Command = "!помощь", Preset = new ActionDefinition { HandlerId = "add_radiation", DisplayName = "Помощь игроку" } },
            new VotingOption { Command = "!мимо", Preset = new ActionDefinition { HandlerId = "add_radiation", DisplayName = "Ничего страшного, повезёт в следующий раз" } }];
        await runtime.Voting.SaveSettingsAsync(draft);
        await runtime.Voting.AddCollectionAsync(sourceCollectionId: draft.CollectionId, profileId: runtime.Settings.ActiveProfileId);
        await runtime.Voting.SetCollectionActiveAsync(draft.CollectionId, true, runtime.Settings.ActiveProfileId);
        using var controller = new VotingObsController(runtime);
        var window = new VotingWindow(runtime, controller); var root = (FrameworkElement)window.Content;
        try
        {
            ThemeManager.Apply(false); Layout(root, 1310, 860);
            Check(Find<Button>(Field<FrameworkElement>(window, "_generalPanel")).Contains(Field<Button>(window, "_obs")), "OBS is not in general settings");
            Save(root, Path.Combine(output, "voting-panel-general.png"));
            Invoke(Find<Button>(root).First(button => Equals(button.Tag, draft.CollectionId)));
            await Wait(() => !Field<bool>(window, "_generalSelected") && !Field<bool>(window, "_busy"));
            var dots = Find<TextBlock>(Field<StackPanel>(window, "_sidebar")).Where(label => label.Text == "●").ToList();
            Check(dots.Count == 1 && ((SolidColorBrush)dots[0].Foreground).Color == ((SolidColorBrush)window.FindResource("GoodBrush")).Color,
                "Inactive sidebar dots must be absent and only the active green one must remain");
            var add = Find<Button>(root).Single(button => Equals(button.Content, "+ Добавить"));
            Check(Equals(add.ToolTip, "Добавить голосование"), "Short add label lost its purpose tooltip");
            var delete = Field<Button>(window, "_delete");
            Check(Equals(delete.Content, "\uE74D") && delete.FontFamily.Source == "Segoe MDL2 Assets" &&
                ((SolidColorBrush)delete.Foreground).Color == ((SolidColorBrush)window.FindResource("BadBrush")).Color &&
                System.Windows.Automation.AutomationProperties.GetName(delete) == "Удалить это голосование", "Delete must be an accessible red trash icon");
            var beforeToggle = await File.ReadAllTextAsync(Path.Combine(paths.RootDirectory, "voting.json"));
            Invoke(Field<Button>(window, "_panelToggle"));
            await Wait(() => runtime.UiPreferences.VotingSettingsPanelCollapsed);
            Check(await File.ReadAllTextAsync(Path.Combine(paths.RootDirectory, "voting.json")) == beforeToggle, "Panel toggle changed durable voting data");
            Check((await new JsonUiPreferencesStore(paths).LoadAsync()).VotingSettingsPanelCollapsed, "Collapsed panel preference is not durable");
            Layout(root, 744, 524);
            var commands = Field<Grid>(window, "_commandsPanel"); var summary = Field<TextBlock>(window, "_compactSummary");
            var commandsAt = commands.TranslatePoint(new Point(), root); var summaryAt = summary.TranslatePoint(new Point(), root);
            Check(Field<FrameworkElement>(window, "_settingsCard").Visibility == Visibility.Collapsed &&
                Field<ColumnDefinition>(window, "_settingsColumn").ActualWidth == 0 && Field<ColumnDefinition>(window, "_settingsGap").ActualWidth == 0 &&
                commandsAt.X + commands.ActualWidth <= root.ActualWidth + 1 && Math.Abs(commands.ActualWidth - (root.ActualWidth - commandsAt.X)) < 1,
                "Collapsed settings reserve space or clip commands");
            Check(summary.Visibility == Visibility.Visible && summaryAt.Y + summary.ActualHeight <= commandsAt.Y && summary.Text.EndsWith(" (таймер)") &&
                !summary.Text.Contains("рублей"), "Compact conditions must appear above commands without disabled amount");
            var spin = Field<Button>(window, "_spin");
            Check(ReferenceEquals(spin.Parent, Field<StackPanel>(window, "_compactActions")) && spin.Visibility == Visibility.Visible &&
                spin.TranslatePoint(new Point(spin.ActualWidth, spin.ActualHeight), root).X <= root.ActualWidth + 1 &&
                spin.TranslatePoint(new Point(spin.ActualWidth, spin.ActualHeight), root).Y <= root.ActualHeight + 1, "Compact mode hides/clips the spin control");
            Save(root, Path.Combine(output, "voting-panel-compact-minimum.png"));
            Invoke(Field<Button>(window, "_collectionToggle"));
            await Wait(() => !runtime.Voting.SnapshotFor(draft.CollectionId).IsActive && !Field<bool>(window, "_busy"));
            Check(Field<TextBlock>(window, "_statusDot").Visibility == Visibility.Collapsed, "Paused heading keeps an inactive dot");
            Invoke(Field<Button>(window, "_panelToggle"));
            await Wait(() => !runtime.UiPreferences.VotingSettingsPanelCollapsed);
            Layout(root, 1310, 860); CheckLayout(window, root);
            Check(ReferenceEquals(spin.Parent, Field<StackPanel>(window, "_settingsActions")) && summary.Visibility == Visibility.Collapsed, "Expanded panel did not restore its controls");
            Save(root, Path.Combine(output, "voting-panel-expanded.png"));
            Field<CheckBox>(window, "_money").IsChecked = true;
            Field<TextBox>(window, "_target").Text = "2500";
            Invoke(Field<Button>(window, "_panelToggle"));
            await Wait(() => runtime.UiPreferences.VotingSettingsPanelCollapsed && !Field<bool>(window, "_dirty"));
            Check(runtime.Voting.SnapshotFor(draft.CollectionId).Target == 2500 && summary.Text.StartsWith("0/2500 рублей ИЛИ "), "Collapsing panel lost the pending settings edit");
            ThemeManager.Apply(true); Layout(root, 1000, 700); Save(root, Path.Combine(output, "voting-panel-compact-light.png"));
            window.Close(); await Wait(() => Field<bool>(window, "_closed"));
            window = new VotingWindow(runtime, controller); root = (FrameworkElement)window.Content;
            Layout(root, 1310, 860);
            Invoke(Find<Button>(root).First(button => Equals(button.Tag, draft.CollectionId)));
            await Wait(() => !Field<bool>(window, "_generalSelected") && !Field<bool>(window, "_busy"));
            Check(Field<FrameworkElement>(window, "_settingsCard").Visibility == Visibility.Collapsed &&
                runtime.Voting.SnapshotFor(draft.CollectionId).Phase == VotingPhase.Paused, "Reopening the window lost the compact preference or resumed voting");
            Invoke(Find<Button>(root).First(button => Equals(button.Content, "Общие настройки")));
            await Wait(() => Field<bool>(window, "_generalSelected") && !Field<bool>(window, "_busy"));
            Check(Field<Grid>(window, "_headerActions").Visibility == Visibility.Collapsed && Field<TextBlock>(window, "_compactSummary").Visibility == Visibility.Collapsed &&
                Field<FrameworkElement>(window, "_generalPanel").Visibility == Visibility.Visible, "General settings show per-vote compact controls");
        }
        finally { window.Close(); ThemeManager.Apply(false); }
    }

    private static async Task CheckAsync(string output)
    {
        CheckObsPresentation(output);
        var paths = new AppPaths(Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N")));
        await using var runtime = new BridgeRuntime(paths);
        var gameApplied = false;
        runtime.VotingPresentationTransportForTests = (request, id, cancel, token) =>
            Task.FromResult(new VotingGameReply(id, cancel || gameApplied ? "done" : "active", "isolated_game_ui"));
        var first = new ActionDefinition { DisplayName = "Отряд Долга", HandlerId = "add_radiation", IsActive = true,
            Triggers = [new TriggerRule { Amount = 100, Currency = "RUB" }] };
        var second = first.CloneAsNewInstance(); second.DisplayName = "Отряд Свободы";
        runtime.Settings.ActiveProfileId = "test-profile"; runtime.Settings.Actions.AddRange([first, second]);
        typeof(BridgeRuntime).GetField("_presets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, new List<ActionDefinition> { first, second });
        await runtime.InitializeVotingModuleAsync();
        Check(runtime.Voting.Automation is { Enabled: false, AutomaticDraw: true, AutomaticWinnerEffect: true,
            AutoNextCollection: true, Order: VotingSequenceOrder.InOrder, ShowGameHud: true, GameHudFormat: VotingHudFormat.Percent, ShowGameHudCommands: true, ShowGameHudTemplateNames: false },
            "Fresh general voting defaults must match requested screenshot without enabling collection");
        var draft = runtime.Voting.Snapshot; draft.TimeGoal = false;
        foreach (var phase in new[] { VotingPhase.Ready, VotingPhase.Failed, VotingPhase.Drawn })
            Check(VotingWindow.RemainingText(new VotingRound { TimeGoal = true, Phase = phase, Deadline = DateTimeOffset.UtcNow.AddMinutes(9) }).Length == 0,
                "Program and OBS must not display a countdown on a completed vote: " + phase);
        draft.Options = [new VotingOption { Command = "!долг", Preset = first }, new VotingOption { Command = "!свобода", Preset = second }];
        await runtime.Voting.SaveSettingsAsync(draft);
        var trace = new StringWriter(); var listener = new TextWriterTraceListener(trace);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        using var captureController = new VotingObsController(runtime);
        var window = new VotingWindow(runtime, captureController); var root = (FrameworkElement)window.Content;
        var capture = captureController.GetOrCreateWindow();
        capture.Width = 660; capture.Height = 360; capture.Left = 30; capture.Top = 40;
        var captureHandle = new WindowInteropHelper(capture).EnsureHandle(); // Native identity, never shown or activated.
        var captureClosed = false; capture.Closed += (_, _) => captureClosed = true;
        void CheckCaptureIdentity() => Check(!captureClosed && captureHandle != IntPtr.Zero &&
            ReferenceEquals(capture, captureController.GetOrCreateWindow()) && new WindowInteropHelper(capture).Handle == captureHandle &&
            capture.Title == VotingObsWindow.CaptureTitle && capture.Width == 660 && capture.Height == 360 && capture.Left == 30 && capture.Top == 40,
            "Universal OBS window/handle/title/geometry changed between votes");
        var obs = new VotingObsWindow();
        try
        {
            Check(capture.Wheel.Round is null && ((FrameworkElement)capture.Content).Visibility == Visibility.Hidden,
                "OBS shows a selected draft when no vote is active");
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light); Layout(root, 1300, 690); Save(root, Path.Combine(output, light ? "voting-general-light.png" : "voting-general-dark.png"));
                Check(Find<Button>(root).Any(button => Equals(button.Content, "Крутить")), "Manual spin button missing");
                Check(Find<Button>(root).Any(button => Equals(button.Content, "Общие настройки")), "Permanent sidebar missing");
                Check(!Find<Button>(root).Any(button => Equals(button.Content, "Сохранить")), "Manual save button must not remain in voting UI");
                Check(Field<TextBlock>(window, "_title").Text == "Голосование: Общие настройки", "General title is not the requested colon form");
                var hudCard = Find<Border>(root).Single(border => Equals(border.Tag, "VotingGameHudSettings"));
                Check(hudCard.BorderThickness.Left == 1 && hudCard.Padding.Left == 16 && hudCard.Background is not null &&
                    Find<CheckBox>(hudCard).Contains(Field<CheckBox>(window,"_gameHud")) &&
                    Find<CheckBox>(hudCard).Contains(Field<CheckBox>(window,"_gameHudCommands")) &&
                    Find<CheckBox>(hudCard).Contains(Field<CheckBox>(window,"_gameHudTemplateNames")) &&
                    Find<ComboBox>(hudCard).Contains(Field<ComboBox>(window,"_gameHudFormat")) &&
                    Find<Button>(hudCard).Contains(Field<Button>(window,"_gameHudEdit")),
                    "All game-progress controls must share one padded, themed card");
                Check(!Find<TextBlock>(root).Any(text => text.Text.StartsWith("Серый текст с чёрной обводкой")),
                    "Removed HUD explanation must not remain in settings");
                var automation = Field<CheckBox>(window,"_automaticDraw");
                Check(hudCard.TranslatePoint(new Point(),root).X >= automation.TranslatePoint(new Point(),root).X + automation.ActualWidth + 20 &&
                    Math.Abs(hudCard.TranslatePoint(new Point(),root).Y-automation.TranslatePoint(new Point(),root).Y)<2,
                    "HUD card must occupy the second column, aligned with automation settings, not follow the queue vertically");
                Check(VotingWindow.ResetConfirmation.Contains("настройки программы") && VotingWindow.ResetConfirmation.Contains("оставшееся время"), "Reset confirmation omits preserved settings");
                Check(Find<CheckBox>(root).Any(box => box.Content is TextBlock label && label.Text == "Автоматический запуск рулетки по окончании голосования"), "Automatic draw checkbox missing");
            }
            Layout(root,1084,572);
            var compactHudCard = Find<Border>(root).Single(border => Equals(border.Tag,"VotingGameHudSettings"));
            var compactHudOrigin = compactHudCard.TranslatePoint(new Point(),root);
            Check(compactHudOrigin.X+compactHudCard.ActualWidth<=root.ActualWidth && Field<Button>(window,"_gameHudEdit").ActualWidth>=190,
                "Second HUD column and desktop-position button must stay inside the minimum window width");
            Save(root,Path.Combine(output,"voting-general-minimum.png")); Layout(root,1300,690);
            var templateNames = Field<CheckBox>(window, "_gameHudTemplateNames");
            Check(templateNames.IsChecked == false && templateNames.IsEnabled, "Template names must default off in the game HUD");
            templateNames.IsChecked = true;
            await Wait(() => runtime.Voting.Automation.ShowGameHudTemplateNames && !Field<bool>(window, "_dirty"));
            Check((await new JsonVotingStore(paths).LoadAsync()).Automation.ShowGameHudTemplateNames, "HUD template-name preference was not saved");
            Field<CheckBox>(window, "_gameHudCommands").IsChecked = false;
            await Wait(() => !runtime.Voting.Automation.ShowGameHudCommands && !Field<bool>(window, "_dirty"));
            Check(!templateNames.IsEnabled && templateNames.IsChecked == true, "Hidden command list should disable names without forgetting the preference");
            Field<CheckBox>(window, "_gameHudCommands").IsChecked = true;
            await Wait(() => runtime.Voting.Automation.ShowGameHudCommands && !Field<bool>(window, "_dirty"));
            Layout(root, 1300, 690); Save(root, Path.Combine(output, "voting-general-template-names.png"));
            Field<CheckBox>(window, "_gameHud").IsChecked = false;
            await Wait(() => !runtime.Voting.Automation.ShowGameHud);
            Field<CheckBox>(window, "_gameHud").IsChecked = true;
            Field<ComboBox>(window, "_gameHudFormat").SelectedIndex = 1;
            await Wait(() => runtime.Voting.Automation.ShowGameHud &&
                runtime.Voting.Automation.GameHudFormat == VotingHudFormat.Amount);
            Check(Field<Button>(window,"_gameHudEdit").Content.Equals("Редактировать положение") &&
                !Find<ComboBox>(root).Any(box => box.Items.Cast<object>().Any(item => Equals(item,"Слева"))),
                "Desktop position editor must replace preset screen-side selector");
            var desktopPosition = VotingHudPositionWindow.Normalise(new Point(-1440,360),new Rect(-1920,0,1920,1080),new Size(450,100));
            Check(desktopPosition.X == .25m && Math.Abs(desktopPosition.Y-1m/3m)<.000001m,
                "Desktop position must be monitor-relative even on a negative-coordinate monitor");
            desktopPosition = VotingHudPositionWindow.Normalise(new Point(4000,3000),new Rect(0,0,1920,1080),new Size(450,100));
            Check(desktopPosition.X<=1-450m/1920 && desktopPosition.Y<=1-100m/1080,
                "Entire desktop preview must be clamped inside the target game screen");
            await runtime.Voting.SaveHudPositionAsync(.3m,.6m);
            var placement = new VotingHudPositionWindow(draft,runtime.Voting.Automation);
            var placementRoot = (FrameworkElement)placement.Content; Layout(placementRoot,640,250);
            Check(Find<Grid>(placementRoot).Any(row => row.Height == 8) &&
                Find<TextBlock>(placementRoot).Any(text => text.Text == "Отряд Долга"), "Desktop HUD preview lost template names or compact inter-option gaps; names=" +
                runtime.Voting.Automation.ShowGameHudTemplateNames + "; commands=" + runtime.Voting.Automation.ShowGameHudCommands +
                "; rows=" + string.Join(" / ", VotingGameHud.ContentLines(draft, runtime.Voting.Automation, DateTimeOffset.UtcNow)));
            Save(placementRoot, Path.Combine(output, "voting-hud-template-names-preview.png"));
            Check(placement.WindowStyle==WindowStyle.None && placement.AllowsTransparency && placement.Topmost &&
                ((SolidColorBrush)placement.Background).Color.A==1 &&
                Find<Button>(placementRoot).Any(button=>Equals(button.Content,"Применить")) &&
                Find<Button>(placementRoot).Any(button=>Equals(button.Content,"Отмена")),
                "Placement must be a draggable desktop preview with explicit Apply/Cancel, not a game UI");
            placement.Close();
            Check(!runtime.Voting.Automation.Enabled && (await new JsonVotingStore(paths).LoadAsync()).Automation.GameHudFormat == VotingHudFormat.Amount,
                "Global game-HUD controls must autosave without enabling a vote or starting effects");
            Invoke(Find<Button>(root).First(button => Equals(button.Tag, draft.CollectionId)));
            await Wait(() => Field<FrameworkElement>(window, "_collectionPanel").Visibility == Visibility.Visible);
            Invoke(Field<Button>(window, "_collectionToggle"));
            await Wait(() => runtime.Voting.Snapshot.Phase == VotingPhase.Collecting && !Field<bool>(window, "_busy"));
            await Wait(() => capture.Wheel.Round?.Id == runtime.Voting.Snapshot.Id);
            CheckCaptureIdentity();
            Layout(root, 1300, 690);
            CheckLayout(window, root);
            Check(Find<TextBox>(root).Count() >= 6, "Editable command/settings inputs missing");
            Check(window.Rows.All(row => row.CanEdit), "Live collection editing is disabled");
            Field<TextBox>(window, "_testAmount").Text = "200"; Field<TextBox>(window, "_testMessage").Text = "!долг";
            Invoke(Field<Button>(window, "_test")); await Wait(() => runtime.Voting.Snapshot.Total == 200);
            Check(!File.Exists(paths.PendingQueueFile), "Test contribution unexpectedly enqueued a normal price effect");
            await Wait(() => !Field<bool>(window, "_busy")); Invoke(Field<Button>(window, "_collectionToggle"));
            await Wait(() => !runtime.Voting.Snapshot.IsActive && !Field<bool>(window, "_busy"));
            await Wait(() => capture.Wheel.Round is null);
            CheckCaptureIdentity();
            Check(runtime.Voting.Snapshot.Phase == VotingPhase.Paused && runtime.Voting.Snapshot.Total == 200 &&
                Field<TextBlock>(window, "_statusDot").Visibility == Visibility.Collapsed,
                "Per-vote toggle does not pause safely or hide the inactive status dot");
            Invoke(Field<Button>(window, "_collectionToggle")); await Wait(() => runtime.Voting.Snapshot.Phase == VotingPhase.Collecting && !Field<bool>(window, "_busy"));
            Check(Field<TextBlock>(window, "_statusDot").Visibility == Visibility.Visible &&
                ((SolidColorBrush)Field<TextBlock>(window, "_statusDot").Foreground).Color == ((SolidColorBrush)window.FindResource("GoodBrush")).Color,
                "Active vote status is not green");
            await window.AddTemplateAsync(first, draft.CollectionId); window.Rows.Last().Command = "!третья";
            window.Rows[0].Bind(new ActionDefinition { DisplayName = "Изменённый шаблон", HandlerId = "add_radiation", IsActive = true });
            await Wait(() => runtime.Voting.Snapshot.Options.Count == 3 && !Field<bool>(window, "_dirty"));
            Check(runtime.Voting.Snapshot.Total == 200 && runtime.Voting.Snapshot.Options[0].Preset!.DisplayName == "Изменённый шаблон", "Live UI save lost balance or binding change");
            Invoke(Field<Button>(window, "_add")); await Wait(() => window.Rows.Count == 4); Layout(root, 1300, 690);
            var picker = Find<Button>(root).Single(button => button.Content is TextBlock block && block.Text == "Выбрать из активных");
            Check(((SolidColorBrush)picker.Foreground).Color == ((SolidColorBrush)window.FindResource("AccentBrush")).Color && picker.MinHeight == 20,
                "Unbound preset picker lacks accent or consistent compact spacing");
            Check(picker.BorderThickness.Left==1 && ((SolidColorBrush)picker.Background).Color.A>0 && picker.Padding.Left==7 && picker.ActualHeight<=22,
                "Choose-from-active must look like a compact bordered button, not bare text or an oversized control");
            var optionCards = Find<Border>(root).Where(border=>border.DataContext is VotingOptionRow && border.MinHeight==78).ToList();
            Check(optionCards.Count==4 && optionCards.All(card=>Math.Abs(card.ActualHeight-78)<1),
                "Showing the unbound picker button must not change command-card heights");
            Save(root, Path.Combine(output, "voting-unbound-picker.png"));
            window.Rows.Last().Bind(first); Layout(root,1300,690);
            Check(picker.BorderThickness.Left==0 && window.Rows.Last().Model.Amount==0 && optionCards.All(card=>Math.Abs(card.ActualHeight-78)<1),
                "Selecting a preset must update picker appearance without changing the row layout or funds");
            window.Rows.Remove(window.Rows.Last());
            Layout(root, 1300, 690); Save(root, Path.Combine(output, "voting-collection-live.png"));
            Field<TextBox>(window, "_testAmount").Text = "800"; Field<TextBox>(window, "_testMessage").Text = "!свобода";
            Invoke(Field<Button>(window, "_test")); await Wait(() => runtime.Voting.Snapshot.Phase == VotingPhase.Ready);
            Check(Field<Button>(window, "_spin").IsEnabled, "Ready round does not enable manual spin");
            var manualEffect = runtime.Voting.Automation; manualEffect.AutomaticWinnerEffect = false;
            await runtime.Voting.SaveAutomationAsync(manualEffect);
            var selected = await runtime.BeginVotingDrawAsync();
            obs.Update(selected, false); var obsRoot = (FrameworkElement)obs.Content; Layout(obsRoot, 784, 460);
            Save(obsRoot, Path.Combine(output, "voting-obs-alpha.png"));
            Check(obs.AllowsTransparency && obs.WindowStyle == WindowStyle.None && ((SolidColorBrush)obs.Background).Color.A == 1,
                "OBS must have a near-transparent native input surface, not alpha-zero click-through");
            Check(VisualTreeHelper.HitTest(obsRoot, new Point(1, 1)) is not null, "Empty OBS area has no hit-test geometry");
            Check(VotingObsWindow.IsInteractiveSource(new ResizeGrip()) && VotingObsWindow.IsInteractiveSource(new ScrollBar()) &&
                !VotingObsWindow.IsInteractiveSource(Field<TextBlock>(obs, "_title")), "OBS dragging blocks resize/scroll controls or excludes labels");
            Check(Find<TextBlock>(obsRoot).All(block => block.Effect is DropShadowEffect { Opacity: 1, BlurRadius: <= 2, ShadowDepth: >= 2 }), "Dense OBS shadows missing");
            SaveOnWhite(obsRoot, Path.Combine(output, "voting-obs-white-background.png"));
            var bitmap = Bitmap(obsRoot); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var wheelHost = Field<Grid>(obs, "_circle");
            Check(!Find<TextBlock>(wheelHost).Any(), "OBS ring still contains unreadable center text");
            var wheelBox = Find<Viewbox>(wheelHost).Single();
            Check(wheelBox.ActualWidth <= 250 && wheelBox.ActualHeight <= 250, "OBS ring exceeds the smaller display size");
            var wheelCenter = obs.Wheel.TranslatePoint(new Point(obs.Wheel.ActualWidth / 2, obs.Wheel.ActualHeight / 2), obsRoot);
            var centerPixel = ((int)wheelCenter.Y * bitmap.PixelWidth + (int)wheelCenter.X) * 4;
            Check(pixels[centerPixel + 3] == 0, "OBS ring center must remain transparent");
            Check(Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 3] == 0) > pixels.Length / 8, "OBS alpha is opaque");
            Layout(obsRoot, 500, 365); obsRoot.UpdateLayout();
            Check(!obs.IsCompact && obs.MinWidth < 650 && obs.MinHeight < 420, "OBS ring cannot shrink below former limit");
            Save(obsRoot, Path.Combine(output, "voting-obs-small-ring.png"));
            Layout(obsRoot, 300, 150); obsRoot.UpdateLayout();
            Check(obs.IsCompact && Field<Grid>(obs, "_circle").Visibility == Visibility.Collapsed &&
                Find<ProgressBar>(obsRoot).Single().Visibility == Visibility.Collapsed &&
                Field<TextBlock>(obs, "_title").Visibility == Visibility.Visible, "Compact OBS mode must retain text and hide both graphics");
            Check(Find<TextBlock>(obsRoot).Any(block => new TextRange(block.ContentStart, block.ContentEnd).Text.Contains("!долг")), "Compact OBS commands missing");
            Save(obsRoot, Path.Combine(output, "voting-obs-compact.png"));
            await runtime.FinishVotingDrawAsync();
            await Wait(() => capture.Wheel.HasAnimatedProperties);
            await runtime.AdvanceVotingAsync();
            Check(!runtime.Voting.Snapshot.DeliveryAccepted && !File.Exists(paths.PendingQueueFile), "Manual-effect mode unexpectedly auto-dispatched winner");
            await Wait(() => Field<Button>(window, "_add").IsEnabled);
            Check(window.Rows.All(row => row.CanEdit) && Field<Button>(window, "_reset").IsEnabled, "Winner still freezes editing or reset");
            window.Rows[0].Command = "!edited-after-win";
            await Wait(() => runtime.Voting.Snapshot.Options[0].Command == "!edited-after-win" && !Field<bool>(window, "_dirty"));
            Check(capture.Wheel.Round?.Options[0].Command == selected.Options[0].Command,
                "Universal OBS must retain the drawn options while the editor changes the next configuration");
            Check(runtime.Voting.PendingWinners.Single().Winner!.Command == selected.Winner!.Command, "Editing changed frozen winner command");
            await Wait(() => Equals(Field<Button>(window, "_spin").Content, "Ожидает игровую рулетку"));
            Check(!Field<Button>(window, "_spin").IsEnabled, "Program button cannot skip a still-open game result");
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light); Layout(root, 1300, 690); CheckLayout(window, root);
                Save(root, Path.Combine(output, light ? "voting-after-victory-light.png" : "voting-after-victory-dark.png"));
            }
            Layout(root, 1084, 572); CheckLayout(window, root); Save(root, Path.Combine(output, "voting-minimum-layout.png"));
            gameApplied = true; await runtime.AdvanceVotingPresentationAsync(); await Wait(() => runtime.Voting.Snapshot.DeliveryAccepted);
            var queued = await new JsonPendingDispatchStore(paths).LoadAsync();
            Check(queued.Count == 1 && queued[0].Donation.Id == selected.DeliveryId && queued[0].Donation.Message == selected.Winner!.Command, "Incorrect frozen winner outbox identity");
            await runtime.FinishVotingDrawAsync();
            Check((await new JsonPendingDispatchStore(paths).LoadAsync()).Count == 1, "Duplicate winner was enqueued");
            // Real ingestion method, but isolated ledger and disconnected transport.
            await runtime.Voting.ResetAmountsAsync(); await runtime.Voting.SetEnabledAsync(true, "test-profile");
            draft = runtime.Voting.Snapshot; draft.Options[0].Command = "!долг"; await runtime.Voting.SaveSettingsAsync(draft);
            runtime.Settings.Armed = true;
            await runtime.ProcessDonationAsync(new DonationEvent { Id = "donatex:integration-test", Amount = 100,
                Currency = "RUB", Message = "!долг", Username = "Тест", CreatedAt = DateTimeOffset.UtcNow });
            Check(runtime.Voting.Snapshot.Total == 100, "Runtime ingestion did not account for the vote");
            Check((await new JsonPendingDispatchStore(paths).LoadAsync()).Count >= 2, "Runtime ingestion suppressed normal price effect");
            await runtime.ProcessDonationAsync(new DonationEvent { Id = "donatex:integration-test", Amount = 100,
                Currency = "RUB", Message = "!долг", CreatedAt = DateTimeOffset.UtcNow });
            Check(runtime.Voting.Snapshot.Total == 100, "Provider replay counted twice in runtime");
            var restored = new VotingCoordinator(new JsonVotingStore(paths)); await restored.InitializeAsync();
            Check(restored.Snapshot.Total == 100, "Live progress did not survive reload");
            var secondCollection = await runtime.Voting.AddCollectionAsync();
            draft = runtime.Voting.SnapshotFor(secondCollection); draft.Options = runtime.Voting.Snapshot.Options.Select(option => option.Clone()).ToList();
            draft.TimeGoal = false; await runtime.Voting.SaveSettingsAsync(draft);
            var automatic = runtime.Voting.Automation; automatic.AutomaticDraw = true; automatic.AutoNextCollection = true; automatic.AutomaticWinnerEffect = true;
            await runtime.Voting.SaveAutomationAsync(automatic);
            await runtime.Voting.RecordAsync(new DonationEvent { Id = "auto-finish", Amount = 900, Message = "!свобода", CreatedAt = DateTimeOffset.UtcNow }, "test-profile");
            await runtime.AdvanceVotingAsync();
            Check(runtime.Voting.Snapshot is { Phase: VotingPhase.Drawn, DeliveryAccepted: true }, "Automatic draw did not dispatch saved winner");
            var autoWinner = runtime.Voting.Snapshot;
            await runtime.AdvanceVotingAsync();
            Check(runtime.Voting.Snapshot.Id == autoWinner.Id, "Automatic workflow redraws winner");
            await Task.Delay(TimeSpan.FromSeconds(7.1)); await runtime.AdvanceVotingAsync();
            Check(runtime.Voting.SnapshotFor(secondCollection).Total == 0 && runtime.Voting.SnapshotFor(secondCollection).Phase == VotingPhase.Collecting,
                "Automatic next vote did not start configured second entry");
            await Wait(() => capture.Wheel.Round?.CollectionId == secondCollection);
            CheckCaptureIdentity();
            var thirdCollection = await runtime.Voting.AddCollectionAsync();
            draft = runtime.Voting.SnapshotFor(thirdCollection); draft.TimeGoal = false;
            draft.Options = [new VotingOption { Command = "!третье", Preset = first }, new VotingOption { Command = "!другое", Preset = second }];
            await runtime.Voting.SaveSettingsAsync(draft); await runtime.Voting.SetCollectionActiveAsync(thirdCollection, true, "test-profile");
            await runtime.Voting.RecordAsync(new DonationEvent { Id = "parallel-three", Amount = 333, Message = "!третье", CreatedAt = DateTimeOffset.UtcNow }, "test-profile");
            await Wait(() => capture.Wheel.Round is { Total: 333 } current && current.CollectionId == thirdCollection);
            CheckCaptureIdentity();
            runtime.Settings.ActiveProfileId = "other-profile";
            await Wait(() => capture.Wheel.Round is null);
            runtime.Settings.ActiveProfileId = "test-profile";
            await Wait(() => capture.Wheel.Round?.CollectionId == thirdCollection);
            await runtime.Voting.SetCollectionActiveAsync(secondCollection, true, "test-profile");
            await Wait(() => capture.Wheel.Round?.CollectionId == secondCollection);
            CheckCaptureIdentity();
            Check(runtime.Voting.Collections.Count(round => round.IsActive) == 1 && runtime.Voting.SnapshotFor(thirdCollection).Phase == VotingPhase.Paused,
                "Enabling the second vote did not pause the third");
            await runtime.Voting.RecordAsync(new DonationEvent { Id = "parallel-second-winner", Amount = 1000, Message = "!свобода", CreatedAt = DateTimeOffset.UtcNow }, "test-profile");
            await runtime.AdvanceVotingAsync();
            Check(runtime.Voting.SnapshotFor(secondCollection) is { Phase: VotingPhase.Drawn, DeliveryAccepted: true } &&
                runtime.Voting.SnapshotFor(thirdCollection).Total == 333 && runtime.Voting.Snapshot.CollectionId != secondCollection,
                "Background draw used selected page instead of the independently completed vote");
            Invoke(Find<Button>(root).First(button => Equals(button.Content, "Общие настройки")));
            await Wait(() => Field<FrameworkElement>(window, "_generalPanel").Visibility == Visibility.Visible);
            ThemeManager.Apply(false); Layout(root, 1300, 690); Save(root, Path.Combine(output, "voting-general-two-collections.png"));
            Check(Field<TextBlock>(window, "_automationStatus").Text.StartsWith("Активно:") && Field<FrameworkElement>(window, "_collectionPanel").Visibility == Visibility.Collapsed &&
                !Find<Button>(root).Any(button => button.Content is TextBlock block && block.Text.StartsWith("Голосование: активно")),
                "General settings must show status, not a second global activation switch");
            var automaticCheckbox = Field<CheckBox>(window, "_automaticDraw"); automaticCheckbox.IsChecked = false;
            await Wait(() => !runtime.Voting.Automation.AutomaticDraw && !Field<bool>(window, "_dirty"));
            automaticCheckbox.IsChecked = true;
            await Wait(() => runtime.Voting.Automation.AutomaticDraw && !Field<bool>(window, "_dirty"));
            var orderBefore = runtime.Voting.Collections.Select(round => round.CollectionId).ToList();
            await window.MoveSequenceAsync(thirdCollection, orderBefore[0], false);
            Check(runtime.Voting.Collections[0].CollectionId == thirdCollection && runtime.Voting.SnapshotFor(thirdCollection).Total == 333,
                "Drag reorder did not persist stable IDs or lost funds");
            await window.MoveSequenceAsync(thirdCollection, orderBefore[1], true);
            Check(runtime.Voting.Collections.Select(round => round.CollectionId).SequenceEqual(orderBefore), "Drag reorder restore failed");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout(root, 1300, 690);
            Check(!Find<Button>(Field<StackPanel>(window, "_sequence")).Any(button => Equals(button.Content, "↑") || Equals(button.Content, "↓")) &&
                Field<StackPanel>(window, "_sequence").Children.OfType<Border>().Count(border => !border.AllowDrop) == 3, "Reorder still uses arrows or OLE drop targets");
            Check(VotingWindow.InsertionIndex([25, 85, 145], -20) == 0 && VotingWindow.InsertionIndex([25, 85, 145], 55) == 1 &&
                VotingWindow.InsertionIndex([25, 85, 145], 200) == 3, "Pointer reorder must support gaps, beginning and end of list");
            var sidebar = Field<StackPanel>(window, "_sidebar");
            var dots = Find<Button>(sidebar).Where(button => button.Tag is string).SelectMany(button => Find<TextBlock>(button).Where(block => block.Text == "●"))
                .Select(dot => dot.TranslatePoint(new Point(), root).X).ToList();
            Check(dots.Count == runtime.Voting.Collections.Count(round => round.IsActive), "Sidebar shows status dots for inactive votes");
            var menu = new MenuItem(); string? menuSelected = null;
            var tenTargets = Enumerable.Range(1, 10).Select(index => new VotingRound { CollectionId = "menu-" + index, Title = "Голосование " + index }).ToList();
            MainWindow.PopulateVotingTargets(menu, tenTargets, id => { menuSelected = id; return Task.CompletedTask; });
            menu.Style = (Style)window.FindResource("VotingTargetsMenuItemStyle"); menu.ApplyTemplate();
            Check(menu.Template.FindName("PART_Popup", menu) is Popup popup && BindingOperations.GetBinding(popup, Popup.IsOpenProperty)?.Path.Path == "IsSubmenuOpen",
                "Voting menu template has no usable submenu popup");
            ((MenuItem)menu.Items[7]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(menu.Items.Count == 10 && menuSelected == "menu-8", "Preset submenu does not select exact target among ten votes");
            tenTargets[7].Title = "Переименованное";
            MainWindow.PopulateVotingTargets(menu, tenTargets, _ => Task.CompletedTask);
            Check(((TextBlock)((MenuItem)menu.Items[7]).Header).Text == "Переименованное", "Preset submenu uses stale vote titles");
            var firstCount = runtime.Voting.SnapshotFor(orderBefore[0]).Options.Count;
            await window.AddTemplateAsync(second, thirdCollection); window.Rows.Last().Command = "!добавлено-в-третье";
            await Wait(() => runtime.Voting.SnapshotFor(thirdCollection).Options.Count == 3 && !Field<bool>(window, "_dirty"));
            Check(runtime.Voting.SnapshotFor(orderBefore[0]).Options.Count == firstCount && runtime.Voting.SnapshotFor(thirdCollection).Total == 333 &&
                runtime.Voting.SnapshotFor(thirdCollection).Options.Last().Preset!.DisplayName == second.DisplayName,
                "Adding a preset to the third vote modified the first vote or lost progress");
            var afterNextRestore = new VotingCoordinator(new JsonVotingStore(paths)); await afterNextRestore.InitializeAsync();
            Check(afterNextRestore.Collections.Count == 3 && afterNextRestore.Automation.AutomaticDraw &&
                afterNextRestore.Automation.AutoNextCollection && afterNextRestore.SnapshotFor(thirdCollection).Total == 333,
                "JSON restart lost multiple collection settings or active selection");
            var manual = runtime.Voting.Automation; manual.AutomaticDraw = manual.AutomaticWinnerEffect = manual.AutoNextCollection = false;
            await runtime.Voting.SaveAutomationAsync(manual);
            await runtime.Voting.SetCollectionActiveAsync(thirdCollection, true, "test-profile");
            await runtime.Voting.RecordAsync(new DonationEvent { Id = "reset-winner", Amount = 667, Message = "!третье", CreatedAt = DateTimeOffset.UtcNow }, "test-profile");
            var resetWinner = await runtime.BeginVotingDrawAsync(collectionId: thirdCollection);
            var queuedBeforeReset = (await new JsonPendingDispatchStore(paths).LoadAsync()).Count;
            await window.ResetSelectedAsync(); await runtime.FinishVotingDrawAsync();
            await Wait(() => capture.Wheel.Round is { Total: 0, Winner: null } current && current.Id != resetWinner.Id);
            Check(!capture.Wheel.HasAnimatedProperties, "Previous round animation survived a progress reset");
            CheckCaptureIdentity();
            await runtime.SubmitVotingWinnerAsync(roundId: resetWinner.Id);
            await Wait(() => Equals(Field<Button>(window, "_spin").Content, "Крутить"));
            Check(runtime.Voting.PendingWinners.All(round => round.CollectionId != thirdCollection) && !Field<Button>(window, "_spin").IsEnabled &&
                (await new JsonPendingDispatchStore(paths).LoadAsync()).Count == queuedBeforeReset,
                "Reset left an old winner button or enqueued the cancelled result after animation");
            Layout(root, 1300, 690); CheckLayout(window, root); Save(root, Path.Combine(output, "voting-after-reset.png"));
            Check(Field<Button>(window, "_delete").Visibility == Visibility.Visible && VotingWindow.DeleteConfirmation(runtime.Voting.SnapshotFor(thirdCollection)).Contains("Другие голосования"),
                "Delete vote control or scoped confirmation missing");
            await window.DeleteSelectedAsync();
            await Wait(() => capture.Wheel.Round is null);
            CheckCaptureIdentity();
            Check(runtime.Voting.Collections.Count == 2 && !runtime.Voting.Automation.Enabled, "Deleting the active vote did not stop collection");
            while (runtime.Voting.Collections.Count > 0) await window.DeleteSelectedAsync();
            Check(Field<FrameworkElement>(window, "_generalPanel").Visibility == Visibility.Visible && Field<Button>(window, "_obs").IsEnabled,
                "The universal OBS window must remain available even without configured votes");
            Layout(root, 1300, 690); Save(root, Path.Combine(output, "voting-empty.png"));
            Invoke(Find<Button>(root).First(button => Equals(button.Content, "+ Добавить")));
            await Wait(() => runtime.Voting.Collections.Count == 1 && !Field<bool>(window, "_busy"));
            Check(!runtime.Voting.Collections[0].IsActive && runtime.Voting.Collections[0].Target == 1000 &&
                runtime.Voting.Collections[0].MoneyGoal && !runtime.Voting.Collections[0].TimeGoal, "New default must be 1000 RUB with money ON and time OFF");
            await CheckAutosaveAsync(window, runtime, root, paths, output);
            await CheckYouTubeSettingsAsync(window, runtime, root, obs, output);
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                foreach (var deleting in new[] { false, true })
                {
                    var dialog = new VotingConfirmationWindow(deleting ? "Удалить голосование" : "Сбросить прогресс",
                        deleting ? VotingWindow.DeleteConfirmation(runtime.Voting.Collections[0]) : VotingWindow.ResetConfirmation, deleting ? "Удалить" : "Сбросить");
                    var dialogRoot = (FrameworkElement)dialog.Content;
                    dialogRoot.Measure(new Size(520, double.PositiveInfinity)); Layout(dialogRoot, 520, Math.Ceiling(dialogRoot.DesiredSize.Height));
                    Check(Find<Button>(dialogRoot).Single(button => Equals(button.Content, "Отмена")) is { IsDefault: true, IsCancel: true },
                        "Confirmation must default to cancel rather than destructive action");
                    Check(Find<Button>(dialogRoot).Single(button => Equals(button.Content, deleting ? "Удалить" : "Сбросить")).Style ==
                        (Style)dialog.FindResource("DangerButtonStyle"), "Confirmation is not themed");
                    Save(dialogRoot, Path.Combine(output, $"voting-confirm-{(deleting ? "delete" : "reset")}-{(light ? "light" : "dark")}.png")); dialog.Close();
                }
            }
            Check(string.IsNullOrWhiteSpace(trace.ToString()), "Binding errors: " + trace);
            var closingId = Field<string>(window, "_selectedId");
            Field<TextBox>(window, "_target").Text = "4700"; window.Close();
            await Wait(() => Field<bool>(window, "_closed"));
            Check(runtime.Voting.SnapshotFor(closingId).Target == 4700, "Closing before debounce did not flush the final edit");
            CheckCaptureIdentity();
            draft = runtime.Voting.SnapshotFor(closingId); draft.Title = "Новое текущее голосование";
            draft.Options = [new VotingOption { Command = "!active", Preset = first }];
            await runtime.Voting.SaveSettingsAsync(draft);
            await runtime.Voting.SetCollectionActiveAsync(closingId, true, "test-profile");
            await Wait(() => capture.Wheel.Round?.Title == draft.Title);
            CheckCaptureIdentity();
            var reopened = new VotingWindow(runtime, captureController);
            reopened.Close(); await Wait(() => Field<bool>(reopened, "_closed"));
            await runtime.Voting.RecordAsync(new DonationEvent { Id = "closed-editor-obs", Amount = 19, Message = "!active", CreatedAt = DateTimeOffset.UtcNow }, "test-profile");
            await Wait(() => capture.Wheel.Round?.Total == 19);
            CheckCaptureIdentity();
            capture.Close();
            var replacement = captureController.GetOrCreateWindow();
            Check(!ReferenceEquals(capture, replacement) && replacement.Wheel.Round?.Total == 19 && replacement.Title == VotingObsWindow.CaptureTitle,
                "Explicitly closing and reopening OBS did not restore the active vote");
            var replacementClosed = false; replacement.Closed += (_, _) => replacementClosed = true;
            captureController.Dispose();
            Check(replacementClosed, "Application shutdown did not close the OBS window");
        }
        finally
        {
            window.Close(); obs.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); ThemeManager.Apply(false);
        }
    }

    private static async Task CheckAutosaveAsync(VotingWindow window, BridgeRuntime runtime, FrameworkElement root, AppPaths paths, string output)
    {
        var id = runtime.Voting.Collections.Single().CollectionId; var target = Field<TextBox>(window, "_target");
        target.Text = "2"; await Task.Delay(60); target.Text = "23"; await Task.Delay(60); target.Text = "2300"; target.CaretIndex = 2;
        Check(runtime.Voting.SnapshotFor(id).Target == 1000, "Autosave writes on every keystroke instead of debouncing");
        await Wait(() => runtime.Voting.SnapshotFor(id).Target == 2300 && !Field<bool>(window, "_dirty"));
        Check(target.Text == "2300" && target.CaretIndex == 2 && Field<TextBlock>(window, "_autosaveStatus").Text.Contains("сохранены автоматически"), "Autosave reset the caret/text or failed to confirm the edited field");
        target.Text = string.Empty; await Task.Delay(850);
        Check(runtime.Voting.SnapshotFor(id).Target == 2300 && target.Text.Length == 0 && Field<bool>(window, "_dirty") &&
            Field<TextBlock>(window, "_autosaveStatus").Text.Contains("Пока не сохранено"), "Partial invalid input overwrote the last valid setting or lost typed text");
        Layout(root, 1300, 690); Save(root, Path.Combine(output, "voting-autosave-validation.png"));
        target.Text = "2600";
        await Wait(() => runtime.Voting.SnapshotFor(id).Target == 2600 && !Field<bool>(window, "_dirty"));
        window.Rows.Clear();
        var definition = runtime.Settings.Actions[0];
        await window.AddTemplateAsync(definition, id); window.Rows[0].Command = "!autosave-a";
        await window.AddTemplateAsync(definition, id); window.Rows[1].Command = "!autosave-b";
        await Wait(() => runtime.Voting.SnapshotFor(id).Options.Count == 2 && !Field<bool>(window, "_dirty"));
        await runtime.Voting.SetCollectionActiveAsync(id, true, "test-profile");
        window.Rows[0].Command = "!autosave-new";
        await runtime.Voting.RecordAsync(new DonationEvent { Id = "autosave-live-donation", Amount = 321, Message = "!autosave-a", CreatedAt = DateTimeOffset.UtcNow }, "test-profile");
        await Wait(() => runtime.Voting.SnapshotFor(id).Options[0].Command == "!autosave-new" && !Field<bool>(window, "_dirty"));
        Check(runtime.Voting.SnapshotFor(id).Total == 321, "Autosave overwrote money received during the edit");
        var time = Field<CheckBox>(window, "_time"); time.IsChecked = true;
        await Wait(() => runtime.Voting.SnapshotFor(id).TimeGoal && !Field<bool>(window, "_dirty"));
        var timerEditedAt = DateTimeOffset.UtcNow;
        Field<TextBox>(window, "_minutes").Text = "3";
        await Wait(() => runtime.Voting.SnapshotFor(id).DurationSeconds == 180 && !Field<bool>(window, "_dirty"));
        var timer = runtime.Voting.SnapshotFor(id);
        Check(timer.Deadline >= timerEditedAt.AddMinutes(3) && timer.Deadline <= DateTimeOffset.UtcNow.AddMinutes(3) &&
            timer.Total == 321 && VotingWindow.RemainingText(timer) == "Осталось 03:00",
            "Minutes autosave must restart the full countdown and preserve current funds");
        time.IsChecked = false;
        await Wait(() => !runtime.Voting.SnapshotFor(id).TimeGoal && !Field<bool>(window, "_dirty"));
        var gate = Field<SemaphoreSlim>(window, "_saveGate"); await gate.WaitAsync();
        try
        {
            target.Text = "2700"; await Wait(() => Field<bool>(window, "_autosaving")); target.Text = "2800";
        }
        finally { gate.Release(); }
        await Wait(() => runtime.Voting.SnapshotFor(id).Target == 2800 && !Field<bool>(window, "_dirty"));
        var store = typeof(VotingCoordinator).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(field => field.FieldType == typeof(IVotingStore)).GetValue(runtime.Voting)!;
        var diskGate = Field<SemaphoreSlim>(store, "_gate"); await diskGate.WaitAsync();
        var observed = new List<decimal>(); void Saved() => observed.Add(runtime.Voting.SnapshotFor(id).Target);
        runtime.Voting.Changed += Saved;
        try
        {
            target.Text = "3900";
            await Wait(() => Field<bool>(window, "_autosaving") && Field<SemaphoreSlim>(runtime.Voting, "_gate").CurrentCount == 0);
            target.Text = "4000";
        }
        finally { diskGate.Release(); }
        await Wait(() => runtime.Voting.SnapshotFor(id).Target == 4000 && !Field<bool>(window, "_dirty"));
        runtime.Voting.Changed -= Saved;
        Check(observed.Contains(3900) && observed.Contains(4000) && target.Text == "4000", "New edit made during disk write was marked clean and lost");
        using (var blocked = new FileStream(Path.Combine(paths.RootDirectory, "voting.json.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            target.Text = "4200";
            await Wait(() => Field<TextBlock>(window, "_autosaveStatus").Text.StartsWith("Не удалось сохранить:"));
            Check(runtime.Voting.SnapshotFor(id).Target == 4000 && Field<bool>(window, "_dirty") && target.Text == "4200",
                "Failed autosave changed durable settings or discarded the edit");
        }
        await Wait(() => runtime.Voting.SnapshotFor(id).Target == 4200 && !Field<bool>(window, "_dirty"));
        target.Text = "3100";
        Invoke(Find<Button>(root).First(button => Equals(button.Content, "Общие настройки")));
        await Wait(() => Field<FrameworkElement>(window, "_generalPanel").Visibility == Visibility.Visible && !Field<bool>(window, "_busy"));
        Check(runtime.Voting.SnapshotFor(id).Target == 3100, "Page switch did not flush before debounce");
        Field<CheckBox>(window, "_winnerAuto").IsChecked = false;
        await Wait(() => !runtime.Voting.Automation.AutomaticWinnerEffect && !Field<bool>(window, "_dirty"));
        Field<ComboBox>(window, "_order").SelectedIndex = 1;
        await Wait(() => runtime.Voting.Automation.Order == VotingSequenceOrder.Random && !Field<bool>(window, "_dirty"));
        Invoke(Find<Button>(root).First(button => Equals(button.Tag, id)));
        await Wait(() => Field<FrameworkElement>(window, "_collectionPanel").Visibility == Visibility.Visible && !Field<bool>(window, "_busy"));
        target.Text = "3600";
        Field<CheckBox>(window, "_time").IsChecked = true;
        Field<TextBox>(window, "_minutes").Text = "10";
        Invoke(Find<Button>(root).First(button => Equals(button.Content, "+ Добавить")));
        await Wait(() => runtime.Voting.Collections.Count == 2 && !Field<bool>(window, "_busy"));
        var created = runtime.Voting.Collections.Single(round => round.CollectionId != id);
        Check(created.Target == 3600 && created.MoneyGoal && created.TimeGoal && created.DurationSeconds == 600 && !created.IsActive && created.Options.Count == 0,
            "New vote did not inherit the latest source amount and ten-minute timer before debounce");
        var reloaded = new VotingCoordinator(new JsonVotingStore(paths)); await reloaded.InitializeAsync();
        Check(reloaded.SnapshotFor(id).Target == 3600 && reloaded.SnapshotFor(id).Total == 321 && reloaded.SnapshotFor(created.CollectionId).Target == 3600,
            "Autosave did not durably persist both settings and money");
        Layout(root, 1300, 690); CheckLayout(window, root); Save(root, Path.Combine(output, "voting-autosave-defaults.png"));
    }

    private static async Task CheckYouTubeSettingsAsync(VotingWindow window, BridgeRuntime runtime, FrameworkElement root, VotingObsWindow obs, string output)
    {
        var id = Field<string>(window, "_selectedId");
        await window.AddTemplateAsync(runtime.Settings.Actions[0], id); window.Rows[0].Command = "!кров";
        await window.AddTemplateAsync(runtime.Settings.Actions[1], id); window.Rows[1].Command = "!псина";
        var chat = Field<CheckBox>(window, "_youTubeVotes");
        var donation = Field<CheckBox>(window, "_donationVotes");
        var weight = Field<TextBox>(window, "_youTubeWeight");
        var change = Field<CheckBox>(window, "_youTubeChange");
        Check(!chat.IsChecked.GetValueOrDefault() && donation.IsChecked == true && weight.Text == "1" && change.IsChecked == false,
            "Existing votes must keep donation-only defaults");
        chat.IsChecked = true; donation.IsChecked = false; weight.Text = "2,5"; change.IsChecked = true;
        await Wait(() => runtime.Voting.SnapshotFor(id) is { AcceptDonationVotes: false, AcceptYouTubeVotes: true, YouTubeVoteWeight: 2.5m, AllowYouTubeVoteChange: true } && !Field<bool>(window, "_dirty"));
        Check(weight.IsEnabled && change.IsEnabled && Field<TextBlock>(window, "_youTubeChangeLabel").Text == "Да", "Chat controls or yes/no label failed");
        weight.Text = "0";
        await Wait(() => Field<TextBlock>(window, "_autosaveStatus").Text.Contains("Пока не сохранено"));
        Check(runtime.Voting.SnapshotFor(id).YouTubeVoteWeight == 2.5m, "Invalid weight overwrote valid state");
        weight.Text = "2,5"; await Wait(() => !Field<bool>(window, "_dirty"));
        chat.IsChecked = false;
        await Wait(() => Field<TextBlock>(window, "_autosaveStatus").Text.Contains("Пока не сохранено"));
        Check(runtime.Voting.SnapshotFor(id).AcceptYouTubeVotes, "Both sources switched off without validation");
        chat.IsChecked = true; donation.IsChecked = true;
        await Wait(() => !Field<bool>(window, "_dirty"));
        await runtime.Voting.SetCollectionActiveAsync(id, true, "test-profile");
        var round = runtime.Voting.SnapshotFor(id);
        await runtime.Voting.RecordYouTubeAsync(round.Id, "test-profile", [new("ui-vote", "channel", "!кров", DateTimeOffset.UtcNow)]);
        weight.Text = "5";
        await Wait(() => runtime.Voting.SnapshotFor(id).YouTubeVoteWeight == 5 && !Field<bool>(window, "_dirty"));
        Check(runtime.Voting.SnapshotFor(id).Total == 2.5m && runtime.Voting.SnapshotFor(id).YouTubeParticipants.Count == 1,
            "UI autosave erased chat weight/ledger");
        foreach (var light in new[] { false, true })
        {
            ThemeManager.Apply(light); Layout(root, 1300, 740); CheckLayout(window, root);
            Save(root, Path.Combine(output, "voting-youtube-" + (light ? "light" : "dark") + ".png"));
        }
        ThemeManager.Apply(false); Layout(root, 1084, 572); CheckLayout(window, root);
        Save(root, Path.Combine(output, "voting-youtube-minimum.png"));
        round = runtime.Voting.SnapshotFor(id); round.Currency = "EUR"; round.Target = 10;
        round.Phase = VotingPhase.Paused; round.TimeGoal = true; round.PausedAt = DateTimeOffset.UtcNow;
        round.Deadline = round.PausedAt.Value.AddSeconds(90);
        var obsRoot = (FrameworkElement)obs.Content;
        foreach (var format in Enum.GetValues<VotingHudFormat>())
        {
            obs.Update(round, false, new VotingAutomation { GameHudFormat = format }); Layout(obsRoot, 784, 460);
            var value = Field<TextBlock>(obs, "_total").Text;
            Check(value == (format == VotingHudFormat.Amount ? "2,5 EUR / 10 EUR" : format == VotingHudFormat.Timer ? "01:30" : "25%"), "OBS ignored selected format/currency");
            Check(format != VotingHudFormat.Timer || Field<TextBlock>(obs, "_time").Text == "", "Timer was duplicated");
            SaveOnWhite(obsRoot, Path.Combine(output, "voting-obs-" + format + ".png"));
            Layout(obsRoot, 300, 150);
            Check(Field<TextBlock>(obs, "_time").Text == "Осталось 01:30" &&
                Field<DockPanel>(obs, "_summary").Visibility == Visibility.Visible &&
                Field<TextBlock>(obs, "_total").Visibility == Visibility.Collapsed &&
                Find<ProgressBar>(obsRoot).Single().Visibility == Visibility.Collapsed,
                "Compact OBS must keep a single timer without the progress bar/value");
            SaveOnWhite(obsRoot, Path.Combine(output, "voting-obs-compact-" + format + ".png"));
        }
        obs.Update(round, false, new VotingAutomation { ShowGameHudCommands = false });
        Check(Field<ScrollViewer>(obs, "_list").Visibility == Visibility.Collapsed, "OBS command-list setting ignored");
        Layout(root, 1300, 740);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void CheckLayout(VotingWindow window, FrameworkElement root)
    {
        var commands = Field<Grid>(window, "_commandsPanel"); var settings = Field<Grid>(window, "_settings");
        var commandsAt = commands.TranslatePoint(new Point(), root); var settingsAt = settings.TranslatePoint(new Point(), root);
        Check(commandsAt.X + commands.ActualWidth < settingsAt.X && settingsAt.X + settings.ActualWidth <= root.ActualWidth,
            "Commands must be in center, settings on right, without overlap/clipping");
        var target = Field<TextBox>(window, "_target"); var minutes = Field<TextBox>(window, "_minutes"); var condition = Field<Button>(window, "_condition");
        Check(Math.Abs(target.TranslatePoint(new Point(), root).Y - minutes.TranslatePoint(new Point(), root).Y) < 1 &&
            target.ActualHeight == 36 && minutes.ActualHeight == 36 && condition.ActualHeight == 36, "Goal fields and condition button are misaligned");
        var spin = Field<Button>(window, "_spin");
        Check(spin.TranslatePoint(new Point(), root).X >= settingsAt.X, "Winner/spin button is outside the right settings column");
        var previousRight = -1d;
        foreach (var name in new[] { "_reset", "_delete", "_panelToggle" })
        {
            var button = Field<Button>(window, name); var at = button.TranslatePoint(new Point(), root);
            Check(at.X >= previousRight && at.Y < settingsAt.Y && at.X + button.ActualWidth <= root.ActualWidth + 1,
                "Header order must be reset, delete, panel toggle without overlap or clipping");
            previousRight = at.X + button.ActualWidth;
        }
        var title = Field<TextBlock>(window, "_title");
        Check(title.TranslatePoint(new Point(title.ActualWidth, 0), root).X <= Field<Button>(window, "_reset").TranslatePoint(new Point(), root).X,
            "Title overlaps the header actions");
        var card = Field<FrameworkElement>(window, "_settingsCard"); var cardAt = card.TranslatePoint(new Point(), root);
        Check(Math.Abs(previousRight - cardAt.X - card.ActualWidth) < 1, "Header actions are not aligned to the right edge of the settings card");
        Check(Find<Button>(Field<FrameworkElement>(window, "_generalPanel")).Contains(Field<Button>(window, "_obs")) &&
            !Find<Button>(Field<Grid>(window, "_headerActions")).Contains(Field<Button>(window, "_obs")), "OBS must appear only in general settings");
        Check(Field<Button>(window, "_collectionToggle").TranslatePoint(new Point(), root).X < Field<Button>(window, "_rename").TranslatePoint(new Point(), root).X &&
            Field<Button>(window, "_rename").TranslatePoint(new Point(), root).X < Field<TextBlock>(window, "_title").TranslatePoint(new Point(), root).X,
            "Heading order must be status, edit, title");
        foreach (var input in Find<TextBox>(root).Where(input => input.ActualHeight == 36 && input.Text.Length > 0))
        {
            var textRect = input.GetRectFromCharacterIndex(0);
            Check(textRect.IsEmpty || textRect.Top >= 0 && textRect.Bottom <= input.ActualHeight - 1, "Text is clipped vertically inside a voting field");
        }
    }
    private static void CheckObsPresentation(string output)
    {
        var now = DateTimeOffset.UtcNow;
        var round = new VotingRound { Title = "Крутим рулетку?", Phase = VotingPhase.Paused, MoneyGoal = true,
            Target = 100, TimeGoal = true, DurationSeconds = 60, PausedAt = now, Deadline = now.AddSeconds(19),
            Options = [
                new VotingOption { Command = "!да", Amount = 35.19m, Preset = new ActionDefinition { DisplayName = "Крутить рулетку" } },
                new VotingOption { Command = "!имя", Amount = 33.81m, Preset = new ActionDefinition { DisplayName = "Сменить имя героя на имя донатчика" } }
            ] };
        var obs = new VotingObsWindow();
        try
        {
            obs.Update(round, false); var root = (FrameworkElement)obs.Content;
            foreach (var size in new[] { new Size(800, 508), new Size(340, 340), new Size(800, 190), new Size(800, 508) })
            {
                Layout(root, size.Width, size.Height);
                var compact = size.Width == 340 || size.Height == 190;
                Check(obs.IsCompact == compact && Field<TextBlock>(obs, "_title").Visibility == Visibility.Visible &&
                    Field<TextBlock>(obs, "_time").Text == "Осталось 00:19", "Resizing OBS lost title/timer");
                Check(Field<ProgressBar>(obs, "_progress").Visibility == (compact ? Visibility.Collapsed : Visibility.Visible),
                    "OBS progress graphic must follow ring visibility on both resize directions");
                foreach (var option in round.Options)
                    Check(Find<TextBlock>(root).Any(label => label.Text == option.Preset!.DisplayName && label.Visibility == Visibility.Visible),
                        "Resizing OBS lost the effect name");
                var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
                {
                    var bounds = new Rect(0, 0, size.Width, size.Height);
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), null, bounds);
                    drawing.DrawImage(Bitmap(root), bounds);
                }
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"obs-preview-{size.Width}x{size.Height}.png")); encoder.Save(file);
            }
            var wheel = new VotingWheelControl { Round = round }; Layout(wheel, 320, 320);
            foreach (var amounts in new[] { (35.19m, 33.81m), (69m, 0m), (0m, 0m) })
            {
                round.Options[0].Amount = amounts.Item1; round.Options[1].Amount = amounts.Item2;
                wheel.InvalidateVisual(); wheel.UpdateLayout();
                var bitmap = Bitmap(wheel); var pixels = new byte[320 * 320 * 4]; bitmap.CopyPixels(pixels, 320 * 4, 0);
                for (var y = 70; y <= 250; y += 10)
                    for (var x = 70; x <= 250; x += 10)
                        if ((x - 160) * (x - 160) + (y - 160) * (y - 160) < 90 * 90)
                            Check(pixels[(y * 320 + x) * 4 + 3] == 0, "A ring sector filled the transparent centre");
                if (amounts.Item2 > 0)
                {
                    var boundary = .51 * Math.PI * 2; var middleRadius = 320 * .43 * .87;
                    var x = (int)(160 + Math.Sin(boundary) * middleRadius);
                    var y = (int)(160 - Math.Cos(boundary) * middleRadius); var offset = (y * 320 + x) * 4;
                    Check(pixels[offset] + pixels[offset + 1] + pixels[offset + 2] < 210 && pixels[offset + 3] > 220,
                        "Sector divider is missing");
                }
            }
        }
        finally { obs.Close(); }
    }

    private static void Invoke(Button button)
    { var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke(); }
    private static async Task Wait(Func<bool> predicate)
    { var until = DateTime.UtcNow.AddSeconds(5); while (!predicate()) { if (DateTime.UtcNow > until) throw new InvalidOperationException("Timed out waiting for voting UI"); await Task.Delay(10); } await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Layout(FrameworkElement root, double width, double height)
    { root.Margin = new Thickness(0); root.Width = width; root.Height = height; root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static RenderTargetBitmap Bitmap(FrameworkElement root)
    { var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root); return bitmap; }
    private static void Save(FrameworkElement root, string path)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap(root))); using var output = File.Create(path); encoder.Save(output); }
    private static void SaveOnWhite(FrameworkElement root, string path)
    {
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
        { var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight); drawing.DrawRectangle(Brushes.White, null, bounds); drawing.DrawImage(Bitmap(root), bounds); }
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    { if (root is T match) yield return match; for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
