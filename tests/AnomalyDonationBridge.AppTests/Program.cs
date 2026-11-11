using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Globalization;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.AppTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (CrashWatchdog.TryRun(args, out var watchdogResult)) return watchdogResult;
        if (args is ["--named-observer-test", var helper]) return CrashRecoveryTests.RunNamedObserverAsync(helper).GetAwaiter().GetResult();
        if (args is ["--manual-replay-test"]) { ManualReplayTests.RunAsync().GetAwaiter().GetResult(); return 0; }
        if (args.Length >= 2 && args[0] is "--crash-recovery-probe" or "--crash-loop-probe")
            return CrashRecoveryTests.RunProbe(args);
        if (args is ["--render-pda", var outputDirectory]) return PdaUiChecks.Run(outputDirectory);
        if (args is ["--render-price-editor", var priceOutput]) return PdaUiChecks.RunPriceEditor(priceOutput);
        if (args is ["--render-bridge-update", var updateOutput]) return PdaUiChecks.Run(updateOutput, updatedUiOnly: true);
        if (args is ["--render-browser-view", var browserOutput]) return BrowserViewUiChecks.Run(browserOutput);
        if (args is ["--render-selected-random-grid", var selectedOutput]) return SelectedRandomGridUiChecks.Run(selectedOutput);
        if (args is ["--selected-random-test"]) { SelectedRandomTests.RunAsync().GetAwaiter().GetResult(); return 0; }
        if (args is ["--render-compact-window", var compactOutput]) return CompactWindowUiChecks.Run(compactOutput);
        if (args is ["--render-group-delay", var groupDelayOutput]) return GroupDelayUiChecks.Run(groupDelayOutput);
        if (args is ["--render-template-description", var descriptionOutput]) return TemplateDescriptionUiChecks.Run(descriptionOutput);
        if (args is ["--render-roulette-template", var rouletteOutput]) return RouletteTemplateUiChecks.Run(rouletteOutput);
        if (args is ["--render-actor-boost-reset", var resetOutput]) return ActorBoostResetUiChecks.Run(resetOutput);
        if (args is ["--render-btr-sources", var btrOutput]) return BtrSourcesUiChecks.Run(btrOutput);
        if (args is ["--render-history", var historyOutput]) return HistoryUiChecks.Run(historyOutput);
        if (args is ["--render-needs", var needsOutput]) return NeedsTemplateTests.Render(needsOutput);
        if (args is ["--render-trader", var traderOutput]) return WanderingTraderTests.Render(traderOutput);
        if (args is ["--render-theme-picker", var themeOutput]) return ThemePickerUiChecks.Run(themeOutput);
        if (args is ["--render-voting", var votingOutput]) return VotingUiChecks.Run(votingOutput);
        if (args is ["--render-dialogs", var dialogsOutput]) return DialogUiChecks.Run(dialogsOutput);
        if (args is ["--render-safe-zone", var safeZoneOutput]) return SafeZoneUiChecks.Run(safeZoneOutput);
        if (args is ["--render-youtube-chat", var youtubeOutput]) return YouTubeChatUiChecks.Run(youtubeOutput);
        if (args is ["--youtube-chat-test"]) { YouTubeChatRuntimeTests.RunAsync().GetAwaiter().GetResult(); return 0; }
        if (args is ["--safe-zone-test"]) { SafeZoneTests.RunAsync().GetAwaiter().GetResult(); return 0; }
        if (args is ["--wpf-layout-test"]) { WpfLayoutContractsAsync().GetAwaiter().GetResult(); return 0; }
        if (args is ["--addon-upgrade-test", var previousPackage, var hotfixes])
        {
            AddonUpgradeTests.RunAsync(previousPackage, hotfixes).GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--addon-package-test"])
        {
            ShippedAddonPackageAsync().GetAwaiter().GetResult();
            AddonInstallLifecycleAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS shipped addon and install/update/conflict lifecycle.");
            return 0;
        }
        return RunTestsAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunTestsAsync()
    {
        var failures = new List<string>();
        await RunAsync("YouTube chat connect/disconnect, restore, key removal and game isolation", YouTubeChatRuntimeTests.RunAsync, failures);
        await RunAsync("crash recovery stays hidden without activation; manual opening is preserved", StartupPresentationChecks.RunAsync, failures);
        await RunAsync("retired integration stays disabled and history is preserved", RetiredIntegrationChecks.RunAsync, failures);
        await RunAsync("safe-zone settings, scanned locations, wire guard and skip/wait queue", SafeZoneTests.RunAsync, failures);
        await RunAsync("scenario editor order, hotkeys and persistence", SequenceEditorTests.RunAsync, failures);
        await RunAsync("selected active random editor, migration and persistence", SelectedRandomTests.RunAsync, failures);
        await RunAsync("file preset discovery and installation", FilePresetTests.RunAsync, failures);
        await RunAsync("voting game UI acknowledgement and isolated winner delivery", VotingGameRuntimeTests.RunAsync, failures);
        await RunAsync("voting profiles, compact persistence and independent donation processing", VotingProfileRuntimeTests.RunAsync, failures);
        await RunAsync("catalog cache roundtrip", CatalogCacheRoundtripAsync, failures);
        await RunAsync("game path and cache isolation", GamePathSelectionAsync, failures);
        await RunAsync("dynamic picker quarantine and search", DynamicPickerAsync, failures);
        await RunAsync("catalog errors stay action-local while saving", CatalogPersistenceValidationAsync, failures);
        await RunAsync("invalid actions do not block unrelated settings or deletion", InvalidActionIsolationAsync, failures);
        await RunAsync("settings omit runtime catalog payload", SettingsProjectionAsync, failures);
        await RunAsync("failed startup never overwrites settings", FailedStartupPreservesSettingsAsync, failures);
        await RunAsync("shipped addon manifest pairing", ShippedAddonPackageAsync, failures);
        await RunAsync("stationary gun model physics and resource contract", StationaryMgunModelTests.RunAsync, failures);
        await RunAsync("managed addon install/update/conflict", AddonInstallLifecycleAsync, failures);
        await RunAsync("mutant V2 keys, variants and installation", MutantCompanionV2Tests.RunAsync, failures);
        await RunAsync("game and catalog error presentation", GameStatusPresentationAsync, failures);
        await RunAsync("declarative extension manifest safety", ExtensionManifestSafetyAsync, failures);
        await RunAsync("all-currency price recalculation", CurrencyPriceRecalculationAsync, failures);
        await RunAsync("batched parameter editing and persistence", BatchedParameterEditingAsync, failures);
        await RunAsync("actor-boost recovery, manual acknowledgement and queue isolation", ActorBoostRecoveryTests.RunAsync, failures);
        await RunAsync("donation countdown preserves editable template search", SearchQueueVisibilityTests.RunAsync, failures);
        await RunAsync("history selection, status replacement and selected-only immediate queue", HistorySelectionTests.RunAsync, failures);
        await RunAsync("needs wait checkbox default, old-template migration and disabled option persistence", NeedsTemplateTests.RunAsync, failures);
        await RunAsync("wandering trader catalogue, stock controls, validation and persistence", WanderingTraderTests.RunAsync, failures);
        await RunAsync("browser filters, independent UI preferences and transactional popup", BrowserEditorTests.RunAsync, failures);
        await RunAsync("WPF layout and catalog contracts", WpfLayoutContractsAsync, failures);
        await RunAsync("theme and tray contracts", ThemeAndTrayContractsAsync, failures);
        await RunAsync("global hotkeys and unconditional repeat", GlobalHotkeyTests.RunAsync, failures);
        await RunAsync("manual replay with reception disabled", ManualReplayTests.RunAsync, failures);
        await RunAsync("single application instance gate", SingleInstanceGateAsync, failures);
        await RunAsync("crash observer, manual close, live preference and bounded restart", CrashRecoveryTests.RunAsync, failures);
        if (failures.Count == 0)
        {
            Console.WriteLine("App/Persistence tests passed.");
            return 0;
        }
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        return 1;
    }

    private static async Task RunAsync(string name, Func<Task> test, ICollection<string> failures)
    {
        try
        {
            await test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures.Add($"FAIL {name}: {exception}");
        }
    }

    private static async Task CatalogCacheRoundtripAsync()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
        var game = Path.Combine(temporary.Path, "game");
        var snapshot = CreateCatalog();
        var capabilities = CapabilitySet.Create(
            EngineProfile.Anomaly153Vanilla,
            "1.5.3",
            ["catalog", "catalog_localization"],
            ["spawn_mutants", "spawn_items"]);
        var store = new JsonCatalogCacheStore(paths);
        await store.SaveAsync(game, capabilities, snapshot);
        var loaded = await store.LoadAsync(game) ?? throw new InvalidOperationException("Cache was not loaded.");
        Assert(loaded.ToSnapshot().Entries.Count == 2, "Cache entry count changed.");
        Assert(loaded.ToCapabilities().SupportsHandler("spawn_mutants"), "Capabilities were not preserved.");
        Assert(await store.LoadAsync(Path.Combine(temporary.Path, "other-game")) is null,
            "Cache from a different game root was exposed.");

        await File.WriteAllTextAsync(paths.CatalogCacheFile, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            gameRoot = game,
            engineProfile = "Anomaly153Vanilla",
            capabilityIds = Array.Empty<string>(),
            supportedHandlerIds = Array.Empty<string>(),
            revision = "session:broken",
            contentHash = new string('a', 64),
            entries = (object?)null
        }));
        Assert(await store.LoadAsync(game) is null,
            "A cache with a null entries collection escaped read-only cache validation.");
    }

    private static Task SingleInstanceGateAsync()
    {
        var name = @"Local\AnomalyDonationBridge.AppTests." + Guid.NewGuid().ToString("N");
        var first = SingleInstanceGate.TryAcquire(name)
            ?? throw new InvalidOperationException("The first application instance did not acquire its gate.");
        using (first)
        {
            using var blocked = SingleInstanceGate.TryAcquire(name);
            Assert(blocked is null,
                "A second application instance acquired the same per-user gate.");
        }
        using var replacement = SingleInstanceGate.TryAcquire(name);
        Assert(replacement is not null,
            "The application gate remained locked after the owning instance exited.");
        return Task.CompletedTask;
    }

    private static async Task FailedStartupPreservesSettingsAsync()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
        paths.EnsureExists();
        const string original = "{\"schemaVersion\":999,\"gamePath\":\"C:\\\\MustSurvive\"}";
        await File.WriteAllTextAsync(paths.SettingsFile, original);
        await using (var runtime = new BridgeRuntime(paths))
        {
            var rejected = false;
            try { await runtime.InitializeAsync(); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Unsupported settings schema was not rejected.");
        }
        Assert(await File.ReadAllTextAsync(paths.SettingsFile) == original,
            "Disposing a runtime after failed settings load overwrote settings.json.");
        Assert(!File.Exists(paths.SettingsBackupFile),
            "Failed startup should not create a backup from uninitialized defaults.");
    }

    private static Task DynamicPickerAsync()
    {
        var snapshot = CreateCatalog();
        var action = DefaultActionCatalog.CreatePresets().Single(item => item.HandlerId == "spawn_mutants");
        DynamicCatalogBinding.Apply(snapshot, [action]);
        var capabilities = CapabilitySet.Create(
            EngineProfile.Anomaly153Vanilla,
            "1.5.3",
            ["catalog"],
            ["spawn_mutants", "spawn_items", "spawn_anomaly"]);
        var state = new CatalogRuntimeState(capabilities, snapshot, false, "Офлайн-кэш");
        var hiddenViewModel = new ActionViewModel(action, isPreset: true, catalogState: state);
        var hiddenParameter = hiddenViewModel.SpawnGroupRows.Single();
        Assert(hiddenParameter.Picker.CatalogOptions.Count == 2,
            "Hiding unavailable entries discarded catalog diagnostics from the view model.");
        var hiddenVisible = hiddenParameter.Picker.CatalogOptionsView.Cast<CatalogOptionViewModel>().ToList();
        Assert(hiddenVisible.Count == 1 && hiddenVisible[0].SectionId == "pseudodog_normal" &&
            !hiddenParameter.Picker.CatalogCategories.Contains("Химеры", StringComparer.CurrentCultureIgnoreCase),
            "Unavailable catalog entries or their empty categories are visible by default.");

        var viewModel = new ActionViewModel(
            action,
            isPreset: true,
            catalogState: state,
            hideUnavailableCatalogSections: false);
        var parameter = viewModel.SpawnGroupRows.Single();
        Assert(parameter.Picker.CatalogOptionsView.Cast<CatalogOptionViewModel>().Count() == 2,
            "Disabling the catalog filter did not expose quarantined entries for diagnostics.");
        var quarantined = parameter.Picker.CatalogOptions.Single(item => item.SectionId == "story_chimera");
        Assert(!quarantined.IsAvailable, "Quarantined entry can be selected.");
        Assert(quarantined.Metadata.Contains("Карантин", StringComparison.Ordinal), "Quarantine reason is not visible.");
        Assert(!viewModel.CanTest, "Offline catalog enabled a dynamic test command.");

        parameter.Picker.CatalogSearch = "pseudo";
        var visible = parameter.Picker.CatalogOptionsView.Cast<CatalogOptionViewModel>().ToList();
        Assert(visible.Count == 1 && visible[0].SectionId == "pseudodog_normal",
            "Search does not match the localized name/section id.");

        var live = new ActionViewModel(
            action,
            isPreset: true,
            catalogState: state with { IsLive = true, Status = "Живой каталог" });
        Assert(live.CanTest, "A supported action with a live catalog remained blocked.");

        var weaponSnapshot = new CatalogSnapshot(
            new CatalogRevision("session:weapon-picker"),
            new string('c', 64),
            [
                new CatalogEntry
                {
                    SectionId = "wpn_pm",
                    Kind = CatalogEntryKind.Item,
                    DisplayName = "ПМ",
                    Category = "weapons",
                    Strength = CatalogStrength.Unclassified,
                    IsAvailable = true
                },
                new CatalogEntry
                {
                    SectionId = "novice_outfit",
                    Kind = CatalogEntryKind.Item,
                    DisplayName = "Куртка новичка",
                    Category = "outfits",
                    Strength = CatalogStrength.Unclassified,
                    IsAvailable = true
                },
                new CatalogEntry
                {
                    SectionId = "helm_m40",
                    Kind = CatalogEntryKind.Item,
                    DisplayName = "Шлем М40",
                    Category = "helmets",
                    Strength = CatalogStrength.Unclassified,
                    IsAvailable = true
                },
                new CatalogEntry
                {
                    SectionId = "af_fire",
                    Kind = CatalogEntryKind.Item,
                    DisplayName = "Огненный шар",
                    Category = "artefacts",
                    Strength = CatalogStrength.Unclassified,
                    IsAvailable = true
                }
            ]);
        var itemAction = DefaultActionCatalog.CreatePresets()
            .Single(item => item.HandlerId == "spawn_items");
        DynamicCatalogBinding.Apply(weaponSnapshot, [itemAction]);
        var itemViewModel = new ActionViewModel(
            itemAction,
            isPreset: true,
            catalogState: new CatalogRuntimeState(
                capabilities, weaponSnapshot, true, "Живой каталог"));
        var itemParameter = itemViewModel.ItemSpawnRows.Single().Picker;
        var randomWeapon = itemParameter.CatalogOptions.Single(item =>
            item.SectionId == DefaultActionCatalog.RandomWeaponSectionId);
        Assert(randomWeapon.IsAvailable && randomWeapon.DisplayName == "Случайное оружие" && randomWeapon.Category == "Случайное оружие",
            "The synthetic random-weapon entry is missing from the live item picker.");
        var randomOutfit = itemParameter.CatalogOptions.Single(item =>
            item.SectionId == DefaultActionCatalog.RandomOutfitSectionId);
        Assert(randomOutfit.IsAvailable && randomOutfit.DisplayName == "Случайная броня" &&
               randomOutfit.Category == "Случайная броня",
            "The synthetic random-outfit entry is missing from the live item picker.");
        var randomHelmet = itemParameter.CatalogOptions.Single(item =>
            item.SectionId == DefaultActionCatalog.RandomHelmetSectionId);
        Assert(randomHelmet.IsAvailable && randomHelmet.DisplayName == "Случайный шлем" &&
               randomHelmet.Category == "Случайный шлем",
            "The synthetic random-helmet entry is missing from the live item picker.");
        var randomArtifact = itemParameter.CatalogOptions.Single(item =>
            item.SectionId == DefaultActionCatalog.RandomArtifactSectionId);
        Assert(randomArtifact.IsAvailable && randomArtifact.DisplayName == "Случайный артефакт" &&
               randomArtifact.Category == "Случайный артефакт",
            "The synthetic random-artifact entry is missing from the live item picker.");
        itemParameter.Value = DefaultActionCatalog.RandomWeaponSectionId;
        Assert(itemViewModel.CanTest,
            "The live random-weapon selection remained blocked by the UI validator.");

        var anomalySnapshot = new CatalogSnapshot(
            new CatalogRevision("session:anomaly-picker"),
            new string('d', 64),
            [
                new CatalogEntry
                {
                    SectionId = "zone_mine_electric_weak",
                    Kind = CatalogEntryKind.Anomaly,
                    DisplayName = "Электра",
                    Category = "Электрические",
                    Strength = CatalogStrength.Lite,
                    IsAvailable = true
                }
            ]);
        var anomalyAction = DefaultActionCatalog.CreatePresets()
            .Single(item => item.HandlerId == "spawn_anomaly");
        DynamicCatalogBinding.Apply(anomalySnapshot, [anomalyAction]);
        var anomalyViewModel = new ActionViewModel(
            anomalyAction,
            isPreset: true,
            catalogState: new CatalogRuntimeState(
                capabilities, anomalySnapshot, true, "Живой каталог"));
        var anomalyParameter = anomalyViewModel.Parameters.Single(item =>
            item.Definition.Key == "section_id");
        var randomAnomaly = anomalyParameter.CatalogOptions.Single(item =>
            item.SectionId == DefaultActionCatalog.RandomAnomalySectionId);
        var eachRandomAnomaly = anomalyParameter.CatalogOptions.Single(item =>
            item.SectionId == DefaultActionCatalog.EachRandomAnomalySectionId);
        Assert(randomAnomaly.IsAvailable &&
               randomAnomaly.DisplayName == "Спавнить один случайный тип аномалии",
            "The synthetic random-anomaly entry is missing from the live picker.");
        Assert(eachRandomAnomaly.IsAvailable &&
               eachRandomAnomaly.DisplayName == "Каждая аномалия будет случайной",
            "The per-anomaly random entry is missing from the live picker.");
        anomalyParameter.Value = DefaultActionCatalog.EachRandomAnomalySectionId;
        Assert(anomalyViewModel.CanTest,
            "The live random-anomaly selection remained blocked by the UI validator.");

        var squadSnapshot = new CatalogSnapshot(
            new CatalogRevision("session:squad-summary"),
            new string('b', 64),
            [
                new CatalogEntry
                {
                    SectionId = "army_sim_squad_alpha",
                    Kind = CatalogEntryKind.StalkerSquad,
                    DisplayName = "Военный отряд Alpha",
                    Category = "army",
                    Strength = CatalogStrength.Normal,
                    Faction = "army",
                    IsAvailable = true,
                    QuarantineReason = QuarantineReason.None
                },
                new CatalogEntry
                {
                    SectionId = "army_sim_squad_beta",
                    Kind = CatalogEntryKind.StalkerSquad,
                    DisplayName = "Военный отряд Beta",
                    Category = "army",
                    Strength = CatalogStrength.Strong,
                    Faction = "army",
                    IsAvailable = true,
                    QuarantineReason = QuarantineReason.None
                },
                new CatalogEntry
                {
                    SectionId = "story_sim_squad_unique",
                    Kind = CatalogEntryKind.StalkerSquad,
                    DisplayName = "Сюжетный отряд",
                    Category = "story",
                    Strength = CatalogStrength.Strong,
                    Faction = "army",
                    IsAvailable = false,
                    QuarantineReason = QuarantineReason.StoryOrUnique
                }
            ]);
        var squadAction = DefaultActionCatalog.CreatePresets()
            .Single(item => item.HandlerId == "spawn_random_hostile_squad");
        DynamicCatalogBinding.Apply(squadSnapshot, [squadAction]);
        var squadViewModel = new ActionViewModel(
            squadAction,
            isPreset: true,
            catalogState: state with { Catalog = squadSnapshot });
        var squadParameter = squadViewModel.Parameters
            .Single(item => item.Definition.Key == "squad_sections");
        Assert(squadParameter.DisplayValue == "выбрано 2 из 2" &&
            squadViewModel.ParameterSummary.Contains("Разрешённые отряды: выбрано 2 из 2", StringComparison.Ordinal) &&
            !squadViewModel.ParameterSummary.Contains("army_sim_squad", StringComparison.Ordinal),
            "A dynamic multi-choice dumped every section ID into the collapsed card summary.");
        squadParameter.CatalogOptions.Single(item => item.SectionId == "army_sim_squad_alpha").IsSelected = false;
        Assert(!squadParameter.Definition.SelectedOptions(squadParameter.Value)
                .Contains("army_sim_squad_alpha", StringComparer.OrdinalIgnoreCase) &&
            squadParameter.Definition.SelectedOptions(squadParameter.Value)
                .SequenceEqual(["army_sim_squad_beta"], StringComparer.OrdinalIgnoreCase) &&
            squadParameter.DisplayValue == "выбрано 1 из 2" &&
            squadParameter.ValidationMessage.Length == 0,
            "Clearing a live catalog checkbox did not immediately update the stored multi-selection.");
        var hostilityParameter = squadViewModel.Parameters
            .Single(item => item.Definition.Key == "force_hostile");
        Assert(hostilityParameter.IsBooleanChoice && !hostilityParameter.IsFlatChoice &&
            hostilityParameter.BooleanValue,
            "The squad hostility option is not exposed as a real checkbox.");
        hostilityParameter.BooleanValue = false;
        Assert(squadAction.Parameters["force_hostile"] == "false",
            "Toggling the hostility checkbox did not update the action parameters.");

        var staleAction = DefaultActionCatalog.CreatePresets().Single(item => item.HandlerId == "spawn_mutants");
        staleAction.SpawnGroups.Single().VariantId = "mutant_removed_by_modpack_change";
        DynamicCatalogBinding.Apply(snapshot, [staleAction]);
        var staleViewModel = new ActionViewModel(
            staleAction,
            isPreset: false,
            catalogState: state with { IsLive = true, Status = "Живой каталог" });
        var staleParameter = staleViewModel.SpawnGroupRows.Single().Picker;
        Assert(staleParameter.Value == "mutant_removed_by_modpack_change" &&
            staleParameter.ValidationMessage.Contains("mutant_removed_by_modpack_change", StringComparison.Ordinal),
            "A disappeared saved section ID was replaced instead of remaining visible with a warning.");
        Assert(!staleViewModel.CanTest,
            "A command with a disappeared dynamic section remained testable.");

        var explicitEmptyCapabilities = CapabilitySet.Create(
            EngineProfile.Anomaly153Vanilla,
            "1.5.3",
            ["catalog"],
            []);
        var unsupported = new ActionViewModel(
            action,
            isPreset: true,
            catalogState: new CatalogRuntimeState(
                explicitEmptyCapabilities,
                snapshot,
                true,
                "Живой профиль без обработчиков"));
        Assert(!unsupported.IsSupported && !unsupported.CanTest,
            "A live CAPABILITIES message with an empty handler set was treated as support for every action.");
        return Task.CompletedTask;
    }

    private static async Task GamePathSelectionAsync()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
        var firstGame = Path.Combine(temporary.Path, "game-a");
        var secondGame = Path.Combine(temporary.Path, "game-b");
        foreach (var game in new[] { firstGame, secondGame })
        {
            Directory.CreateDirectory(Path.Combine(game, "bin"));
            await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "$game_data$=true|true|$fs_root$|gamedata\\");
            await File.WriteAllTextAsync(Path.Combine(game, "bin", "AnomalyDX11.exe"), "test engine marker");
        }

        var snapshot = CreateCatalog();
        var capabilities = CapabilitySet.Create(
            EngineProfile.Anomaly153Vanilla,
            "1.5.3",
            ["catalog"],
            ["spawn_mutants"]);
        await new JsonCatalogCacheStore(paths).SaveAsync(firstGame, capabilities, snapshot);

        await using var runtime = new BridgeRuntime(paths);
        runtime.Settings.Actions.Add(DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "spawn_mutants").CloneAsNewInstance());
        Assert(runtime.ValidateGamePath(firstGame).Length == 0,
            "A vanilla AnomalyDX executable inside bin was rejected.");
        await runtime.SelectGamePathAsync(firstGame);
        Assert(runtime.CatalogState is { IsLive: false, Catalog: not null },
            "The selected installation did not expose its own offline cache.");
        var cachedDynamic = runtime.Settings.Actions.Single(action => action.HandlerId == "spawn_mutants");
        Assert(cachedDynamic.CatalogRevision is null &&
            cachedDynamic.ParameterDefinitions.Single(definition => definition.CatalogKind is not null).Options.Count == 1,
            "Offline cache was made executable or was not available for browsing.");

        await runtime.SelectGamePathAsync(secondGame);
        Assert(runtime.CatalogState.Catalog is null,
            "Changing the selected installation leaked the previous modpack catalog.");
        var resetDynamic = runtime.Settings.Actions.Single(action => action.HandlerId == "spawn_mutants");
        Assert(resetDynamic.CatalogRevision is null &&
            resetDynamic.ParameterDefinitions.Single(definition => definition.CatalogKind is not null).Options.Count == 0,
            "Changing the selected installation left stale executable catalog bindings.");
        Assert(new ActionManifestLoader().MergeFromGame(secondGame, runtime.Presets.ToList()).Count == 0,
            "MO2 virtualized addon files were treated as a fatal physical-install mismatch.");
    }

    private static Task CatalogPersistenceValidationAsync()
    {
        var staleSquad = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "spawn_random_hostile_squad")
            .CloneAsNewInstance();
        var squadDefinition = staleSquad.ParameterDefinitions
            .Single(definition => definition.Key == "squad_sections");
        squadDefinition.Options = ["army_sim_squad_alpha"];
        staleSquad.Parameters["squad_sections"] = "army_sim_squad_10";
        Assert(staleSquad.Validate().Any(error => error.Contains("army_sim_squad_10", StringComparison.Ordinal)),
            "The fixture did not contain the expected action-local stale catalog error.");
        Assert(BridgeRuntime.ValidateActionsForPersistence([staleSquad]).Count == 0,
            "A stale dynamic section blocked saving unrelated valid actions.");

        var unresolvedMutant = DefaultActionCatalog.CreateDefaultActiveInstances()
            .Single(action => action.HandlerId == "spawn_mutants");
        Assert(unresolvedMutant.Validate().Any(error => error.Contains("недопустимая секция", StringComparison.Ordinal)),
            "The fixture did not contain the expected unresolved catalog selection.");
        Assert(BridgeRuntime.ValidateActionsForPersistence([unresolvedMutant]).Count == 0,
            "An unresolved catalog selection blocked storing or importing settings.");

        var invalidHealth = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "change_health")
            .CloneAsNewInstance();
        invalidHealth.Parameters["percent"] = "101";
        Assert(BridgeRuntime.ValidateActionsForPersistence([staleSquad, invalidHealth])
                .Any(error => error.Contains("100", StringComparison.Ordinal)),
            "Persistence stopped validating non-catalog parameters.");
        return Task.CompletedTask;
    }

    private static async Task InvalidActionIsolationAsync()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
        await using var runtime = new BridgeRuntime(paths);
        runtime.Settings.Actions.AddRange(DefaultActionCatalog.CreateDefaultActiveInstances());
        var invalidMutant = runtime.Settings.Actions.Single(action => action.HandlerId == "spawn_mutants");
        Assert(invalidMutant.Validate().Any(error => error.Contains("недопустимая секция", StringComparison.Ordinal)),
            "The fixture no longer reproduces the unresolved default catalog action.");

        runtime.Settings.DonationAlertsClientId = "unrelated-client-id";
        await runtime.SaveSettingsAsync();
        var radiation = runtime.Settings.Actions.Single(action => action.HandlerId == "add_radiation");
        await runtime.DeleteActionInstanceAsync(radiation.Id);

        var reloaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert(reloaded.DonationAlertsClientId == "unrelated-client-id",
            "An invalid catalog action blocked saving unrelated integration settings.");
        Assert(reloaded.Actions.All(action => action.HandlerId != "add_radiation"),
            "An invalid catalog action blocked deleting another active action.");
        Assert(reloaded.Actions.Any(action => action.HandlerId == "spawn_mutants"),
            "Saving unrelated settings silently discarded the unresolved action.");
    }

    private static async Task SettingsProjectionAsync()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
        var settings = new AppSettings();
        settings.Actions.Add(DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "spawn_mutants").CloneAsNewInstance());
        Assert(settings.HideUnavailableCatalogSections,
            "Unavailable catalog sections are not hidden by default.");
        settings.HideUnavailableCatalogSections = false;
        var snapshot = CreateCatalog();
        DynamicCatalogBinding.Apply(snapshot, settings.Actions);
        var mutant = settings.Actions.Single(action => action.HandlerId == "spawn_mutants");
        mutant.SpawnGroups.Single().VariantId = "pseudodog_normal";
        var store = new JsonSettingsStore(paths);
        await store.SaveAsync(settings);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
        var serializedMutant = json.RootElement.GetProperty("actions")
            .EnumerateArray()
            .Single(action => action.GetProperty("handlerId").GetString() == "spawn_mutants");
        var definition = serializedMutant.GetProperty("parameterDefinitions")
            .EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "section_id");
        Assert(definition.GetProperty("options").GetArrayLength() == 0,
            "Runtime catalog was embedded into settings.json.");
        Assert(serializedMutant.GetProperty("spawnGroups").EnumerateArray().Single()
                .GetProperty("variantId").GetString() == "pseudodog_normal",
            "Selected opaque composite row was not persisted.");
        Assert(serializedMutant.GetProperty("catalogRevision").ValueKind == JsonValueKind.Null,
            "Session catalog revision leaked into settings.json.");

        var loaded = await store.LoadAsync();
        Assert(!loaded.HideUnavailableCatalogSections,
            "The unavailable-section visibility preference was not persisted.");
        Assert(loaded.Actions.Single(action => action.HandlerId == "spawn_mutants")
                .SpawnGroups.Single().VariantId == "pseudodog_normal",
            "Dynamic selection was lost during settings migration.");

        await File.WriteAllTextAsync(paths.SettingsFile,
            "{\"schemaVersion\":3,\"observedCurrencies\":null,\"actions\":[null]}");
        await AssertThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    private static async Task AddonInstallLifecycleAsync()
    {
        using var temporary = new TemporaryDirectory();
        var game = Path.Combine(temporary.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "$game_data$=true|true|$fs_root$|gamedata\\");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "xrEngine.exe"), "test engine marker");
        var package = Path.Combine(temporary.Path, "package");
        var data = new AppPaths(Path.Combine(temporary.Path, "data"));
        await WritePackageAsync(package, "1.0.0", "return 'v1'\n", includeObsoleteFile: true, includeLegacyMgun: true);
        var manager = new AddonPackageManager(data, package, "1.0.0");

        var first = await manager.InspectAsync(game);
        Assert(first.State == AddonInstallState.NotInstalled, "Fresh game was not detected as uninstalled.");
        var installed = await manager.InstallOrUpdateAsync(game);
        Assert(installed.State == AddonInstallState.Current, "Initial installation did not complete.");

        await WritePackageAsync(package, "1.0.0", "return 'v1'\n");
        var removeObsolete = await manager.InspectAsync(game);
        Assert(removeObsolete.State == AddonInstallState.UpdateAvailable,
            "A managed file removed from a same-version manifest was ignored.");
        await manager.InstallOrUpdateAsync(game);
        Assert(!File.Exists(Path.Combine(game, "gamedata", "scripts", "anomaly_donation_bridge_obsolete.script")),
            "A no-longer-shipped managed file was left in the game directory.");
        Assert(File.Exists(Path.Combine(game, "gamedata/meshes/anomaly_donation_bridge/stationary_mgun_r020.ogf")) &&
            (await manager.InspectAsync(game)).State == AddonInstallState.Current,
            "Retired native gun visuals needed by existing saves were deleted or keep the app permanently outdated.");

        await WritePackageAsync(package, "1.1.0", "return 'v2'\n");
        manager = new AddonPackageManager(data, package, "1.1.0");
        var update = await manager.InspectAsync(game);
        Assert(update.State == AddonInstallState.UpdateAvailable, "Managed old version was not updateable.");
        await manager.InstallOrUpdateAsync(game);
        Assert(Directory.EnumerateFiles(data.AddonBackupsDirectory, "*.script", SearchOption.AllDirectories).Any(),
            "Updated managed file was not backed up.");

        var target = Path.Combine(game, "gamedata", "scripts", "anomaly_donation_bridge_main.script");
        var duplicate = Path.Combine(game, "gamedata", "scripts", "anomaly_donation_bridge_main (2).script");
        File.Copy(target, duplicate);
        var duplicateConflict = await manager.InspectAsync(game);
        Assert(duplicateConflict.State == AddonInstallState.Conflict &&
            duplicateConflict.Message.Contains("anomaly_donation_bridge_main (2).script", StringComparison.Ordinal),
            "An untracked duplicate namespaced script was accepted as a current installation.");
        await AssertThrowsAsync<InvalidOperationException>(() => manager.InstallOrUpdateAsync(game));
        File.Delete(duplicate);

        await File.AppendAllTextAsync(target, "-- user edit\n");
        await WritePackageAsync(package, "1.2.0", "return 'v3'\n");
        manager = new AddonPackageManager(data, package, "1.2.0");
        var conflict = await manager.InspectAsync(game);
        Assert(conflict.State == AddonInstallState.Conflict, "Manual edit was not treated as a conflict.");
        await AssertThrowsAsync<InvalidOperationException>(() => manager.InstallOrUpdateAsync(game));

        await File.WriteAllTextAsync(
            Path.Combine(package, "addon-manifest.json"),
            "{\"schemaVersion\":1,\"product\":\"AnomalyDonationBridge\",\"addonVersion\":\"1.2.0\"," +
            "\"protocolVersion\":\"1\",\"pipeName\":\"AnomalyDonationBridge.v1\",\"files\":null}");
        var malformed = await manager.InspectAsync(game);
        Assert(malformed.State == AddonInstallState.InvalidPackage,
            "A package manifest with a null files collection crashed or bypassed validation.");
    }

    private static Task GameStatusPresentationAsync()
    {
        var failure = GameStatusPresentation.CatalogFailure("section_enumeration_failed");
        Assert(failure is not null &&
            failure.Contains("ошибка обхода секций игры", StringComparison.Ordinal) &&
            failure.Contains("section_enumeration_failed", StringComparison.Ordinal),
            "The catalog failure was hidden behind a perpetual synchronization label.");
        Assert(GameStatusPresentation.CatalogFailure("actor_unavailable") is null,
            "A temporary actor readiness state was mislabeled as a catalog failure.");
        Assert(GameStatusPresentation.CatalogFailure("catalog_snapshot_failed")?.Contains(
                "внутренняя ошибка", StringComparison.Ordinal) == true,
            "A protected Lua catalog exception was not surfaced to the user.");

        var readyWithFailure = GameStatusPresentation.Connection(new GameConnectionSnapshot(
            true,
            true,
            "catalog_api_unavailable",
            "1.0.1",
            1234));
        Assert(readyWithFailure.Contains("движок не предоставляет", StringComparison.Ordinal) &&
            readyWithFailure.Contains("addon 1.0.1", StringComparison.Ordinal),
            "A ready connection hid the detailed catalog error.");
        Assert(GameStatusPresentation.Connection(new GameConnectionSnapshot(
                true,
                true,
                "ok",
                "1.0.1",
                1234)) == "Готова · addon 1.0.1",
            "The normal ready label regressed while adding error presentation.");
        Assert(GameStatusPresentation.Connection(new GameConnectionSnapshot(
                true,
                false,
                "game_suspended",
                "1.0.1",
                1234)) == "Подключена · игровой цикл не отвечает — события ждут в очереди",
            "An unresponsive game loop was incorrectly presented as a lost connection.");
        Assert(GameStatusPresentation.Connection(new GameConnectionSnapshot(
                true,
                false,
                "addon_update_required",
                "1.0.1",
                1234)) == "Подключена · требуется обновить игровой аддон",
            "An outdated addon was not clearly identified.");
        return Task.CompletedTask;
    }

    private static async Task ShippedAddonPackageAsync()
    {
        using var temporary = new TemporaryDirectory();
        var game = Path.Combine(temporary.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "$game_data$=true|true|$fs_root$|gamedata\\");
        await File.WriteAllTextAsync(Path.Combine(game, "bin", "AnomalyDX11.exe"), "test engine marker");
        var packageRoot = Path.Combine(AppContext.BaseDirectory, "addon-package");
        var manager = new AddonPackageManager(
            new AppPaths(Path.Combine(temporary.Path, "data")),
            packageRoot);
        var status = await manager.InspectAsync(game);
        Assert(status.State == AddonInstallState.NotInstalled && status.PackageVersion == AddonPackageManager.ShippedAddonVersion,
            "The shipped app/addon manifest, protocol, namespaced file set, or SHA-256 pairing is invalid: " + status.Message);
        var installed = await manager.InstallOrUpdateAsync(game);
        Assert(installed.State == AddonInstallState.Current &&
            File.ReadAllBytes(Path.Combine(game, "gamedata/textures/ui/anomaly_donation_bridge_mutant_v2.dds"))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(packageRoot, "gamedata/textures/ui/anomaly_donation_bridge_mutant_v2.dds"))) &&
            File.Exists(Path.Combine(game, "gamedata/configs/ui/textures_descr/anomaly_donation_bridge_mutant_v2.xml")),
            "Own UI atlas/descriptor not installed atomically with the shipped addon.");
        foreach (var speed in new[] { "060", "090", "120" })
        {
            var relative = $"gamedata/meshes/anomaly_donation_bridge/stationary_mgun_r{speed}.ogf";
            Assert(File.ReadAllBytes(Path.Combine(game, relative)).SequenceEqual(File.ReadAllBytes(Path.Combine(packageRoot, relative))),
                "Native gun speed model is absent/corrupt after atomic installation: " + speed);
        }
        var speedModel = Path.Combine(game, "gamedata/meshes/anomaly_donation_bridge/stationary_mgun_r090.ogf");
        var originalSpeedModel = await File.ReadAllBytesAsync(speedModel);
        await File.AppendAllTextAsync(speedModel, "foreign change");
        Assert((await manager.InspectAsync(game)).State == AddonInstallState.Conflict,
            "External changes to a native speed profile must not be overwritten silently.");
        await File.WriteAllBytesAsync(speedModel, originalSpeedModel);
        Assert((await manager.InspectAsync(game)).State == AddonInstallState.Current, "Restored speed profile did not verify.");

        var coreFiles = Directory.EnumerateFiles(Path.Combine(packageRoot, "gamedata", "scripts"), "*.script")
            .Select(Path.GetFileName).ToList();
        foreach (var relative in new[] {
            "gamedata/configs/ui/textures_descr/anomaly_donation_bridge_roulette.xml",
            "gamedata/configs/ui/anomaly_donation_bridge_voting.xml",
            "gamedata/configs/ui/textures_descr/anomaly_donation_bridge_voting.xml",
            "gamedata/configs/text/rus/st_anomaly_donation_bridge_roulette.xml",
            "gamedata/textures/ui/anomaly_donation_bridge_roulette_atlas.dds",
            "gamedata/textures/ui/anomaly_donation_bridge_voting_material.dds",
            "gamedata/sounds/ui/anomaly_donation_bridge_roulette_coins.ogg",
            "gamedata/sounds/ui/anomaly_donation_bridge_roulette_tick.ogg" })
            Assert(File.ReadAllBytes(Path.Combine(game, relative)).SequenceEqual(File.ReadAllBytes(Path.Combine(packageRoot, relative))),
                "Private roulette resource omitted or changed during atomic installation: " + relative);
        Assert(coreFiles.Count(name => name!.StartsWith("anomaly_donation_bridge_roulette", StringComparison.Ordinal)) == 5 &&
            !Directory.EnumerateFiles(game, "hc_upgrader*", SearchOption.AllDirectories).Any(),
            "Roulette integration must ship five private modules without replacing the standalone addon.");
        Assert(coreFiles.Count == 28 && coreFiles.Contains("anomaly_donation_bridge_btr_ai.script") &&
            coreFiles.Contains("anomaly_donation_bridge_reincarnation.script") &&
            coreFiles.Contains("anomaly_donation_bridge_relife_companions.script") &&
            coreFiles.Contains("anomaly_donation_bridge_azazel_hud.script") &&
            coreFiles.Contains("anomaly_donation_bridge_wandering_trader.script") &&
            coreFiles.Contains("anomaly_donation_bridge_trader_catalog.script") &&
            coreFiles.Contains("anomaly_donation_bridge_npc_visuals.script") &&
            coreFiles.Contains("anomaly_donation_bridge_visual_safety.script") &&
            coreFiles.Contains("anomaly_donation_bridge_named_companion.script") &&
            coreFiles.All(name => !name!.Contains("_actions_", StringComparison.Ordinal)),
            "The bridge package must include the independent BTR runtime and exclude combined effect modules.");
        var btrRuntimePath = Path.Combine("gamedata", "scripts", "anomaly_donation_bridge_btr_ai.script");
        foreach (var relative in new[] {
            "gamedata/meshes/anomaly_donation_bridge/btr_olive.ogf",
            "gamedata/textures/anomaly_donation_bridge/btr_olive.dds" })
            Assert(File.ReadAllBytes(Path.Combine(game, relative)).SequenceEqual(File.ReadAllBytes(Path.Combine(packageRoot, relative))),
                "Private BTR skin is absent/corrupt after installation: " + relative);
        Assert(!File.Exists(Path.Combine(game, "gamedata/textures/veh/veh_bt9.dds")) &&
            !File.Exists(Path.Combine(game, "gamedata/meshes/dynamics/vehicles/veh_btr/veh_btr_u_01.ogf")),
            "BTR skin must not overwrite original game assets.");
        Assert(!Directory.EnumerateFiles(packageRoot, "*sidorovich*", SearchOption.AllDirectories).Any() &&
            !File.Exists(Path.Combine(game, "gamedata/meshes/actors/stalker_trader/stalker_trader_1.ogf")),
            "Special companion must use only scanned models, with no supplied Sidorovich workaround or original replacement.");
        Assert(File.ReadAllBytes(Path.Combine(game, btrRuntimePath)).SequenceEqual(File.ReadAllBytes(Path.Combine(packageRoot, btrRuntimePath))),
            "The independent BTR runtime was omitted during addon installation.");
        var entrySource = await File.ReadAllTextAsync(Path.Combine(packageRoot,
            "gamedata", "scripts", "anomaly_donation_bridge_main.script"));
        Assert(!entrySource.Contains("register_actions", StringComparison.Ordinal),
            "The entry script still registers all old effects unconditionally.");
        var sharedSource = await File.ReadAllTextAsync(Path.Combine(packageRoot,
            "gamedata", "scripts", "anomaly_donation_bridge_effects_direct_spawn.script"));
        Assert(sharedSource.Contains("function descriptor(kind)", StringComparison.Ordinal) &&
            !sharedSource.Contains("register_builtin", StringComparison.Ordinal),
            "Direct-spawn shared logic unexpectedly registered a preset itself.");
        var effectRoot = Path.Combine(AppContext.BaseDirectory, "effects", "gamedata", "scripts");
        Assert(Directory.EnumerateFiles(effectRoot, "*.script").Count() == 70,
            "The application delivery must contain 70 one-file templates, excluding the archived chimera and psy-dog.");
        var taskSource = await File.ReadAllTextAsync(Path.Combine(packageRoot,
            "gamedata", "configs", "misc", "task", "tm_anomaly_donation_bridge.ltx"));
        Assert(System.Text.RegularExpressions.Regex.Matches(taskSource,
                @"(?m)^\[adb_donor_thief_\d{2}\]").Count == 32 &&
            System.Text.RegularExpressions.Regex.Matches(taskSource,
                @"(?m)^\[adb_donor_boss_\d{2}\]").Count == 32 &&
            System.Text.RegularExpressions.Regex.Matches(taskSource,
                @"(?m)^\[adb_helicopter_boss_\d{2}\]").Count == 16,
            "The allowed shared task config must preserve boss and thief save slots.");
        var heliConfig = await File.ReadAllTextAsync(Path.Combine(packageRoot,
            "gamedata", "configs", "mod_system_anomaly_donation_bridge.ltx"));
        var heliSection = System.Text.RegularExpressions.Regex.Match(heliConfig,
            @"(?ms)^\[adb_helicopter_boss\]:helicopter\r?\n.*?(?=^\[|\z)").Value;
        Assert(heliConfig.Contains("[adb_helicopter_boss]:helicopter") &&
            !heliConfig.Contains("![helicopter]") && !heliSection.Contains("visual =") &&
            heliConfig.Contains("script_binding = anomaly_donation_bridge_effect_hardcoon_adb_helicopter_boss.bind") &&
            heliConfig.Contains("immunities_sect = adb_helicopter_boss_immunities"),
            "Helicopter must inherit merged vanilla configuration without replacing stock settings.");
        Assert(heliConfig.Contains("[adb_stationary_mgun]:stationary_mgun") &&
            heliConfig.Contains("visual = anomaly_donation_bridge\\stationary_mgun.ogf") &&
            heliConfig.Contains("net_spawn_after = anomaly_donation_bridge_effect_hardcoon_adb_stationary_mgun.net_spawn_after") &&
            heliConfig.Contains("script_binding = anomaly_donation_bridge_effect_hardcoon_adb_stationary_mgun.bind") &&
            heliConfig.Contains("unlimited_ammo = true") && heliConfig.Contains("lock_exit = false") &&
            heliConfig.Contains("[adb_stationary_mgun_blast]:grenade_rgd5") && heliConfig.Contains("can_take = false") &&
            !heliConfig.Contains("![stationary_mgun]") && !heliConfig.Contains("[deployable_mgun]"),
            "Private native gun and explosion must inherit game assets, keep infinite ammo and safe exit, without changing stock guns.");
        foreach (var type in new[] { "burn", "strike", "shock", "wound", "radiation", "telepatic", "chemical_burn", "fire_wound", "explosion" })
            Assert(System.Text.RegularExpressions.Regex.IsMatch(heliConfig,
                @"(?m)^" + type + @"_immunity\s*=\s*0\.[1-9]" + "|^" + type + @"_immunity\s*=\s*0\.0[1-9]"),
                "Private helicopter native damage coefficient is missing/zero: " + type);
        var mutantCompanionSource = await File.ReadAllTextAsync(Path.Combine(effectRoot,
            "anomaly_donation_bridge_effect_spawn_mutant_companion.script"));
        Assert(mutantCompanionSource.Contains("AIM_LABEL_STATIC = \"bar_measurement\"", StringComparison.Ordinal) &&
            mutantCompanionSource.Contains("hud:AddCustomStatic(AIM_LABEL_STATIC, true)", StringComparison.Ordinal) &&
            mutantCompanionSource.Contains("wnd:SetWndPos(position)", StringComparison.Ordinal) &&
            mutantCompanionSource.Contains("AIM_LABEL_TOP_Y = 424", StringComparison.Ordinal) &&
            mutantCompanionSource.Contains("mutant:remove_all_restrictions()", StringComparison.Ordinal) &&
            mutantCompanionSource.Contains("mutant:remove_home()", StringComparison.Ordinal) &&
            !mutantCompanionSource.Contains("mutant:set_home(", StringComparison.Ordinal) &&
            !mutantCompanionSource.Contains("ui_enemy_health.hbar", StringComparison.Ordinal) &&
            !mutantCompanionSource.Contains("cs_enemy_health", StringComparison.Ordinal) &&
            !mutantCompanionSource.Contains("type(vector2)", StringComparison.Ordinal),
            "The one-file mutant companion must use a text-only stock HUD static below the crosshair.");
        var commonSource = await File.ReadAllTextAsync(Path.Combine(packageRoot,
            "gamedata", "scripts", "anomaly_donation_bridge_common.script"));
        Assert(commonSource.Contains("local function level_vertex_count(start_vertex)", StringComparison.Ordinal) &&
            commonSource.Contains("level.vertex_in_direction(vertex_id, vector():set(1, 0, 0), 0)", StringComparison.Ordinal) &&
            commonSource.Contains("local function greatest_common_divisor(left, right)", StringComparison.Ordinal) &&
            commonSource.Contains("greatest_common_divisor(step, vertex_count) ~= 1", StringComparison.Ordinal) &&
            commonSource.Contains("(start + attempt * step) % vertex_count", StringComparison.Ordinal) &&
            commonSource.Contains("if distance >= minimum and distance <= maximum then return point end", StringComparison.Ordinal),
            "The shipped random teleport must sample unique real vertices across the complete level AI grid.");

        var catalogSource = await File.ReadAllTextAsync(Path.Combine(packageRoot,
            "gamedata", "scripts", "anomaly_donation_bridge_catalog.script"));
        Assert(catalogSource.Contains("class_name ~= \"ON_OFF_S\"", StringComparison.Ordinal) &&
            catalogSource.Contains("npc_list, random_list", StringComparison.Ordinal) &&
            catalogSource.Contains("normalized_spawn_path", StringComparison.Ordinal) &&
            catalogSource.Contains("spawn_path_starts_with(spawn_path, \"respawn\\\\\")", StringComparison.Ordinal) &&
            catalogSource.Contains("spawn_path_starts_with(spawn_path, \"monsters\\\\\")", StringComparison.Ordinal) &&
            catalogSource.Contains("function resolve_squad_members", StringComparison.Ordinal),
            "The shipped catalog lacks the structural guards required before native spawning.");

        var runtimeSource = await File.ReadAllTextAsync(Path.Combine(packageRoot,
            "gamedata", "scripts", "anomaly_donation_bridge_runtime.script"));
        Assert(runtimeSource.Contains("action_dispatch_failed", StringComparison.Ordinal) &&
            runtimeSource.Contains("local dispatch_ok", StringComparison.Ordinal) &&
            runtimeSource.Contains("log_failure(\"runtime update\"", StringComparison.Ordinal),
            "The shipped runtime cannot guarantee a terminal RESULT for protected Lua failures.");
    }

    private static async Task ExtensionManifestSafetyAsync()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
        Directory.CreateDirectory(paths.ExtensionsDirectory);
        var valid = new ExtensionManifest
        {
            SchemaVersion = ExtensionManifestLoader.SchemaVersion,
            ProtocolVersion = ExtensionManifestLoader.ProtocolVersion,
            ExtensionId = "vendor.modpack",
            DisplayName = "Vendor Modpack",
            Actions =
            [
                new ExtensionActionManifest
                {
                    HandlerId = "vendor.modpack.weather",
                    DisplayName = "Сменить погоду",
                    Description = "Декларативный шаблон тестовой сборки.",
                    ScriptRelativePath = "gamedata/scripts/vendor_modpack_weather.script",
                    Parameters =
                    [
                        new ExtensionParameterManifest
                        {
                            Key = "duration",
                            DisplayName = "Длительность",
                            Kind = ParameterKind.Integer,
                            Minimum = 1,
                            Maximum = 60,
                            DefaultValue = "10"
                        }
                    ]
                },
                new ExtensionActionManifest
                {
                    HandlerId = "vendor.modpack.mutant",
                    DisplayName = "Создать мутанта из каталога",
                    Description = "Динамический декларативный шаблон.",
                    ScriptRelativePath = "gamedata/scripts/vendor_modpack_mutant.script",
                    Parameters =
                    [
                        new ExtensionParameterManifest
                        {
                            Key = "section_id",
                            DisplayName = "Секция",
                            Kind = ParameterKind.ItemChoice,
                            CatalogKind = CatalogEntryKind.Mutant,
                            DefaultValue = string.Empty
                        }
                    ]
                }
            ]
        };
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "vendor.adb-extension.json"),
            JsonSerializer.Serialize(valid, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "short-segments.adb-extension.json"),
            "{\"schemaVersion\":1,\"protocolVersion\":\"1\",\"extensionId\":\"a.b\",\"displayName\":\"Short segments\",\"actions\":[{" +
            "\"handlerId\":\"a.b.c\",\"displayName\":\"Short handler\",\"scriptRelativePath\":\"gamedata/scripts/a_b_c.script\"}]}");
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "unsafe.adb-extension.json"),
            "{\"schemaVersion\":1,\"protocolVersion\":\"1\",\"extensionId\":\"evil.pack\",\"displayName\":\"Unsafe\",\"actions\":[{" +
            "\"handlerId\":\"evil.pack.escape\",\"displayName\":\"Escape\",\"description\":\"x\",\"scriptRelativePath\":\"../escape.script\"}]}" );
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "null-actions.adb-extension.json"),
            "{\"schemaVersion\":1,\"protocolVersion\":\"1\",\"extensionId\":\"broken.actions\",\"displayName\":\"Broken\",\"actions\":null}");
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "null-parameters.adb-extension.json"),
            "{\"schemaVersion\":1,\"protocolVersion\":\"1\",\"extensionId\":\"broken.parameters\",\"displayName\":\"Broken\",\"actions\":[{" +
            "\"handlerId\":\"broken.parameters.effect\",\"displayName\":\"Broken\",\"scriptRelativePath\":\"gamedata/scripts/broken_parameters_effect.script\",\"parameters\":null}]}");
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "null-options.adb-extension.json"),
            "{\"schemaVersion\":1,\"protocolVersion\":\"1\",\"extensionId\":\"broken.options\",\"displayName\":\"Broken\",\"actions\":[{" +
            "\"handlerId\":\"broken.options.effect\",\"displayName\":\"Broken\",\"scriptRelativePath\":\"gamedata/scripts/broken_options_effect.script\",\"parameters\":[{" +
            "\"key\":\"mode\",\"displayName\":\"Mode\",\"kind\":\"Choice\",\"options\":null}]}]}");
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "reserved.adb-extension.json"),
            "{\"schemaVersion\":1,\"protocolVersion\":\"1\",\"extensionId\":\"broken.reserved\",\"displayName\":\"Broken\",\"actions\":[{" +
            "\"handlerId\":\"broken.reserved.effect\",\"displayName\":\"Broken\",\"scriptRelativePath\":\"gamedata/scripts/broken_reserved_effect.script\",\"parameters\":[{" +
            "\"key\":\"catalog_revision\",\"displayName\":\"Revision\",\"kind\":\"Integer\",\"defaultValue\":\"1\"}]}]}");
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "trailing-handler.adb-extension.json"),
            "{\"schemaVersion\":1,\"protocolVersion\":\"1\",\"extensionId\":\"broken.trailing\",\"displayName\":\"Broken\",\"actions\":[{" +
            "\"handlerId\":\"broken.trailing.\",\"displayName\":\"Broken\",\"scriptRelativePath\":\"gamedata/scripts/broken_trailing_effect.script\"}]}");
        await File.WriteAllTextAsync(
            Path.Combine(paths.ExtensionsDirectory, "consecutive-handler.adb-extension.json"),
            "{\"schemaVersion\":1,\"protocolVersion\":\"1\",\"extensionId\":\"broken.dots\",\"displayName\":\"Broken\",\"actions\":[{" +
            "\"handlerId\":\"broken.dots.effect..extra\",\"displayName\":\"Broken\",\"scriptRelativePath\":\"gamedata/scripts/broken_dots_effect.script\"}]}");

        var loader = new ExtensionManifestLoader(paths);
        var result = loader.Load(DefaultActionCatalog.CreatePresets().Select(action => action.HandlerId));
        Assert(result.Actions.Count == 3 && result.Actions.Any(action => action.HandlerId == "vendor.modpack.weather") &&
            result.Actions.Any(action => action.HandlerId == "a.b.c"),
            "Valid namespaced extension actions were not loaded.");
        Assert(result.Origins["vendor.modpack.weather"].DisplayLabel.Contains("Vendor Modpack", StringComparison.Ordinal),
            "Extension origin was not retained for the UI.");
        Assert(result.Errors.Count == 7 && result.Errors.Any(error => error.Message.Contains("scriptRelativePath", StringComparison.Ordinal)),
            "Unsafe traversal manifest was not rejected with a visible error.");
        Assert(result.Errors.Any(error => error.Message.Contains("Manifest должен объявлять", StringComparison.Ordinal)),
            "A null actions collection was not rejected without crashing the loader.");
        Assert(result.Errors.Any(error => error.Message.Contains("parameters не может быть null", StringComparison.Ordinal)),
            "A null parameters collection was not rejected without crashing the loader.");
        Assert(result.Errors.Any(error => error.Message.Contains("options не может быть null", StringComparison.Ordinal)),
            "A null options collection was not rejected without crashing the loader.");
        Assert(result.Errors.Any(error => error.Message.Contains("зарезервирован протоколом", StringComparison.Ordinal)),
            "An extension declared the reserved catalog_revision parameter.");
        Assert(result.Errors.Count(error => error.Message.Contains("непустой namespaced-сегмент", StringComparison.Ordinal)) == 2,
            "Trailing or consecutive-dot handler IDs bypassed strict namespace validation.");

        var dynamicAction = result.Actions.Single(action => action.HandlerId == "vendor.modpack.mutant");
        var catalog = CreateCatalog();
        DynamicCatalogBinding.Apply(catalog, [dynamicAction]);
        Assert(CatalogRevisionEnforcement.RequiresCatalog(dynamicAction),
            "A dynamic extension action was not marked as catalog-dependent.");
        Assert(dynamicAction.CatalogRevision == catalog.Revision,
            "A dynamic extension action was not stamped with the current catalog revision.");
        var dynamicParameter = dynamicAction.ParameterDefinitions.Single();
        Assert(dynamicParameter.Options.SequenceEqual(["pseudodog_normal"], StringComparer.Ordinal),
            "A dynamic extension received a quarantined or non-current catalog section.");

        var overrideResult = loader.Load(
            DefaultActionCatalog.CreatePresets().Select(action => action.HandlerId).Append("vendor.modpack.weather"));
        Assert(overrideResult.Actions.Count == 1 && overrideResult.Actions[0].HandlerId == "a.b.c" &&
            overrideResult.Errors.Count >= 8,
            "An extension was allowed to override an existing handler.");

        await using var runtime = new BridgeRuntime(paths);
        _ = runtime.RefreshActionManifest();
        _ = runtime.RefreshActionManifest();
        Assert(runtime.Presets.All(action => !action.HandlerId.StartsWith("vendor.modpack.", StringComparison.Ordinal)),
            "A JSON-only extension appeared without a matching installed Lua file.");
        Assert(runtime.GetActionOriginLabel("removed.vendor.effect").Contains("Скрипт не установлен", StringComparison.Ordinal),
            "A persisted action whose script disappeared has no visible availability label.");

        var limitedPaths = new AppPaths(Path.Combine(temporary.Path, "limit-data"));
        Directory.CreateDirectory(limitedPaths.ExtensionsDirectory);
        await WriteExtensionManifestAsync(
            limitedPaths.ExtensionsDirectory,
            "00-first.adb-extension.json",
            CreateExtensionManifest("limit.first", 63));
        await WriteExtensionManifestAsync(
            limitedPaths.ExtensionsDirectory,
            "01-overflow.adb-extension.json",
            CreateExtensionManifest("limit.overflow", 2));
        await WriteExtensionManifestAsync(
            limitedPaths.ExtensionsDirectory,
            "02-tail.adb-extension.json",
            CreateExtensionManifest("limit.tail", 1));

        var limitedResult = new ExtensionManifestLoader(limitedPaths).Load([]);
        Assert(limitedResult.Actions.Count == ExtensionManifestLoader.MaxTotalActions,
            "The aggregate declarative extension action limit was not enforced at 64.");
        Assert(limitedResult.Actions.Count(action => action.HandlerId.StartsWith("limit.first.", StringComparison.Ordinal)) == 63 &&
            limitedResult.Actions.Any(action => action.HandlerId == "limit.tail.effect0"),
            "Extension manifests were not accepted in deterministic sorted-path order.");
        Assert(!limitedResult.Actions.Any(action => action.HandlerId.StartsWith("limit.overflow.", StringComparison.Ordinal)),
            "A manifest that exceeded the remaining aggregate capacity was partially loaded.");
        Assert(limitedResult.Errors.Count == 1 &&
            Path.GetFileName(limitedResult.Errors[0].ManifestPath) == "01-overflow.adb-extension.json" &&
            limitedResult.Errors[0].Message.Contains("общий лимит декларативных расширений — 64", StringComparison.Ordinal) &&
            limitedResult.Errors[0].Message.Contains("доступно 1", StringComparison.Ordinal),
            "The aggregate-limit rejection was not surfaced as a deterministic manifest error.");
    }

    private static ExtensionManifest CreateExtensionManifest(string extensionId, int actionCount) => new()
    {
        SchemaVersion = ExtensionManifestLoader.SchemaVersion,
        ProtocolVersion = ExtensionManifestLoader.ProtocolVersion,
        ExtensionId = extensionId,
        DisplayName = extensionId,
        Actions = Enumerable.Range(0, actionCount)
            .Select(index => new ExtensionActionManifest
            {
                HandlerId = $"{extensionId}.effect{index}",
                DisplayName = $"Effect {index}",
                ScriptRelativePath = $"gamedata/scripts/{extensionId.Replace('.', '_').Replace('-', '_')}_effect.script"
            })
            .Cast<ExtensionActionManifest?>()
            .ToList()
    };

    private static Task WriteExtensionManifestAsync(
        string directory,
        string fileName,
        ExtensionManifest manifest) => File.WriteAllTextAsync(
            Path.Combine(directory, fileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));

    private static async Task CurrencyPriceRecalculationAsync()
    {
        const string xml = """
            <?xml version="1.0" encoding="windows-1251"?>
            <ValCurs Date="04.09.2026" name="Foreign Currency Market">
              <Valute><CharCode>USD</CharCode><Nominal>1</Nominal><Value>80,0000</Value><VunitRate>80,0000</VunitRate></Valute>
              <Valute><CharCode>EUR</CharCode><Nominal>1</Nominal><Value>100,0000</Value><VunitRate>100,0000</VunitRate></Valute>
              <Valute><CharCode>BYN</CharCode><Nominal>1</Nominal><Value>25,0000</Value><VunitRate>25,0000</VunitRate></Valute>
              <Valute><CharCode>KZT</CharCode><Nominal>100</Nominal><Value>20,0000</Value><VunitRate>0,2000</VunitRate></Valute>
              <Valute><CharCode>UAH</CharCode><Nominal>10</Nominal><Value>20,0000</Value><VunitRate>2,0000</VunitRate></Valute>
              <Valute><CharCode>BRL</CharCode><Nominal>1</Nominal><Value>16,0000</Value><VunitRate>16,0000</VunitRate></Valute>
              <Valute><CharCode>TRY</CharCode><Nominal>10</Nominal><Value>20,0000</Value><VunitRate>2,0000</VunitRate></Valute>
            </ValCurs>
            """;
        var parsed = CbrExchangeRateProvider.Parse(xml);
        Assert(parsed.EffectiveDate == new DateOnly(2026, 9, 4) && parsed.RublesPerUnit["KZT"] == 0.2m,
            "The official-rate feed date or per-unit rate was parsed incorrectly.");

        var seed = new TriggerRule
        {
            Comparator = TriggerComparator.GreaterOrEqual,
            Amount = 100m,
            Currency = "RUB"
        };
        var calculated = DonationCurrencyPriceCalculator.CalculateAll(seed, parsed);
        Assert(calculated.Select(rule => rule.Currency).SequenceEqual(
                DonationCurrencyPriceCalculator.SupportedCurrencies, StringComparer.Ordinal) &&
            calculated.Single(rule => rule.Currency == "USD").Amount == 1.25m &&
            calculated.Single(rule => rule.Currency == "KZT").Amount == 500m &&
            calculated.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() == calculated.Count,
            "The eight DonationAlerts prices were not calculated deterministically.");

        var editor = new TriggerEditorViewModel(
            existing: null,
            currencies: ["RUB"],
            exchangeRateProvider: new FixedExchangeRateProvider(parsed))
        {
            Comparator = TriggerComparator.GreaterOrEqual,
            AmountText = "100",
            Currency = "RUB"
        };
        await editor.CalculateAllAsync();
        var result = editor.Build() ?? throw new InvalidOperationException("The calculated editor result was not built.");
        Assert(result.ReplaceAllPrices && result.Rules.Count == 8 && editor.SaveButtonText.Contains("8", StringComparison.Ordinal),
            "The editor did not request an atomic replacement of all existing prices.");

        editor.AmountText = "101";
        Assert(!editor.HasCalculatedPrices && editor.Build()?.ReplaceAllPrices == false,
            "A stale conversion preview survived an edited source amount.");

        var usdOnlyRates = new ExchangeRateSnapshot(parsed.EffectiveDate,
            new Dictionary<string, decimal> { ["RUB"] = 1m, ["USD"] = 80m }, parsed.SourceName);
        var selected = DonationCurrencyPriceCalculator.CalculateAll(seed, usdOnlyRates, ["usd", "USD", "JPY"]);
        Assert(selected.Count == 1 && selected[0].Currency == "USD" && selected[0].Amount == 1.25m,
            "Disabled currencies still required rates, or the source currency was added against the selection.");

        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        var store = new JsonUiPreferencesStore(paths);
        Assert((await store.LoadAsync()).AutomaticPriceCurrencies is null,
            "An existing installation should enable every currency until configured.");
        await using var runtime = new BridgeRuntime(paths);
        var profile = new DonationProfile();
        runtime.Settings.Profiles.Add(profile);runtime.Settings.ActiveProfileId = profile.Id;
        await runtime.UpdateUiPreferencesAsync(current => current with { ThemeColor = "blue" });
        var provider = new FixedExchangeRateProvider(parsed);
        var configurable = new TriggerEditorViewModel(null, ["RUB"], provider,
            runtime.UiPreferences.AutomaticPriceCurrencies,
            currencies => runtime.UpdateUiPreferencesAsync(current => current with
            {
                AutomaticPriceCurrencies = currencies.ToList()
            })) { AmountText = "100" };
        Assert(await configurable.SetCalculationCurrenciesAsync(["rub", "EUR", "EUR", "invalid"]),
            "Currency selection was not saved.");
        await configurable.CalculateAllAsync();
        Assert(configurable.Build() is { ReplaceAllPrices: true, Rules.Count: 2 } subset &&
            subset.Rules.Select(rule => rule.Currency).SequenceEqual(["RUB", "EUR"]),
            "Price replacement included disabled currencies.");
        var main = new MainViewModel(runtime) { ShowTemplateDescriptions = false };
        // Wait behind the browser's queued preference save, then change theme:
        // neither write is allowed to overwrite the newer currency selection.
        await runtime.UpdateUiPreferencesAsync(current => current with { ThemeColor = "green" });
        var reloaded = await store.LoadAsync();
        Assert(reloaded.AutomaticPriceCurrencies!.SequenceEqual(["RUB", "EUR"]) &&
            reloaded.ThemeColor == "green" && !reloaded.ShowTemplateDescriptions && !File.Exists(paths.SettingsFile),
            "A browser/theme save lost the currency preference or changed donation settings.");
        var reopened = new TriggerEditorViewModel(null, ["RUB"], provider, reloaded.AutomaticPriceCurrencies);
        Assert(reopened.CalculationCurrencies.SequenceEqual(["RUB", "EUR"]),
            "A reopened editor forgot the client's currency selection.");
        Assert(await configurable.SetCalculationCurrenciesAsync([]) && !configurable.HasCalculatedPrices &&
            !configurable.CanCalculate && configurable.Build()?.ReplaceAllPrices == false,
            "Disabling every currency left stale calculated prices or disabled manual pricing.");
        var requests = provider.Requests;
        await configurable.CalculateAllAsync();
        Assert(provider.Requests == requests && (await store.LoadAsync()).AutomaticPriceCurrencies is { Count: 0 },
            "An empty selection queried exchange rates or was replaced by default currencies after saving.");

        var pendingRates = new TaskCompletionSource<ExchangeRateSnapshot>();
        var racing = new TriggerEditorViewModel(null, ["RUB"], new DeferredExchangeRateProvider(pendingRates.Task));
        var calculation = racing.CalculateAllAsync();
        await racing.SetCalculationCurrenciesAsync(["USD"]);
        pendingRates.SetResult(parsed);await calculation;
        Assert(!racing.HasCalculatedPrices && racing.Error.Contains("изменились", StringComparison.Ordinal),
            "An in-flight rate response restored a preview for the previous currency selection.");
        var failing = new TriggerEditorViewModel(null, ["RUB"], provider,
            saveCalculationCurrencies: _ => Task.FromException(new IOException("test write failure")));
        Assert(!await failing.SetCalculationCurrenciesAsync(["USD"]) && failing.CalculationCurrencies.Count == 8 &&
            failing.CanConfigureCurrencies && failing.Error.Contains("сохранить", StringComparison.Ordinal),
            "A failed preference write silently changed the active selection or left settings locked.");
    }

    private static async Task BatchedParameterEditingAsync()
    {
        using var temporary = new TemporaryDirectory();
        var entries = new[] { "actor_greh", "actor_dolg", "actor_monolith", "actor_freedom" }
            .Select((section, index) => new CatalogEntry
            {
                SectionId = "adb_faction_" + section,
                Kind = CatalogEntryKind.Faction,
                DisplayName = section,
                Category = "factions",
                Strength = CatalogStrength.Unclassified,
                Faction = section,
                IsAvailable = true,
                QuarantineReason = QuarantineReason.None
            })
            .ToList();
        var catalog = new CatalogSnapshot(
            new CatalogRevision("session:batched-edit"),
            new string('d', 64),
            entries);
        var draft = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "change_player_faction");
        DynamicCatalogBinding.Apply(catalog, [draft]);
        var instance = draft.CloneAsNewInstance();
        instance.Triggers.Add(new TriggerRule { Amount = 22m, Currency = "RUB" });
        var viewModel = new ActionViewModel(
            instance,
            catalogState: new CatalogRuntimeState(
                CapabilitySet.Empty,
                catalog,
                true,
                "Живой каталог"));
        var factions = viewModel.Parameters.Single(parameter => parameter.Definition.Kind == ParameterKind.MultiChoice);
        var expected = new HashSet<string>(
            ["adb_faction_actor_monolith", "adb_faction_actor_freedom"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var option in factions.CatalogOptions)
        {
            option.IsSelected = expected.Contains(option.SectionId);
        }
        Assert(factions.Definition.SelectedOptions(factions.Value).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected),
            "Several catalog check-box changes were not committed to one parameter value.");

        viewModel.CooldownText = string.Empty;
        Assert(!viewModel.TryCommitPendingEdits(out _),
            "An empty transient numeric field was silently committed.");
        viewModel.CooldownText = "30";
        Assert(viewModel.TryCommitPendingEdits(out _) && instance.CooldownSeconds == 30,
            "A numeric field could not be replaced without an intermediate save.");

        var settings = new AppSettings { Actions = [instance] };
        var originalInstance = settings.Actions[0];
        var store = new JsonSettingsStore(new AppPaths(Path.Combine(temporary.Path, "data")));
        await store.SaveAsync(settings);
        Assert(ReferenceEquals(originalInstance, settings.Actions[0]),
            "Saving replaced the live action object and detached the open editor.");
        var loaded = await store.LoadAsync();
        var saved = loaded.Actions.Single(action => action.Id == instance.Id);
        Assert(saved.CooldownSeconds == 30 &&
            saved.ParameterDefinitions.Single(definition => definition.Kind == ParameterKind.MultiChoice)
                .SelectedOptions(saved.Parameters["faction_communities"])
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(expected),
            "One save did not persist every staged field and multi-selection change.");
    }

    private static Task WpfLayoutContractsAsync()
    {
        var updateResult = GitHubReleaseUpdateChecker.ParsePublishedReleases("""
            [
              { "tag_name": "v1.0.75-beta.1", "html_url": "https://github.com/hardcoon/anomaly-donation-bridge/releases/tag/v1.0.75-beta.1", "draft": false, "prerelease": true },
              { "tag_name": "v99.0.0", "html_url": "https://github.com/hardcoon/anomaly-donation-bridge/releases/tag/v99.0.0", "draft": true, "prerelease": false },
              { "tag_name": "v1.0.77-beta.2", "html_url": "https://github.com/hardcoon/anomaly-donation-bridge/releases/tag/v1.0.77-beta.2", "draft": false, "prerelease": true },
              { "tag_name": "v100.0.0", "html_url": "https://example.com/fake-release", "draft": false, "prerelease": false }
            ]
            """, "1.0.76-beta.1");
        Assert(updateResult.IsUpdateAvailable &&
            updateResult.LatestVersion == "1.0.77-beta.2" &&
            updateResult.ReleaseUri.Host == "github.com",
            "The update checker ignored beta releases, accepted a draft/untrusted URL, or compared versions incorrectly.");
        var relUpdateJson = """
            [
              { "tag_name": "v1.1.0-rel", "html_url": "https://github.com/hardcoon/anomaly-donation-bridge/releases/tag/v1.1.0-rel", "draft": false, "prerelease": false },
              { "tag_name": "v1.0.94-beta.1", "html_url": "https://github.com/hardcoon/anomaly-donation-bridge/releases/tag/v1.0.94-beta.1", "draft": false, "prerelease": true }
            ]
            """;
        var relUpdateFromOldVersion = GitHubReleaseUpdateChecker.ParsePublishedReleases(relUpdateJson, "1.0.104-beta.1");
        var relUpdateFromSameVersion = GitHubReleaseUpdateChecker.ParsePublishedReleases(relUpdateJson, "1.1.0-rel");
        Assert(relUpdateFromOldVersion is { IsUpdateAvailable: true, LatestVersion: "1.1.0-rel" } &&
            !relUpdateFromSameVersion.IsUpdateAvailable,
            "The update checker does not recognize the 1.1.0-rel release tag or treats the installed release as outdated.");

        var projectRoot = FindProjectRoot();
        var mainWindow = XDocument.Load(Path.Combine(
            projectRoot,
            "src",
            "AnomalyDonationBridge.App",
            "MainWindow.xaml"));
        var window = mainWindow.Root ?? throw new InvalidDataException("MainWindow.xaml has no root element.");
        Assert(decimal.TryParse(window.Attribute("Width")?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var width) && width >= 1440,
            "The main window default width is too narrow for four action-card columns.");
        Assert(decimal.TryParse(window.Attribute("MinWidth")?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var minimumWidth) && minimumWidth is >= 600 and <= 640,
            "The main window cannot shrink to the compact layout.");

        var visibleLabels = window.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => element.Attribute("Text")?.Value)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert(new[] { "Активные", "Шаблоны", "История" }.All(visibleLabels.Contains),
            "One of the three primary tabs is missing.");

        var attributeValues = window.DescendantsAndSelf()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToList();
        Assert(attributeValues.Any(value => value.Contains("CatalogSearch", StringComparison.Ordinal)),
            "The catalog search binding is missing.");
        Assert(attributeValues.Any(value => value.Contains("SelectedCatalogCategory", StringComparison.Ordinal)),
            "The catalog category filter is missing.");
        Assert(attributeValues.Any(value => value.Contains("RefreshCatalogCommand", StringComparison.Ordinal)),
            "The manual catalog refresh button is missing.");
        Assert(attributeValues.Any(value => value.Contains("RefreshEventsCommand", StringComparison.Ordinal)) &&
            window.Descendants().Any(element =>
                element.Attribute("Text")?.Value == "Обновить события" ||
                element.Attribute("Content")?.Value == "Обновить события" ||
                element.Attribute("Content")?.Value.Contains("RefreshEventsButtonText", StringComparison.Ordinal) == true),
            "The history result-recovery button is missing.");
        Assert(attributeValues.Any(value => value.Contains("QueueStatusText", StringComparison.Ordinal)) &&
            attributeValues.Any(value => value.Contains("ClearQueueCommand", StringComparison.Ordinal)),
            "The history header lost the live queue countdown or its clear-queue control.");
        var historyButtons = window.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Select(element => new
            {
                Command = element.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName == "Command")?.Value ?? string.Empty,
                Style = element.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName == "Style")?.Value ?? string.Empty
            })
            .Where(button => button.Command.Contains("RetryCommand", StringComparison.Ordinal) ||
                button.Command.Contains("RefreshEventsCommand", StringComparison.Ordinal) ||
                button.Command.Contains("ClearQueueCommand", StringComparison.Ordinal))
            .ToList();
        Assert(historyButtons.Count == 3 &&
            historyButtons[0].Command.Contains("RetryCommand", StringComparison.Ordinal) &&
            historyButtons[1].Command.Contains("RefreshEventsCommand", StringComparison.Ordinal) &&
            historyButtons[2].Command.Contains("ClearQueueCommand", StringComparison.Ordinal),
            "History actions must be ordered retry, refresh, then clear queue.");
        Assert(historyButtons[0].Style.Contains("AccentOutlineButtonStyle", StringComparison.Ordinal) &&
            historyButtons[1].Style == historyButtons[0].Style &&
            historyButtons[2].Style.Contains("DangerButtonStyle", StringComparison.Ordinal),
            "Retry and refresh must share one style while clear queue remains destructive.");
        Assert(attributeValues.Any(value => value.Contains("CatalogProgressPercent", StringComparison.Ordinal)) &&
            attributeValues.Any(value => value.Contains("CatalogProgressText", StringComparison.Ordinal)),
            "The catalog synchronization progress indicator is missing.");
        Assert(window.DescendantsAndSelf().Attributes().Any(attribute =>
                attribute.Name.LocalName.EndsWith(".IsVirtualizing", StringComparison.Ordinal) &&
                string.Equals(attribute.Value, "True", StringComparison.OrdinalIgnoreCase)) &&
            window.DescendantsAndSelf().Attributes().Any(attribute =>
                attribute.Name.LocalName.EndsWith(".VirtualizationMode", StringComparison.Ordinal) &&
                string.Equals(attribute.Value, "Recycling", StringComparison.OrdinalIgnoreCase)),
            "Long catalog lists are not configured for recycling virtualization.");
        Assert(window.Descendants("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}Setter").Any(element =>
                string.Equals(element.Attribute("Property")?.Value, "IsEnabled", StringComparison.Ordinal) &&
                element.Attribute("Value")?.Value.Contains("IsAvailable", StringComparison.Ordinal) == true),
            "Quarantined catalog entries are not disabled by the XAML item container.");

        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace mainX = "http://schemas.microsoft.com/winfx/2006/xaml";
        var catalogListStyle = mainWindow.Descendants(presentation + "Style")
            .SingleOrDefault(element => element.Attribute(mainX + "Key")?.Value == "CatalogListBoxStyle");
        var catalogItemStyle = mainWindow.Descendants(presentation + "Style")
            .SingleOrDefault(element => element.Attribute(mainX + "Key")?.Value == "CatalogListBoxItemStyle");
        var categoryTabStyle = mainWindow.Descendants(presentation + "Style")
            .SingleOrDefault(element => element.Attribute(mainX + "Key")?.Value == "CategoryTabStyle");
        Assert(catalogListStyle is not null && catalogItemStyle is not null &&
            catalogListStyle.Descendants(presentation + "Setter").Any(element =>
                element.Attribute("Property")?.Value == "Background" &&
                element.Attribute("Value")?.Value.Contains("InputBrush", StringComparison.Ordinal) == true) &&
            catalogListStyle.Descendants(presentation + "Setter").Any(element =>
                element.Attribute("Property")?.Value == "Foreground" &&
                element.Attribute("Value")?.Value.Contains("TextBrush", StringComparison.Ordinal) == true) &&
            catalogItemStyle.Descendants(presentation + "ControlTemplate").Any(),
            "The dynamic catalog list can fall back to unreadable system light colors in the dark theme.");
        var categoryFocusTrigger = categoryTabStyle?.Descendants(presentation + "Trigger")
            .SingleOrDefault(element => element.Attribute("Property")?.Value == "IsKeyboardFocusWithin");
        Assert(categoryFocusTrigger?.Descendants(presentation + "Setter").Any(element =>
                element.Attribute("TargetName")?.Value == "TabSurface" &&
                element.Attribute("Property")?.Value == "BorderBrush" &&
                element.Attribute("Value")?.Value.Contains("AccentBrush", StringComparison.Ordinal) == true) == true,
            "Keyboard focus replaces the gold category selection with an unrelated color.");

        var actionEditorWindow = XDocument.Load(Path.Combine(
            projectRoot,
            "src",
            "AnomalyDonationBridge.App",
            "ActionEditorWindow.xaml"));
        Assert(actionEditorWindow.Root?.Attribute("Width")?.Value == "980" &&
            actionEditorWindow.Root?.Attribute("SizeToContent")?.Value == "Height" &&
            actionEditorWindow.Root?.Attribute("MaxHeight")?.Value == "780" &&
            actionEditorWindow.Root?.Attribute("WindowState") is null,
            "The action editor no longer opens as the widened non-maximized modal.");
        var settingsWindow = XDocument.Load(Path.Combine(
            projectRoot,
            "src",
            "AnomalyDonationBridge.App",
            "SettingsWindow.xaml"));
        Assert(settingsWindow.Root?.Attribute("Width")?.Value == "1120" &&
            settingsWindow.Root?.Attribute("MinWidth")?.Value == "900",
            "The default settings width can wrap the final top-level tab onto a second row.");
        var settingsButtons = settingsWindow.Descendants(presentation + "Button").ToList();
        var settingsTabsRoot = settingsWindow.Descendants(presentation + "TabControl")
            .Single(element => element.Attribute(mainX + "Name")?.Value == "SettingsTabs");
        var settingsTabs = settingsTabsRoot.Elements(presentation + "TabItem").ToList();
        var safeZoneTab = settingsTabs.Single(element => element.Attribute("Header")?.Value == "Безопасная зона");
        Assert(safeZoneTab.Attribute("VerticalContentAlignment")?.Value == "Stretch" &&
            !safeZoneTab.Descendants(presentation + "ListBox").Any() &&
            safeZoneTab.Descendants(presentation + "ItemsControl").Any(element => element.Attribute(mainX + "Name")?.Value == "SafeZoneRules") &&
            safeZoneTab.Descendants(presentation + "ScrollViewer").Count() == 1 &&
            safeZoneTab.Descendants(presentation + "ScrollViewer").Single().Attribute("VerticalContentAlignment")?.Value == "Top",
            "Safe-zone compact rules lost top alignment or reintroduced the native white empty list / nested scrolling.");
        var settingsTabHeaders = settingsTabs.Select(element => element.Attribute("Header")?.Value).ToList();
        var settingsAttributeValues = settingsWindow.Root!.DescendantsAndSelf().Attributes()
            .Select(attribute => attribute.Value)
            .ToList();
        Assert(settingsButtons.Any(element =>
                element.Attribute("Content")?.Value == "Установить / обновить аддон и эффекты" &&
                element.Attribute("IsEnabled")?.Value.Contains("CanInstallBridge", StringComparison.Ordinal) == true) &&
            settingsButtons.All(element => element.Attribute("Content")?.Value != "Установить эффекты"),
            "The unified core and effect installation gate was lost.");
        Assert(settingsButtons.Any(element =>
                element.Attribute(mainX + "Name")?.Value == "CheckForUpdatesButton" &&
                element.Attribute("Content")?.Value == "Проверить обновление" &&
                element.Attribute("Click")?.Value == "CheckForUpdates_Click"),
            "The About page lost its explicit no-download update check button.");
        Assert(settingsAttributeValues.Any(value =>
                value.Contains("ShowOnlyExcludedCatalogEntries", StringComparison.Ordinal)) &&
            settingsAttributeValues.Any(value =>
                value.Contains("SelectedCatalogExclusionKindFilter", StringComparison.Ordinal)) &&
            settingsWindow.Descendants(presentation + "TextBox").Any(element =>
                element.Attribute("Tag")?.Value == "Поиск по названию или ID...") &&
            settingsButtons.Any(element =>
                element.Attribute("Content")?.Value == "+ Вручную" &&
                element.Attribute("Click")?.Value == "AddManualExclusion_Click"),
            "Catalog exclusions lost the excluded/all switch, kind filter, search hint, or manual add button.");
        var exclusionToolbar = settingsWindow.Descendants(presentation + "Grid")
            .Single(element => element.Attribute(mainX + "Name")?.Value == "CatalogExclusionToolbar");
        var toolbarHeights = exclusionToolbar.Elements()
            .Where(element => element.Name.LocalName is "TextBox" or "ComboBox" or "StackPanel" or "Button" or "Border")
            .Select(element => element.Attribute("Height")?.Value)
            .ToList();
        Assert(toolbarHeights.Count == 5 && toolbarHeights.All(height => height == "48"),
            "Catalog exclusion toolbar controls no longer share one visual height.");
        var generalSettingsLabels = settingsWindow.Descendants(presentation + "TextBlock")
            .Where(element => element.Attribute("Text")?.Value is "Скрывать недоступные секции" or "Имя анонимного донатчика" or "Данные программы" or "Работа в системном трее" or "Правила и действия")
            .ToList();
        var integrationTab = settingsTabs.Single(element => element.Attribute("Header")?.Value == "Интеграция");
        var integrationTabControl = integrationTab
            .Descendants(presentation + "TabControl")
            .Single();
        var integrationTabItems = integrationTabControl.Elements(presentation + "TabItem").ToList();
        var integrationTabs = integrationTabItems
            .Select(element => element.Attribute("Header")?.Value)
            .ToList();
        Assert(settingsTabHeaders.SequenceEqual(new[] { "Игра и редактор", "Интеграция", "Исключения", "Очередь", "Общие настройки", "Горячие клавиши", "Безопасная зона", "О программе" }) &&
            integrationTabs.SequenceEqual(new[] { "DonationAlerts", "DonateX", "Чат YouTube" }) &&
            integrationTab.Attribute("VerticalContentAlignment")?.Value == "Stretch" &&
            integrationTabControl.Attribute("VerticalContentAlignment")?.Value == "Stretch" &&
            integrationTabItems.All(element => element.Attribute("VerticalContentAlignment")?.Value == "Stretch") &&
            settingsWindow.Descendants(presentation + "PasswordBox").Any(element =>
                element.Attribute(mainX + "Name")?.Value == "DonateXTokenBox") &&
            settingsButtons.Any(element =>
                element.Attribute("Click")?.Value == "ConnectDonateX_Click") &&
            settingsWindow.Descendants().Attributes().Any(attribute =>
                attribute.Value.Contains("HideUnavailableCatalogSections", StringComparison.Ordinal)) &&
            settingsWindow.Descendants().Attributes().Any(attribute =>
                attribute.Value.Contains("AnonymousDonorName", StringComparison.Ordinal)) &&
            generalSettingsLabels.Count == 5 &&
            generalSettingsLabels.All(element => element.Ancestors(presentation + "TabItem")
                    .Any(tab => tab.Attribute("Header")?.Value == "Общие настройки")),
            "General settings lost the anonymous donor name, catalog filter, data folder, tray behavior, or rules transfer controls.");
        var scrollbarResources = XDocument.Load(Path.Combine(
            projectRoot,
            "src",
            "AnomalyDonationBridge.App",
            "App.xaml"));
        var verticalThumbStyle = scrollbarResources.Descendants(presentation + "Style")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "VerticalScrollThumbStyle");
        var horizontalThumbStyle = scrollbarResources.Descendants(presentation + "Style")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "HorizontalScrollThumbStyle");
        Assert(verticalThumbStyle.Descendants(presentation + "Setter").Any(element =>
                element.Attribute("Property")?.Value == "MinHeight" &&
                double.TryParse(element.Attribute("Value")?.Value, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var value) && value >= 48) &&
            horizontalThumbStyle.Descendants(presentation + "Setter").Any(element =>
                element.Attribute("Property")?.Value == "MinWidth" &&
                double.TryParse(element.Attribute("Value")?.Value, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var value) && value >= 48) &&
            verticalThumbStyle.Descendants(presentation + "Border").Any(element =>
                element.Attribute("CornerRadius")?.Value == "2") &&
            horizontalThumbStyle.Descendants(presentation + "Border").Any(element =>
                element.Attribute("CornerRadius")?.Value == "2"),
            "Global scrollbars lost their usable minimum thumb length or reverted to pill-shaped thumbs.");
        Assert(scrollbarResources.Descendants().Any(element =>
                element.Name.LocalName == nameof(MinimumThumbViewportConverter) &&
                element.Attribute(mainX + "Key")?.Value == "MinimumThumbViewportConverter" &&
                element.Attribute("MinimumThumbLength")?.Value == "64") &&
            scrollbarResources.Descendants(presentation + "MultiBinding").Count(element =>
                element.Attribute("Converter")?.Value.Contains("MinimumThumbViewportConverter", StringComparison.Ordinal) == true) == 2,
            "Scrollbar tracks can once again shrink their thumbs below the configured usable size.");
        var textBoxStyle = scrollbarResources.Descendants(presentation + "Style")
            .Single(element => element.Attribute("TargetType")?.Value == "TextBox" &&
                element.Attribute(mainX + "Key") is null);
        var focusedTextBoxTrigger = textBoxStyle.Descendants(presentation + "Trigger")
            .Single(element => element.Attribute("Property")?.Value == "IsKeyboardFocusWithin" &&
                element.Attribute("Value")?.Value == "True");
        Assert(focusedTextBoxTrigger.Descendants(presentation + "Setter").Any(element =>
                element.Attribute("TargetName")?.Value == "Placeholder" &&
                element.Attribute("Property")?.Value == "Visibility" &&
                element.Attribute("Value")?.Value == "Collapsed"),
            "TextBox placeholder can overlap the caret after receiving focus.");
        Assert(settingsWindow.Descendants(presentation + "Hyperlink").Any(element =>
                element.Attribute("NavigateUri")?.Value == "https://github.com/hardcoon/anomaly-donation-bridge") &&
            settingsWindow.Descendants(presentation + "TextBlock").Any(element =>
                element.Attribute("Text")?.Value == "Поддержать автора") &&
            settingsWindow.Descendants(presentation + "TextBlock").Any(element =>
                element.Attribute("Text")?.Value == "2200 3961 2613 9183") &&
            settingsButtons.Any(element =>
                element.Attribute("Content")?.Value == "Копировать" &&
                element.Attribute("Click")?.Value == "CopySupportCard_Click"),
            "The About tab lost the repository link or T-Bank support details.");
        var themeButton = window.Descendants(presentation + "Button")
            .SingleOrDefault(element => element.Attribute("Click")?.Value == "ThemePicker_Click");
        var profileButton = window.Descendants(presentation + "Button")
            .SingleOrDefault(element => element.Attribute("Content")?.Value == "Профили");
        var settingsButton = window.Descendants(presentation + "Button")
            .SingleOrDefault(element => element.Attribute("Content")?.Value == "Настройки");
        Assert(themeButton is not null &&
            themeButton.Attribute("Content")?.Value == "Тема" &&
            window.Descendants(presentation + "Popup").Any(popup =>
                popup.Attribute("StaysOpen")?.Value == "False" &&
                popup.Attribute("PlacementTarget")?.Value.Contains("ThemeButton", StringComparison.Ordinal) == true) &&
            window.Descendants(presentation + "RadioButton").Where(button =>
                button.Attribute("GroupName")?.Value == "ApplicationThemeColor")
                .Select(button => button.Attribute("Tag")?.Value).Order().SequenceEqual(new[] { "blue", "green", "red", "yellow" }) &&
            window.Descendants(presentation + "RadioButton").Where(button =>
                button.Attribute("GroupName")?.Value == "ApplicationThemeBackground")
                .Select(button => button.Attribute("Tag")?.Value).Order().SequenceEqual(new[] { "black", "white" }),
            "Theme must be a separate button and dismissible popup with four colors and independent black/white backgrounds.");
        Assert(profileButton is not null && settingsButton is not null &&
            ReferenceEquals(profileButton.Parent, settingsButton.Parent) &&
            profileButton.ElementsBeforeSelf().Count() + 1 == settingsButton.ElementsBeforeSelf().Count() &&
            profileButton.Attribute("Click")?.Value == "Profiles_Click",
            "The Profiles button is missing or is not immediately left of Settings.");

        var profilesWindow = XDocument.Load(Path.Combine(
            projectRoot,
            "src",
            "AnomalyDonationBridge.App",
            "ProfilesWindow.xaml"));
        var profileButtonLabels = profilesWindow.Descendants(presentation + "Button")
            .Select(button => button.Attribute("Content")?.Value)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert(new[] { "+ Создать", "Переименовать", "Удалить", "Использовать", "Закрыть" }
                .All(profileButtonLabels.Contains) &&
            profilesWindow.Descendants(presentation + "TextBlock").Any(text =>
                text.Attribute("Text")?.Value.Contains("и голосования", StringComparison.Ordinal) == true),
            "The profile manager lost its create/rename/delete/switch controls or scope explanation.");

        var parameterTemplate = mainWindow.Descendants(presentation + "DataTemplate")
            .SingleOrDefault(element => element.Attribute(mainX + "Key")?.Value == "ParameterTemplate");
        var activeCardTemplate = mainWindow.Descendants(presentation + "DataTemplate")
            .SingleOrDefault(element => element.Attribute(mainX + "Key")?.Value == "ActiveActionCardTemplateV2");
        var presetCardTemplate = mainWindow.Descendants(presentation + "DataTemplate")
            .SingleOrDefault(element => element.Attribute(mainX + "Key")?.Value == "PresetCardTemplateV2");
        var editorTemplate = mainWindow.Descendants(presentation + "DataTemplate")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "ActionEditorTemplate");
        var editorAttributeValues = editorTemplate.DescendantsAndSelf().Attributes().Select(attribute => attribute.Value).ToList();
        Assert(editorAttributeValues.Count(value => value.Contains("Picker.CatalogOptionsView", StringComparison.Ordinal)) == 2 &&
            editorAttributeValues.Count(value => value.Contains("Picker.CatalogSearch", StringComparison.Ordinal)) == 2 &&
            editorAttributeValues.Count(value => value.Contains("Picker.SelectedCatalogCategory", StringComparison.Ordinal)) == 2 &&
            editorAttributeValues.All(value => !value.Contains("Strength", StringComparison.Ordinal)) &&
            editorAttributeValues.Count(value => value.Contains("CountText", StringComparison.Ordinal)) == 2,
            "Grouped NPC, mutant and item editors lost the compact category/search/selection/count rows.");
        var compactSearchStyle = mainWindow.Descendants(presentation + "Style")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "CompactEditorSearchTextBoxStyle");
        Assert(compactSearchStyle.Descendants(presentation + "Setter").Any(setter =>
                setter.Attribute("Property")?.Value == "Padding" && setter.Attribute("Value")?.Value == "4,4") &&
            editorTemplate.Descendants(presentation + "TextBox").Count(textBox =>
                textBox.Attribute("Style")?.Value.Contains("CompactEditorSearchTextBoxStyle", StringComparison.Ordinal) == true) == 2,
            "Compact search fields lost the shared tight caret/watermark inset.");
        var compactCatalogTemplate = mainWindow.Descendants(presentation + "DataTemplate")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "CompactCatalogOptionTemplate");
        Assert(compactCatalogTemplate.Descendants(presentation + "DataTrigger").Any(trigger =>
                trigger.Attribute("Binding")?.Value.Contains("IsAvailable", StringComparison.Ordinal) == true &&
                trigger.Attribute("Value")?.Value == "False" &&
                trigger.Descendants(presentation + "Setter").Any(setter =>
                    setter.Attribute("Value")?.Value.Contains("DangerTextBrush", StringComparison.Ordinal) == true)) &&
            !compactCatalogTemplate.Descendants(presentation + "Setter").Any(setter =>
                setter.Attribute("Value")?.Value.Contains("BadDarkBrush", StringComparison.Ordinal) == true),
            "Unavailable catalog entries must use pale red text without a red background.");
        var parameterLabel = parameterTemplate?.Descendants(presentation + "TextBlock")
            .FirstOrDefault(element => element.Attribute("Text")?.Value.Contains("DisplayName", StringComparison.Ordinal) == true);
        Assert(mainWindow.Descendants().Any(element =>
                element.Name.LocalName == nameof(ParameterColumnWidthConverter) &&
                element.Attribute(mainX + "Key")?.Value == "ParameterColumnWidth") &&
            editorTemplate.Descendants(presentation + "WrapPanel").Count(element =>
                element.Elements(presentation + "WrapPanel.ItemWidth").Any()) == 1 &&
            new[] { activeCardTemplate, presetCardTemplate }.All(template =>
                template?.Descendants(presentation + "ContentPresenter").Any(element =>
                    element.Attribute("ContentTemplate")?.Value.Contains("PdaActionCardContentTemplate", StringComparison.Ordinal) == true) == true) &&
            !mainWindow.Descendants(presentation + "UniformGrid").Any(element =>
                element.Attributes().Any(attribute =>
                    attribute.Value.Contains("CompactParameterColumns", StringComparison.Ordinal))) &&
            parameterLabel?.Attribute("VerticalAlignment")?.Value == "Top" &&
            parameterTemplate?.Elements(presentation + "Grid").Elements(presentation + "Grid.RowDefinitions").Any() == true &&
            parameterTemplate.Descendants(presentation + "StackPanel").Any(element => element.Attribute("Grid.Row")?.Value == "1"),
            "Parameter rows can still inherit the tallest catalog editor height or drift away from their own input.");
        var parameterWidth = new ParameterColumnWidthConverter();
        Assert(parameterWidth.CalculateItemWidth(900, 1) == 900 &&
            parameterWidth.CalculateItemWidth(900, 2) == 450 &&
            parameterWidth.CalculateItemWidth(900, 3) == 300,
            "The variable-height parameter panel no longer preserves the requested column widths.");
        var minimumThumbViewport = new MinimumThumbViewportConverter { MinimumThumbLength = 64 };
        var adjustedViewport = minimumThumbViewport.CalculateViewportSize(10, 0, 9990, 400);
        var resultingThumbLength = 398 * adjustedViewport / (9990 + adjustedViewport);
        Assert(resultingThumbLength >= 63.9,
            "The scrollbar viewport adjustment no longer enforces the visible minimum thumb length.");

        var appResources = XDocument.Load(Path.Combine(
            projectRoot,
            "src",
            "AnomalyDonationBridge.App",
            "App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resourceKeys = appResources.Descendants()
            .Select(element => element.Attribute(x + "Key")?.Value)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert(resourceKeys.Contains("BackgroundColor") && resourceKeys.Contains("BackgroundBrush") &&
            resourceKeys.Contains("TextColor") && resourceKeys.Contains("TextBrush"),
            "Theme color/brush resources are incomplete.");
        var tabControlTemplate = appResources.Descendants(presentation + "ControlTemplate")
            .Single(template => template.Attribute("TargetType")?.Value == "TabControl");
        var tabItemStyle = appResources.Descendants(presentation + "Style")
            .Single(style => style.Attribute("TargetType")?.Value == "TabItem" && style.Attribute(mainX + "Key") is null);
        Assert(tabControlTemplate.Descendants(presentation + "WrapPanel").Any(panel =>
                panel.Attribute("IsItemsHost")?.Value == "True") &&
            !tabControlTemplate.Descendants(presentation + "TabPanel").Any() &&
            tabItemStyle.Descendants(presentation + "Setter").Any(setter =>
                setter.Attribute("Property")?.Value == "Margin" &&
                setter.Attribute("Value")?.Value == "0,0,7,7"),
            "Wrapped setting tabs can reorder themselves or have no stable row spacing.");
        var comboDropDownBorder = appResources.Descendants(presentation + "Border")
            .Single(element => element.Attribute(mainX + "Name")?.Value == "DropDownBorder");
        Assert(comboDropDownBorder.Attribute("Width")?.Value.Contains("ActualWidth", StringComparison.Ordinal) == true &&
            comboDropDownBorder.Attribute("MinWidth") is null,
            "ComboBox dropdown width can still jump when its scrollbar appears.");

        var templateKeys = window.Descendants(presentation + "DataTemplate")
            .Select(element => element.Attribute(mainX + "Key")?.Value)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert(templateKeys.Contains("ActiveActionCardTemplateV2") &&
            templateKeys.Contains("PresetCardTemplateV2") &&
            templateKeys.Contains("CompactActiveActionRowTemplate") &&
            templateKeys.Contains("ActivePriceGroupCardTemplate") &&
            templateKeys.Contains("CompactActivePriceGroupRowTemplate") &&
            templateKeys.Contains("ActiveActionListEntryTemplate") &&
            templateKeys.Contains("CompactActiveActionListEntryTemplate") &&
            templateKeys.Contains("CompactUserPresetRowTemplate") &&
            attributeValues.Any(value => value.Contains("ActiveActionCardTemplateV2", StringComparison.Ordinal)) &&
            attributeValues.Any(value => value.Contains("PresetCardTemplateV2", StringComparison.Ordinal)) &&
            attributeValues.Any(value => value.Contains("CompactActiveActionRowTemplate", StringComparison.Ordinal)) &&
            attributeValues.Any(value => value.Contains("CompactUserPresetRowTemplate", StringComparison.Ordinal)),
            "The polished action-card templates are not connected to the live lists.");
        var activeList = window.Descendants(presentation + "ItemsControl")
            .Single(element => element.Attribute(mainX + "Name")?.Value == "ActiveActionsList");
        Assert(activeList.Attribute("ItemsSource")?.Value.Contains("ActiveBrowser.VisibleEntries", StringComparison.Ordinal) == true,
            "The Active list does not consume the equal-price visual grouping projection.");
        foreach (var key in new[] { "ActivePriceGroupCardTemplate", "CompactActivePriceGroupRowTemplate" })
        {
            var groupTemplate = window.Descendants(presentation + "DataTemplate")
                .Single(element => element.Attribute(mainX + "Key")?.Value == key);
            Assert(groupTemplate.Descendants(presentation + "Grid").Any(element =>
                    element.Attribute("MouseLeftButtonUp")?.Value == "PriceGroupHeader_MouseLeftButtonUp" &&
                    element.Attribute("KeyDown")?.Value == "PriceGroupHeader_KeyDown") &&
                groupTemplate.Descendants(presentation + "ItemsControl").Any(element =>
                    element.Attribute("ItemsSource")?.Value.Contains("Actions", StringComparison.Ordinal) == true) &&
                groupTemplate.Descendants(presentation + "Button").Any(element =>
                    element.Attribute("AutomationProperties.Name")?.Value == "Переименовать группу" &&
                    element.Attribute("Command")?.Value.Contains("RenamePriceGroupCommand", StringComparison.Ordinal) == true) &&
                !groupTemplate.Descendants(presentation + "Button").Any(element =>
                    element.Attribute("Content")?.Value is "Название" or "Переименовать") &&
                groupTemplate.Descendants(presentation + "Button").Any(element =>
                    element.Attribute("Content")?.Value == "Тест" &&
                    element.Attribute("Command")?.Value.Contains("TestPriceGroupCommand", StringComparison.Ordinal) == true) &&
                groupTemplate.Descendants(presentation + "Button").Any(element =>
                    element.Attribute("Content")?.Value == "+ Сумма" &&
                    element.Attribute("Command")?.Value.Contains("AddPriceGroupTriggerCommand", StringComparison.Ordinal) == true),
                $"Equal-price group template {key} lost expansion, grouped actions, rename, Test or Add amount.");
        }
        var compactActiveTemplate = window.Descendants(presentation + "DataTemplate")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "CompactActiveActionRowTemplate");
        var compactGroupTemplate = window.Descendants(presentation + "DataTemplate")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "CompactActivePriceGroupRowTemplate");
        var compactActiveGrid = compactActiveTemplate.Descendants(presentation + "Grid")
            .Single(element => element.Elements(presentation + "Grid.ColumnDefinitions")
                .Elements(presentation + "ColumnDefinition").Count() == 4);
        var compactGroupHeader = compactGroupTemplate.Descendants(presentation + "Grid")
            .Single(element => element.Attribute("MouseLeftButtonUp")?.Value == "PriceGroupHeader_MouseLeftButtonUp");
        var compactActiveColumns = compactActiveGrid.Elements(presentation + "Grid.ColumnDefinitions")
            .Elements(presentation + "ColumnDefinition")
            .Select(column => $"{column.Attribute("Width")?.Value}|{column.Attribute("MinWidth")?.Value}");
        var compactGroupColumns = compactGroupHeader.Elements(presentation + "Grid.ColumnDefinitions")
            .Elements(presentation + "ColumnDefinition")
            .Select(column => $"{column.Attribute("Width")?.Value}|{column.Attribute("MinWidth")?.Value}");
        var compactGroupChildren = compactGroupTemplate.Descendants(presentation + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value.Contains("Actions", StringComparison.Ordinal) == true);
        Assert(compactGroupColumns.Skip(1).SequenceEqual(compactActiveColumns.Skip(1)) &&
            compactGroupChildren.Attribute("Margin")?.Value == "18,1,-1,4" &&
            compactGroupHeader.Elements().Any(element =>
                element.Name.LocalName == "CardToolbarPanel" && element.Attribute("Grid.Column")?.Value == "3") &&
            compactGroupHeader.Descendants(presentation + "Button").Any(element =>
                element.Attribute("Width")?.Value == "32" &&
                element.Attribute("AutomationProperties.Name")?.Value == "Переименовать группу"),
            "Compact price-group columns, prices or nested action buttons no longer align with ordinary active rows.");
        var compactUserTemplate = window.Descendants(presentation + "DataTemplate")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "CompactUserPresetRowTemplate");
        Assert(compactActiveTemplate.Descendants(presentation + "ItemsControl").Any(element =>
                element.Attribute("ItemsSource")?.Value.Contains("Triggers", StringComparison.Ordinal) == true) &&
            new[] { "Настроить", "+ Сумма" }.All(label =>
                compactActiveTemplate.Descendants(presentation + "Button").Any(element =>
                    element.Attribute("Content")?.Value == label)) &&
            compactActiveTemplate.Descendants(presentation + "Button").Any(element =>
                element.Attribute("Content")?.Value == "+ Сумма" &&
                element.Attribute("Command")?.Value.Contains("AddTriggerCommand", StringComparison.Ordinal) == true),
            "Compact active rows do not show all prices or the direct Configure/Add amount buttons.");
        Assert(compactUserTemplate.Descendants(presentation + "ItemsControl").Any(element =>
                element.Attribute("ItemsSource")?.Value.Contains("FirstThreeTriggers", StringComparison.Ordinal) == true) &&
            new[] { "Настроить", "+ Сумма", "В активные" }.All(label =>
                compactUserTemplate.Descendants(presentation + "Button").Any(element =>
                    element.Attribute("Content")?.Value == label)),
            "Compact user-preset rows do not show the first three prices and direct action buttons.");
        var activeTemplate = window.Descendants(presentation + "DataTemplate")
            .Single(element => element.Attribute(mainX + "Key")?.Value == "PdaActionCardContentTemplate");
        var summaryItems = activeTemplate.Descendants(presentation + "ItemsControl").Single(element =>
            element.Attribute("ItemsSource")?.Value == "{Binding ParameterSummaryItems}");
        Assert(summaryItems.Attribute("Visibility")?.Value.Contains("DataContext.ShowTemplateDescriptions", StringComparison.Ordinal) == true &&
            summaryItems.Attribute("Visibility")?.Value.Contains("BoolToVisibility", StringComparison.Ordinal) == true,
            "The Description switch no longer hides selected settings on cards.");
        Assert(!activeTemplate.Descendants().Attributes().Any(attribute =>
            attribute.Value.Contains("ResultSummary", StringComparison.Ordinal) ||
            attribute.Value.Contains("LastResult", StringComparison.Ordinal) ||
            attribute.Value.Contains("Результат:", StringComparison.Ordinal)),
            "Execution results have returned to template cards instead of staying in history.");
        var ordinalLabel = activeTemplate.Descendants(presentation + "TextBlock")
            .SingleOrDefault(element =>
                element.Attribute("Text")?.Value.Contains("DisplayOrdinalLabel", StringComparison.Ordinal) == true);
        Assert(ordinalLabel?.Attribute("Foreground")?.Value.Contains("AccentBrush", StringComparison.Ordinal) == true,
            "Active action sequence numbers are missing or are not highlighted with the gold accent.");
        var cardButtons = activeTemplate.Descendants(presentation + "Button").ToList();
        var saveBuiltInButton = cardButtons.SingleOrDefault(button => button.Attribute("Content")?.Value == "В пользовательские");
        var activateUserButton = cardButtons.SingleOrDefault(button => button.Attribute("Content")?.Value == "В активные");
        var pricePresenter = activeTemplate.Descendants(presentation + "ContentPresenter").Single(element =>
            element.Attribute("ContentTemplate")?.Value.Contains("PriceConditionsTemplate", StringComparison.Ordinal) == true);
        Assert(saveBuiltInButton?.Attribute("Visibility")?.Value.Contains("IsBuiltInPreset", StringComparison.Ordinal) == true &&
            saveBuiltInButton.Attribute("Command")?.Value.Contains("SaveAsUserPresetCommand", StringComparison.Ordinal) == true &&
            activateUserButton?.Attribute("Visibility")?.Value.Contains("IsUserPreset", StringComparison.Ordinal) == true &&
            activateUserButton.Attribute("Command")?.Value.Contains("AddFromPresetCommand", StringComparison.Ordinal) == true &&
            pricePresenter.Attribute("Visibility")?.Value.Contains("IsBuiltInPreset", StringComparison.Ordinal) == true,
            "Built-in presets can still edit prices or bypass the user-preset activation pipeline.");

        var activeTab = window.Descendants(presentation + "TabItem")
            .Single(element => element.Descendants(presentation + "TextBlock")
                .Any(label => label.Attribute("Text")?.Value == "Активные"));
        var presetTab = window.Descendants(presentation + "TabItem")
            .Single(element => element.Descendants(presentation + "TextBlock")
                .Any(label => label.Attribute("Text")?.Value == "Шаблоны"));
        Assert(new[] { activeTab, presetTab }.All(tab =>
        {
            var wrapper = tab.Elements(presentation + "Border").SingleOrDefault();
            return wrapper?.Attribute("Background")?.Value == "Transparent" &&
                wrapper.Attribute("BorderThickness")?.Value == "0";
        }), "An extra panel card still surrounds the active or preset card list.");

        var numberedAction = new ActionViewModel(
            DefaultActionCatalog.CreatePresets().First(),
            displayOrdinal: 7);
        Assert(numberedAction.DisplayOrdinalLabel == "№7",
            "The active action ordinal label is not deterministic.");
        Assert(numberedAction.VisualDisplayOrdinalLabel == "№7",
            "The active action visual ordinal does not default to its persistent ordinal.");
        Assert(attributeValues.Any(value => value.Contains("CooldownText", StringComparison.Ordinal)) &&
            window.Descendants(presentation + "Style").Any(element =>
                element.Attribute(mainX + "Key")?.Value == "ReplaceOnFocusTextBoxStyle" &&
                element.Descendants(presentation + "EventSetter").Count() == 2),
            "Numeric parameter fields lost their replace-on-focus and transient-input bindings.");
        Assert(attributeValues.Any(value => value.Contains("BooleanValue", StringComparison.Ordinal)) &&
            attributeValues.Any(value => value.Contains("IsBooleanChoice", StringComparison.Ordinal)),
            "Boolean preset options are still rendered as true/false combo boxes.");

        var triggerEditor = XDocument.Load(Path.Combine(
            projectRoot,
            "src",
            "AnomalyDonationBridge.App",
            "TriggerEditorWindow.xaml"));
        var triggerAttributes = triggerEditor.Root!.DescendantsAndSelf().Attributes()
            .Select(attribute => attribute.Value)
            .ToList();
        Assert(triggerAttributes.Any(value => value.Contains("CalculateAll_Click", StringComparison.Ordinal)) &&
            triggerAttributes.Any(value => value.Contains("HasCalculatedPrices", StringComparison.Ordinal)) &&
            triggerAttributes.Any(value => value.Contains("SaveButtonText", StringComparison.Ordinal)),
            "The all-currency calculator is not connected to the trigger editor UI.");
        return Task.CompletedTask;
    }

    private static Task ThemeAndTrayContractsAsync()
    {
        var application = Application.Current as AnomalyDonationBridge.App.App ??
            new AnomalyDonationBridge.App.App(startRuntime: false);
        if (!application.Resources.Contains("BackgroundColor"))
        {
            application.InitializeComponent();
        }
        ThemeManager.Apply(useLightTheme: false);
        var dark = (Color)application.Resources["BackgroundColor"];
        Assert(!ThemeManager.IsLightTheme, "Dark theme state was not applied.");
        ThemeManager.Apply(useLightTheme: true);
        var light = (Color)application.Resources["BackgroundColor"];
        Assert(ThemeManager.IsLightTheme && dark != light,
            "Light theme did not replace the live resource palette.");
        ThemeManager.Apply(useLightTheme: false);
        Assert((Color)application.Resources["BackgroundColor"] == dark,
            "Theme roundtrip did not restore the dark palette.");
        foreach (var lightMode in new[] { false, true })
        {
            ThemeManager.Apply(lightMode);
            var success = (Color)application.Resources["GoodColor"];
            var failure = (Color)application.Resources["BadColor"];
            var warning = (Color)application.Resources["WarningTextColor"];
            var accentValues = new HashSet<Color>();
            foreach (var color in new[] { "red", "green", "blue", "yellow" })
            {
                ThemeManager.Apply(lightMode, color);
                Assert(ThemeManager.ColorKey == color && ThemeManager.IsLightTheme == lightMode,
                    "Color and background are coupled.");
                Assert((Color)application.Resources["GoodColor"] == success &&
                    (Color)application.Resources["BadColor"] == failure &&
                    (Color)application.Resources["WarningTextColor"] == warning,
                    "A theme changed semantic success/error/warning colors.");
                accentValues.Add((Color)application.Resources["AccentColor"]);
                var background = (Color)application.Resources["BackgroundColor"];
                Assert(Math.Max(background.R, Math.Max(background.G, background.B)) -
                    Math.Min(background.R, Math.Min(background.G, background.B)) <= 8,
                    "Theme background is not muted.");
            }
            Assert(accentValues.Count == 4, "Theme presets do not have distinct accents.");
        }
        ThemeManager.Apply(false, "not-a-theme");
        Assert(ThemeManager.ColorKey == "yellow" && (Color)application.Resources["BackgroundColor"] == dark,
            "Invalid color did not restore the existing yellow theme.");
        var contextMenuStyle = (Style)application.Resources[typeof(ContextMenu)];
        Assert(contextMenuStyle.Setters.OfType<Setter>().Any(setter =>
            setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate),
            "Context menus must own their template to avoid the system icon gutter.");
        var menuItemStyle = (Style)application.Resources[typeof(MenuItem)];
        Assert(menuItemStyle.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == MenuItem.IsHighlightedProperty) &&
            menuItemStyle.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusWithinProperty),
            "Menu mouse and keyboard focus states are missing.");
        var dangerMenuStyle = (Style)application.Resources["DangerMenuItemStyle"];
        Assert(ReferenceEquals(dangerMenuStyle.BasedOn, menuItemStyle) &&
            dangerMenuStyle.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == MenuItem.IsHighlightedProperty &&
                trigger.Setters.OfType<Setter>().Any(setter => setter.Property == Control.BackgroundProperty)),
            "Danger menu commands lost their distinct hover state.");

        var trayType = typeof(TrayIconController);
        Assert(typeof(IDisposable).IsAssignableFrom(trayType),
            "Tray controller no longer owns a disposable native lifecycle.");
        Assert(trayType.GetMethod(nameof(TrayIconController.PrepareForShutdown)) is { IsPublic: true },
            "Tray controller lost its explicit shutdown contract.");
        Assert(trayType.GetConstructors().Any(constructor => constructor.GetParameters().Length == 4),
            "Tray controller lost the non-static window/view-model integration contract.");
        var activeIcon = System.Drawing.SystemIcons.Application;
        var pausedIcon = System.Drawing.SystemIcons.Warning;
        Assert(ReferenceEquals(TrayIconController.SelectTrayIcon(true, activeIcon, pausedIcon), activeIcon) &&
            ReferenceEquals(TrayIconController.SelectTrayIcon(false, activeIcon, pausedIcon), pausedIcon),
            "The tray icon no longer follows the donation-processing state.");
        var pausedResource = Application.GetResourceStream(new Uri(
            "/AnomalyDonationBridge;component/Assets/AnomalyDonationBridge.Paused.ico",
            UriKind.Relative));
        Assert(pausedResource is not null, "The monochrome paused tray icon is not packaged as an app resource.");
        pausedResource?.Stream.Dispose();
        return Task.CompletedTask;
    }

    private static CatalogSnapshot CreateCatalog() => new(
        new CatalogRevision("session:test-1"),
        new string('a', 64),
        [
            new CatalogEntry
            {
                SectionId = "pseudodog_normal",
                Kind = CatalogEntryKind.Mutant,
                DisplayName = "Псевдособака",
                Category = "Псы",
                Strength = CatalogStrength.Normal,
                IsAvailable = true,
                QuarantineReason = QuarantineReason.None
            },
            new CatalogEntry
            {
                SectionId = "story_chimera",
                Kind = CatalogEntryKind.Mutant,
                DisplayName = "Сюжетная химера",
                Category = "Химеры",
                Strength = CatalogStrength.Strong,
                IsAvailable = false,
                QuarantineReason = QuarantineReason.StoryOrUnique
            }
        ]);

    private static async Task WritePackageAsync(
        string packageRoot,
        string version,
        string script,
        bool includeObsoleteFile = false,
        bool includeLegacyMgun = false)
    {
        var relative = "gamedata/scripts/anomaly_donation_bridge_main.script";
        var full = Path.Combine(packageRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, script);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(full)));
        var obsoleteRelative = "gamedata/scripts/anomaly_donation_bridge_obsolete.script";
        var obsoleteFull = Path.Combine(packageRoot, obsoleteRelative.Replace('/', Path.DirectorySeparatorChar));
        if (includeObsoleteFile)
        {
            await File.WriteAllTextAsync(obsoleteFull, "return 'obsolete'\n");
        }
        else if (File.Exists(obsoleteFull))
        {
            File.Delete(obsoleteFull);
        }
        var files = new List<AddonPackageFile> { new() { Path = relative, Sha256 = hash } };
        if (includeLegacyMgun)
        {
            const string legacy = "gamedata/meshes/anomaly_donation_bridge/stationary_mgun_r020.ogf";
            var legacyFile = Path.Combine(packageRoot, legacy);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
            await File.WriteAllTextAsync(legacyFile, "mock legacy native visual");
            files.Add(new() { Path = legacy, Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(legacyFile))) });
        }
        if (includeObsoleteFile)
        {
            files.Add(new AddonPackageFile
            {
                Path = obsoleteRelative,
                Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(obsoleteFull)))
            });
        }
        var manifest = new AddonPackageManifest
        {
            SchemaVersion = 1,
            Product = AddonPackageManager.ProductId,
            AddonVersion = version,
            ProtocolVersion = AddonPackageManager.ProtocolVersion,
            PipeName = AddonPackageManager.PipeName,
            Files = files
        };
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "addon-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string FindProjectRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "version.json")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "src", "AnomalyDonationBridge.App")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Не удалось определить корень AnomalyDonationBridge для тестового прогона.");
    }

    private static bool IsInside(string candidate, string parent)
    {
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _projectRoot;

        public TemporaryDirectory()
        {
            _projectRoot = FindProjectRoot();
            var configuredRoot = Environment.GetEnvironmentVariable("ADB_TEST_ROOT");
            var testRoot = string.IsNullOrWhiteSpace(configuredRoot)
                ? System.IO.Path.Combine(_projectRoot, "work", "tests", "runtime")
                : System.IO.Path.IsPathRooted(configuredRoot)
                    ? System.IO.Path.GetFullPath(configuredRoot)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectRoot, configuredRoot));
            if (!IsInside(testRoot, _projectRoot))
            {
                throw new InvalidOperationException("ADB_TEST_ROOT должен находиться внутри проекта AnomalyDonationBridge.");
            }
            Path = System.IO.Path.Combine(testRoot, "app-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!IsInside(Path, _projectRoot))
            {
                throw new InvalidOperationException("Отказ от удаления тестовой папки вне проекта.");
            }
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }

    private sealed class FixedExchangeRateProvider(ExchangeRateSnapshot snapshot) : IExchangeRateProvider
    {
        public int Requests { get; private set; }
        public Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            Requests++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class DeferredExchangeRateProvider(Task<ExchangeRateSnapshot> result) : IExchangeRateProvider
    {
        public Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken = default) => result;
    }
}
