using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.DonationAlerts;
using AnomalyDonationBridge.DonateX;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;
using System.Text.Json;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace AnomalyDonationBridge.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("YouTube chat protocol, reconnect, quotas, isolation and protected settings", YouTubeChatTests.RunAsync),
            ("voting conditions, accounting, atomic persistence and outbox", VotingTests.RunAsync),
            ("YouTube voting sources, identity, atomic batches, round boundaries and HUD formats", YouTubeVotingTests.RunAsync),
            ("default catalog", TestDefaultCatalogAsync),
            ("dynamic catalog fixtures", TestDynamicCatalogFixturesAsync),
            ("catalog quarantine and stale revision", TestCatalogQuarantineAndStaleRevisionAsync),
            ("catalog exclusions and history selection", TestCatalogExclusionsAndHistorySelectionAsync),
            ("manifest is authoritative and preserves random selection", TestManifestCatalogAndMultiChoiceMigrationAsync),
            ("active action price ordering", TestActionPriceOrderingAsync),
            ("scenario reordering, strict retries and durable sequence", SequenceTests.RunAsync),
            ("group pause overrides global pause without leaking across donations", GroupDelayTests.RunAsync),
            ("spawn navigation retries bypass gameplay pauses without leaking to other commands", SpawnPreparationTests.RunAsync),
            ("selected queued commands execute immediately without replaying completed or unselected events", ImmediateQueueTests.RunAsync),
            ("needs wait polls safely without cooldown reservation, duplicates or queue blocking", NeedsQueueTests.RunAsync),
            ("BTR passenger preserves queue IDs and permits safe effects without a cooldown reset", BtrQueueTests.RunAsync),
            ("history shows latest status per event", TestEventHistoryProjectionAsync),
            ("preset instances are independent", TestPresetInstancesAsync),
            ("legacy settings migrate to instances", TestLegacySettingsMigrationAsync),
            ("legacy handlers are not collapsed", TestLegacyHandlersNotCollapsedAsync),
            ("retired built-in actions are removed", TestRetiredBuiltInActionsAsync),
            ("schema v2 actions are active by membership", TestSchemaV2MembershipAsync),
            ("schema v2 requires handler id", TestSchemaV2RequiresHandlerAsync),
            ("schema v6 preserves file-authored preset metadata", TestFileAuthoredMetadataAsync),
            ("duplicate trigger ids are repaired", TestDuplicateTriggerIdsRepairedAsync),
            ("same handler instances dispatch independently", TestSameHandlerInstancesAsync),
            ("donation batch carries HUD and save context", TestDonationBatchContextAsync),
            ("exact priority", TestExactPriorityAsync),
            ("highest threshold", TestHighestThresholdAsync),
            ("strict currency", TestStrictCurrencyAsync),
            ("decimal money", TestMoneyAsync),
            ("duplicate trigger validation", TestDuplicateTriggerAsync),
            ("skip policy executes when ready", TestSkipReadyAsync),
            ("legacy skip policy preserves unavailable", TestLegacySkipPreservesAsync),
            ("readiness race leaves event queued", TestReadinessRaceAsync),
            ("legacy queue TTL is migrated", TestLegacyQueueTtlAsync),
            ("action cooldown cannot block queue", TestActionCooldownFairnessAsync),
            ("uncertain is at-most-once", TestUncertainAsync),
            ("wire percent encoding", TestWireEncodingAsync),
            ("wire size limit", TestWireLimitAsync),
            ("first run has no backlog", TestFirstRunRecoveryAsync),
            ("recovery TTL", TestRecoveryTtlAsync),
            ("recovery ignores advanced terminal checkpoint", TestRecoveryContinuityAsync),
            ("Centrifugo subscription response", TestSubscriptionResponseAsync),
            ("JSON settings persistence", TestJsonSettingsAsync),
            ("profile migration and isolation", TestProfilePersistenceAsync),
            ("DPAPI secret roundtrip", TestDpapiAsync)
            ,("secret log redaction", TestLogRedactionAsync)
            ,("OAuth refresh preserves token", TestRefreshTokenAsync)
            ,("OAuth loopback and state", TestOAuthLoopbackAsync)
            ,("DonationAlerts API rate limiter", TestDonationAlertsApiRateLimiterAsync)
            ,("DonationAlerts REST requests use limiter", TestDonationAlertsRestUsesRateLimiterAsync)
            ,("DonateX payload and REST authentication", TestDonateXRestAsync)
            ,("DonateX API rate limiter defaults", TestDonateXRateLimiterAsync)
            ,("retired integration credentials are removed without affecting current providers", RetiredIntegrationTests.RunAsync)
            ,("schema 14 is read without raising the persisted schema", TestSchema14CompatibilityAsync)
            ,("settings writes retain the previous backup", TestSettingsBackupAsync)
            ,("IPC waits safely without game", TestIpcIdleWithoutGameAsync)
            ,("IPC death and loading preserve queued effects", TestIpcDeathQueueAsync)
            ,("IPC app-first and result", TestIpcAppFirstAsync)
            ,("IPC manual refresh recovers exact result", TestIpcManualResultRecoveryAsync)
            ,("IPC manual refresh waits for ready without disconnect", TestIpcManualRecoveryWhileLoadingAsync)
            ,("IPC game-first and loading reconnect", TestIpcGameFirstAsync)
            ,("deferred retry moves to queue tail", TestDeferredRetryAsync)
            ,("legacy pending snapshot is guaranteed", TestLegacyPendingRestoreAsync)
            ,("queued recovery and sequential dispatch", TestQueuedRecoveryAsync)
            ,("remove selected queued event", TestQueueCancellationAsync)
            ,("queue removal persists across restart", TestQueueCancellationPersistenceAsync)
            ,("interrupted send history recovery", TestInterruptedSendHistoryRecoveryAsync)
            ,("invalid action is rejected before IPC", TestInvalidActionRejectedAsync)
            ,("donor-named effects receive donor name", TestNamedCompanionDonorNameAsync)
            ,("ordered preset ranges are validated", TestOrderedPresetRangesAsync)
            ,("random preset resolves to an active action", TestRandomPresetAsync)
            ,("selected active instances preserve settings and retry identity", TestRandomAllPresetsAsync)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
            }
        }
        Console.WriteLine($"RESULT {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestDefaultCatalogAsync()
    {
        True(string.IsNullOrEmpty(new AppSettings().ExpectedAccountCode), "public install is not locked to one streamer");
        True(new AppSettings().LimitDonationAlertsApiRequests, "DonationAlerts API limiter is enabled by default");
        True(new AppSettings().LimitDonateXApiRequests, "DonateX API limiter is enabled by default");
        Equal("Анонимный донатчик", new AppSettings().AnonymousDonorName,
            "legacy anonymous donor name remains the default");
        var catalog = DefaultActionCatalog.Create();
        Equal(3, catalog.Count, "active default count");
        SequenceEqual(new[] { "add_radiation", "drop_active_weapon", "spawn_mutants" }, catalog.Select(x => x.HandlerId), "active default handlers");
        Equal(3, catalog.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count(), "active instance ids");
        Equal(1m, catalog.Single(x => x.HandlerId == "add_radiation").Triggers.Single().Amount, "radiation price");
        var weaponDrop = catalog.Single(x => x.HandlerId == "drop_active_weapon");
        Equal(2m, weaponDrop.Triggers.Single().Amount, "weapon price");
        Equal("30", weaponDrop.Parameters["wait_seconds"], "weapon wait defaults to 30 seconds");
        var weaponWait = weaponDrop.ParameterDefinitions.Single(x => x.Key == "wait_seconds");
        Equal(0m, weaponWait.Minimum, "weapon wait minimum");
        Equal(300m, weaponWait.Maximum, "weapon wait maximum");
        Equal("0", weaponDrop.Parameters["duration_seconds"], "weapon drop period defaults to one shot");
        var weaponDuration = weaponDrop.ParameterDefinitions.Single(x => x.Key == "duration_seconds");
        Equal(0m, weaponDuration.Minimum, "weapon drop period accepts the one-shot sentinel");
        Equal(600m, weaponDuration.Maximum, "weapon drop period maximum");
        var mutants = catalog.Single(x => x.HandlerId == "spawn_mutants");
        Equal(3m, mutants.Triggers.Single().Amount, "mutant price");
        Equal(string.Empty, mutants.Parameters["section_id"], "dynamic mutant section is empty before synchronization");
        Equal("3", mutants.Parameters["count"], "mutant count");
        True(mutants.CatalogRevision is null, "dynamic preset has no invented catalog revision");
        True(mutants.Validate().Any(error => error.Contains("каталог", StringComparison.OrdinalIgnoreCase)),
            "dynamic preset is blocked until the live catalog is synchronized");

        var presets = DefaultActionCatalog.CreatePresets();
        Equal(41, presets.Count, "unique template count");
        SequenceEqual(new[]
        {
            "add_radiation", "drop_active_weapon", "block_inventory", "spawn_mutants", "spawn_npcs",
            "spawn_npc_squads", "spawn_mutant_squads",
            "spawn_random_hostile_squad", "spawn_random_mutant_pack",
            "spawn_random_npcs", "spawn_random_mutants", "spawn_named_companion", "spawn_mutant_companion", "spawn_donor_boss", "kill_companions", "change_health",
            "change_needs", "spawn_items", "change_money", "spawn_anomaly", "get_drunk",
            "trigger_emission", "give_random_stash", "tell_joke", "sleep_hours", "remove_equipment", "damage_equipment", "god_mode",
            "drop_random_items", "teleport_random", "teleport_random_level",
            "delete_recent_saves", "kill_nearby_npcs", DonationDispatcher.RandomActiveHandlerId,
            DonationDispatcher.RandomAllPresetsHandlerId, "use_random_consumable",
            "change_player_faction", "change_player_name", "actor_boost", "actor_parameters", "hide_hud"
        }, presets.Select(x => x.HandlerId), "template display order follows the documented 1..41 list");
        Equal(presets.Count, presets.Select(x => x.HandlerId).Distinct(StringComparer.Ordinal).Count(), "preset handlers unique");
        True(presets.All(x => !x.IsActive && x.Triggers.Count == 0), "presets are not active instances");
        True(presets.All(x => x.Id == x.HandlerId), "preset ids are stable");
        True(presets.All(x => x.HandlerId != "spawn_dogs_3"), "legacy dog preset removed");
        var donorBoss = presets.Single(x => x.HandlerId == "spawn_donor_boss");
        Equal("10000", donorBoss.Parameters["reward_money"], "donor boss default reward");
        Equal("3000", donorBoss.Parameters["health_percent"], "donor boss default health");
        Equal("1", donorBoss.Parameters["lives"], "donor boss default lives");
        Equal("300", donorBoss.Parameters["radius_meters"], "donor boss default spawn radius");
        var bossHealth = donorBoss.ParameterDefinitions.Single(x => x.Key == "health_percent");
        Equal(100m, bossHealth.Minimum, "donor boss health minimum");
        Equal(1000000m, bossHealth.Maximum, "donor boss health maximum");
        var bossLives = donorBoss.ParameterDefinitions.Single(x => x.Key == "lives");
        Equal(1m, bossLives.Minimum, "donor boss lives minimum");
        Equal(100m, bossLives.Maximum, "donor boss lives maximum");
        var bossRadius = donorBoss.ParameterDefinitions.Single(x => x.Key == "radius_meters");
        Equal(10m, bossRadius.Minimum, "donor boss radius minimum");
        Equal(1000m, bossRadius.Maximum, "donor boss radius maximum");
        var godMode = presets.Single(x => x.HandlerId == "god_mode");
        Equal("false", godMode.Parameters["use_g_god"], "legacy console god mode is opt-in");
        Equal("true", godMode.Parameters["show_timer"], "native centered countdown is default-on");
        Equal("30", godMode.Parameters["duration_seconds"], "god mode retains duration default");
        var inventoryLock = presets.Single(x => x.HandlerId == "block_inventory");
        Equal("30", inventoryLock.Parameters["duration_seconds"], "inventory lock default duration");
        var inventoryDuration = inventoryLock.ParameterDefinitions.Single(x => x.Key == "duration_seconds");
        Equal(10m, inventoryDuration.Minimum, "inventory lock minimum duration");
        Equal(600m, inventoryDuration.Maximum, "inventory lock maximum duration");
        Equal("Временно изменить параметры героя", presets.Single(x => x.HandlerId == "actor_boost").DisplayName,
            "temporary actor parameters no longer carry the experimental suffix");
        foreach (var handlerId in new[] { "actor_boost", "actor_parameters" })
        {
            var speed = presets.Single(x => x.HandlerId == handlerId)
                .ParameterDefinitions.Single(x => x.Key == "speed_percent");
            Equal(50m, speed.Maximum, $"{handlerId} exposes the safe speed ceiling");
        }
        var hostileSquad = presets.Single(x => x.HandlerId == "spawn_random_hostile_squad");
        var randomMutantPack = presets.Single(x => x.HandlerId == "spawn_random_mutant_pack");
        var selectedNpcSquads = presets.Single(x => x.HandlerId == "spawn_npc_squads");
        var selectedMutantSquads = presets.Single(x => x.HandlerId == "spawn_mutant_squads");
        Equal("Спавн отряда NPC (вызывает проблемы)", selectedNpcSquads.DisplayName, "selected NPC squad preset name");
        Equal("Спавн отряда мутантов (вызывает проблемы)", selectedMutantSquads.DisplayName, "selected mutant squad preset name");
        Equal(CatalogEntryKind.StalkerSquad,
            selectedNpcSquads.ParameterDefinitions.Single(x => x.Key == "section_id").CatalogKind,
            "selected NPC squad uses the squad catalog");
        Equal(CatalogEntryKind.MutantSquad,
            selectedMutantSquads.ParameterDefinitions.Single(x => x.Key == "section_id").CatalogKind,
            "selected mutant squad uses the pack catalog");
        selectedNpcSquads.SpawnGroups[0] = new SpawnGroupEntry
        {
            VariantId = "bandit_sim_squad_novice", Strength = "any", Count = 2
        };
        selectedNpcSquads.Parameters["radius_meters"] = "42";
        selectedNpcSquads.Parameters["force_hostile"] = "false";
        var selectedSquadParameters = selectedNpcSquads.EffectiveParameters();
        Equal("bandit_sim_squad_novice*any*2", selectedSquadParameters[SpawnGroupBundleCodec.ParameterKey],
            "selected squad serializes its exact template and squad count");
        Equal("42", selectedSquadParameters["radius_meters"], "selected squad preserves spawn radius");
        Equal("false", selectedSquadParameters["force_hostile"], "selected squad preserves hostility setting");
        Equal("Случайный сквад NPC (вызывает проблемы)", hostileSquad.DisplayName, "NPC squad preset name");
        Equal("Случайный сквад мутантов (вызывает проблемы)", randomMutantPack.DisplayName, "mutant squad preset name");
        foreach (var randomSpawn in new[] { hostileSquad, randomMutantPack })
        {
            Equal(5, randomSpawn.ParameterDefinitions.Count, $"{randomSpawn.HandlerId} parameter count");
            var sections = randomSpawn.ParameterDefinitions.Single(x => x.Key == "squad_sections");
            Equal(ParameterKind.MultiChoice, sections.Kind, $"{randomSpawn.HandlerId} uses real squad sections");
            Equal(
                randomSpawn.HandlerId == "spawn_random_hostile_squad"
                    ? CatalogEntryKind.StalkerSquad
                    : CatalogEntryKind.MutantSquad,
                sections.CatalogKind,
                $"{randomSpawn.HandlerId} catalog kind");
            Equal(0, sections.Options.Count, $"{randomSpawn.HandlerId} has no compiled fallback squads");
            Equal(string.Empty, randomSpawn.Parameters["squad_sections"], $"{randomSpawn.HandlerId} waits for a live catalog");
            var radius = randomSpawn.ParameterDefinitions.Single(x => x.Key == "radius_meters");
            Equal(ParameterKind.Integer, radius.Kind, $"{randomSpawn.HandlerId} radius kind");
            Equal(10m, radius.Minimum, $"{randomSpawn.HandlerId} radius minimum");
            Equal(100m, radius.Maximum, $"{randomSpawn.HandlerId} radius maximum");
            Equal("25", randomSpawn.Parameters["radius_meters"],
                $"{randomSpawn.HandlerId} default radius");
            var forceHostile = randomSpawn.ParameterDefinitions.Single(x => x.Key == "force_hostile");
            Equal(ParameterKind.Choice, forceHostile.Kind,
                $"{randomSpawn.HandlerId} hostility checkbox kind");
            SequenceEqual(new[] { "false", "true" }, forceHostile.Options,
                $"{randomSpawn.HandlerId} hostility values");
            Equal("true", randomSpawn.Parameters["force_hostile"],
                $"{randomSpawn.HandlerId} forces hostility by default");
            Equal("1", randomSpawn.Parameters["minimum_count"],
                $"{randomSpawn.HandlerId} minimum squad size");
            Equal("10", randomSpawn.Parameters["maximum_count"],
                $"{randomSpawn.HandlerId} maximum squad size");
        }
        var randomActive = presets.Single(x => x.HandlerId == DonationDispatcher.RandomActiveHandlerId);
        var randomAll = presets.Single(x => x.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId);
        Equal("Рандомный по активным шаблонам", randomActive.DisplayName, "active random preset name");
        Equal(ActiveTemplateSelection.DisplayName, randomAll.DisplayName, "all-presets random name");
        Equal(1, randomAll.ParameterDefinitions.Count, "all-presets random only exposes the safe selection list");
        var randomAllSelection = randomAll.ParameterDefinitions.Single(definition =>
            definition.Key == DefaultActionCatalog.RandomAllSelectionParameterKey);
        Equal(ParameterKind.MultiChoice, randomAllSelection.Kind, "all-presets selection kind");
        Equal(DefaultActionCatalog.RandomAllSelectionParameterKey, randomAllSelection.Key, "all-presets selection key");
        Equal(0, randomAllSelection.Options.Count, "profile instances are supplied when opening the editor");
        Equal(0, randomAllSelection.SelectedOptions(randomAllSelection.DefaultValue).Count, "no instance is selected implicitly");
        True(randomAllSelection.Options.All(option => randomAllSelection.OptionLabels.TryGetValue(option, out var label) &&
                !string.IsNullOrWhiteSpace(label) && !string.Equals(label, option, StringComparison.Ordinal)),
            "all random-all checkboxes have localized labels");
        True(!randomAllSelection.Options.Contains("spawn_items", StringComparer.Ordinal) &&
                !randomAllSelection.Options.Contains(DonationDispatcher.RandomActiveHandlerId, StringComparer.Ordinal) &&
                !randomAllSelection.Options.Contains(DonationDispatcher.RandomAllPresetsHandlerId, StringComparer.Ordinal),
            "preset handler IDs are not stored as instance choices");
        True(randomAllSelection.Validate(string.Empty) is not null, "empty selection requires explicit configuration");
        True(randomAllSelection.Validate("bad id!") is not null, "unsafe random-all selection is rejected");
        True(randomAllSelection.Validate("change_health,change_health") is not null, "duplicate random-all selection is rejected");
        var randomAllClone = randomAll.CloneAsNewInstance();
        randomAllClone.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = "change_health,teleport_random";
        var independentRandomAllClone = randomAll.CloneAsNewInstance();
        Equal(randomAllSelection.DefaultValue,
            independentRandomAllClone.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey],
            "random-all checkbox state is independent between active instances");
        var mutantSection = presets.Single(x => x.HandlerId == "spawn_mutants")
            .ParameterDefinitions.Single(x => x.Key == "section_id");
        Equal(CatalogEntryKind.Mutant, mutantSection.CatalogKind, "mutants use a dynamic section catalog");
        Equal(0, mutantSection.Options.Count, "mutants do not ship a hard-coded species list");
        var itemParameter = presets.Single(x => x.HandlerId == "spawn_items")
            .ParameterDefinitions.Single(x => x.Key == "section_id");
        Equal(CatalogEntryKind.Item, itemParameter.CatalogKind, "items use a dynamic section catalog");
        Equal(0, itemParameter.Options.Count, "items do not ship a hard-coded item list");
        var itemSpawner = presets.Single(x => x.HandlerId == "spawn_items");
        var weaponOptions = itemSpawner.ParameterDefinitions.Single(x => x.Key == "weapon_options");
        Equal(ParameterKind.MultiChoice, weaponOptions.Kind, "weapon options use checkboxes");
        SequenceEqual(new[] { "equip", "add_ammo" }, weaponOptions.Options,
            "weapon option checkbox values");
        Equal(0, weaponOptions.MinimumSelections, "weapon checkboxes are optional");
        Equal(string.Empty, itemSpawner.Parameters["weapon_options"],
            "spawned weapon is not equipped by default");
        var ammoPacks = itemSpawner.ParameterDefinitions.Single(x => x.Key == "ammo_packs");
        Equal(ParameterKind.Integer, ammoPacks.Kind, "ammo pack count is numeric");
        Equal("1", itemSpawner.Parameters["ammo_packs"], "one ammo pack is the default");
        itemSpawner.ItemSpawns = [new ItemSpawnEntry { ItemId = "wpn_test", Count = 2 }];
        itemSpawner.Parameters["weapon_options"] = "add_ammo";
        itemSpawner.Parameters["ammo_packs"] = "5";
        var effectiveItemParameters = itemSpawner.EffectiveParameters();
        Equal("wpn_test*2", effectiveItemParameters[ItemBundleCodec.ParameterKey],
            "item rows are serialized into the command");
        Equal("add_ammo", effectiveItemParameters["weapon_options"],
            "weapon checkbox is passed to the command");
        Equal("5", effectiveItemParameters["ammo_packs"],
            "configured ammo pack count is passed to the command");
        var consumable = presets.Single(x => x.HandlerId == "use_random_consumable");
        var consumableSources = consumable.ParameterDefinitions.Single(x => x.Key == "sources");
        Equal(ParameterKind.MultiChoice, consumableSources.Kind, "consumable sources use checkboxes");
        SequenceEqual(new[] { "hotbar", "inventory" }, consumableSources.Options,
            "consumable has hotbar and inventory checkboxes");
        Equal("hotbar,inventory", consumable.Parameters["sources"],
            "both consumable sources are enabled by default");
        True(consumableSources.Validate(string.Empty) is not null,
            "at least one consumable source is required");
        True(consumableSources.Validate("hotbar") is null &&
            consumableSources.Validate("inventory") is null,
            "either consumable source can be used alone");
        var factionAction = presets.Single(x => x.HandlerId == "change_player_faction");
        var factionSelection = factionAction.ParameterDefinitions.Single(x =>
            x.Key == "faction_communities");
        Equal(ParameterKind.MultiChoice, factionSelection.Kind,
            "player factions use a multi-selection checklist");
        Equal(CatalogEntryKind.Faction, factionSelection.CatalogKind,
            "player factions come from the live game catalog");
        True(factionSelection.Options.Count == 0 &&
            factionAction.Parameters["faction_communities"] == string.Empty,
            "player factions have no compiled fallback list");

        var vanillaCatalog = LoadCatalogFixture("catalog-vanilla.json");
        DynamicCatalogBinding.Apply(vanillaCatalog, presets);
        SequenceEqual(new[] { "dog_normal", "chimera_strong" }, mutantSection.Options,
            "live mutant sections replace compiled species guesses");
        Equal("Слепой пёс", mutantSection.OptionLabels["dog_normal"], "localized mutant name is retained");
        True(itemParameter.OptionGroups.ContainsKey("Медицина") &&
            itemParameter.OptionGroups["Медицина"].Contains("medkit"), "item catalog has live medicine category");
        True(itemParameter.OptionGroups.ContainsKey("Еда"), "item catalog has live food category");
        var weaponCatalogEntries = vanillaCatalog.Entries.Concat(new[]
        {
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
                SectionId = "af_fire",
                Kind = CatalogEntryKind.Item,
                DisplayName = "Огненный шар",
                Category = "artefacts",
                Strength = CatalogStrength.Unclassified,
                IsAvailable = true
            }
        }).ToList();
        var weaponCatalog = new CatalogSnapshot(
            new CatalogRevision("vanilla-153-with-weapon"),
            CatalogContentHash.Compute(weaponCatalogEntries),
            weaponCatalogEntries);
        DynamicCatalogBinding.Apply(weaponCatalog, presets);
        True(itemParameter.Options.Contains(DefaultActionCatalog.RandomWeaponSectionId,
                StringComparer.Ordinal),
            "item picker includes the random weapon option when safe weapons exist");
        Equal("Случайное оружие",
            itemParameter.OptionLabels[DefaultActionCatalog.RandomWeaponSectionId],
            "random weapon option is localized");
        True(itemParameter.Options.Contains(DefaultActionCatalog.RandomArtifactSectionId,
                StringComparer.Ordinal),
            "item picker includes the random artifact option when safe artifacts exist");
        Equal("Случайный артефакт",
            itemParameter.OptionLabels[DefaultActionCatalog.RandomArtifactSectionId],
            "random artifact option is localized");
        SequenceEqual(new[] { "adb_faction_actor_bandit", "adb_faction_actor_stalker" },
            factionSelection.Options, "live player factions populate the multi-selection");
        Equal("adb_faction_actor_bandit,adb_faction_actor_stalker",
            factionAction.Parameters["faction_communities"],
            "all live player factions are checked by default");
        var itemClone = itemParameter.Clone();
        itemClone.OptionGroups["Медицина"].Clear();
        True(itemParameter.OptionGroups["Медицина"].Count > 0, "item categories are deep-cloned");
        itemParameter.OptionLabels["medkit"] = "аптечка";
        var labeledItemClone = itemParameter.Clone();
        labeledItemClone.OptionLabels["medkit"] = "изменено";
        Equal("аптечка", itemParameter.OptionLabels["medkit"], "item labels are deep-cloned");
        var anomaly = presets.Single(x => x.HandlerId == "spawn_anomaly");
        var anomalySection = anomaly.ParameterDefinitions.Single(x => x.Key == "section_id");
        True(anomalySection.Options.Contains(DefaultActionCatalog.RandomAnomalySectionId,
                StringComparer.Ordinal),
            "anomaly picker includes a random live-catalog option");
        Equal("Спавнить один случайный тип аномалии",
            anomalySection.OptionLabels[DefaultActionCatalog.RandomAnomalySectionId],
            "random anomaly option is localized");
        var anomalyDistance = anomaly.ParameterDefinitions.Single(x => x.Key == "distance_meters");
        Equal("Радиус спавна, м", anomalyDistance.DisplayName, "anomaly radius label");
        Equal(0m, anomalyDistance.Minimum, "anomaly distance minimum");
        Equal(100m, anomalyDistance.Maximum, "anomaly distance maximum");
        Equal("7", anomaly.Parameters["distance_meters"], "anomaly distance default");
        var anomalyCount = anomaly.ParameterDefinitions.Single(x => x.Key == "count");
        Equal(1m, anomalyCount.Minimum, "anomaly count minimum");
        Equal(100m, anomalyCount.Maximum, "anomaly count maximum");
        Equal("6", anomaly.Parameters["count"], "anomaly count default");
        True(anomaly.ParameterDefinitions.All(x => x.Key != "randomization_options"),
            "anomaly per-instance random mode is part of the anomaly picker");
        var levelTeleport = presets.Single(x => x.HandlerId == "teleport_random_level");
        var levelOptions = levelTeleport.ParameterDefinitions.Single(x => x.Key == "level_options");
        Equal(ParameterKind.MultiChoice, levelOptions.Kind, "visited-level filter uses a checkbox");
        Equal(0, levelOptions.MinimumSelections, "visited-level filter is optional");
        SequenceEqual(new[] { "visited_only" }, levelOptions.Options,
            "visited-level filter checkbox value");
        Equal("Телепортировать только в посещённые локации",
            levelOptions.OptionLabels["visited_only"], "visited-level filter label");
        Equal(string.Empty, levelTeleport.Parameters["level_options"],
            "visited-level filter is disabled by default");
        True(presets.All(x => x.HandlerId != "drop_outfit"),
            "retired equipment-drop preset is absent");
        var drunk = presets.Single(x => x.HandlerId == "get_drunk");
        var drunkDuration = drunk.ParameterDefinitions.Single(x => x.Key == "duration_seconds");
        Equal(5m, drunkDuration.Minimum, "drunk duration minimum");
        Equal(300m, drunkDuration.Maximum, "drunk duration maximum");
        Equal("30", drunk.Parameters["duration_seconds"], "drunk duration default");
        True(!drunk.Parameters.ContainsKey("portions"), "drunk effect does not model inventory portions");
        var needs = presets.Single(x => x.HandlerId == "change_needs");
        Equal(2, needs.ParameterDefinitions.Count, "needs parameter count");
        var hunger = needs.ParameterDefinitions.Single(x => x.Key == "hunger_percent");
        var thirst = needs.ParameterDefinitions.Single(x => x.Key == "thirst_percent");
        Equal(ParameterKind.Percent, hunger.Kind, "hunger parameter kind");
        Equal(-100m, hunger.Minimum, "hunger minimum");
        Equal(100m, hunger.Maximum, "hunger maximum");
        Equal(ParameterKind.Percent, thirst.Kind, "thirst parameter kind");
        Equal(-100m, thirst.Minimum, "thirst minimum");
        Equal(100m, thirst.Maximum, "thirst maximum");
        Equal("25", needs.Parameters["hunger_percent"], "hunger default");
        Equal("25", needs.Parameters["thirst_percent"], "thirst default");
        True(hunger.Validate("0") is null && thirst.Validate("0") is null, "zero leaves either need unchanged");
        True(hunger.Validate("-101") is not null && thirst.Validate("101") is not null, "need bounds enforced");
        var emission = presets.Single(x => x.HandlerId == "trigger_emission");
        Equal(0, emission.ParameterDefinitions.Count, "emission has no parameters");
        Equal(3, emission.CooldownSeconds, "new actions use the common three-second cooldown");
        var sleep = presets.Single(x => x.HandlerId == "sleep_hours");
        var sleepHours = sleep.ParameterDefinitions.Single(x => x.Key == "hours");
        Equal(ParameterKind.Integer, sleepHours.Kind, "sleep parameter kind");
        Equal(1m, sleepHours.Minimum, "sleep minimum");
        Equal(24m, sleepHours.Maximum, "sleep maximum");
        Equal("6", sleep.Parameters["hours"], "sleep default");
        True(sleepHours.Validate("1.5") is not null, "sleep accepts whole hours only");
        var alternativeSleep = sleep.ParameterDefinitions.Single(x => x.Key == "alternative_sleep");
        Equal(ParameterKind.Choice, alternativeSleep.Kind, "alternative sleep parameter kind");
        SequenceEqual(new[] { "false", "true" }, alternativeSleep.Options,
            "alternative sleep checkbox options");
        Equal("false", sleep.Parameters["alternative_sleep"],
            "alternative sleep is disabled by default");
        var damageEquipment = presets.Single(x => x.HandlerId == "damage_equipment");
        Equal(2, damageEquipment.ParameterDefinitions.Count, "equipment damage parameter count");
        var damageTarget = damageEquipment.ParameterDefinitions.Single(x => x.Key == "target");
        Equal(ParameterKind.Choice, damageTarget.Kind, "equipment damage target kind");
        SequenceEqual(new[] { "outfit", "helmet", "both" }, damageTarget.Options,
            "equipment damage target options");
        Equal("outfit", damageEquipment.Parameters["target"], "equipment damage default target");
        var damagePercent = damageEquipment.ParameterDefinitions.Single(x => x.Key == "percent");
        Equal(ParameterKind.Percent, damagePercent.Kind, "equipment damage percent kind");
        Equal(1m, damagePercent.Minimum, "equipment damage percent minimum");
        Equal(100m, damagePercent.Maximum, "equipment damage percent maximum");
        Equal("25", damageEquipment.Parameters["percent"], "equipment damage default percent");
        True(damagePercent.Validate("0") is not null && damagePercent.Validate("101") is not null,
            "equipment damage percent bounds enforced");
        var dropRandomItems = presets.Single(x => x.HandlerId == "drop_random_items");
        var dropRandomCount = dropRandomItems.ParameterDefinitions.Single(x => x.Key == "count");
        Equal(999m, dropRandomCount.Maximum, "random item drop maximum");
        True(dropRandomCount.Validate("999") is null && dropRandomCount.Validate("1000") is not null,
            "random item drop accepts up to 999 items");
        True(randomAllSelection.Options.Count == 0,
            "no effects are selected without the user's configured active instances");
        SequenceEqual(
            new[] { "get_drunk", "damage_equipment", "drop_random_items", "teleport_random", "random_active", "random_all_presets" },
            presets.Where(action => new[] { "get_drunk", "damage_equipment", "drop_random_items", "teleport_random", "random_active", "random_all_presets" }
                .Contains(action.HandlerId, StringComparer.Ordinal))
                .Select(action => action.HandlerId),
            "new chaos presets");
        SequenceEqual(
            new[] { "spawn_random_hostile_squad", "spawn_random_mutant_pack" },
            presets.Where(action => new[] { "spawn_random_hostile_squad", "spawn_random_mutant_pack" }
                    .Contains(action.HandlerId, StringComparer.Ordinal))
                .Select(action => action.HandlerId),
            "random spawn presets");
        SequenceEqual(
            new[] { "change_needs", "trigger_emission", "sleep_hours" },
            presets.Where(action => new[] { "change_needs", "trigger_emission", "sleep_hours" }
                    .Contains(action.HandlerId, StringComparer.Ordinal))
                .Select(action => action.HandlerId),
            "needs and world presets");
        return Task.CompletedTask;
    }

    private static Task TestCatalogExclusionsAndHistorySelectionAsync()
    {
        var entries = new[]
        {
            new CatalogEntry
            {
                SectionId = "wpn_safe", Kind = CatalogEntryKind.Item, DisplayName = "Safe",
                Category = "weapons", IsAvailable = true, QuarantineReason = QuarantineReason.None
            },
            new CatalogEntry
            {
                SectionId = "wpn_broken", Kind = CatalogEntryKind.Item, DisplayName = "Broken",
                Category = "weapons", IsAvailable = true, QuarantineReason = QuarantineReason.None
            }
        };
        var catalog = new CatalogSnapshot(new CatalogRevision("exclusion-test"),
            CatalogContentHash.Compute(entries), entries);
        var filtered = CatalogExclusionFilter.Apply(catalog,
            [(CatalogEntryKind.Item, "wpn_broken")]);
        True(filtered.ContainsAvailable(CatalogEntryKind.Item, "wpn_safe"),
            "unrelated catalog entry was filtered");
        True(!filtered.ContainsAvailable(CatalogEntryKind.Item, "wpn_broken"),
            "excluded catalog entry remains available");
        Equal(QuarantineReason.Blacklisted,
            filtered.Entries.Single(entry => entry.SectionId == "wpn_broken").QuarantineReason,
            "excluded catalog entry has wrong quarantine reason");

        var action = DefaultActionCatalog.CreatePresets()
            .Single(item => item.HandlerId == ItemBundleCodec.HandlerId);
        action.ItemSpawns = [new ItemSpawnEntry { ItemId = "wpn_broken", Count = 1 }];
        var history = new EventHistoryEntry { ActionSnapshot = action };
        var candidates = HistoryCatalogSelection.Resolve(history, catalog);
        Equal(1, candidates.Count, "single history section was not resolved exactly");
        Equal("wpn_broken", candidates[0].SectionId, "history resolved the wrong section");

        action.ItemSpawns = [new ItemSpawnEntry
        {
            ItemId = DefaultActionCatalog.RandomWeaponSectionId,
            Count = 1
        }];
        candidates = HistoryCatalogSelection.Resolve(history, catalog);
        Equal(2, candidates.Count, "random history selection did not expand to its real catalog pool");
        return Task.CompletedTask;
    }

    private static Task TestEventHistoryProjectionAsync()
    {
        var firstCreated = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var secondCreated = firstCreated.AddSeconds(10);
        var entries = new[]
        {
            new EventHistoryEntry { EventId = "first-executed", CommandId = "first", DonationCreatedAt = firstCreated, Timestamp = firstCreated.AddSeconds(3), State = DispatchState.Executed, Detail = "done" },
            new EventHistoryEntry { EventId = "second-queued", CommandId = "second", DonationCreatedAt = secondCreated, Timestamp = secondCreated, State = DispatchState.Queued, Detail = "queued" },
            new EventHistoryEntry { EventId = "first-sent", CommandId = "first", DonationCreatedAt = firstCreated, Timestamp = firstCreated.AddSeconds(2), State = DispatchState.Sent, Detail = "sent" },
            new EventHistoryEntry { EventId = "first-queued", CommandId = "first", DonationCreatedAt = firstCreated, Timestamp = firstCreated, State = DispatchState.Queued, Detail = "queued" },
            new EventHistoryEntry { EventId = "standalone-a", DonationCreatedAt = firstCreated.AddSeconds(20), Timestamp = firstCreated.AddSeconds(20), State = DispatchState.Rejected },
            new EventHistoryEntry { EventId = "standalone-b", DonationCreatedAt = firstCreated.AddSeconds(21), Timestamp = firstCreated.AddSeconds(21), State = DispatchState.Rejected }
        };

        var visible = EventHistoryProjection.LatestPerCommand(entries);
        Equal(4, visible.Count, "one row remains for every command or standalone event");
        Equal("standalone-b", visible[0].EventId, "logical events stay ordered by event time");
        Equal(DispatchState.Executed, visible.Single(entry => entry.CommandId == "first").State,
            "the latest command status replaces queued and sent rows");
        True(EventHistoryProjection.IsSameLogicalEvent(entries[0], entries[2]),
            "command transitions are recognized as one event");
        True(!EventHistoryProjection.IsSameLogicalEvent(entries[4], entries[5]),
            "standalone records without command ids remain independent");
        return Task.CompletedTask;
    }

    private static Task TestDynamicCatalogFixturesAsync()
    {
        var vanilla = LoadCatalogFixture("catalog-vanilla.json");
        Equal("vanilla-153-session-1", vanilla.Revision.Value, "vanilla fixture revision");
        True(vanilla.ContainsAvailable(CatalogEntryKind.Mutant, "dog_normal"),
            "vanilla mutant section is available");
        var chimera = vanilla.Entries.Single(entry => entry.SectionId == "chimera_strong");
        Equal(CatalogEntryKind.Mutant, chimera.Kind, "catalog entry kind is retained");
        Equal("Химеры", chimera.Category, "catalog category is retained");
        Equal(CatalogStrength.Strong, chimera.Strength, "catalog strength is retained");

        var vanillaPresets = DefaultActionCatalog.CreatePresets();
        DynamicCatalogBinding.Apply(vanilla, vanillaPresets);
        var hostileSquads = vanillaPresets.Single(action => action.HandlerId == "spawn_random_hostile_squad");
        var hostileSections = hostileSquads.ParameterDefinitions.Single(definition =>
            definition.Key == "squad_sections");
        Equal(vanilla.Revision, hostileSquads.CatalogRevision, "live revision is attached to a dynamic preset");
        True(hostileSections.Options.Contains("bandit_sim_squad_novice", StringComparer.Ordinal) &&
            hostileSections.Options.Contains("merc_sim_squad_veteran", StringComparer.Ordinal),
            "real stalker squad sections are selectable");
        SequenceEqual(hostileSections.Options,
            hostileSections.SelectedOptions(hostileSquads.Parameters["squad_sections"]),
            "all safe live squads are selected by default");

        var modded = LoadCatalogFixture("catalog-modded.json");
        var moddedPresets = DefaultActionCatalog.CreatePresets();
        DynamicCatalogBinding.Apply(modded, moddedPresets);
        var moddedMutants = moddedPresets.Single(action => action.HandlerId == "spawn_mutants");
        var moddedMutantSections = moddedMutants.ParameterDefinitions.Single(definition =>
            definition.Key == "section_id");
        True(moddedMutantSections.Options.Contains("mod_chimera_alpha", StringComparer.Ordinal),
            "an added mutant is discovered from the installed build");
        True(moddedMutantSections.Options.Contains("mod_unknown_beast", StringComparer.Ordinal),
            "safe unclassified mutant remains selectable");
        Equal("mod_unknown_beast", moddedMutantSections.OptionLabels["mod_unknown_beast"],
            "missing localization falls back to the section id");
        True(moddedMutantSections.OptionGroups.TryGetValue("Без классификации", out var unclassified) &&
            unclassified.Contains("mod_unknown_beast", StringComparer.Ordinal),
            "unrecognized safe section is grouped without being quarantined");
        Equal(CatalogStrength.Unclassified,
            modded.Entries.Single(entry => entry.SectionId == "mod_unknown_beast").Strength,
            "unknown strength is explicit");

        var grenadeCatalog = new CatalogSnapshot(
            new CatalogRevision("grenade-filter-session"), new string('b', 64),
            [
                new CatalogEntry
                {
                    SectionId = "grenade_f1", Kind = CatalogEntryKind.Item,
                    DisplayName = "Граната Ф-1", Category = "grenades",
                    IsAvailable = true, QuarantineReason = QuarantineReason.None
                },
                new CatalogEntry
                {
                    SectionId = "wpn_ak74", Kind = CatalogEntryKind.Item,
                    DisplayName = "АК-74", Category = "weapons",
                    IsAvailable = true, QuarantineReason = QuarantineReason.None
                }
            ]);
        var grenadeAction = new ActionDefinition
        {
            Id = "hardcoon.adb.explosive",
            HandlerId = "hardcoon.adb.explosive",
            DisplayName = "РАЗРЫВНАЯ",
            ParameterDefinitions =
            [
                new ParameterDefinition
                {
                    Key = "grenade_sections", DisplayName = "Допустимые гранаты",
                    Kind = ParameterKind.MultiChoice, CatalogKind = CatalogEntryKind.Item,
                    CatalogCategories = ["grenades"], MinimumSelections = 1
                }
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["grenade_sections"] = string.Empty
            }
        };
        DynamicCatalogBinding.Apply(grenadeCatalog, [grenadeAction]);
        var grenadeDefinition = grenadeAction.ParameterDefinitions.Single();
        SequenceEqual(new[] { "grenade_f1" }, grenadeDefinition.Options,
            "a filtered catalog selector exposes only hand grenades");
        Equal("grenade_f1", grenadeAction.Parameters["grenade_sections"],
            "all matching grenades are checked by default on first installation");
        return Task.CompletedTask;
    }

    private static Task TestCatalogQuarantineAndStaleRevisionAsync()
    {
        var staleAnomalyCatalog = new CatalogSnapshot(
            new CatalogRevision("stale-torrid-session"), new string('a', 64),
            [new CatalogEntry
            {
                SectionId = "fireball_electric_zone", Kind = CatalogEntryKind.Anomaly,
                DisplayName = "Электрический огненный шар", Category = "ZS_TORRD",
                IsAvailable = true, QuarantineReason = QuarantineReason.None
            }]);
        True(!staleAnomalyCatalog.ContainsAvailable(CatalogEntryKind.Anomaly,
                "fireball_electric_zone"),
            "old available torrid zones are blocked even before the game refreshes its catalog");
        var anomalyAction = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "spawn_anomaly");
        DynamicCatalogBinding.Apply(staleAnomalyCatalog, [anomalyAction]);
        True(!anomalyAction.ParameterDefinitions.Single(definition =>
                definition.Key == "section_id").Options.Contains("fireball_electric_zone"),
            "random-all cannot draw the unsafe zone from a stale preset picker");

        var quarantine = LoadCatalogFixture("catalog-quarantine.json");
        var quarantinedItem = quarantine.Entries.Single(entry => entry.SectionId == "quest_case_unique");
        True(quarantinedItem.IsQuarantined, "story item remains visible in the full catalog");
        Equal(QuarantineReason.StoryOrUnique, quarantinedItem.QuarantineReason,
            "quarantine reason remains visible");
        True(!quarantine.AvailableEntries(CatalogEntryKind.Item)
                .Any(entry => entry.SectionId == quarantinedItem.SectionId),
            "story item is excluded from executable choices");

        var itemAction = DefaultActionCatalog.CreatePresets().Single(action => action.HandlerId == "spawn_items");
        DynamicCatalogBinding.Apply(quarantine, [itemAction]);
        var itemSections = itemAction.ParameterDefinitions.Single(definition => definition.Key == "section_id");
        SequenceEqual(new[] { "medkit" }, itemSections.Options, "only safe items are bound to the action");

        var quarantinedCommand = new GameCommand
        {
            ActionId = "spawn_items",
            CatalogRevision = quarantine.Revision,
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = "quest_case_unique",
                ["count"] = "1"
            }
        };
        True(!CatalogRevisionEnforcement.Validate(quarantinedCommand, quarantine).IsValid,
            "a quarantined section cannot bypass the command validator");

        var modded = LoadCatalogFixture("catalog-modded.json");
        var oldCommand = new GameCommand
        {
            ActionId = "spawn_mutants",
            CatalogRevision = modded.Revision,
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                [SpawnGroupBundleCodec.ParameterKey] = "mod_chimera_alpha*strong*1"
            }
        };
        True(CatalogRevisionEnforcement.Validate(oldCommand, modded).IsValid,
            "fresh dynamic command is valid for its source catalog");

        var stale = LoadCatalogFixture("catalog-stale.json");
        var revisionMismatch = CatalogRevisionEnforcement.Validate(oldCommand, stale);
        True(!revisionMismatch.IsValid &&
            revisionMismatch.Reason.Contains("устарев", StringComparison.OrdinalIgnoreCase),
            "a command from the previous modpack revision is rejected");

        oldCommand.CatalogRevision = stale.Revision;
        var vanishedSection = CatalogRevisionEnforcement.Validate(oldCommand, stale);
        True(!vanishedSection.IsValid &&
            vanishedSection.Reason.Contains("отсутствует", StringComparison.OrdinalIgnoreCase),
            "changing only the revision cannot revive a vanished section");

        var selectedAction = DefaultActionCatalog.CreatePresets().Single(action => action.HandlerId == "spawn_mutants");
        DynamicCatalogBinding.Apply(modded, [selectedAction]);
        selectedAction.SpawnGroups = [new SpawnGroupEntry
            { VariantId = "mod_chimera_alpha", Strength = "strong", Count = 1 }];
        DynamicCatalogBinding.Apply(stale, [selectedAction]);
        Equal("mod_chimera_alpha", selectedAction.SpawnGroups.Single().VariantId,
            "a stale saved selection stays visible instead of silently changing the effect");
        True(selectedAction.Validate().Any(error =>
                error.Contains("отсутствует в свежем безопасном каталоге", StringComparison.OrdinalIgnoreCase)),
            "the stale visible selection blocks execution until the user chooses a fresh section");
        Equal(stale.Revision, selectedAction.CatalogRevision, "action tracks the newly synchronized revision");
        return Task.CompletedTask;
    }

    private static Task TestManifestCatalogAndMultiChoiceMigrationAsync()
    {
        var catalog = DefaultActionCatalog.CreatePresets();
        catalog.Add(new ActionDefinition
        {
            Id = "spawn_dogs_3",
            HandlerId = "spawn_dogs_3",
            DisplayName = "Старый совместимый спавн"
        });
        var declaredVisibleHandlers = new HashSet<string>(StringComparer.Ordinal)
        {
            "change_health",
            "teleport_random",
            DonationDispatcher.RandomAllPresetsHandlerId
        };

        CatalogReconciliation.RetainDeclaredActions(catalog, declaredVisibleHandlers);
        DefaultActionCatalog.RefreshRandomAllPresetSelection(catalog);
        SequenceEqual(
            new[] { "change_health", "teleport_random", DonationDispatcher.RandomAllPresetsHandlerId },
            catalog.Select(action => action.HandlerId),
            "compiled presets absent from a readable manifest are removed");
        True(!catalog.Any(action => action.HandlerId == "spawn_dogs_3"),
            "legacy executable compatibility action stays hidden from the editor");

        var selectionDefinition = catalog
            .Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
            .ParameterDefinitions.Single(definition =>
                definition.Key == DefaultActionCatalog.RandomAllSelectionParameterKey);
        SequenceEqual(
            Array.Empty<string>(),
            selectionDefinition.Options,
            "game manifest does not substitute profile instance choices");

        var partiallyStale = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultActionCatalog.RandomAllSelectionParameterKey] = "change_health,spawn_mutants"
        };
        Equal("change_health,spawn_mutants",
            CatalogReconciliation.ResolveSavedParameterValue(selectionDefinition, partiallyStale),
            "opaque instance references survive schema reconciliation");
        var intentionallyEmpty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultActionCatalog.RandomAllSelectionParameterKey] = string.Empty
        };
        Equal(string.Empty,
            CatalogReconciliation.ResolveSavedParameterValue(selectionDefinition, intentionallyEmpty),
            "an intentionally empty random-all selection remains empty");
        Equal(selectionDefinition.DefaultValue,
            CatalogReconciliation.ResolveSavedParameterValue(
                selectionDefinition,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            "a newly introduced selection still receives the safe default");

        var unboundDynamicSingle = new ParameterDefinition
        {
            Key = "section_id",
            DisplayName = "Секция",
            Kind = ParameterKind.Choice,
            CatalogKind = CatalogEntryKind.Anomaly
        };
        Equal("zone_gravi_zone",
            CatalogReconciliation.ResolveSavedParameterValue(
                unboundDynamicSingle,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["section_id"] = "zone_gravi_zone"
                }),
            "a saved dynamic single selection survives startup before catalog binding");
        var unboundDynamicMulti = new ParameterDefinition
        {
            Key = "squad_sections",
            DisplayName = "Отряды",
            Kind = ParameterKind.MultiChoice,
            CatalogKind = CatalogEntryKind.StalkerSquad
        };
        Equal("army_sim_squad_10,army_sim_squad_alpha",
            CatalogReconciliation.ResolveSavedParameterValue(
                unboundDynamicMulti,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["squad_sections"] = "army_sim_squad_10,army_sim_squad_alpha,army_sim_squad_10"
                }),
            "saved dynamic multi-selections survive startup and are de-duplicated before catalog binding");
        return Task.CompletedTask;
    }

    private static async Task TestActionPriceOrderingAsync()
    {
        static ActionDefinition Action(string id, params (decimal Amount, string Currency)[] prices) => new()
        {
            Id = id,
            HandlerId = id,
            DisplayName = id,
            IsActive = true,
            Triggers = prices.Select(price => new TriggerRule
            {
                Amount = price.Amount,
                Currency = price.Currency
            }).ToList()
        };

        var actions = new List<ActionDefinition>
        {
            Action("without_price"),
            Action("ten", (10m, "KZT")),
            Action("five_first", (5m, "USD")),
            Action("minimum_of_many", (50m, "RUB"), (2m, "EUR")),
            Action("disabled_cheaper", (1m, "RUB"), (7m, "RUB")),
            Action("five_second", (5m, "RUB"))
        };
        actions.Single(action => action.Id == "disabled_cheaper").Triggers[0].Enabled = false;
        var originalOrder = actions.Select(action => action.Id).ToList();

        var sorted = ActionPriceOrdering.SortByMinimumAssignedAmount(actions);

        SequenceEqual(
            new[] { "minimum_of_many", "five_first", "five_second", "disabled_cheaper", "ten", "without_price" },
            sorted.Select(action => action.Id),
            "actions sorted by raw minimum enabled amount with stable ties and missing prices last");
        var descending = ActionPriceOrdering.SortByMinimumAssignedAmount(actions, descending: true);
        SequenceEqual(
            new[] { "ten", "disabled_cheaper", "five_first", "five_second", "minimum_of_many", "without_price" },
            descending.Select(action => action.Id),
            "actions sorted by descending minimum price with missing prices last");
        var export = ActivePresetTextExporter.Build(sorted);
        True(export.StartsWith("№1 minimum_of_many: 50 RUB, 2 EUR", StringComparison.Ordinal),
            "stream export preserves current active order and all enabled prices");
        True(export.Contains("№6 without_price: цена не назначена", StringComparison.Ordinal),
            "stream export marks actions without a price");
        var mixedPrices = Action("Создать кольцо аномалий", (372m, "RUB"), (500m, "RUB"));
        mixedPrices.Triggers[1].Comparator = TriggerComparator.GreaterOrEqual;
        Equal("№1 Создать кольцо аномалий: 372 RUB, от 500 RUB" + Environment.NewLine,
            ActivePresetTextExporter.Build([mixedPrices]),
            "stream export uses a colon, comma-separated prices and words for thresholds");
        Equal("№1 Любое случайное событие из интерактива: 665 RUB" + Environment.NewLine,
            ActivePresetTextExporter.Build([Action("Любое случайное событие из интерактива =", (665m, "RUB"))]),
            "stream export removes trailing equals decoration from the displayed title");
        SequenceEqual(originalOrder, actions.Select(action => action.Id), "price ordering does not mutate the source list");

        var directory = CreateTestDirectoryPath();
        try
        {
            var store = new JsonSettingsStore(new AppPaths(directory));
            await store.SaveAsync(new AppSettings { Actions = sorted });
            var loaded = await store.LoadAsync();
            SequenceEqual(
                sorted.Select(action => action.Id),
                loaded.Actions.Select(action => action.Id),
                "sorted action order persisted in settings");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static Task TestPresetInstancesAsync()
    {
        var preset = DefaultActionCatalog.CreatePresets().Single(x => x.HandlerId == "spawn_mutants");
        preset.Triggers.Add(new TriggerRule { Amount = 10m, Currency = "RUB" });
        var first = preset.CloneAsNewInstance();
        var second = preset.CloneAsNewInstance();
        True(first.Id != second.Id && first.Id != preset.Id && second.Id != preset.Id, "each activation gets a unique id");
        True(first.Triggers.Single().Id != second.Triggers.Single().Id, "trigger ids are regenerated per instance");
        first.Parameters["section_id"] = "chimera_strong";
        first.Triggers[0].Amount = 20m;
        Equal(string.Empty, second.Parameters["section_id"], "dynamic parameters are deep-cloned");
        Equal(10m, second.Triggers[0].Amount, "triggers are deep-cloned");
        return Task.CompletedTask;
    }

    private static Task TestLegacySettingsMigrationAsync()
    {
        var dogsTrigger = new TriggerRule { Id = "legacy-trigger", Amount = 42m, Currency = "RUB" };
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            QueuePolicy = new QueuePolicy { Mode = QueueMode.Skip },
            Actions =
            [
                new ActionDefinition
                {
                    Id = "spawn_dogs_3",
                    IsActive = true,
                    CooldownSeconds = 17,
                    Parameters = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["species"] = "dog",
                        ["strength"] = "medium",
                        ["count"] = "3"
                    },
                    Triggers = [dogsTrigger]
                },
                new ActionDefinition { Id = "spawn_mutants", IsActive = false }
            ]
        };

        var migrated = SettingsNormalizer.Normalize(settings);
        Equal(SettingsNormalizer.CurrentSchemaVersion, migrated.SchemaVersion, "settings schema");
        True(migrated.ShowGameEffectLog, "legacy settings keep the game effect log enabled");
        True(!migrated.ShowPdaEffectMessage, "legacy settings keep the universal PDA message disabled");
        Equal(QueueMode.WaitIndefinitely, migrated.QueuePolicy.Mode,
            "legacy lossy queue mode is migrated to guaranteed delivery");
        Equal(1, migrated.Actions.Count, "inactive legacy presets are not instances");
        var dogs = migrated.Actions.Single();
        Equal("spawn_dogs_3", dogs.Id, "legacy instance id preserved");
        Equal("spawn_mutants", dogs.HandlerId, "legacy dogs use universal handler");
        Equal(17, dogs.CooldownSeconds, "cooldown preserved");
        True(!dogs.Parameters.ContainsKey("species"),
            "obsolete compiled species is not trusted as a live section");
        Equal(string.Empty, dogs.SpawnGroups.Single().VariantId,
            "legacy mutant waits for a fresh catalog selection");
        Equal(3, dogs.SpawnGroups.Single().Count, "count preserved");
        True(dogs.CatalogRevision is null, "legacy settings cannot invent a catalog revision");
        True(dogs.Validate().Any(error => error.Contains("каталог", StringComparison.OrdinalIgnoreCase)),
            "migrated dynamic action fails closed until synchronization");
        Equal("legacy-trigger", dogs.Triggers.Single().Id, "trigger identity preserved");
        Equal(42m, dogs.Triggers.Single().Amount, "trigger amount preserved");
        return Task.CompletedTask;
    }

    private static Task TestLegacyHandlersNotCollapsedAsync()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            Actions =
            [
                new ActionDefinition
                {
                    Id = "spawn_dogs_3",
                    IsActive = true,
                    CooldownSeconds = 30,
                    Parameters = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["species"] = "dog", ["strength"] = "medium", ["count"] = "3"
                    },
                    Triggers = [new TriggerRule { Amount = 30m, Currency = "RUB" }]
                },
                new ActionDefinition
                {
                    Id = "spawn_mutants",
                    IsActive = true,
                    CooldownSeconds = 30,
                    Parameters = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["species"] = "chimera", ["strength"] = "medium", ["count"] = "1"
                    },
                    Triggers = [new TriggerRule { Amount = 40m, Currency = "RUB" }]
                }
            ]
        };

        var migrated = SettingsNormalizer.Normalize(settings);
        Equal(2, migrated.Actions.Count, "both active legacy configurations survive");
        True(migrated.Actions.All(action => action.HandlerId == "spawn_mutants"), "both use universal handler");
        Equal(2, migrated.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count(), "instance ids remain unique");
        True(migrated.Actions.All(action => !action.Parameters.ContainsKey("species") &&
                action.Parameters["section_id"] == string.Empty),
            "obsolete species names do not become executable section ids");
        True(migrated.Actions.Any(action => action.Parameters["count"] == "3" &&
                action.Triggers.Single().Amount == 30m), "first legacy configuration preserved independently");
        True(migrated.Actions.Any(action => action.Parameters["count"] == "1" &&
                action.Triggers.Single().Amount == 40m), "second legacy configuration preserved independently");
        return Task.CompletedTask;
    }

    private static Task TestRetiredBuiltInActionsAsync()
    {
        var retired = new ActionDefinition
        {
            Id = ActionDefinition.NewInstanceId(),
            HandlerId = "drop_outfit",
            DisplayName = "Раздеть персонажа",
            IsActive = true,
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["target"] = "both" },
            Triggers = [new TriggerRule { Amount = 15m, Currency = "RUB" }]
        };
        var retained = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "damage_equipment")
            .CloneAsNewInstance();

        var normalized = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = [retired, retained]
        });

        Equal(1, normalized.Actions.Count, "retired built-in action is removed from saved instances");
        Equal("damage_equipment", normalized.Actions.Single().HandlerId,
            "other inventory actions remain active");
        return Task.CompletedTask;
    }

    private static Task TestSchemaV2MembershipAsync()
    {
        var instance = DefaultActionCatalog.CreatePresets().Single(action => action.HandlerId == "change_health").CloneAsNewInstance();
        instance.IsActive = false; // The legacy flag is ignored in the active-only v2 collection.
        instance.Triggers = [new TriggerRule { Amount = 50m, Currency = "RUB" }];
        var normalized = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = [instance]
        });
        Equal(1, normalized.Actions.Count, "v2 collection membership is authoritative");
        True(normalized.Actions.Single().IsActive, "v2 instance normalized active");
        Equal(instance.Id, normalized.Actions.Single().Id, "v2 instance id preserved");
        return Task.CompletedTask;
    }

    private static Task TestSchemaV2RequiresHandlerAsync()
    {
        var instance = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "change_health")
            .CloneAsNewInstance();
        instance.HandlerId = string.Empty;
        Throws<InvalidDataException>(() => SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = [instance]
        }), "v2 must not infer a Lua handler from the instance id");
        return Task.CompletedTask;
    }

    private static Task TestDuplicateTriggerIdsRepairedAsync()
    {
        var preset = DefaultActionCatalog.CreatePresets().Single(action => action.HandlerId == "change_health");
        var first = preset.CloneAsNewInstance();
        var second = preset.CloneAsNewInstance();
        first.Triggers = [new TriggerRule { Id = "duplicate", Amount = 10m, Currency = "RUB" }];
        second.Triggers = [new TriggerRule { Id = "duplicate", Amount = 20m, Currency = "RUB" }];

        var normalized = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = [first, second]
        });

        Equal(2, normalized.Actions.SelectMany(action => action.Triggers)
            .Select(trigger => trigger.Id).Distinct(StringComparer.Ordinal).Count(),
            "trigger ids must be unique after normalization");
        return Task.CompletedTask;
    }

    private static async Task TestSameHandlerInstancesAsync()
    {
        var preset = DefaultActionCatalog.CreatePresets().Single(x => x.HandlerId == "spawn_mutants");
        var dogs = preset.CloneAsNewInstance();
        dogs.Triggers = [new TriggerRule { Amount = 10m, Currency = "RUB" }];
        var chimera = preset.CloneAsNewInstance();
        chimera.Triggers = [new TriggerRule { Amount = 20m, Currency = "RUB" }];
        var liveCatalog = LoadCatalogFixture("catalog-vanilla.json");
        DynamicCatalogBinding.Apply(liveCatalog, [dogs, chimera]);
        dogs.SpawnGroups = [new SpawnGroupEntry { VariantId = "dog_normal", Strength = "normal", Count = 3 }];
        chimera.SpawnGroups = [new SpawnGroupEntry { VariantId = "chimera_strong", Strength = "strong", Count = 3 }];
        Equal(0, CatalogValidation.Validate([dogs, chimera]).Count, "duplicate handlers are allowed");

        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history) { GlobalCooldown = TimeSpan.Zero };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.EnqueueAsync(Donation(20m), [dogs, chimera], new QueuePolicy());
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        var command = transport.Commands.Single();
        Equal("spawn_mutants", command.ActionId, "wire uses handler id");
        Equal("chimera_strong*any*3", command.Parameters[SpawnGroupBundleCodec.ParameterKey],
            "wire uses the matched live section bundle");
        Equal(liveCatalog.Revision, command.CatalogRevision, "wire command keeps the live catalog revision");
        Equal(chimera.Id, history.Entries.Last().ActionId, "history keeps instance id");
        Equal("spawn_mutants", history.Entries.Last().HandlerId, "history keeps handler id");
    }

    private static async Task TestDonationBatchContextAsync()
    {
        var preset = DefaultActionCatalog.CreatePresets().Single(action => action.HandlerId == "change_health");
        var first = preset.CloneAsNewInstance();
        first.DisplayName = "Первый шаблон";
        first.GroupDelaySeconds = 0;
        first.Triggers = [new TriggerRule { Amount = 15m, Currency = "RUB" }];
        var second = preset.CloneAsNewInstance();
        second.DisplayName = "Второй шаблон";
        second.GroupDelaySeconds = 0;
        second.Triggers = [new TriggerRule { Amount = 15m, Currency = "RUB" }];
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), new InMemoryHistorySink())
        { GlobalCooldown = TimeSpan.Zero };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        var donation = Donation(15m);
        donation.Username = "Тестовый донатчик";
        await dispatcher.EnqueueAsync(
            donation,
            [first, second],
            new QueuePolicy(),
            availablePresets: null,
            executionOptions: new EffectExecutionOptions
            {
                SaveBeforeBatch = true,
                SaveAfterBatch = true,
                EffectSaveRetentionCount = 37,
                ShowGameEffectLog = false,
                ShowPdaEffectMessage = true
            });

        await dispatcher.DrainAsync(transport, new QueuePolicy());
        var firstCommand = transport.Commands[0];
        Equal("Тестовый донатчик", firstCommand.DonorName, "donor added to HUD context");
        Equal("Первый шаблон", firstCommand.ActionName, "first action name added to HUD context");
        Equal(1, firstCommand.BatchIndex, "first batch index");
        Equal(2, firstCommand.BatchSize, "shared batch size");
        True(firstCommand.SaveBeforeBatch && firstCommand.SaveAfterBatch, "save settings attached to batch");
        Equal(37, firstCommand.EffectSaveRetentionCount, "retention attached to queued batch");
        True(!firstCommand.ShowGameEffectLog, "disabled game log attached to batch");
        True(firstCommand.ShowPdaEffectMessage, "PDA effect message attached to batch");
        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = firstCommand.CommandId,
            Status = GameResultStatus.Executed,
            Reason = "ok"
        });
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        var secondCommand = transport.Commands[1];
        Equal("Второй шаблон", secondCommand.ActionName, "second action name added to HUD context");
        Equal(2, secondCommand.BatchIndex, "second batch index");
        Equal(2, secondCommand.BatchSize, "second command keeps shared batch size");
        Equal(37, secondCommand.EffectSaveRetentionCount, "retention survives pending-command snapshots");
    }

    private static Task TestExactPriorityAsync()
    {
        var actions = DefaultActionCatalog.Create();
        var exact = actions[0];
        exact.Triggers = [new TriggerRule { Comparator = TriggerComparator.Exact, Amount = 100m, Currency = "RUB" }];
        var greater = actions[1];
        greater.Triggers = [new TriggerRule { Comparator = TriggerComparator.GreaterOrEqual, Amount = 90m, Currency = "RUB" }];
        var match = RuleMatcher.Match(Donation(100m), [greater, exact]);
        Equal(exact.Id, match?.Action.Id, "exact must win");
        return Task.CompletedTask;
    }

    private static Task TestHighestThresholdAsync()
    {
        var actions = DefaultActionCatalog.Create();
        actions[0].Triggers = [new TriggerRule { Comparator = TriggerComparator.GreaterOrEqual, Amount = 10m, Currency = "RUB" }];
        actions[1].Triggers = [new TriggerRule { Comparator = TriggerComparator.GreaterOrEqual, Amount = 50m, Currency = "RUB" }];
        var match = RuleMatcher.Match(Donation(100m), actions.Take(2));
        Equal(actions[1].Id, match?.Action.Id, "highest >= threshold must win");
        return Task.CompletedTask;
    }

    private static Task TestStrictCurrencyAsync()
    {
        var action = DefaultActionCatalog.Create()[0];
        action.Triggers[0].Amount = 1m;
        action.Triggers[0].Currency = "USD";
        Null(RuleMatcher.Match(Donation(1m, "RUB"), [action]), "no FX conversion");
        return Task.CompletedTask;
    }

    private static Task TestMoneyAsync()
    {
        Equal(101L, Money.ToMinor(1.005m, "RUB"), "away-from-zero rounding");
        Equal(1234L, Money.ToMinor(12.34m, "USD"), "USD minor");
        Equal(12.34m, Money.FromMinor(1234, "EUR"), "EUR from minor");
        return Task.CompletedTask;
    }

    private static async Task TestDuplicateTriggerAsync()
    {
        var actions = DefaultActionCatalog.Create().Take(2).ToList();
        actions[0].Triggers = [new TriggerRule { Amount = 10m, Currency = "RUB" }];
        actions[1].Triggers = [new TriggerRule { Amount = 10m, Currency = "RUB" }];
        foreach (var action in actions) { action.CooldownSeconds = 0; action.GroupDelaySeconds = 0; }
        Equal(0, CatalogValidation.Validate(actions).Count,
            "same-price active conditions are valid and execute sequentially");
        Equal(2, RuleMatcher.MatchAll(Donation(10m), actions).Count,
            "every action at the winning price is selected");
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), new InMemoryHistorySink())
        { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };
        Equal(DispatchState.Queued, await dispatcher.EnqueueAsync(Donation(10m), actions, policy),
            "same-price actions enter one stable queue");
        Equal(2, dispatcher.Count, "both same-price actions are queued");
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.DrainAsync(transport, policy);
        Equal(actions[0].HandlerId, transport.Commands.Single().ActionId,
            "same-price queue keeps active-card order");
        await dispatcher.CompleteAsync(new GameExecutionResult
            { CommandId = transport.Commands.Single().CommandId, Status = GameResultStatus.Executed });
        await dispatcher.DrainAsync(transport, policy);
        Equal(actions[1].HandlerId, transport.Commands[1].ActionId,
            "second same-price action runs after the first result");
    }

    private static async Task TestSkipReadyAsync()
    {
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history);
        await dispatcher.EnqueueAsync(Donation(1m), DefaultActionCatalog.Create(), new QueuePolicy { Mode = QueueMode.Skip });
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.DrainAsync(transport, new QueuePolicy { Mode = QueueMode.Skip });
        Equal(1, transport.Commands.Count, "ready skip policy must send");
    }

    private static async Task TestLegacySkipPreservesAsync()
    {
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history);
        var policy = new QueuePolicy { Mode = QueueMode.Skip, MaximumQueuedEvents = 1 };
        await dispatcher.EnqueueAsync(Donation(1m), DefaultActionCatalog.Create(), policy);
        await dispatcher.EnqueueAsync(Donation(2m), DefaultActionCatalog.Create(), policy);
        await dispatcher.DrainAsync(new FakeTransport(), policy);
        Equal(2, dispatcher.Count, "legacy skip and capacity settings must not discard queued events");
        True(history.Entries.All(x => x.State != DispatchState.Skipped),
            "unavailable game leaves every event queued");
    }

    private static async Task TestReadinessRaceAsync()
    {
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var donation = Donation(1m);
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };
        await dispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), policy);

        await dispatcher.DrainAsync(new FakeTransport
        {
            IsConnected = true,
            IsGameReady = true,
            NotReadyBeforeSend = true
        }, policy);

        Equal(1, dispatcher.Count, "safe pre-send failure must restore the queue head");
        Equal(DispatchState.Queued, (await processed.FindAsync(donation.Id))?.State,
            "safe pre-send failure restores the queued ledger state");
        True(history.Entries.Last().State == DispatchState.Queued &&
                history.Entries.All(entry => entry.State != DispatchState.Uncertain),
            "safe pre-send failure must not be reported as uncertain delivery");

        var recovered = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.DrainAsync(recovered, policy);
        Equal(1, recovered.Commands.Count,
            "restored queue head is delivered after the game becomes active again");
    }

    private static async Task TestLegacyQueueTtlAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history, () => now);
        var policy = new QueuePolicy { Mode = QueueMode.WaitForDuration, WaitMinutes = 1 };
        await dispatcher.EnqueueAsync(Donation(1m), DefaultActionCatalog.Create(), policy);
        now = now.AddMinutes(2);
        await dispatcher.DrainAsync(new FakeTransport(), policy);
        Equal(1, dispatcher.Count, "legacy TTL must not expire an accepted event");
        True(history.Entries.All(x => x.State != DispatchState.Expired),
            "guaranteed queue never emits an expired status");
    }

    private static async Task TestActionCooldownFairnessAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-03T01:00:00Z");
        var actions = DefaultActionCatalog.Create().Take(2).ToList();
        actions[0].CooldownSeconds = 30;
        actions[1].CooldownSeconds = 0;
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => now)
        { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy();
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };

        await dispatcher.EnqueueAsync(Donation(1m), actions, policy);
        await dispatcher.DrainAsync(transport, policy);
        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = transport.Commands[0].CommandId,
            Status = GameResultStatus.Executed
        });

        var coolingDonation = Donation(1m);
        var readyDonation = Donation(2m);
        await dispatcher.EnqueueAsync(coolingDonation, actions, policy);
        await dispatcher.EnqueueAsync(readyDonation, actions, policy);
        await dispatcher.DrainAsync(transport, policy);
        Equal(readyDonation.Id, transport.Commands[1].DonationId,
            "cooling-down head moves aside for an unrelated action");
        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = transport.Commands[1].CommandId,
            Status = GameResultStatus.Executed
        });
        Equal(1, dispatcher.Count, "cooling-down event remains queued");
        Equal(30, (await dispatcher.GetTimingAsync(true, true)).SecondsUntilNext,
            "timer follows the earliest runnable queued action");

        now = now.AddSeconds(31);
        await dispatcher.DrainAsync(transport, policy);
        Equal(coolingDonation.Id, transport.Commands[2].DonationId,
            "rotated event executes after its own cooldown");
    }

    private static async Task TestUncertainAsync()
    {
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history);
        var donation = Donation(1m);
        await dispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy());
        await dispatcher.DrainAsync(new FakeTransport { IsConnected = true, IsGameReady = true, ThrowOnSend = true }, new QueuePolicy());
        Equal(DispatchState.Uncertain, (await processed.FindAsync(donation.Id))?.State, "uncertain ledger");
        Equal(DispatchState.Duplicate, await dispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy()), "no auto duplicate");
    }

    private static Task TestWireEncodingAsync()
    {
        var command = new GameCommand
        {
            CommandId = "cmd 1",
            DonationId = "донат;=1",
            ActionId = "spawn_items",
            AmountMinor = 100,
            Currency = "RUB",
            CatalogRevision = new CatalogRevision("session-encoded-1"),
            Parameters = new() { ["section_id"] = "medkit;special=1", ["note"] = "ёж" }
        };
        var fields = PipeProtocol.Command(command).Split('\t');
        Equal("донат;=1", PipeProtocol.PercentDecode(fields[2]), "outer regular field");
        var structured = PipeProtocol.PercentDecode(fields[6]);
        var parsed = PipeProtocol.ParseStructuredParameters(structured);
        Equal("medkit;special=1", parsed["section_id"], "nested separator-safe parameter");
        Equal("ёж", parsed["note"], "UTF-8 parameter");
        Equal("session-encoded-1", parsed[CatalogRevisionEnforcement.RevisionParameterKey],
            "dynamic command carries its catalog revision");
        return Task.CompletedTask;
    }

    private static Task TestWireLimitAsync()
    {
        var command = new GameCommand
        {
            CommandId = "c",
            DonationId = "d",
            ActionId = "spawn_items",
            Currency = "RUB",
            CatalogRevision = new CatalogRevision("session-limit-1"),
            Parameters = new() { ["section_id"] = new string('x', PipeProtocol.MaximumLineBytes) }
        };
        Throws<InvalidDataException>(() => PipeProtocol.Command(command), "oversized command");
        return Task.CompletedTask;
    }

    private static Task TestFirstRunRecoveryAsync()
    {
        var selected = DonationRecoveryPlanner.Select(new RecoveryCheckpoint(), [Donation(1m)], new QueuePolicy(), DateTimeOffset.UtcNow);
        Equal(0, selected.Count, "first run no backlog");
        return Task.CompletedTask;
    }

    private static Task TestRecoveryTtlAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        var recent = Donation(1m); recent.Id = "recent"; recent.CreatedAt = now.AddMinutes(-2);
        var old = Donation(1m); old.Id = "old"; old.CreatedAt = now.AddMinutes(-20);
        var selected = DonationRecoveryPlanner.Select(
            new RecoveryCheckpoint { Initialized = true, LastCreatedAt = now.AddHours(-1), LastDonationId = "checkpoint" },
            [old, recent],
            new QueuePolicy { Mode = QueueMode.WaitForDuration, WaitMinutes = 5 }, now);
        SequenceEqual(new[] { "recent" }, selected.Select(x => x.Id), "TTL recovery");
        return Task.CompletedTask;
    }

    private static Task TestRecoveryContinuityAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
        var queuedOlder = Donation(1m); queuedOlder.Id = "queued-older"; queuedOlder.CreatedAt = now.AddMinutes(-4);
        var terminalNewer = Donation(99m); terminalNewer.Id = "terminal-newer"; terminalNewer.CreatedAt = now.AddMinutes(-1);
        var selected = DonationRecoveryPlanner.Select(
            new RecoveryCheckpoint
            {
                Initialized = true,
                LastDonationId = terminalNewer.Id,
                LastCreatedAt = terminalNewer.CreatedAt
            },
            [terminalNewer, queuedOlder],
            new QueuePolicy { Mode = QueueMode.WaitForDuration, WaitMinutes = 5 },
            now);
        SequenceEqual(new[] { "queued-older", "terminal-newer" }, selected.Select(item => item.Id), "ledger, not checkpoint, decides terminal state");
        return Task.CompletedTask;
    }

    private static Task TestSubscriptionResponseAsync()
    {
        const string channel = "$alerts:donation_42";
        using var arrayDocument = JsonDocument.Parse("{\"channels\":[{\"channel\":\"$alerts:donation_42\",\"token\":\"array-token\"}]}");
        Equal("array-token", DonationAlertsRestClient.ParseCentrifugeSubscriptionToken(arrayDocument.RootElement, channel), "official array response");
        using var legacyDocument = JsonDocument.Parse("{\"channels\":{\"$alerts:donation_42\":{\"token\":\"object-token\"}}}");
        Equal("object-token", DonationAlertsRestClient.ParseCentrifugeSubscriptionToken(legacyDocument.RootElement, channel), "defensive object response");
        return Task.CompletedTask;
    }

    private static Task TestFileAuthoredMetadataAsync()
    {
        var action = new ActionDefinition
        {
            HandlerId = "change_health",
            DisplayName = "Новый файловый шаблон",
            Description = "Из заголовка скрипта",
            CategoryId = "world",
            ScriptRelativePath = "gamedata/scripts/anomaly_donation_bridge_effect_change_health.script",
            ParameterDefinitions =
            [
                new ParameterDefinition
                {
                    Key = "new_mode",
                    DisplayName = "Режим",
                    Kind = ParameterKind.Choice,
                    Options = ["chaos", "calm"],
                    DefaultValue = "chaos"
                }
            ],
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["new_mode"] = "calm"
            }
        };
        var settings = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = [action.Clone()],
            UserPresets = [action.CloneAsNewInstance()]
        });
        foreach (var saved in settings.Actions.Concat(settings.UserPresets))
        {
            Equal("Новый файловый шаблон", saved.DisplayName, "file-authored display name retained");
            Equal("world", saved.CategoryId, "file-authored category retained");
            Equal("calm", saved.Parameters["new_mode"], "file-authored parameter retained");
            True(saved.ParameterDefinitions.Any(definition => definition.Key == "new_mode"),
                "file-authored parameter schema retained");
        }
        return Task.CompletedTask;
    }

    private static async Task TestJsonSettingsAsync()
    {
        var directory = CreateTestDirectoryPath();
        var paths = new AppPaths(directory);
        try
        {
            var store = new JsonSettingsStore(paths);
            var settings = new AppSettings
            {
                GamePath = "C:\\Game",
                AnonymousDonorName = "Таинственный зритель",
                Armed = true,
                MinimizeToTrayOnClose = false,
                HideUnavailableCatalogSections = false,
                UseLightTheme = true,
                LimitDonationAlertsApiRequests = false,
                LimitDonateXApiRequests = false,
                DonateXUserId = "user-1",
                DonateXAccountName = "streamer",
                SaveBeforeDonationEffects = true,
                SaveAfterDonationEffects = true,
                EffectSaveRetentionCount = 73,
                ShowGameEffectLog = false,
                ShowPdaEffectMessage = true,
                CatalogExclusions =
                [
                    new CatalogExclusion
                    {
                        Kind = CatalogEntryKind.Item,
                        SectionId = "wpn_broken",
                        DisplayName = "Broken weapon",
                        Category = "weapons"
                    }
                ]
            };
            var starter = DefaultActionCatalog.CreatePresets()
                .Single(action => action.HandlerId == "spawn_items")
                .CloneAsNewInstance();
            var instanceId = starter.Id;
            var handlerId = starter.HandlerId;
            starter.Triggers.Add(new TriggerRule { Amount = 42m, Currency = "USD" });
            settings.Actions.Add(starter);
            var randomAll = DefaultActionCatalog.CreatePresets()
                .Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
                .CloneAsNewInstance();
            randomAll.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] =
                "change_health,teleport_random";
            settings.Actions.Add(randomAll);
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Equal("C:\\Game", loaded.GamePath, "game path");
            Equal("Таинственный зритель", loaded.AnonymousDonorName,
                "anonymous donor replacement persisted");
            True(loaded.Armed, "armed");
            True(!loaded.MinimizeToTrayOnClose, "tray close setting persisted");
            True(!loaded.HideUnavailableCatalogSections, "unavailable catalog visibility setting persisted");
            True(loaded.UseLightTheme, "light theme setting persisted");
            True(!loaded.LimitDonationAlertsApiRequests, "API limiter setting persisted");
            True(!loaded.LimitDonateXApiRequests, "DonateX API limiter setting persisted");
            Equal("streamer", loaded.DonateXAccountName, "DonateX account persisted");
            True(loaded.SaveBeforeDonationEffects, "save-before setting persisted");
            True(loaded.SaveAfterDonationEffects, "save-after setting persisted");
            Equal(73, loaded.EffectSaveRetentionCount, "retention persisted and normalized");
            Equal(50, JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Options)!.EffectSaveRetentionCount, "old settings default retention");
            Equal(0, SettingsNormalizer.Normalize(new AppSettings { EffectSaveRetentionCount = -2 }).EffectSaveRetentionCount, "invalid low retention clamped safely");
            Equal(10_000, SettingsNormalizer.Normalize(new AppSettings { EffectSaveRetentionCount = 20_000 }).EffectSaveRetentionCount, "retention has upper bound");
            True(!loaded.ShowGameEffectLog, "game effect log setting persisted");
            True(loaded.ShowPdaEffectMessage, "PDA effect message setting persisted");
            Equal("wpn_broken", loaded.CatalogExclusions.Single().SectionId,
                "catalog exclusion persisted");
            Equal(instanceId, loaded.Actions[0].Id, "instance id persisted");
            Equal(handlerId, loaded.Actions[0].HandlerId, "handler id persisted");
            True(loaded.Actions[0].Triggers.Any(x => x.Amount == 42m && x.Currency == "USD"), "trigger persisted");
            Equal(
                "change_health,teleport_random",
                loaded.Actions.Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
                    .Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey],
                "random-all checkbox selection persisted as an ordinary action parameter");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static Task TestSchema14CompatibilityAsync()
    {
        var normalized = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = 14,
            GamePath = "C:\\ExistingGame",
            DonateXAccountName = "streamer"
        });
        Equal(13, SettingsNormalizer.CurrentSchemaVersion, "persisted settings schema stays compatible");
        Equal(13, normalized.SchemaVersion, "schema 14 is normalized back to 13");
        Equal("C:\\ExistingGame", normalized.GamePath, "schema 14 game path retained");
        Equal("streamer", normalized.DonateXAccountName, "schema 14 DonateX fields retained");
        return Task.CompletedTask;
    }

    private static async Task TestSettingsBackupAsync()
    {
        var directory = CreateTestDirectoryPath();
        var paths = new AppPaths(directory);
        try
        {
            var store = new JsonSettingsStore(paths);
            await store.SaveAsync(new AppSettings { GamePath = "C:\\Before" });
            True(!File.Exists(paths.SettingsBackupFile), "first settings write has no previous file to back up");
            await store.SaveAsync(new AppSettings { GamePath = "C:\\After" });
            True(File.Exists(paths.SettingsBackupFile), "previous settings backup created");
            var backup = JsonSerializer.Deserialize<AppSettings>(
                await File.ReadAllTextAsync(paths.SettingsBackupFile), JsonDefaults.Options)
                ?? throw new InvalidDataException("settings backup was empty");
            Equal("C:\\Before", backup.GamePath, "backup contains the previous settings snapshot");
            Equal("C:\\After", (await store.LoadAsync()).GamePath, "primary settings contain the new snapshot");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestProfilePersistenceAsync()
    {
        var directory = CreateTestDirectoryPath();
        var paths = new AppPaths(directory);
        try
        {
            var legacyAction = DefaultActionCatalog.CreatePresets()
                .Single(action => action.HandlerId == "change_health")
                .CloneAsNewInstance();
            var migrated = SettingsNormalizer.Normalize(new AppSettings
            {
                SchemaVersion = 7,
                Actions = [legacyAction],
                ActivePresetHotkeys =
                [
                    new ActivePresetHotkey { PresetNumber = 1, Hotkey = "Ctrl+F7" }
                ]
            });
            Equal(1, migrated.Profiles.Count, "legacy settings migrated into one profile");
            Equal("Основной", migrated.Profiles[0].Name, "migrated profile name");
            True(ReferenceEquals(migrated.Actions, migrated.Profiles[0].Actions),
                "active action list is bound to active profile");
            True(ReferenceEquals(migrated.ActivePresetHotkeys, migrated.Profiles[0].ActivePresetHotkeys) &&
                migrated.ActivePresetHotkeys.Single().Hotkey == "Ctrl+F7",
                "legacy preset hotkeys migrated into the active profile");

            var first = migrated.Profiles[0];
            first.Name = "Выживание";
            var secondAction = DefaultActionCatalog.CreatePresets()
                .Single(action => action.HandlerId == "change_money")
                .CloneAsNewInstance();
            var secondPreset = DefaultActionCatalog.CreatePresets()
                .Single(action => action.HandlerId == "spawn_items")
                .CloneAsNewInstance();
            var second = new DonationProfile
            {
                Name = "Хаос",
                Actions = [secondAction],
                UserPresets = [secondPreset],
                ActivePresetHotkeys =
                [
                    new ActivePresetHotkey { PresetNumber = 1, Hotkey = "Alt+F8" }
                ]
            };
            migrated.Profiles.Add(second);
            migrated.ActiveProfileId = second.Id;
            migrated.Actions = second.Actions;
            migrated.UserPresets = second.UserPresets;
            migrated.ActivePresetHotkeys = second.ActivePresetHotkeys;

            var store = new JsonSettingsStore(paths);
            await store.SaveAsync(migrated);
            var loaded = await store.LoadAsync();
            Equal(2, loaded.Profiles.Count, "all profiles persisted");
            Equal("Хаос", loaded.Profiles.Single(profile => profile.Id == loaded.ActiveProfileId).Name,
                "active profile persisted");
            SequenceEqual(new[] { "change_money" }, loaded.Actions.Select(action => action.HandlerId),
                "only active profile actions are exposed");
            SequenceEqual(new[] { "spawn_items" }, loaded.UserPresets.Select(action => action.HandlerId),
                "only active profile user presets are exposed");
            Equal("Alt+F8", loaded.ActivePresetHotkeys.Single().Hotkey,
                "only active profile preset hotkeys are exposed");
            SequenceEqual(new[] { "change_health" },
                loaded.Profiles.Single(profile => profile.Name == "Выживание").Actions.Select(action => action.HandlerId),
                "inactive profile remains isolated");
            True(ReferenceEquals(
                    loaded.Actions,
                    loaded.Profiles.Single(profile => profile.Id == loaded.ActiveProfileId).Actions),
                "loaded active lists are bound to the selected profile");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestDpapiAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = CreateTestDirectoryPath();
        var paths = new AppPaths(directory);
        try
        {
            var store = new DpapiSecretStore(paths);
            await store.SaveAsync(new SecretBundle
            {
                ClientSecret = "secret",
                AccessToken = "access",
                RefreshToken = "refresh",
                DonateXExternalToken = "donatex-token"
            });
            var loaded = await store.LoadAsync();
            Equal("secret", loaded.ClientSecret, "DPAPI secret");
            Equal("donatex-token", loaded.DonateXExternalToken, "DonateX token");
            var raw = await File.ReadAllTextAsync(paths.SecretsFile);
            True(!raw.Contains("secret", StringComparison.Ordinal), "secret not plaintext");
            True(!raw.Contains("donatex-token", StringComparison.Ordinal), "DonateX token not plaintext");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestLogRedactionAsync()
    {
        var directory = CreateTestDirectoryPath();
        var paths = new AppPaths(directory);
        try
        {
            var logger = new SafeFileLogger(paths);
            await logger.WriteAsync("ERROR", "client_secret=alpha access_token:\"beta\" refresh_token=gamma Authorization: Bearer delta-token-123456 eyJabcdefghijklmnop.qwertyuiop");
            var text = await File.ReadAllTextAsync(paths.LogFile);
            foreach (var secret in new[] { "alpha", "beta", "gamma", "delta-token-123456", "eyJabcdefghijklmnop" })
            {
                True(!text.Contains(secret, StringComparison.Ordinal), "log leaked " + secret);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestRefreshTokenAsync()
    {
        using var http = new HttpClient(new DelegateHandler(_ => JsonResponse("{\"access_token\":\"new-access\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")));
        var client = new DonationAlertsRestClient(http, PassThroughRateLimiter.Instance);
        var tokens = await client.RefreshTokenAsync("id", "secret", "old-refresh");
        Equal("old-refresh", tokens.RefreshToken, "refresh endpoint may omit refresh_token");

        using var authHttp = new HttpClient(new DelegateHandler(_ => JsonResponse("{\"access_token\":\"access\",\"expires_in\":3600}")));
        var authClient = new DonationAlertsRestClient(authHttp, PassThroughRateLimiter.Instance);
        await ThrowsAsync<InvalidDataException>(() => authClient.ExchangeAuthorizationCodeAsync("id", "secret", "code"), "initial grant must require refresh_token");
    }

    private static async Task TestOAuthLoopbackAsync()
    {
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal) == true)
            {
                return JsonResponse("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            }
            return JsonResponse("{\"data\":{\"id\":42,\"code\":\"test_channel\",\"name\":\"Tester\",\"socket_connection_token\":\"socket\"}}");
        }));
        var flow = new LoopbackOAuthFlow(new DonationAlertsRestClient(http, PassThroughRateLimiter.Instance));
        Task? callbackTask = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await flow.AuthorizeAsync("client", "secret", authorizationUri =>
        {
            var state = QueryValue(authorizationUri.Query, "state");
            callbackTask = Task.Run(async () =>
            {
                using var callbackClient = new HttpClient();
                _ = await callbackClient.GetStringAsync(DonationAlertsEndpoints.RedirectUri + "?code=test-code&state=" + Uri.EscapeDataString(state), timeout.Token);
            }, timeout.Token);
        }, timeout.Token);
        if (callbackTask is not null) await callbackTask;
        Equal("test_channel", result.Profile.Code, "profile code");
        Equal("refresh", result.Tokens.RefreshToken, "authorization tokens");

        await ThrowsAsync<InvalidDataException>(() => flow.AuthorizeAsync("client", "secret", _authorizationUri =>
        {
            _ = Task.Run(async () =>
            {
                using var callbackClient = new HttpClient();
                _ = await callbackClient.GetStringAsync(DonationAlertsEndpoints.RedirectUri + "?code=test-code&state=wrong", timeout.Token);
            }, timeout.Token);
        }, timeout.Token), "mismatched state must fail");
    }

    private static async Task TestDonationAlertsApiRateLimiterAsync()
    {
        Equal(60, DonationAlertsApiRateLimiter.DefaultMaximumRequests, "default rolling-window request limit");
        Equal(TimeSpan.FromMinutes(1), DonationAlertsApiRateLimiter.DefaultWindow, "default rolling window");
        Equal(TimeSpan.FromSeconds(1), DonationAlertsApiRateLimiter.DefaultMinimumInterval, "default request spacing");

        var enabled = true;
        var now = DateTimeOffset.Parse("2026-09-04T00:00:00Z");
        var delays = new List<TimeSpan>();
        using (var limiter = new DonationAlertsApiRateLimiter(
            () => enabled,
            maximumRequests: 60,
            window: TimeSpan.FromMinutes(1),
            minimumInterval: TimeSpan.FromSeconds(1),
            utcNow: () => now,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                now += duration;
                return Task.CompletedTask;
            }))
        {
            await limiter.WaitAsync();
            await limiter.WaitAsync();
            Equal(1, delays.Count, "second request is delayed");
            Equal(TimeSpan.FromSeconds(1), delays[0], "safe one-request-per-second spacing");

            enabled = false;
            await limiter.WaitAsync();
            Equal(1, delays.Count, "disabled limiter does not delay HTTP requests");
        }

        now = DateTimeOffset.Parse("2026-09-04T01:00:00Z");
        delays.Clear();
        using var rollingLimiter = new DonationAlertsApiRateLimiter(
            () => true,
            maximumRequests: 2,
            window: TimeSpan.FromMinutes(1),
            minimumInterval: TimeSpan.Zero,
            utcNow: () => now,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                now += duration;
                return Task.CompletedTask;
            });
        await rollingLimiter.WaitAsync();
        await rollingLimiter.WaitAsync();
        await rollingLimiter.WaitAsync();
        Equal(1, delays.Count, "rolling-window overflow is delayed");
        Equal(TimeSpan.FromMinutes(1), delays[0], "rolling-window limit waits for the oldest request to expire");
    }

    private static async Task TestDonationAlertsRestUsesRateLimiterAsync()
    {
        var limiter = new CountingRateLimiter();
        using var http = new HttpClient(new DelegateHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/oauth/token" => JsonResponse("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"),
            "/api/v1/user/oauth" => JsonResponse("{\"data\":{\"id\":42,\"code\":\"test_channel\",\"name\":\"Tester\",\"socket_connection_token\":\"socket\"}}"),
            "/api/v1/alerts/donations" => JsonResponse("{\"data\":[{\"id\":\"da-1\",\"username\":\"viewer\",\"message\":\"hello da\",\"amount\":100,\"currency\":\"RUB\"}],\"meta\":{\"current_page\":1,\"last_page\":1}}"),
            "/api/v1/centrifuge/subscribe" => JsonResponse("{\"data\":{\"channels\":[{\"channel\":\"$alerts:donation_42\",\"token\":\"subscription\"}]}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new DonationAlertsRestClient(http, limiter);

        await client.ExchangeAuthorizationCodeAsync("id", "secret", "code");
        await client.GetProfileAsync("access");
        var donations = await client.GetDonationsAsync("access");
        await client.CreateCentrifugeSubscriptionAsync("access", 42, "client");

        Equal(4, limiter.WaitCount, "every outbound DonationAlerts HTTP request is rate-limited");
        Equal("hello da", donations.Donations.Single().Message,
            "DonationAlerts donation message parsed");
    }

    private static async Task TestDonateXRestAsync()
    {
        var limiter = new DonateXCountingRateLimiter();
        var requests = new List<Uri>();
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri?.Query.Contains("token=", StringComparison.Ordinal) == true &&
                request.RequestUri.Query.Contains("access_token=", StringComparison.Ordinal) == false &&
                QueryValue(request.RequestUri.Query, "token") == "external-secret")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/user/me" => JsonResponse("{\"id\":\"profile-id\",\"username\":\"streamer\"}"),
                "/api/v1/donations" => JsonResponse("[{\"id\":\"donation-id\",\"username\":\"viewer\",\"message\":\"hello\",\"currency\":\"RUB\",\"amount\":150.5,\"timestamp\":\"2026-09-27T12:00:00Z\",\"isTest\":false}]"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new DonateXRestClient(http, limiter);
        var profile = await client.GetProfileAsync("external-secret");
        var donations = await client.GetDonationsAsync("external-secret", 0, 20);

        Equal("streamer", profile.Username, "DonateX profile parsed");
        Equal(1, donations.Count, "DonateX donation parsed");
        Equal("donatex:donation-id", donations[0].Id, "DonateX IDs are provider-prefixed");
        Equal(150.5m, donations[0].Amount, "DonateX amount parsed");
        Equal("hello", donations[0].Message, "DonateX donation message parsed");
        Equal(string.Empty, new DonateXDonation
        {
            Id = "anonymous",
            Username = " \t ",
            Amount = 1m,
            Currency = "RUB"
        }.ToDonationEvent().Username, "DonateX leaves an absent name for the shared configurable fallback");
        Equal(2, requests.Where(uri => uri.Query.Contains("token=", StringComparison.Ordinal) &&
                !uri.Query.Contains("access_token=", StringComparison.Ordinal))
            .Count(uri => QueryValue(uri.Query, "token") == "external-secret"),
            "external token first uses the documented token query parameter");
        Equal(2, requests.Where(uri => uri.Query.Contains("access_token=", StringComparison.Ordinal))
            .Count(uri => QueryValue(uri.Query, "access_token") == "external-secret"),
            "401 retries with the documented access_token query parameter");
        Equal(4, limiter.WaitCount, "every outbound DonateX REST attempt is rate-limited");
        Equal("external-secret", DonateXToken.Normalize(" \u200BBearer external-secret\r\n"),
            "clipboard token normalization");

        var bearerRequests = new List<HttpRequestMessage>();
        using var bearerHttp = new HttpClient(new DelegateHandler(request =>
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            clone.Headers.Authorization = request.Headers.Authorization;
            bearerRequests.Add(clone);
            if (request.Headers.Authorization is null)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return JsonResponse("{\"id\":\"profile-id\",\"username\":\"streamer\"}");
        }));
        var bearerClient = new DonateXRestClient(bearerHttp, new DonateXCountingRateLimiter());
        await bearerClient.GetProfileAsync("external-secret");
        Equal(3, bearerRequests.Count, "Bearer fallback follows both query forms");
        Equal("Bearer", bearerRequests[^1].Headers.Authorization?.Scheme, "Bearer fallback scheme");
        Equal("external-secret", bearerRequests[^1].Headers.Authorization?.Parameter, "Bearer fallback token");
        foreach (var request in bearerRequests) request.Dispose();
    }

    private static async Task TestDonateXRateLimiterAsync()
    {
        Equal(10, DonateXApiRateLimiter.PerSecond, "DonateX per-second limit");
        Equal(600, DonateXApiRateLimiter.PerMinute, "DonateX per-minute limit");
        Equal(10_000, DonateXApiRateLimiter.PerHour, "DonateX per-hour limit");
        using var limiter = new DonateXApiRateLimiter(() => false);
        await limiter.WaitAsync();
    }

    private static async Task TestIpcAppFirstAsync()
    {
        var pipeName = CreateTestPipeName();
        await using var server = new NamedPipeGameServer("1.0.0", pipeName);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<GameExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConnectionChanged += state => { if (state.Ready) ready.TrySetResult(true); };
        server.ResultReceived += value => result.TrySetResult(value);
        server.Start();

        using var client = await ConnectPipeAsync(pipeName);
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { NewLine = "\n", AutoFlush = true };
        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t1234");
        True((await reader.ReadLineAsync())?.StartsWith("WELCOME\t1\t", StringComparison.Ordinal) == true, "welcome");
        await writer.WriteLineAsync("CAPABILITIES\tunknown\ttest\tnamed_pipe%3Bcorrelated_ping\tadd_radiation");
        await writer.WriteLineAsync("STATUS\tready\tok");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var commandLine = await SendCommandWithReadinessProbeAsync(
            server,
            new GameCommand { CommandId = "cmd", DonationId = "don", ActionId = "add_radiation", AmountMinor = 100, Currency = "RUB", Parameters = new() { ["percent"] = "25" } },
            reader,
            writer,
            commandTimeout.Token);
        True(commandLine.StartsWith("COMMAND\tcmd\tdon\tadd_radiation", StringComparison.Ordinal), "command line");
        await writer.WriteLineAsync("RESULT\tcmd\texecuted\tok");
        Equal(GameResultStatus.Executed, (await result.Task.WaitAsync(TimeSpan.FromSeconds(3))).Status, "result status");
    }

    private static async Task TestIpcIdleWithoutGameAsync()
    {
        await using var server = new NamedPipeGameServer("1.0.0", CreateTestPipeName());
        server.Start();
        await Task.Delay(300);
        True(!server.IsConnected, "server must remain idle while the game is absent");
        True(!server.IsGameReady, "absent game cannot be reported ready");
    }

    private static async Task TestIpcDeathQueueAsync()
    {
        var pipeName=CreateTestPipeName();
        await using var server=new NamedPipeGameServer("test",pipeName);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        GameConnectionSnapshot? snapshot=null;
        server.ConnectionChanged+=value=>Volatile.Write(ref snapshot,value);
        server.Start();
        using var client=await ConnectPipeAsync(pipeName);
        using var reader=new StreamReader(client,new UTF8Encoding(false),false,4096,true);
        using var writer=new StreamWriter(client,new UTF8Encoding(false),4096,true){NewLine="\n",AutoFlush=true};
        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t1234");
        True((await reader.ReadLineAsync(timeout.Token))!.StartsWith("WELCOME\t1\t"),"death fixture handshake");
        await writer.WriteLineAsync("CAPABILITIES\tunknown\ttest\tnamed_pipe%3Bcorrelated_ping\tadd_radiation");
        await writer.WriteLineAsync("STATUS\tready\tok");
        await WaitUntilAsync(()=>server.IsGameReady,timeout.Token);
        var history=new InMemoryHistorySink();
        var dispatcher=new DonationDispatcher(new InMemoryProcessedDonationStore(),history){GlobalCooldown=TimeSpan.Zero};
        var policy=new QueuePolicy();
        Equal(DispatchState.Queued,await dispatcher.EnqueueAsync(Donation(1m),DefaultActionCatalog.Create(),policy),"death fixture enqueue");
        foreach(var reason in new[]{"actor_dead","game_loading"})
        {
            await writer.WriteLineAsync("STATUS\twaiting\t"+reason);
            await WaitUntilAsync(()=>Volatile.Read(ref snapshot)?.Reason==reason,timeout.Token);
            await dispatcher.DrainAsync(server,policy,timeout.Token);
            Equal(1,dispatcher.Count,"effect must remain queued during "+reason);
            True(history.Entries.All(e=>e.State==DispatchState.Queued),"effect sent/consumed during "+reason);
        }
        await writer.WriteLineAsync("STATUS\tready\tok");
        await WaitUntilAsync(()=>server.IsGameReady,timeout.Token);
        var drain=dispatcher.DrainAsync(server,policy,timeout.Token);
        string? command=null;
        while(command is null)
        {
            var line=await reader.ReadLineAsync(timeout.Token)??throw new IOException("death fixture disconnected");
            if(line.StartsWith("PING\t")) await writer.WriteLineAsync("PONG\t"+line[5..]);
            else if(line.StartsWith("COMMAND\t")) command=line;
        }
        await drain;
        True(command.Contains("\tadd_radiation\t"),"wrong effect after actor became alive");
        await dispatcher.DrainAsync(server,policy,timeout.Token);
        Equal(1,history.Entries.Count(e=>e.State==DispatchState.Sent),"alive recovery sent duplicate effects");
    }

    private static async Task TestIpcManualResultRecoveryAsync()
    {
        var pipeName = CreateTestPipeName();
        await using var server = new NamedPipeGameServer("1.0.0", pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = new TaskCompletionSource<GameExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ResultReceived += value => result.TrySetResult(value);
        server.Start();

        using var firstClient = await ConnectPipeAsync(pipeName);
        using var firstReader = new StreamReader(
            firstClient, new UTF8Encoding(false), false, 4096, true);
        using var firstWriter = new StreamWriter(
            firstClient, new UTF8Encoding(false), 4096, true)
        { NewLine = "\n", AutoFlush = true };
        await firstWriter.WriteLineAsync("HELLO\t1\t1.0.0\t2468");
        True((await firstReader.ReadLineAsync(cancellation.Token))?.StartsWith(
            "WELCOME\t1\t", StringComparison.Ordinal) == true, "refresh welcome");
        await firstWriter.WriteLineAsync(
            "CAPABILITIES\tunknown\ttest\tnamed_pipe%3Bcorrelated_ping\tadd_radiation");
        await firstWriter.WriteLineAsync("STATUS\tready\tok");
        await WaitUntilAsync(() => server.IsGameReady, cancellation.Token);

        var refreshCommand = new GameCommand
        {
            CommandId = "refresh-command",
            DonationId = "refresh-donation",
            ActionId = "add_radiation",
            AmountMinor = 100,
            Currency = "RUB",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["percent"] = "25" }
        };
        var original = await SendCommandWithReadinessProbeAsync(
            server, refreshCommand, firstReader, firstWriter, cancellation.Token);
        True(original?.StartsWith(
            "COMMAND\trefresh-command\trefresh-donation\tadd_radiation",
            StringComparison.Ordinal) == true, "original refresh command");

        var recoveryTask = server.RecoverPendingResultsAsync(cancellation.Token);
        var replay = await firstReader.ReadLineAsync(cancellation.Token);
        True(replay?.StartsWith(
            "COMMAND\trefresh-command\trefresh-donation\tadd_radiation",
            StringComparison.Ordinal) == true, "refresh replays the exact command id");
        await firstWriter.WriteLineAsync("RESULT\trefresh-command\texecuted\trecovered");

        var report = await recoveryTask;
        Equal(1, report.RequestedCount, "one result requested by refresh");
        Equal(0, report.RemainingCount, "refreshed result is no longer pending");
        Equal(1, report.RecoveredCount, "one result recovered by refresh");
        True(report.ReplayRequested, "refresh replays over the live pipe");
        True(report.GameConnected && server.IsConnected,
            "refresh must not manufacture a game disconnect");
        Equal(GameResultStatus.Executed,
            (await result.Task.WaitAsync(cancellation.Token)).Status,
            "recovered result status");
    }

    private static async Task TestIpcManualRecoveryWhileLoadingAsync()
    {
        var pipeName = CreateTestPipeName();
        await using var server = new NamedPipeGameServer("1.0.0", pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = new TaskCompletionSource<GameExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ResultReceived += value => result.TrySetResult(value);
        server.Start();

        using var client = await ConnectPipeAsync(pipeName);
        using var reader = new StreamReader(
            client, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), 4096, true)
        { NewLine = "\n", AutoFlush = true };
        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t9753");
        True((await reader.ReadLineAsync(cancellation.Token))?.StartsWith(
            "WELCOME\t1\t", StringComparison.Ordinal) == true, "loading refresh welcome");
        await writer.WriteLineAsync(
            "CAPABILITIES\tunknown\ttest\tnamed_pipe%3Bcorrelated_ping\tadd_radiation");
        await writer.WriteLineAsync("STATUS\tready\tok");
        await WaitUntilAsync(() => server.IsGameReady, cancellation.Token);

        var loadingRefreshCommand = new GameCommand
        {
            CommandId = "loading-refresh-command",
            DonationId = "loading-refresh-donation",
            ActionId = "add_radiation",
            AmountMinor = 100,
            Currency = "RUB",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["percent"] = "25" }
        };
        True((await SendCommandWithReadinessProbeAsync(
            server, loadingRefreshCommand, reader, writer, cancellation.Token)).StartsWith(
            "COMMAND\tloading-refresh-command", StringComparison.Ordinal) == true,
            "loading refresh original command");

        await writer.WriteLineAsync("STATUS\twaiting\tgame_loading");
        await WaitUntilAsync(() => !server.IsGameReady, cancellation.Token);
        var startedAt = DateTimeOffset.UtcNow;
        var report = await server.RecoverPendingResultsAsync(cancellation.Token);
        True(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(2),
            "refresh while loading must return immediately");
        Equal(1, report.RemainingCount, "loading command remains armed for replay");
        True(report.GameConnected && !report.ReplayRequested && server.IsConnected,
            "loading refresh must retain the healthy pipe");

        await writer.WriteLineAsync("STATUS\tready\tok");
        var replay = await reader.ReadLineAsync(cancellation.Token);
        True(replay?.StartsWith(
            "COMMAND\tloading-refresh-command\tloading-refresh-donation\tadd_radiation",
            StringComparison.Ordinal) == true,
            "armed command replays when the same game becomes ready");
        await writer.WriteLineAsync("RESULT\tloading-refresh-command\texecuted\trecovered_after_load");
        Equal(GameResultStatus.Executed,
            (await result.Task.WaitAsync(cancellation.Token)).Status,
            "loading refresh result status");
    }

    private static async Task TestIpcGameFirstAsync()
    {
        var pipeName = CreateTestPipeName();
        await using var server = new NamedPipeGameServer("1.0.0", pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connectTask = firstClient.ConnectAsync(cancellation.Token);
        await Task.Delay(100, cancellation.Token);
        server.Start();
        await connectTask;

        var loadingResult = new TaskCompletionSource<GameExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restartedResult = new TaskCompletionSource<GameExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ResultReceived += value =>
        {
            if (value.CommandId == "loading") loadingResult.TrySetResult(value);
            if (value.CommandId == "restarted") restartedResult.TrySetResult(value);
        };

        using (firstClient)
        using (var firstReader = new StreamReader(
                   firstClient, new UTF8Encoding(false), false, 4096, true))
        using (var firstWriter = new StreamWriter(
                   firstClient, new UTF8Encoding(false), 4096, true)
               { NewLine = "\n", AutoFlush = true })
        {
            await firstWriter.WriteLineAsync("HELLO\t1\t1.0.0\t4321");
            True((await firstReader.ReadLineAsync(cancellation.Token))?.StartsWith(
                "WELCOME\t1\t", StringComparison.Ordinal) == true, "first welcome");
            await firstWriter.WriteLineAsync(
                "CAPABILITIES\tunknown\ttest\tnamed_pipe%3Bcorrelated_ping\tdrop_active_weapon");
            await firstWriter.WriteLineAsync("STATUS\tready\tok");
            await WaitUntilAsync(() => server.IsGameReady, cancellation.Token);
            var loadingCommand = new GameCommand
            {
                CommandId = "loading",
                DonationId = "don-loading",
                ActionId = "drop_active_weapon",
                AmountMinor = 200,
                Currency = "RUB",
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["wait_seconds"] = "30" }
            };
            True((await SendCommandWithReadinessProbeAsync(
                server, loadingCommand, firstReader, firstWriter, cancellation.Token)).StartsWith(
                "COMMAND\tloading\t", StringComparison.Ordinal) == true, "first command delivered");
        }

        await Task.Delay(1500, cancellation.Token);
        True(!loadingResult.Task.IsCompleted,
            "a level-load disconnect keeps an unconfirmed command pending");

        using (var resumedClient = await ConnectPipeAsync(pipeName))
        using (var resumedReader = new StreamReader(
                   resumedClient, new UTF8Encoding(false), false, 4096, true))
        using (var resumedWriter = new StreamWriter(
                   resumedClient, new UTF8Encoding(false), 4096, true)
               { NewLine = "\n", AutoFlush = true })
        {
            await resumedWriter.WriteLineAsync("HELLO\t1\t1.0.0\t4321");
            True((await resumedReader.ReadLineAsync(cancellation.Token))?.StartsWith(
                "WELCOME\t1\t", StringComparison.Ordinal) == true, "resume welcome");
            await resumedWriter.WriteLineAsync(
                "CAPABILITIES\tunknown\ttest\tnamed_pipe%3Bcorrelated_ping\tdrop_active_weapon");
            await resumedWriter.WriteLineAsync("STATUS\tready\tok");
            True((await resumedReader.ReadLineAsync(cancellation.Token))?.StartsWith(
                "COMMAND\tloading\tdon-loading\tdrop_active_weapon", StringComparison.Ordinal) == true,
                "same game process receives the exact pending command again");
            await resumedWriter.WriteLineAsync("RESULT\tloading\texecuted\tweapon_dropped");
            Equal(GameResultStatus.Executed,
                (await loadingResult.Task.WaitAsync(cancellation.Token)).Status,
                "replayed command completes normally");

            var restartedCommand = new GameCommand
            {
                CommandId = "restarted",
                DonationId = "don-restarted",
                ActionId = "drop_active_weapon",
                AmountMinor = 200,
                Currency = "RUB",
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["wait_seconds"] = "30" }
            };
            True((await SendCommandWithReadinessProbeAsync(
                server, restartedCommand, resumedReader, resumedWriter, cancellation.Token)).StartsWith(
                "COMMAND\trestarted\t", StringComparison.Ordinal) == true,
                "second command delivered before process restart");
        }

        await Task.Delay(1500, cancellation.Token);
        using (var restartedClient = await ConnectPipeAsync(pipeName))
        using (var restartedReader = new StreamReader(
                   restartedClient, new UTF8Encoding(false), false, 4096, true))
        using (var restartedWriter = new StreamWriter(
                   restartedClient, new UTF8Encoding(false), 4096, true)
               { NewLine = "\n", AutoFlush = true })
        {
            await restartedWriter.WriteLineAsync("HELLO\t1\t1.0.0\t9876");
            True((await restartedReader.ReadLineAsync(cancellation.Token))?.StartsWith(
                "WELCOME\t1\t", StringComparison.Ordinal) == true, "restart welcome");
            Equal(GameResultStatus.Uncertain,
                (await restartedResult.Task.WaitAsync(cancellation.Token)).Status,
                "a genuinely new game process is not replayed automatically");
            await restartedWriter.WriteLineAsync(
                "CAPABILITIES\tunknown\ttest\tnamed_pipe%3Bcorrelated_ping\tdrop_active_weapon");
            await restartedWriter.WriteLineAsync("STATUS\tready\tok");
            await Task.Delay(250, cancellation.Token);
            True(server.IsGameReady,
                "new process remains connected after the pending command is marked uncertain");
        }
    }

    private static async Task TestDeferredRetryAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T00:00:00Z");
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history, () => now) { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy { Mode = QueueMode.WaitForDuration, WaitMinutes = 1 };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        var actions = DefaultActionCatalog.Create();
        foreach (var action in actions) action.CooldownSeconds = 0;
        var deferredDonation = Donation(1m);
        var followingDonation = Donation(2m);
        await dispatcher.EnqueueAsync(deferredDonation, actions, policy);
        await dispatcher.EnqueueAsync(followingDonation, actions, policy);
        await dispatcher.DrainAsync(transport, policy);
        var first = transport.Commands.Single();
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = first.CommandId, Status = GameResultStatus.Deferred, Reason = "dialog" });
        Equal(2, dispatcher.Count, "deferred event remains queued without blocking its follower");
        var deferredHistory = history.Entries.Last();
        Equal(DispatchState.Queued, deferredHistory.State, "deferred is presented as queued");
        Equal(first.CommandId, deferredHistory.CommandId, "deferred updates the same history row");
        now = now.AddSeconds(6);
        await dispatcher.DrainAsync(transport, policy);
        Equal(followingDonation.Id, transport.Commands[1].DonationId,
            "event behind a deferred item executes first");
        await dispatcher.CompleteAsync(new GameExecutionResult
            { CommandId = transport.Commands[1].CommandId, Status = GameResultStatus.Executed });
        await dispatcher.DrainAsync(transport, policy);
        Equal(3, transport.Commands.Count, "deferred event retries after its follower");
        Equal(first.CommandId, transport.Commands[2].CommandId,
            "safe deferred retry keeps one command and one history row");
        await dispatcher.CompleteAsync(new GameExecutionResult
            { CommandId = first.CommandId, Status = GameResultStatus.Executed });
        Equal(0, dispatcher.Count, "successful retry releases the queue");
        True(history.Entries.All(item => item.State != DispatchState.Expired &&
                item.State != DispatchState.Deferred),
            "retry neither expires nor exposes a deferred terminal status");

        var skipDispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => now) { GlobalCooldown = TimeSpan.Zero };
        var skipTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        var skipPolicy = new QueuePolicy { Mode = QueueMode.Skip };
        await skipDispatcher.EnqueueAsync(Donation(1m), actions, skipPolicy);
        await skipDispatcher.DrainAsync(skipTransport, skipPolicy);
        await skipDispatcher.CompleteAsync(new GameExecutionResult { CommandId = skipTransport.Commands.Single().CommandId, Status = GameResultStatus.Deferred, Reason = "busy" });
        Equal(1, skipDispatcher.Count, "legacy skip mode is ignored for deferred effects");
    }

    private static async Task TestLegacyPendingRestoreAsync()
    {
        var directory = CreateTestDirectoryPath();
        var paths = new AppPaths(directory);
        try
        {
            var now = DateTimeOffset.Parse("2026-09-04T00:30:00Z");
            var donation = Donation(1m);
            var action = DefaultActionCatalog.Create()[0];
            action.CooldownSeconds = 0;
            var trigger = action.Triggers.Single();
            var command = new GameCommand
            {
                DonationId = donation.Id,
                ActionId = action.HandlerId,
                AmountMinor = donation.AmountMinor,
                Currency = donation.Currency,
                Parameters = new Dictionary<string, string>(action.EffectiveParameters(),
                    StringComparer.OrdinalIgnoreCase),
                CreatedAt = now.AddMinutes(-10)
            };
            var processed = new InMemoryProcessedDonationStore();
            await processed.RecordAsync(new ProcessedDonationRecord
            {
                DonationId = donation.Id,
                CommandId = command.CommandId,
                State = DispatchState.Queued,
                RecordedAt = now.AddMinutes(-10)
            });
            var pendingStore = new JsonPendingDispatchStore(paths);
            await pendingStore.SaveAsync(
            [
                new PendingDispatchSnapshot
                {
                    Donation = donation,
                    Action = action,
                    Trigger = trigger,
                    Command = command,
                    EnqueuedAt = now.AddMinutes(-10),
                    ExpiresAt = now.AddMinutes(-9),
                    QueueMode = QueueMode.Skip,
                    // Emulates a stop after a Deferred result was durably
                    // returned to QUEUED but before the snapshot flag flipped.
                    InFlight = true
                }
            ]);

            var dispatcher = new DonationDispatcher(processed, new InMemoryHistorySink(),
                () => now, pendingStore: pendingStore)
            { GlobalCooldown = TimeSpan.Zero };
            Equal(1, await dispatcher.RestoreAsync(),
                "expired legacy snapshot and interrupted deferred transition are restored");
            var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
            await dispatcher.DrainAsync(transport, new QueuePolicy());
            Equal(command.CommandId, transport.Commands.Single().CommandId,
                "restored legacy event executes instead of expiring");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestQueuedRecoveryAsync()
    {
        var directory = CreateTestDirectoryPath();
        var paths = new AppPaths(directory);
        try
        {
            var now = DateTimeOffset.Parse("2026-09-04T01:00:00Z");
            var actions = DefaultActionCatalog.Create();
            foreach (var action in actions) action.CooldownSeconds = 0;
            var firstDonation = Donation(1m);
            var secondDonation = Donation(2m);
            var firstDispatcher = new DonationDispatcher(
                new JsonLineProcessedDonationStore(paths),
                new JsonLineHistoryStore(paths),
                () => now,
                pendingStore: new JsonPendingDispatchStore(paths));
            await firstDispatcher.EnqueueAsync(firstDonation, actions, new QueuePolicy());
            await firstDispatcher.EnqueueAsync(secondDonation, actions, new QueuePolicy());

            var restoredHistory = new InMemoryHistorySink();
            var restoredDispatcher = new DonationDispatcher(
                new JsonLineProcessedDonationStore(paths), restoredHistory, () => now,
                pendingStore: new JsonPendingDispatchStore(paths))
            { GlobalCooldown = TimeSpan.Zero };
            Equal(2, await restoredDispatcher.RestoreAsync(),
                "executable queue snapshots survive an application restart");
            Equal(DispatchState.Duplicate,
                await restoredDispatcher.EnqueueAsync(firstDonation, actions, new QueuePolicy()),
                "a restored donation cannot be enqueued twice");
            var firstTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
            await restoredDispatcher.DrainAsync(firstTransport, new QueuePolicy());
            Equal(1, firstTransport.Commands.Count, "only the queue head may be in flight");

            var finalHistory = new InMemoryHistorySink();
            var finalDispatcher = new DonationDispatcher(
                new JsonLineProcessedDonationStore(paths), finalHistory, () => now,
                pendingStore: new JsonPendingDispatchStore(paths))
            { GlobalCooldown = TimeSpan.Zero };
            Equal(1, await finalDispatcher.RestoreAsync(),
                "a restart keeps later queued work but never replays the in-flight command");
            True(finalHistory.Entries.Any(entry => entry.DonationId == firstDonation.Id &&
                    entry.State == DispatchState.Uncertain),
                "the interrupted in-flight command is marked uncertain explicitly");
            var finalTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
            await finalDispatcher.DrainAsync(finalTransport, new QueuePolicy());
            Equal(secondDonation.Id, finalTransport.Commands.Single().DonationId,
                "the surviving queue preserves original order without replaying the head");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestQueueCancellationAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T01:30:00Z");
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history, () => now) { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };
        var actions = DefaultActionCatalog.Create();
        var thirdStaticAction = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "change_health")
            .CloneAsNewInstance();
        thirdStaticAction.Triggers = [new TriggerRule { Amount = 3m, Currency = "RUB" }];
        actions[2] = thirdStaticAction;
        foreach (var action in actions) action.CooldownSeconds = 0;

        var first = Donation(1m);
        var second = Donation(2m);
        var third = Donation(3m);
        await dispatcher.EnqueueAsync(first, actions, policy);
        await dispatcher.EnqueueAsync(second, actions, policy);
        await dispatcher.EnqueueAsync(third, actions, policy);
        Equal(3, dispatcher.Count, "three events queued");

        var secondQueued = history.Entries.Single(entry => entry.DonationId == second.Id && entry.State == DispatchState.Queued);
        var cancelled = await dispatcher.CancelQueuedAsync(secondQueued);
        True(cancelled is not null, "selected queued event cancelled");
        Equal(2, dispatcher.Count, "only one event removed");
        True(!dispatcher.IsQueued(second.Id), "cancelled donation no longer queued");
        Equal(DispatchState.Cancelled, (await processed.FindAsync(second.Id))?.State, "cancelled state persisted in ledger");
        Equal(secondQueued.CommandId, cancelled!.CommandId, "cancel history keeps command id");
        Equal(secondQueued.ActionId, cancelled.ActionId, "cancel history keeps action id");
        True(cancelled.CanRetryManually, "cancelled event can only be retried manually");
        True(cancelled.Detail.Contains("Удалено из очереди", StringComparison.Ordinal), "cancel reason visible in history");
        Null(await dispatcher.CancelQueuedAsync("missing-donation"), "unknown cancellation is a no-op");

        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.DrainAsync(transport, policy);
        Equal(first.Id, transport.Commands.Single().DonationId, "first event keeps FIFO position");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = transport.Commands[0].CommandId, Status = GameResultStatus.Executed });
        await dispatcher.DrainAsync(transport, policy);
        Equal(third.Id, transport.Commands[1].DonationId, "third event follows first after middle removal");
        True(transport.Commands.All(command => command.DonationId != second.Id), "cancelled event never reaches IPC");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = transport.Commands[1].CommandId, Status = GameResultStatus.Executed });

        var restored = new DonationDispatcher(processed, new InMemoryHistorySink(), () => now);
        Equal(DispatchState.Duplicate, await restored.EnqueueAsync(second, actions, policy), "cancelled donation cannot auto-recover");

        var sent = Donation(1m);
        await dispatcher.EnqueueAsync(sent, actions, policy);
        await dispatcher.DrainAsync(transport, policy);
        var sentCommand = transport.Commands[^1];
        Equal(sent.Id, sentCommand.DonationId, "sent test command dispatched");
        Null(await dispatcher.CancelQueuedAsync(sent.Id), "sent event cannot be cancelled");
        Equal(DispatchState.Sent, (await processed.FindAsync(sent.Id))?.State, "failed cancellation leaves sent ledger intact");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = sentCommand.CommandId, Status = GameResultStatus.Executed });

        var deferred = Donation(2m);
        await dispatcher.EnqueueAsync(deferred, actions, policy);
        await dispatcher.DrainAsync(transport, policy);
        var deferredCommand = transport.Commands[^1];
        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = deferredCommand.CommandId,
            Status = GameResultStatus.Deferred,
            Reason = "dialog"
        });
        True(dispatcher.IsQueued(deferred.Id), "deferred event returned to queue");
        True(await dispatcher.CancelQueuedAsync(deferred.Id) is not null, "deferred retry can be removed");
        Equal(DispatchState.Cancelled, (await processed.FindAsync(deferred.Id))?.State, "deferred removal is terminal");

        var deferredDuringClear = Donation(2m);
        await dispatcher.EnqueueAsync(deferredDuringClear, actions, policy);
        await dispatcher.DrainAsync(transport, policy);
        var inFlightCommand = transport.Commands[^1];
        var clearResult = await dispatcher.CancelAllQueuedAsync();
        Equal(0, clearResult.CancelledEntries.Count, "clear does not claim an in-flight effect was cancelled");
        Equal(1, clearResult.SuppressedInFlightRetries, "clear suppresses a deferred retry for the in-flight effect");
        var clearedDeferredHistory = await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = inFlightCommand.CommandId,
            Status = GameResultStatus.Deferred,
            Reason = "dialog"
        });
        Equal(DispatchState.Cancelled, clearedDeferredHistory?.State, "cleared in-flight deferred effect becomes cancelled");
        True(!dispatcher.IsQueued(deferredDuringClear.Id), "cleared in-flight deferred effect is not requeued");
        Equal(0, dispatcher.Count, "queue remains empty after the deferred result arrives");
        Equal(DispatchState.Cancelled, (await processed.FindAsync(deferredDuringClear.Id))?.State,
            "suppressed deferred retry is terminal in the ledger");
    }

    private static async Task TestQueueCancellationPersistenceAsync()
    {
        var directory = CreateTestDirectoryPath();
        var paths = new AppPaths(directory);
        try
        {
            var donation = Donation(1m);
            donation.Message = "Persistent retry message";
            var processed = new JsonLineProcessedDonationStore(paths);
            var history = new JsonLineHistoryStore(paths);
            var firstDispatcher = new DonationDispatcher(processed, history);
            await firstDispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy());
            var queuedEntry = (await history.LoadRecentAsync()).Single(entry =>
                entry.DonationId == donation.Id && entry.State == DispatchState.Queued);
            Equal("Persistent retry message", queuedEntry.DonationMessage,
                "history persistence lost the hidden donation message");

            var reloadedProcessed = new JsonLineProcessedDonationStore(paths);
            var restoredDispatcher = new DonationDispatcher(reloadedProcessed, history);
            True(!restoredDispatcher.IsQueued(donation.Id), "restored dispatcher starts without an executable in-memory command");
            var cancelled = await restoredDispatcher.CancelQueuedAsync(queuedEntry);
            True(cancelled is not null, "persisted queue event can be cancelled after restart");
            Equal(queuedEntry.CommandId, cancelled!.CommandId, "restored cancellation keeps the persisted command id");
            Equal(queuedEntry.ActionId, cancelled.ActionId, "restored cancellation keeps the action instance id");
            Equal(DispatchState.Cancelled, (await reloadedProcessed.FindAsync(donation.Id))?.State, "cancelled ledger reloads");
            var reloadedHistory = await new JsonLineHistoryStore(paths).LoadRecentAsync();
            True(reloadedHistory.Any(entry => entry.DonationId == donation.Id &&
                entry.State == DispatchState.Cancelled &&
                entry.Detail.Contains("Удалено из очереди", StringComparison.Ordinal)), "cancel history reloads");

            var restored = new DonationDispatcher(reloadedProcessed, new InMemoryHistorySink());
            Equal(DispatchState.Duplicate,
                await restored.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy()),
                "cancelled persistent event does not recover after restart");
            Null(await restored.CancelQueuedAsync(queuedEntry), "cancelled ledger rejects a stale queued history row");

            var sentDonation = Donation(1m);
            var liveHistory = new InMemoryHistorySink();
            var liveDispatcher = new DonationDispatcher(reloadedProcessed, liveHistory) { GlobalCooldown = TimeSpan.Zero };
            var actions = DefaultActionCatalog.Create();
            foreach (var action in actions) action.CooldownSeconds = 0;
            await liveDispatcher.EnqueueAsync(sentDonation, actions, new QueuePolicy());
            var sentQueuedEntry = liveHistory.Entries.Single(entry =>
                entry.DonationId == sentDonation.Id && entry.State == DispatchState.Queued);
            var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
            await liveDispatcher.DrainAsync(transport, new QueuePolicy());

            var afterSendRestart = new DonationDispatcher(reloadedProcessed, new InMemoryHistorySink());
            Null(await afterSendRestart.CancelQueuedAsync(sentQueuedEntry), "sent ledger rejects a stale queued history row");
            Equal(DispatchState.Sent, (await reloadedProcessed.FindAsync(sentDonation.Id))?.State, "failed restored cancellation leaves sent state intact");

            await liveDispatcher.CompleteAsync(new GameExecutionResult
            {
                CommandId = transport.Commands.Single(command => command.DonationId == sentDonation.Id).CommandId,
                Status = GameResultStatus.Executed
            });
            var afterCompletionRestart = new DonationDispatcher(reloadedProcessed, new InMemoryHistorySink());
            Null(await afterCompletionRestart.CancelQueuedAsync(sentQueuedEntry), "terminal ledger rejects a stale queued history row");
            Equal(DispatchState.Executed, (await reloadedProcessed.FindAsync(sentDonation.Id))?.State, "failed restored cancellation leaves terminal state intact");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestInterruptedSendHistoryRecoveryAsync()
    {
        var directory = CreateTestDirectoryPath();
        try
        {
            var historyStore = new JsonLineHistoryStore(new AppPaths(directory));
            var directAction = DefaultActionCatalog.Create()
                .Single(action => action.HandlerId == "add_radiation");
            var donation = Donation(1m);
            donation.Id = "recovered-donation";
            donation.Username = "Точный донатер";
            var sentRecord = new ProcessedDonationRecord
            {
                DonationId = donation.Id,
                CommandId = "recovered-command",
                State = DispatchState.Sent
            };
            await historyStore.AppendAsync(new EventHistoryEntry
            {
                DonationId = donation.Id,
                CommandId = "another-command",
                ActionId = directAction.Id,
                HandlerId = directAction.HandlerId,
                ActionName = "Неверный снимок",
                State = DispatchState.Sent
            });
            await historyStore.AppendAsync(new EventHistoryEntry
            {
                DonationId = donation.Id,
                CommandId = sentRecord.CommandId,
                ActionId = directAction.Id,
                HandlerId = directAction.HandlerId,
                ActionName = directAction.DisplayName,
                State = DispatchState.Queued
            });
            await historyStore.AppendAsync(new EventHistoryEntry
            {
                DonationId = "another-donation",
                CommandId = sentRecord.CommandId,
                ActionId = directAction.Id,
                HandlerId = directAction.HandlerId,
                ActionName = "Тоже неверный снимок",
                State = DispatchState.Sent
            });

            var snapshot = await historyStore.FindCommandSnapshotAsync(donation.Id, sentRecord.CommandId);
            True(snapshot is not null, "exact donation and command history snapshot is found");
            Equal(directAction.DisplayName, snapshot!.ActionName, "snapshot lookup does not mix donation or command ids");

            var timestamp = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
            var recovered = HistoryRecovery.CreateInterruptedSendEntry(
                donation, sentRecord, snapshot, directAction, timestamp);
            Equal(directAction.Id, recovered.ActionId, "recovered uncertain history keeps action instance id");
            Equal(directAction.HandlerId, recovered.HandlerId, "recovered uncertain history keeps handler id");
            Equal(directAction.DisplayName, recovered.ActionName, "recovered uncertain history keeps display name");
            Equal(donation.Username, recovered.Donor, "recovered uncertain history keeps current donation identity");
            True(recovered.CanRetryManually, "ordinary exact action snapshot permits an explicit manual retry");

            var randomAll = DefaultActionCatalog.CreatePresets()
                .Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
                .CloneAsNewInstance();
            var randomSnapshot = new EventHistoryEntry
            {
                DonationId = donation.Id,
                CommandId = sentRecord.CommandId,
                ActionId = randomAll.Id,
                HandlerId = "change_health",
                ActionName = "Изменить здоровье",
                State = DispatchState.Sent
            };
            var recoveredRandom = HistoryRecovery.CreateInterruptedSendEntry(
                donation, sentRecord, randomSnapshot, randomAll, timestamp);
            Equal(randomAll.Id, recoveredRandom.ActionId, "random-all recovery keeps its source instance id");
            Equal("change_health", recoveredRandom.HandlerId, "random-all recovery identifies the frozen concrete handler");
            True(!recoveredRandom.CanRetryManually, "random-all uncertainty cannot reroll through manual retry");

            var withoutSnapshot = HistoryRecovery.CreateInterruptedSendEntry(
                donation, sentRecord, null, null, timestamp);
            Equal(string.Empty, withoutSnapshot.ActionId, "missing snapshot does not invent an action id");
            True(!withoutSnapshot.CanRetryManually, "missing snapshot fails closed for manual retry");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task TestInvalidActionRejectedAsync()
    {
        var action = DefaultActionCatalog.Create().Single(item => item.HandlerId == "add_radiation");
        action.Parameters["percent"] = "not-a-number";
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history) { GlobalCooldown = TimeSpan.Zero };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        var state = await dispatcher.EnqueueActionAsync(Donation(1m), action, new QueuePolicy());
        Equal(DispatchState.Rejected, state, "invalid action rejected");
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(0, transport.Commands.Count, "invalid parameters never reach IPC");
        True(history.Entries.Any(item => item.State == DispatchState.Rejected && item.CanRetryManually), "invalid action is terminal and visible");
    }

    private static async Task TestNamedCompanionDonorNameAsync()
    {
        var action = new ActionDefinition
        {
            Id = "named-companion-test",
            HandlerId = "spawn_named_companion",
            DisplayName = "Напарник донатчика",
            CooldownSeconds = 0
        };
        var donation = Donation(1m);
        donation.Username = "  Локальный\tтест  ";
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), new InMemoryHistorySink())
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        Equal(DispatchState.Queued,
            await dispatcher.EnqueueActionAsync(donation, action, new QueuePolicy()),
            "named companion is queued");
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal("Локальный тест", transport.Commands.Single().Parameters["donor_name"],
            "donor name is normalized and sent to Lua");

        var nameAction = new ActionDefinition
        {
            Id = "change-player-name-test",
            HandlerId = "change_player_name",
            DisplayName = "Имя героя",
            CooldownSeconds = 0
        };
        var nameDonation = Donation(2m);
        nameDonation.Username = "  Другой\nдонатчик  ";
        var nameDispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), new InMemoryHistorySink())
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var nameTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        Equal(DispatchState.Queued,
            await nameDispatcher.EnqueueActionAsync(nameDonation, nameAction, new QueuePolicy()),
            "player-name change is queued");
        await nameDispatcher.DrainAsync(nameTransport, new QueuePolicy());
        Equal("Другой донатчик", nameTransport.Commands.Single().Parameters["donor_name"],
            "player-name change receives the normalized donor name");

        var renameNpcAction = new ActionDefinition
        {
            Id = "rename-nearest-npc-test",
            HandlerId = "hardcoon.adb.rename_nearest_npc",
            DisplayName = "Переименовать ближайшего NPC",
            CooldownSeconds = 0
        };
        var renameNpcDonation = Donation(2m);
        renameNpcDonation.Username = "  Важный\nдонатчик  ";
        var renameNpcDispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), new InMemoryHistorySink())
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var renameNpcTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        Equal(DispatchState.Queued,
            await renameNpcDispatcher.EnqueueActionAsync(renameNpcDonation, renameNpcAction, new QueuePolicy()),
            "nearest NPC rename is queued");
        await renameNpcDispatcher.DrainAsync(renameNpcTransport, new QueuePolicy());
        Equal("Важный донатчик", renameNpcTransport.Commands.Single().Parameters["donor_name"],
            "nearest NPC rename receives the normalized donor name");

        var annoyingFanAction = new ActionDefinition
        {
            Id = "annoying-fan-test",
            HandlerId = "hardcoon.adb.annoying_fan",
            DisplayName = "Доставучий фанат",
            CooldownSeconds = 0
        };
        var annoyingFanDonation = Donation(2m);
        annoyingFanDonation.Username = "  Фанат\nдонатера  ";
        annoyingFanDonation.Message = "  Привет\nиз доната 😀  ";
        var annoyingFanHistory = new InMemoryHistorySink();
        var annoyingFanDispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), annoyingFanHistory)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var annoyingFanTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        Equal(DispatchState.Queued,
            await annoyingFanDispatcher.EnqueueActionAsync(
                annoyingFanDonation, annoyingFanAction, new QueuePolicy()),
            "annoying fan is queued");
        await annoyingFanDispatcher.DrainAsync(annoyingFanTransport, new QueuePolicy());
        Equal("Фанат донатера", annoyingFanTransport.Commands.Single().Parameters["donor_name"],
            "annoying fan receives the normalized donor name");
        Equal("Привет из доната 😀",
            annoyingFanTransport.Commands.Single().Parameters["donation_message"],
            "annoying fan receives the normalized donation message");
        Equal("  Привет\nиз доната 😀  ",
            annoyingFanHistory.Entries.Last().DonationMessage,
            "history keeps the original donation message for manual retry");
        True(!transport.Commands.Single().Parameters.ContainsKey("donation_message"),
            "donation text is not exposed to unrelated handlers");

        foreach (var genericAnnouncement in new[] { false, true })
        {
            var pdaAction = new ActionDefinition
            {
                HandlerId = DonationDispatcher.PdaMessageHandlerId,
                DisplayName = "Отправить сообщение в КПК", CooldownSeconds = 0
            };
            var pdaDonation = Donation(2m);
            pdaDonation.Username = "  Ник\nдонатера  ";
            pdaDonation.Message = "  Описание\nдоната 😀  ";
            var pdaHistory = new InMemoryHistorySink();
            var pdaDispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), pdaHistory)
                { GlobalCooldown = TimeSpan.Zero };
            var pdaTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
            Equal(DispatchState.Queued, await pdaDispatcher.EnqueueActionAsync(pdaDonation,
                pdaAction, new QueuePolicy(), executionOptions: new EffectExecutionOptions
                { ShowPdaEffectMessage = genericAnnouncement }), "PDA message is queued");
            await pdaDispatcher.DrainAsync(pdaTransport, new QueuePolicy());
            var pdaCommand = pdaTransport.Commands.Single();
            Equal("Ник донатера", pdaCommand.Parameters["donor_name"], "PDA receives donor");
            Equal("Описание доната 😀", pdaCommand.Parameters["donation_message"], "PDA receives message");
            True(!pdaCommand.ShowPdaEffectMessage, "PDA template must not produce a duplicate generic notification");
            Equal(pdaDonation.Message, pdaHistory.Entries.Last().DonationMessage, "PDA replay keeps original body");
        }

        var anonymousDonation = Donation(2m);
        anonymousDonation.Username = " \t ";
        var anonymousHistory = new InMemoryHistorySink();
        var anonymousDispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), anonymousHistory)
        {
            GlobalCooldown = TimeSpan.Zero,
            AnonymousDonorName = "Таинственный зритель"
        };
        var anonymousTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        Equal(DispatchState.Queued,
            await anonymousDispatcher.EnqueueActionAsync(
                anonymousDonation, annoyingFanAction, new QueuePolicy()),
            "anonymous donor-named effect is queued");
        await anonymousDispatcher.DrainAsync(anonymousTransport, new QueuePolicy());
        Equal("Таинственный зритель",
            anonymousTransport.Commands.Single().Parameters["donor_name"],
            "configured anonymous donor name is sent to Lua");
        Equal("Таинственный зритель", anonymousTransport.Commands.Single().DonorName,
            "configured anonymous donor name is used in game effect messages");
        Equal("Таинственный зритель", anonymousHistory.Entries.Last().Donor,
            "configured anonymous donor name is recorded in history");

        foreach (var handlerId in new[] { "spawn_npcs", "spawn_random_npcs", "hardcoon.adb.donor_thief" })
        {
            var npcAction = new ActionDefinition
            {
                Id = handlerId + "-donor-name-test",
                HandlerId = handlerId,
                DisplayName = handlerId,
                CooldownSeconds = 0,
                CatalogRevision = new CatalogRevision("donor-name-test"),
                SpawnGroups = handlerId == "spawn_npcs"
                    ? [new SpawnGroupEntry { VariantId = "npc_test", Strength = "any", Count = 1 }]
                    : []
            };
            var npcDonation = Donation(3m);
            npcDonation.Username = "  Имя\tдля NPC  ";
            var npcDispatcher = new DonationDispatcher(
                new InMemoryProcessedDonationStore(), new InMemoryHistorySink())
            {
                GlobalCooldown = TimeSpan.Zero
            };
            var npcTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
            Equal(DispatchState.Queued,
                await npcDispatcher.EnqueueActionAsync(npcDonation, npcAction, new QueuePolicy()),
                $"{handlerId} is queued");
            await npcDispatcher.DrainAsync(npcTransport, new QueuePolicy());
            Equal("Имя для NPC", npcTransport.Commands.Single().Parameters["donor_name"],
                $"{handlerId} receives the normalized donor name");
            Equal("Имя для NPC", npcTransport.Commands.Single().DonorName,
                $"{handlerId} also keeps the donor in command context");
        }
    }

    private static Task TestOrderedPresetRangesAsync()
    {
        var presets = DefaultActionCatalog.CreatePresets();
        foreach (var handlerId in new[] { "spawn_random_hostile_squad", "spawn_random_mutant_pack" })
        {
            var squad = presets.Single(action => action.HandlerId == handlerId).Clone();
            var selector = squad.ParameterDefinitions.Single(definition => definition.Key == "squad_sections");
            Equal(ParameterKind.MultiChoice, selector.Kind, $"{handlerId} uses a live multi-selection");
            SequenceEqual(new[] { "squad_sections", "minimum_count", "maximum_count", "radius_meters", "force_hostile" },
                squad.ParameterDefinitions.Select(definition => definition.Key),
                $"{handlerId} keeps real squad composition and exposes only placement/attitude controls");
            True(selector.CatalogKind is CatalogEntryKind.StalkerSquad or CatalogEntryKind.MutantSquad,
                $"{handlerId} selector is bound to a squad catalog kind");
            True(squad.Validate().Any(error => error.Contains("каталог", StringComparison.OrdinalIgnoreCase)),
                $"{handlerId} remains unavailable before catalog synchronization");
        }

        var teleport = presets.Single(action => action.HandlerId == "teleport_random").Clone();
        teleport.Parameters["minimum_meters"] = "100";
        teleport.Parameters["maximum_meters"] = "25";
        True(teleport.Validate().Any(error =>
                error.Contains("минимум", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("максимум", StringComparison.OrdinalIgnoreCase)),
            "teleport rejects reversed distance range before IPC");
        return Task.CompletedTask;
    }

    private static async Task TestRandomPresetAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T02:00:00Z");
        var presets = DefaultActionCatalog.CreatePresets();
        var random = presets.Single(action => action.HandlerId == DonationDispatcher.RandomActiveHandlerId).CloneAsNewInstance();
        random.Triggers = [new TriggerRule { Amount = 77m, Currency = "RUB" }];
        random.CooldownSeconds = 30;
        var radiation = presets.Single(action => action.HandlerId == "add_radiation").CloneAsNewInstance();
        radiation.Triggers.Clear();
        radiation.CooldownSeconds = 0;
        var randomAll = presets.Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId).CloneAsNewInstance();
        randomAll.Triggers.Clear();
        var actions = new List<ActionDefinition> { random, randomAll, radiation };
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(),
            history,
            () => now,
            _ => 0)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };

        Equal(DispatchState.Queued, await dispatcher.EnqueueAsync(Donation(77m), actions, new QueuePolicy()), "random event queued");
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        var command = transport.Commands.Single();
        Equal("add_radiation", command.ActionId, "meta-action is never sent to Lua");
        Equal("25", command.Parameters["percent"], "selected action parameters preserved");
        True(history.Entries.Any(entry => entry.ActionName == radiation.DisplayName), "history names the selected action");

        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = command.CommandId,
            Status = GameResultStatus.Executed
        });
        now = now.AddSeconds(5);
        Equal(DispatchState.Queued, await dispatcher.EnqueueAsync(Donation(77m), actions, new QueuePolicy()), "second random event queued");
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(1, transport.Commands.Count, "random preset respects its own pause");
        now = now.AddSeconds(26);
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(2, transport.Commands.Count, "random preset runs after its own pause");

        var emptyHistory = new InMemoryHistorySink();
        var emptyDispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), emptyHistory, () => now, _ => 0);
        var rejected = await emptyDispatcher.EnqueueAsync(Donation(77m), [random], new QueuePolicy());
        Equal(DispatchState.Rejected, rejected, "random without an eligible target is rejected safely");
        True(emptyHistory.Entries.Any(entry => entry.State == DispatchState.Rejected), "empty random result is visible in history");
    }

    private static async Task TestRandomAllPresetsAsync()
    {
        var presets = DefaultActionCatalog.CreatePresets();
        DynamicCatalogBinding.Apply(LoadCatalogFixture("catalog-vanilla.json"), presets);
        ActionDefinition Target(string handler) { var a = presets.Single(p => p.HandlerId == handler).CloneAsNewInstance(); a.CooldownSeconds = 0; return a; }
        var random = Target(ActiveTemplateSelection.HandlerId);
        var teleport = Target("teleport_random");
        teleport.Parameters["minimum_meters"] = "71"; teleport.Parameters["maximum_meters"] = "83";
        var duplicateHandler = Target("teleport_random");
        duplicateHandler.Parameters["minimum_meters"] = "500"; duplicateHandler.Parameters["maximum_meters"] = "700";
        var health = Target("change_health"); health.Parameters["percent"] = "17";
        var meta = Target(DonationDispatcher.RandomActiveHandlerId);
        random.Parameters[ActiveTemplateSelection.ParameterKey] = teleport.Id;
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };
        var calls = 0;
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history, randomIndex: n => { calls++; return n - 1; }) { GlobalCooldown = TimeSpan.Zero };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        Equal(DispatchState.Queued, await dispatcher.EnqueueActionAsync(Donation(88), random, policy,
            availableActions: [random, meta, health, duplicateHandler, teleport], availablePresets: presets), "selected instance queues");
        teleport.Parameters["minimum_meters"] = "72";
        await dispatcher.DrainAsync(transport, policy);
        var command = transport.Commands.Single();
        Equal("teleport_random", command.ActionId, "concrete selected handler");
        Equal("71", command.Parameters["minimum_meters"], "selected settings frozen when queued");
        Equal("83", command.Parameters["maximum_meters"], "configured range preserved");
        Equal(0, calls, "single selected instance needs no parameter generation");
        Equal(random.Id, history.Entries.First(e => e.State == DispatchState.Queued).ActionId, "history retains source random instance");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = command.CommandId, Status = GameResultStatus.Deferred, Reason = "loading" });
        random.Parameters[ActiveTemplateSelection.ParameterKey] = health.Id;
        await dispatcher.DrainAsync(transport, policy);
        Equal("71", transport.Commands.Last().Parameters["minimum_meters"], "retry keeps frozen chosen settings");
        var result = await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = command.CommandId, Status = GameResultStatus.Uncertain, Reason = "ack_lost" });
        True(result is { CanRetryManually: false }, "uncertain delivery must not reroll");

        async Task<GameCommand?> Run(string ids, params ActionDefinition[] actions)
        {
            random.Parameters[ActiveTemplateSelection.ParameterKey] = ids;
            var d = new DonationDispatcher(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), randomIndex: n => n - 1) { GlobalCooldown = TimeSpan.Zero };
            var t = new FakeTransport { IsConnected = true, IsGameReady = true };
            var state = await d.EnqueueActionAsync(Donation(88), random, policy, availableActions: actions, availablePresets: presets);
            await d.DrainAsync(t, policy);
            Equal(t.Commands.Count > 0 ? DispatchState.Queued : DispatchState.Rejected, state, "selection result");
            return t.Commands.SingleOrDefault();
        }
        True(await Run(string.Empty, teleport, health) is null, "empty choice never falls back to all");
        True(await Run("teleport_random", teleport) is null, "handler name is not an instance reference");
        True(await Run(teleport.Id, duplicateHandler, health) is null, "removed instance is not replaced by same handler");
        True(await Run(random.Id + "," + meta.Id, random, meta) is null, "recursive random templates excluded");
        teleport.IsActive = false;
        True(await Run(teleport.Id, teleport) is null, "disabled target ignored");
        teleport.IsActive = true;
        teleport.Parameters["minimum_meters"] = "invalid";
        Equal("17", (await Run(teleport.Id + "," + health.Id, health, teleport))!.Parameters["percent"], "invalid selected variant cannot block other valid choices");
        teleport.Parameters["minimum_meters"] = "71";
        Equal("17", (await Run(teleport.Id + "," + health.Id, teleport, health))!.Parameters["percent"], "selection spans exact instances");
        var items = Target("spawn_items"); items.ItemSpawns.Single().Count = 7;
        var itemCommand = await Run(items.Id, items);
        Equal(ItemBundleCodec.Serialize(items.ItemSpawns), itemCommand!.Parameters["items"], "explicitly selected inventory bundle preserved");
        items.CatalogRevision = null;
        True(await Run(items.Id, items) is null, "unverified catalog blocks selected inventory effect");
        var custom = new ActionDefinition { Id = "configured-custom", HandlerId = "custom.example", DisplayName = "Custom", IsActive = true, CooldownSeconds = 0,
            ParameterDefinitions = [new() { Key = "mode", Kind = ParameterKind.Choice, Options = ["keep"], DefaultValue = "keep" }], Parameters = new() { ["mode"] = "keep" } };
        Equal("keep", (await Run(custom.Id, custom))!.Parameters["mode"], "installed custom templates supported without whitelist");
        random.Parameters[ActiveTemplateSelection.ParameterKey] = teleport.Id + "," + health.Id;
        ActiveTemplateSelection.BindEditor(random, [teleport, health, meta, random]);
        Equal(2, random.ParameterDefinitions.Single().Options.Count, "editor lists configured targets, not meta templates");
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<ActionDefinition>(System.Text.Json.JsonSerializer.Serialize(random))!;
        Equal(teleport.Id + "," + health.Id, reloaded.Parameters[ActiveTemplateSelection.ParameterKey], "selection survives serialization");
        ActiveTemplateSelection.BindEditor(reloaded, [health]);
        True(reloaded.ParameterDefinitions.Single().OptionLabels[teleport.Id].Contains("Недоступный"), "deleted selection is visible for removal");
        Equal(teleport.Id + "," + health.Id, reloaded.Parameters[ActiveTemplateSelection.ParameterKey], "rebinding never silently reselects");

        var clock = DateTimeOffset.UtcNow;
        health.CooldownSeconds = 30;
        var timed = new DonationDispatcher(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => clock, _ => 0)
            { GlobalCooldown = TimeSpan.Zero };
        var timedTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await timed.EnqueueActionAsync(Donation(1), health, policy);
        await timed.DrainAsync(timedTransport, policy);
        await timed.CompleteAsync(new() { CommandId = timedTransport.Commands.Last().CommandId, Status = GameResultStatus.Executed });
        random.Parameters[ActiveTemplateSelection.ParameterKey] = health.Id + "," + teleport.Id;
        await timed.EnqueueActionAsync(Donation(2), random, policy, availableActions: [health, teleport]);
        await timed.DrainAsync(timedTransport, policy);
        Equal("teleport_random", timedTransport.Commands.Last().ActionId, "selected random respects the target's direct-execution cooldown");
        await timed.CompleteAsync(new() { CommandId = timedTransport.Commands.Last().CommandId, Status = GameResultStatus.Executed });
        clock = clock.AddSeconds(31);
        await timed.EnqueueActionAsync(Donation(3), random, policy, availableActions: [health, teleport]);
        await timed.DrainAsync(timedTransport, policy);
        Equal("change_health", timedTransport.Commands.Last().ActionId, "expired target cooldown becomes available again");

        var store = new SelectedRandomQueueStore();
        var ledger = new InMemoryProcessedDonationStore();
        random.Parameters[ActiveTemplateSelection.ParameterKey] = teleport.Id;
        var beforeRestart = new DonationDispatcher(ledger, new InMemoryHistorySink(), pendingStore: store);
        await beforeRestart.EnqueueActionAsync(Donation(4), random, policy, availableActions: [teleport]);
        var pending = (await store.LoadAsync()).Single();
        teleport.Parameters["minimum_meters"] = "321";
        var afterRestart = new DonationDispatcher(ledger, new InMemoryHistorySink(), randomIndex: _ => throw new Exception("re-rolled after restart"), pendingStore: store)
            { GlobalCooldown = TimeSpan.Zero };
        Equal(1, await afterRestart.RestoreAsync(), "selected command recovered after restart");
        var restoredTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await afterRestart.DrainAsync(restoredTransport, policy);
        Equal(pending.Command.CommandId, restoredTransport.Commands.Single().CommandId, "restart retains command identity");
        Equal("71", restoredTransport.Commands.Single().Parameters["minimum_meters"], "restart retains configured queued values");
    }

    private sealed class SelectedRandomQueueStore : IPendingDispatchStore
    {
        private string _json = "[]";
        public Task<IReadOnlyList<PendingDispatchSnapshot>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PendingDispatchSnapshot>>(System.Text.Json.JsonSerializer.Deserialize<List<PendingDispatchSnapshot>>(_json)!);
        public Task SaveAsync(IReadOnlyList<PendingDispatchSnapshot> pending, CancellationToken cancellationToken = default)
        { _json = System.Text.Json.JsonSerializer.Serialize(pending); return Task.CompletedTask; }
    }
    private static async Task<NamedPipeClientStream> ConnectPipeAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(cancellation.Token);
        return client;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task<string> SendCommandWithReadinessProbeAsync(
        NamedPipeGameServer server,
        GameCommand command,
        StreamReader reader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var send = server.SendAsync(command, cancellationToken);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ??
                throw new EndOfStreamException("Pipe closed before readiness probe.");
            if (line == "PING")
            {
                await writer.WriteLineAsync("PONG");
                continue;
            }
            if (!line.StartsWith("PING\t", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected correlated PING, received: {line}");
            }
            await writer.WriteLineAsync("PONG\t" + line.Split('\t')[1]);
            await send.ConfigureAwait(false);
            break;
        }

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ??
                throw new EndOfStreamException("Pipe closed before command.");
            if (line == "PING")
            {
                await writer.WriteLineAsync("PONG");
                continue;
            }
            return line;
        }
    }

    private static string CreateTestPipeName()
        => $"{PipeProtocol.PipeName}.Tests.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private static CatalogSnapshot LoadCatalogFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fileName);
        var fixture = JsonSerializer.Deserialize<CatalogFixtureDocument>(
            File.ReadAllText(path),
            CatalogFixtureJsonOptions) ?? throw new InvalidDataException($"Empty catalog fixture: {fileName}");
        return new CatalogSnapshot(
            new CatalogRevision(fixture.Revision),
            fixture.ContentHash,
            fixture.Entries,
            DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
    }

    private static string CreateTestDirectoryPath() => Path.Combine(
        AppContext.BaseDirectory,
        "test-data",
        Guid.NewGuid().ToString("N"));

    private static string QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == key) return Uri.UnescapeDataString(parts[1]);
        }
        throw new Exception("missing query value " + key);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class PassThroughRateLimiter : IDonationAlertsApiRateLimiter
    {
        public static PassThroughRateLimiter Instance { get; } = new();
        public ValueTask WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingRateLimiter : IDonationAlertsApiRateLimiter
    {
        public int WaitCount { get; private set; }
        public ValueTask WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DonateXCountingRateLimiter : IDonateXApiRateLimiter
    {
        public int WaitCount { get; private set; }
        public ValueTask WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static readonly JsonSerializerOptions CatalogFixtureJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class CatalogFixtureDocument
    {
        public string Revision { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public List<CatalogEntry> Entries { get; set; } = [];
    }

    private static DonationEvent Donation(decimal amount, string currency = "RUB") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Username = "tester",
        Amount = amount,
        Currency = currency,
        CreatedAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow
    };

    private sealed class FakeTransport : IGameCommandTransport
    {
        public bool IsConnected { get; init; }
        public bool IsGameReady { get; init; }
        public bool ThrowOnSend { get; init; }
        public bool NotReadyBeforeSend { get; init; }
        public List<GameCommand> Commands { get; } = [];
        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        {
            if (NotReadyBeforeSend)
                throw new GameNotReadyBeforeSendException("simulated readiness race");
            if (ThrowOnSend) throw new IOException("simulated broken pipe");
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Null(object? value, string message) { if (value is not null) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, actual {actual}");
    }
    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual)) throw new Exception(message);
    }
    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception(message);
    }
    private static async Task ThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new Exception(message);
    }
}
