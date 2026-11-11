using System.Collections.ObjectModel;
using System.IO;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class BrowserEditorTests
{
    public static async Task RunAsync()
    {
        var actions = new ObservableCollection<ActionViewModel>(DefaultActionCatalog.CreatePresets().Select(action => new ActionViewModel(action, true)));
        var browser = new ActionBrowserViewModel(actions);
        var categoryCounts = new[] { ("all", 41), ("npc_mutants", 8), ("simulation", 4), ("player_state", 12), ("metamorphosis", 0), ("inventory", 8), ("world", 9) };
        foreach (var (category, count) in categoryCounts)
        {
            browser.SelectCategory(category);
            Check(browser.VisibleActions.Cast<object>().Count() == count, "Wrong category membership: " + category);
        }
        Check(categoryCounts.Where(item => item.Item1 != "all").Sum(item => item.Item2) == 41,
            "Some presets disappeared from the specific categories.");
        var npc = actions.Single(action => action.HandlerId == "spawn_random_hostile_squad");
        npc.Model.CategoryId = "npc_mutants"; // A user preset saved before the Simulation tab existed.
        browser.SelectCategory("npc_mutants");
        Check(!browser.VisibleActions.Cast<ActionViewModel>().Any(action => action.Id == npc.Id),
            "A legacy squad copy still appears among direct NPC/mutant spawns.");
        browser.SelectCategory("simulation");
        Check(browser.VisibleActions.Cast<ActionViewModel>().Any(action => action.Id == npc.Id),
            "A legacy squad copy was not migrated to the Simulation filter.");
        var randomNpc = actions.Single(action => action.HandlerId == "spawn_random_npcs");
        randomNpc.Model.CategoryId = "simulation"; // A saved copy from the previous local build.
        Check(!browser.VisibleActions.Cast<ActionViewModel>().Any(action => action.Id == randomNpc.Id),
            "A direct-spawn random NPC copy remained in Simulation.");
        browser.SelectCategory("npc_mutants");
        Check(browser.VisibleActions.Cast<ActionViewModel>().Any(action => action.Id == randomNpc.Id),
            "A saved random NPC copy did not move to NPC and mutants.");
        var selectedSquads = actions.Single(action => action.HandlerId == "spawn_npc_squads");
        Check(selectedSquads.IsSpawnGroupBundle && selectedSquads.SpawnGroupRows.Count == 1 &&
            selectedSquads.Parameters.Select(parameter => parameter.Definition.Key)
                .SequenceEqual(new[] { "radius_meters", "force_hostile" }),
            "Selected NPC squads do not reuse the compact group editor with radius and hostility controls.");
        npc.Parameters.Single(parameter => parameter.Definition.Key == "radius_meters").Value = "42";
        npc.AddTrigger(new TriggerRule { Amount = 777, Currency = "RUB" });
        var activeBrowser = new ActionBrowserViewModel(actions);
        activeBrowser.SelectCategory("inventory");
        browser.SelectCategory("simulation");
        Check(ReferenceEquals(npc, browser.VisibleActions.Cast<ActionViewModel>().Single(action => action.Id == npc.Id)) &&
            npc.Model.Parameters["radius_meters"] == "42" && npc.Model.Triggers.Last().Amount == 777,
            "Filtering recreated the editable draft or lost its price.");
        Check(activeBrowser.SelectedCategory.Id == "inventory", "Independent filters changed each other.");
        browser.SelectCategory("all");
        browser.SearchText = "  " + npc.DisplayName.ToUpperInvariant() + "  ";
        Check(ReferenceEquals(browser.VisibleActions.Cast<ActionViewModel>().Single(), npc),
            "Search must trim whitespace, ignore case and retain the actual editable instance.");
        browser.SelectCategory("inventory");
        Check(browser.VisibleActions.IsEmpty, "Search ignored the category filter.");
        Check(activeBrowser.SearchText.Length == 0 && activeBrowser.SelectedCategory.Id == "inventory",
            "One browser query unexpectedly mutated another browser.");
        browser.SearchText = "no such template 670392";
        Check(browser.VisibleActions.IsEmpty && actions.Count == 41 && npc.Model.Triggers.Last().Amount == 777,
            "No-match search changed actual actions or their prices.");
        browser.SearchText = "   ";
        browser.SelectCategory("invalid-category");
        Check(browser.SelectedCategory.Id == "all" && browser.VisibleActions.Cast<object>().Count() == 41,
            "Unknown saved category did not fall back to All.");
        browser.SelectCategory("player", "inventory");
        Check(browser.SelectedCategory.Id == "inventory", "Legacy Player inventory filter was not migrated.");
        browser.SelectCategory("player", "state");
        Check(browser.SelectedCategory.Id == "player_state", "Legacy Player state filter was not migrated.");
        browser.Columns = 4;
        Check(browser.EffectiveColumns == 4, "Manual density was ignored.");
        browser.AutomaticColumns = true;
        Check(browser.EffectiveColumns == 0, "Auto mode was not enabled.");
        activeBrowser.Columns = 3;
        Check(activeBrowser.EffectiveColumns == 3 && browser.Columns == 4 && browser.AutomaticColumns,
            "Active grid density changed the preset grid.");
        activeBrowser.AutomaticColumns = true;
        Check(activeBrowser.EffectiveColumns == 0 && browser.Columns == 4, "Active Auto mode changed preset density.");
        activeBrowser.CompactMode = true;
        Check(activeBrowser.CompactMode && !browser.CompactMode,
            "Compact row mode leaked into another preset browser.");
        Check(ActionBrowserViewModel.ViewOptions.Select(option => option.Label).SequenceEqual(new[]
            { "Компактный список", "Сетка: авто", "1 колонка", "2 колонки", "3 колонки", "4 колонки", "5 колонок" }),
            "The unified view options changed their order or lost five columns.");
        Check(activeBrowser.SelectedView.Columns == -1 && browser.SelectedView.Columns == 0,
            "Legacy compact/auto preferences are not represented in the unified selector.");
        var preferenceChanges = 0;
        activeBrowser.PreferencesChanged += () => preferenceChanges++;
        foreach (var option in ActionBrowserViewModel.ViewOptions.Skip(1))
        {
            var previousChanges = preferenceChanges;
            activeBrowser.SelectedView = option;
            Check(!activeBrowser.CompactMode && activeBrowser.EffectiveColumns == option.Columns &&
                activeBrowser.SelectedView == option && preferenceChanges == previousChanges + 1,
                "A unified view change did not save exactly one complete state.");
        }
        activeBrowser.SelectedView = ActionBrowserViewModel.ViewOptions[0];
        Check(activeBrowser.CompactMode && activeBrowser.Columns == 5 && browser.SelectedView.Columns == 0,
            "Compact mode lost the previous grid width or changed another section.");
        activeBrowser.Columns = 999;
        Check(activeBrowser.Columns == 5, "The supported grid range is not capped at five columns.");

        var groupedActions = new ObservableCollection<ActionViewModel>();
        for (var index = 0; index < 3; index++)
        {
            var groupedModel = actions[index].Model.CloneAsNewInstance();
            groupedModel.Triggers.Clear();
            groupedModel.Triggers.Add(new TriggerRule
            {
                Amount = index < 2 ? 100 : 250,
                Currency = "RUB",
                Comparator = TriggerComparator.Exact
            });
            groupedActions.Add(new ActionViewModel(groupedModel, displayOrdinal: index + 1));
        }
        var groupedBrowser = new ActionBrowserViewModel(groupedActions, groupEqualPrices: true);
        var priceGroup = groupedBrowser.VisibleEntries[0].Group;
        Check(groupedBrowser.VisibleEntries.Count == 2 &&
            priceGroup is { Actions.Count: 2, DisplayOrdinal: 1 } &&
            priceGroup.Actions.All(action => action.VisualDisplayOrdinalLabel.Length == 0) &&
            groupedBrowser.VisibleEntries[1].Action is { VisualDisplayOrdinalLabel: "№2" } &&
            groupedActions.Select(action => action.DisplayOrdinalLabel)
                .SequenceEqual(new[] { "№1", "№2", "№3" }),
            "Equal-price active presets were not placed into one visual group with one display ordinal.");
        Check(priceGroup is not null && ReferenceEquals(priceGroup.Actions[0], groupedActions[0]) &&
            ReferenceEquals(priceGroup.Actions[1], groupedActions[1]),
            "Visual grouping recreated old active presets instead of wrapping the existing instances.");
        var existingPriceGroup = priceGroup ?? throw new InvalidOperationException("Price group was not built.");
        groupedBrowser.RenamePriceGroup(existingPriceGroup, "Большой совместный эффект");
        Check(existingPriceGroup.DisplayName == "Большой совместный эффект" &&
            groupedBrowser.PriceGroupNames[existingPriceGroup.Key] == "Большой совместный эффект",
            "A local active-group name was not retained by the browser projection.");
        var exportActions = groupedBrowser.BuildExportActions();
        Check(exportActions.Count == 2 && exportActions[0].DisplayName == "Большой совместный эффект" &&
            exportActions[1].DisplayName == groupedActions[2].DisplayName,
            "Stream export did not collapse an equal-price group to its local group name.");
        groupedBrowser.SearchText = groupedActions[0].DisplayName;
        Check(groupedBrowser.VisibleActions.Cast<ActionViewModel>().Contains(groupedActions[0]) &&
            groupedBrowser.BuildExportActions().Count == exportActions.Count && groupedActions.Count == 3,
            "Searching inside grouped active actions changed the real effect group/export.");
        groupedBrowser.SearchText = string.Empty;
        var restoredGroupedBrowser = new ActionBrowserViewModel(groupedActions, groupEqualPrices: true);
        restoredGroupedBrowser.RestorePriceGroupNames(groupedBrowser.PriceGroupNames);
        Check(restoredGroupedBrowser.VisibleEntries[0].Group?.DisplayName == "Большой совместный эффект",
            "A saved active-group name was not restored.");
        groupedActions[1].Triggers[0].Model.Amount = 175;
        groupedActions[1].Triggers[0].Refresh();
        Check(groupedBrowser.VisibleEntries.Count == 3 &&
            groupedBrowser.VisibleEntries.All(entry => !entry.IsGroup) &&
            groupedBrowser.VisibleEntries.Select(entry => entry.Action!.VisualDisplayOrdinalLabel)
                .SequenceEqual(new[] { "№1", "№2", "№3" }),
            "Price edits did not rebuild the visual active grouping.");

        npc.AddTrigger(new TriggerRule { Amount = 888, Currency = "RUB" });
        npc.AddTrigger(new TriggerRule { Amount = 999, Currency = "USD" });
        npc.AddTrigger(new TriggerRule { Amount = 1_111, Currency = "EUR" });
        Check(npc.HasTriggers && npc.Triggers.Count == 4 && npc.FirstThreeTriggers.Count == 3 &&
            npc.FirstThreeTriggers.Select(trigger => trigger.Model.Amount)
                .SequenceEqual(new decimal[] { 777, 888, 999 }),
            "Compact user rows do not expose exactly the first three assigned prices.");

        var model = new ActionDefinition
        {
            Id = "qa-editor", HandlerId = "qa_editor", DisplayName = "Editor",
            ParameterDefinitions =
            [
                new() { Key = "selection", DisplayName = "Selection", Kind = ParameterKind.MultiChoice, Options = ["a", "b", "c", "d"], DefaultValue = "a,b,c,d", MinimumSelections = 1 },
                new() { Key = "count", DisplayName = "Count", Kind = ParameterKind.Integer, DefaultValue = "1", Minimum = 1, Maximum = 99 },
                new() { Key = "flag", DisplayName = "Flag", Kind = ParameterKind.Choice, Options = ["true", "false"], DefaultValue = "true" }
            ]
        };
        var source = new ActionViewModel(model);
        ActionDefinition? saved = null;
        var editor = new ActionEditorViewModel(source, draft => { saved = draft.Model.Clone(); return Task.CompletedTask; });
        foreach (var option in editor.Draft.Parameters[0].MultiChoiceOptions.Take(3)) option.IsSelected = false;
        editor.Draft.Parameters[1].Value = "";
        Check(!await editor.SaveAsync() && saved is null, "Invalid transient input closed/saved the editor.");
        editor.Draft.Parameters[1].Value = "42";
        editor.Draft.Parameters[2].BooleanValue = false;
        editor.Draft.CooldownText = "17";
        Check(source.Parameters[0].Value == "a,b,c,d" && source.Parameters[1].Value == "1" && source.Parameters[2].BooleanValue,
            "Editing the modal draft modified the source before Save (Cancel would lose data).");
        Check(await editor.SaveAsync() && saved is { CooldownSeconds: 17 } && saved.Parameters["selection"] == "d" &&
            saved.Parameters["count"] == "42" && saved.Parameters["flag"] == "false", "Batched popup edits were not saved together.");
        var failed = new ActionEditorViewModel(source, _ => throw new IOException("write failed"));
        Check(!await failed.SaveAsync() && failed.Error == "write failed" && !failed.IsSaving,
            "A failed save closed the modal or left controls stuck.");

        var liveNpcEntries = new[] { "sim_default_stalker_0", "sim_default_stalker_1", "sim_default_stalker_2" }
            .Select(section => new CatalogEntry
            {
                SectionId = section, Kind = CatalogEntryKind.Npc,
                DisplayName = section, Category = "stalker",
                Strength = CatalogStrength.Normal, Faction = "stalker",
                IsAvailable = true, QuarantineReason = QuarantineReason.None
            })
            .ToList();
        var liveNpcCatalog = new CatalogSnapshot(
            new CatalogRevision("session:npc-multichoice"), new string('e', 64), liveNpcEntries);
        var liveNpcState = new CatalogRuntimeState(
            CapabilitySet.Empty, liveNpcCatalog, true, "Живой каталог");
        var randomNpcModel = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "spawn_random_npcs");
        DynamicCatalogBinding.Apply(liveNpcCatalog, [randomNpcModel]);
        var randomNpcSource = new ActionViewModel(randomNpcModel, true, catalogState: liveNpcState);
        Check(randomNpcSource.Parameters.Single(parameter => parameter.Definition.Key == "npc_sections")
                .DisplayValue == "выбрано 3 из 3",
            "Default live NPC sections were not reflected in the card.");
        var npcEditor = new ActionEditorViewModel(randomNpcSource, draft =>
        {
            randomNpcSource.ApplyEditorDraft(draft);
            return Task.CompletedTask;
        });
        var npcDraftSelection = npcEditor.Draft.Parameters.Single(parameter =>
            parameter.Definition.Key == "npc_sections");
        npcDraftSelection.ClearCatalogSelectionCommand.Execute(null);
        npcDraftSelection.CatalogSearch = "stalker_1";
        npcDraftSelection.SelectAllCatalogSelectionCommand.Execute(null);
        Check(npcDraftSelection.Value == "sim_default_stalker_1",
            "Select all ignored the current catalog search filter.");
        npcDraftSelection.CatalogSearch = string.Empty;
        npcDraftSelection.SelectAllCatalogSelectionCommand.Execute(null);
        Check(npcDraftSelection.DisplayValue == "выбрано 3 из 3",
            "Select all did not select every visible catalog section.");
        npcDraftSelection.ClearCatalogSelectionCommand.Execute(null);
        npcDraftSelection.CatalogOptions.Single(option =>
            option.SectionId == "sim_default_stalker_2").IsSelected = true;
        Check(await npcEditor.SaveAsync() &&
            randomNpcSource.Model.Parameters["npc_sections"] == "sim_default_stalker_2" &&
            randomNpcSource.Parameters.Single(parameter => parameter.Definition.Key == "npc_sections")
                .DisplayValue == "выбрано 1 из 3" &&
            randomNpcSource.CanTest,
            "Saved NPC multi-selection was not synchronized into the card or Test availability.");
        var staleNpcModel = randomNpcSource.Model.Clone();
        staleNpcModel.Parameters["npc_sections"] = "sim_default_stalker_2,stale_scripted_npc";
        var staleNpcSource = new ActionViewModel(staleNpcModel, true, catalogState: liveNpcState);
        var staleSelection = staleNpcSource.Parameters.Single(parameter =>
            parameter.Definition.Key == "npc_sections");
        Check(staleSelection.HasHiddenCatalogSelections &&
            staleSelection.HiddenCatalogSummary.Contains("stale_scripted_npc") &&
            staleSelection.DisplayValue.Contains("скрыто 1") && !staleNpcSource.CanTest,
            "A stale hidden section was not surfaced as the cause of Test rejection.");
        staleSelection.RemoveHiddenCatalogSelectionsCommand.Execute(null);
        Check(staleSelection.Value == "sim_default_stalker_2" &&
            !staleSelection.HasHiddenCatalogSelections && staleNpcSource.CanTest,
            "Removing hidden sections did not retain the user's visible NPC selection.");
        var liveMutantEntries = new[] { "dog_normal", "bloodsucker_normal", "boar_normal" }
            .Select(section => new CatalogEntry
            {
                SectionId = section, Kind = CatalogEntryKind.Mutant,
                DisplayName = section, Category = "mutants",
                Strength = CatalogStrength.Normal, Faction = "monster",
                IsAvailable = true, QuarantineReason = QuarantineReason.None
            })
            .ToList();
        var liveMutantCatalog = new CatalogSnapshot(
            new CatalogRevision("session:mutant-multichoice"), new string('f', 64), liveMutantEntries);
        var liveMutantState = new CatalogRuntimeState(
            CapabilitySet.Empty, liveMutantCatalog, true, "Живой каталог");
        var randomMutantModel = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "spawn_random_mutants");
        DynamicCatalogBinding.Apply(liveMutantCatalog, [randomMutantModel]);
        var randomMutantSource = new ActionViewModel(randomMutantModel, true, catalogState: liveMutantState);
        var mutantEditor = new ActionEditorViewModel(randomMutantSource, draft =>
        {
            randomMutantSource.ApplyEditorDraft(draft);
            return Task.CompletedTask;
        });
        var mutantDraftSelection = mutantEditor.Draft.Parameters.Single(parameter =>
            parameter.Definition.Key == "mutant_sections");
        mutantDraftSelection.ClearCatalogSelectionCommand.Execute(null);
        mutantDraftSelection.CatalogOptions.Single(option => option.SectionId == "boar_normal")
            .IsSelected = true;
        Check(await mutantEditor.SaveAsync() &&
            randomMutantSource.Model.Parameters["mutant_sections"] == "boar_normal" &&
            randomMutantSource.Parameters.Single(parameter => parameter.Definition.Key == "mutant_sections")
                .DisplayValue == "выбрано 1 из 3" &&
            randomMutantSource.CanTest,
            "Saved mutant multi-selection reverted to the full live catalog.");

        var itemBundle = new ActionDefinition
        {
            Id = "qa-item-bundle", HandlerId = ItemBundleCodec.HandlerId,
            DisplayName = "Items",
            ParameterDefinitions =
            [
                new() { Key = "section_id", DisplayName = "Item", Kind = ParameterKind.Choice,
                    Options = ["item_a", "item_b", "item_c"], DefaultValue = "item_a" },
                new() { Key = "count", DisplayName = "Count", Kind = ParameterKind.Integer,
                    Minimum = 1, Maximum = 50, DefaultValue = "1" }
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = "item_a", ["count"] = "1"
            },
            ItemSpawns = [new ItemSpawnEntry { ItemId = "item_a", Count = 1 }]
        };
        var itemSource = new ActionViewModel(itemBundle);
        var itemEditor = new ActionEditorViewModel(itemSource, draft =>
        {
            itemSource.ApplyEditorDraft(draft);
            return Task.CompletedTask;
        });
        itemEditor.Draft.AddItemSpawnCommand.Execute(null);
        itemEditor.Draft.AddItemSpawnCommand.Execute(null);
        itemEditor.Draft.ItemSpawnRows[1].Picker.Value = "item_b";
        itemEditor.Draft.ItemSpawnRows[1].CountText = "2";
        itemEditor.Draft.ItemSpawnRows[2].Picker.Value = "item_c";
        itemEditor.Draft.ItemSpawnRows[2].CountText = "3";
        Check(await itemEditor.SaveAsync() && itemSource.ItemSpawnRows.Count == 3 &&
            itemSource.Model.ItemSpawns.Select(row => $"{row.ItemId}*{row.Count}")
                .SequenceEqual(["item_a*1", "item_b*2", "item_c*3"]),
            "Applying an item-bundle editor draft left the source view model on its old first row.");
        Check(itemSource.TryCommitPendingEdits(out var itemCommitError) &&
            itemSource.Model.EffectiveParameters()[ItemBundleCodec.ParameterKey] ==
                "item_a*1;item_b*2;item_c*3",
            "A later save collapsed the applied item bundle back to one row: " + itemCommitError);

        var spawnBundle = new ActionDefinition
        {
            Id = "qa-spawn-bundle", HandlerId = "spawn_npcs", DisplayName = "NPC",
            ParameterDefinitions =
            [
                new() { Key = "section_id", DisplayName = "NPC", Kind = ParameterKind.Choice,
                    Options = ["npc_a", "npc_b"], DefaultValue = "npc_a" },
                new() { Key = "strength", DisplayName = "Strength", Kind = ParameterKind.Choice,
                    Options = ["any", "strong"], DefaultValue = "any" },
                new() { Key = "count", DisplayName = "Count", Kind = ParameterKind.Integer,
                    Minimum = 1, Maximum = 99, DefaultValue = "1" }
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = "npc_a", ["strength"] = "any", ["count"] = "1"
            },
            SpawnGroups = [new SpawnGroupEntry { VariantId = "npc_a", Strength = "any", Count = 1 }]
        };
        var spawnSource = new ActionViewModel(spawnBundle);
        var spawnEditor = new ActionEditorViewModel(spawnSource, draft =>
        {
            spawnSource.ApplyEditorDraft(draft);
            return Task.CompletedTask;
        });
        spawnEditor.Draft.AddSpawnGroupCommand.Execute(null);
        spawnEditor.Draft.SpawnGroupRows[1].Picker.Value = "npc_b";
        spawnEditor.Draft.SpawnGroupRows[1].CountText = "4";
        var spawnSaved = await spawnEditor.SaveAsync();
        var spawnCommitted = spawnSource.TryCommitPendingEdits(out var spawnCommitError);
        Check(spawnSaved && spawnSource.SpawnGroupRows.Count == 2 && spawnCommitted &&
            spawnSource.Model.EffectiveParameters()[SpawnGroupBundleCodec.ParameterKey] ==
                "npc_a*any*1;npc_b*any*4",
            "A multi-row NPC/mutant editor draft was not retained: " + spawnCommitError);

        var twoTestsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTests = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoTestsFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedTests = 0;
        var finishedTests = 0;
        var repeatableTestCommand = new AsyncRelayCommand(async _ =>
        {
            if (Interlocked.Increment(ref startedTests) == 2) twoTestsStarted.TrySetResult(true);
            await releaseTests.Task;
            if (Interlocked.Increment(ref finishedTests) == 2) twoTestsFinished.TrySetResult(true);
        }, _ => true, allowConcurrentExecutions: true);
        repeatableTestCommand.Execute(null);
        repeatableTestCommand.Execute(null);
        await twoTestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(repeatableTestCommand.CanExecute(null),
            "The repeatable Test command disabled itself while a previous test was queued.");
        releaseTests.TrySetResult(true);
        await twoTestsFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var root = Path.Combine(Environment.GetEnvironmentVariable("ADB_TEST_ROOT") ?? Path.GetTempPath(), "browser-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var store = new JsonUiPreferencesStore(paths);
            Check((await store.LoadAsync()) is { PresetColumns: 2, ActiveColumns: 2,
                CompactActiveActions: false, CompactUserPresets: false, CompactPresets: false,
                ShowTemplateDescriptions: true }, "Default layout changed.");
            paths.EnsureExists();
            await File.WriteAllTextAsync(Path.Combine(paths.RootDirectory, "ui-preferences.json"),
                "{\"activeCategory\":\"player\",\"presetCategory\":\"world\",\"presetColumns\":4,\"automaticPresetColumns\":true}");
            Check(await store.LoadAsync() is { ActiveColumns: 2, AutomaticActiveColumns: false,
                PresetColumns: 4, AutomaticPresetColumns: true,
                CompactActiveActions: false, CompactUserPresets: false, CompactPresets: false,
                ShowTemplateDescriptions: true },
                "Legacy view preferences did not preserve preset layout or initialize the active grid.");
            var preferences = new UiPreferences
            {
                ActiveCategory = "inventory", PresetCategory = "world", UserPresetCategory = "player_state",
                PresetColumns = 5, ActiveColumns = 3, UserPresetColumns = 5,
                ShowTemplateDescriptions = false,
                AutomaticActiveColumns = true,
                CompactActiveActions = true,
                CompactUserPresets = true,
                CompactPresets = true,
                ActivePriceGroupNamesByProfile = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["profile-a"] = new(StringComparer.Ordinal)
                    {
                        ["=|100|RUB"] = "Эффекты за 100 RUB"
                    }
                }
            };
            await store.SaveAsync(preferences);
            var loadedPreferences = await store.LoadAsync();
            Check(loadedPreferences.ActiveCategory == preferences.ActiveCategory &&
                loadedPreferences.PresetCategory == preferences.PresetCategory &&
                loadedPreferences.UserPresetCategory == preferences.UserPresetCategory &&
                loadedPreferences.PresetColumns == preferences.PresetColumns &&
                loadedPreferences.ActiveColumns == preferences.ActiveColumns &&
                loadedPreferences.UserPresetColumns == preferences.UserPresetColumns &&
                !loadedPreferences.ShowTemplateDescriptions && loadedPreferences.CompactPresets &&
                loadedPreferences.AutomaticActiveColumns == preferences.AutomaticActiveColumns &&
                loadedPreferences.CompactActiveActions == preferences.CompactActiveActions &&
                loadedPreferences.CompactUserPresets == preferences.CompactUserPresets &&
                loadedPreferences.ActivePriceGroupNamesByProfile["profile-a"].OrderBy(pair => pair.Key)
                    .SequenceEqual(preferences.ActivePriceGroupNamesByProfile["profile-a"].OrderBy(pair => pair.Key)) &&
                !File.Exists(paths.SettingsFile), "View preferences saved donation rules or failed roundtrip.");
            var storedNpcPreset = randomNpcSource.Model.CloneAsNewInstance();
            var storedMutantPreset = randomMutantSource.Model.CloneAsNewInstance();
            var settingsStore = new JsonSettingsStore(paths);
            await settingsStore.SaveAsync(new AppSettings
            {
                UserPresets = [storedNpcPreset, storedMutantPreset]
            });
            var npcReloaded = await settingsStore.LoadAsync();
            Check(npcReloaded.UserPresets.Single(item => item.HandlerId == "spawn_random_npcs")
                    .Parameters["npc_sections"] == "sim_default_stalker_2" &&
                npcReloaded.UserPresets.Single(item => item.HandlerId == "spawn_random_mutants")
                    .Parameters["mutant_sections"] == "boar_normal",
                "Saved user presets lost the exact NPC or mutant multi-selection on disk.");
            var runtime = new BridgeRuntime(paths);
            var money = DefaultActionCatalog.CreatePresets().Single(action => action.HandlerId == "change_money").CloneAsNewInstance();
            runtime.Settings.Actions = [money];
            var modified = money.Clone();
            modified.Parameters["amount"] = "999";
            modified.CooldownSeconds = 17;
            await runtime.SaveActionParametersAsync(money.Id, modified);
            var loaded = await new JsonSettingsStore(paths).LoadAsync();
            Check(loaded.Actions.Single().Parameters["amount"] == "999" && loaded.Actions.Single().CooldownSeconds == 17,
                "Active popup edits were not persisted together.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
