using System.IO;
using System.Text.Json;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.AppTests;

internal static class FilePresetTests
{
    public static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "file-presets");
        var effectsRoot = Path.Combine(root, "effects");
        var effectsScripts = Path.Combine(effectsRoot, "gamedata", "scripts");
        var gameRoot = Path.Combine(root, "game");
        var gameScripts = Path.Combine(gameRoot, "gamedata", "scripts");
        Directory.CreateDirectory(effectsScripts);
        Directory.CreateDirectory(gameScripts);
        Directory.CreateDirectory(Path.Combine(gameRoot, "bin"));
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "fsgame.ltx"),
            "$game_data$=true|true|$fs_root$|gamedata\\");
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "bin", "AnomalyDX11.exe"),
            "test engine marker");
        {
            var loader = new FilePresetLoader(effectsRoot);
            Assert(loader.Load(gameRoot).Presets.Count == 0, "Empty effects folder showed presets.");
            var shipped = Path.Combine(AppContext.BaseDirectory, "effects", "gamedata", "scripts");
            var shippedFiles = Directory.EnumerateFiles(shipped, "*.script").OrderBy(path => path).ToList();
            Assert(shippedFiles.Count == 70 && shippedFiles.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 70,
                "The app must ship 70 separate effect scripts; archived chimera and psy-dog must not return.");
            var optionalRoot = FindOptionalRoot();
            var optional = new FilePresetLoader(optionalRoot).Load(null);
            Assert(optional.SourcePaths.Count == 66 && optional.Presets.Count == 0 && optional.Warnings.Count == 0,
                "Each additional Lua effect must have a valid self-describing header.");
            Assert(Directory.EnumerateFiles(Path.Combine(optionalRoot, "gamedata", "scripts"), "*.script")
                    .All(path => shippedFiles.Any(shipped => Path.GetFileName(shipped) == Path.GetFileName(path))),
                "An additional effect was omitted from the application package.");
            foreach (var file in shippedFiles)
                File.Copy(file, Path.Combine(effectsScripts, Path.GetFileName(file)));

            var candidates = loader.Load(gameRoot);
            Assert(candidates.SourcePaths.Count == 70 && candidates.Presets.Count == 0 && candidates.Warnings.Count == 0,
                "Uninstalled scripts produced visible presets or invalid headers.");
            var v2Path = Path.Combine(effectsScripts, "anomaly_donation_bridge_effect_hardcoon_adb_mutant_companion_v2.script");
            var v2Text = await File.ReadAllTextAsync(v2Path);
            Assert(v2Text.Contains("\\n\\nУстановка:"), "Installation is not a separate description paragraph.");
            try
            {
                foreach (var invalid in new[] { "\\t", "\\u0000", "\\u001b" })
                {
                    await File.WriteAllTextAsync(v2Path, v2Text.Replace("\\n\\n", invalid));
                    Assert(loader.Load(gameRoot).Warnings.Count == 1, "Multiline support accepted an unsafe control character.");
                }
            }
            finally { await File.WriteAllTextAsync(v2Path, v2Text); }
            var malformed = Path.Combine(effectsScripts,
                "anomaly_donation_bridge_effect_malformed.script");
            await File.WriteAllTextAsync(malformed,
                "--[[ADB_PRESET_V1\n{\"schemaVersion\":1,\"handlerId\":\"author.modpack.malformed\",\"displayName\":\"Bad\",\"category\":\"world\",\"parameterDefinitions\":[{\"key\":\"value\",\"displayName\":\"Value\",\"kind\":\"Choice\",\"options\":null,\"defaultValue\":\"x\"}],\"parameters\":{\"value\":\"x\"}}\nADB_PRESET_END]]\nfunction on_game_start() end\n");
            var malformedLoad = loader.Load(gameRoot);
            Assert(malformedLoad.Presets.Count == 0 && malformedLoad.Warnings.Count == 1,
                "Malformed parameter metadata escaped as a crash or visible preset.");
            File.Delete(malformed);
            var first = Path.GetFileName(shippedFiles[0]);
            File.Copy(Path.Combine(effectsScripts, first), Path.Combine(gameScripts, first));
            Assert(loader.Load(gameRoot).Presets.Count == 1,
                "Installing one script did not produce exactly one preset.");
            await File.AppendAllTextAsync(Path.Combine(gameScripts, first), "\n-- modified\n");
            Assert(loader.Load(gameRoot).Presets.Count == 0,
                "A changed installed script was accepted despite SHA-256 mismatch.");
            File.Delete(Path.Combine(gameScripts, first));

            var manager = new EffectScriptManager(new AppPaths(Path.Combine(root, "data")), loader);
            var before = await manager.InspectAsync(gameRoot);
            Assert(before.CanInstall && before.SourceCount == 70 && before.InstalledCount == 0,
                "Effect installer did not identify missing scripts.");
            await manager.PreflightAsync(gameRoot);
            var after = await manager.InstallAllAsync(gameRoot);
            Assert(after.InstalledCount == 70 && loader.Load(gameRoot).Presets.Count == 70,
                "Installing all effects did not reveal all 70 templates.");
            var squads = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.show_squads");
            var pdaMessage = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == DonationDispatcher.PdaMessageHandlerId);
            Assert(pdaMessage.DisplayName == "Отправить сообщение в КПК" &&
                pdaMessage.ParameterDefinitions.Count == 0 && pdaMessage.Parameters.Count == 0 &&
                pdaMessage.Validate().Count == 0 && !CatalogRevisionEnforcement.RequiresCatalog(pdaMessage),
                "PDA message must install as a self-contained template without catalog or text settings.");
            Assert(squads.DisplayName == "Отобразить отряды" && squads.Parameters["duration_seconds"] == "30" &&
                squads.ParameterDefinitions.Count == 1 &&
                squads.ParameterDefinitions.Single().Kind == ParameterKind.Integer &&
                squads.ParameterDefinitions.Single().Minimum == 1 &&
                squads.ParameterDefinitions.Single().Maximum == 99999999,
                "Show squads must expose one duration control with a 30-second default.");
            var godMode = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "god_mode");
            Assert(godMode.ParameterDefinitions.Single(p => p.Key == "show_timer").DisplayName == "Показывать таймер внизу экрана" &&
                DefaultActionCatalog.CreatePresets().Single(p => p.HandlerId == "god_mode")
                    .ParameterDefinitions.Single(p => p.Key == "show_timer").DisplayName == "Показывать таймер внизу экрана",
                "God mode timer position label must match its new bottom-center position.");
            const string godDescription = "Временно блокирует входящий урон. Раз в секунду снимает радиацию и кровотечение, восстанавливает пси-здоровье до 100%.\n\nЭффект сохраняется при быстрой загрузке и переходах между локациями, но не возобновляется после выхода из игры. Можно отключить таймер на экране или выбрать альтернативный способ через g_god.";
            Assert(godMode.Description == godDescription &&
                DefaultActionCatalog.CreatePresets().Single(p => p.HandlerId == "god_mode").Description == godDescription,
                "God mode file/application descriptions must match the user-approved standalone text.");
            Assert(godMode.Parameters["use_g_god"] == "false" && godMode.Parameters["show_timer"] == "true" &&
                godMode.Parameters["duration_seconds"] == "30" && godMode.ParameterDefinitions.Count == 3,
                "God mode must default to hit protection, native countdown and unchanged duration.");
            foreach (var key in new[] { "show_timer", "use_g_god" })
                Assert(new ActionViewModel(godMode).Parameters.Single(p => p.Definition.Key == key).IsBooleanChoice,
                    "God mode alternatives/timer must be rendered as checkboxes.");
            var oldGodMode = godMode.CloneAsNewInstance();
            oldGodMode.Parameters.Remove("show_timer"); oldGodMode.Parameters.Remove("use_g_god");
            oldGodMode.Parameters["duration_seconds"] = "60";
            new ActionManifestLoader().ApplyPresetsToInstances([oldGodMode], [godMode]);
            Assert(oldGodMode.Parameters["show_timer"] == "true" && oldGodMode.Parameters["use_g_god"] == "false" &&
                oldGodMode.Parameters["duration_seconds"] == "60", "Existing god mode instances receive safe checkbox defaults without losing duration.");
            oldGodMode.Parameters["show_timer"] = "false"; oldGodMode.Parameters["use_g_god"] = "true";
            new ActionManifestLoader().ApplyPresetsToInstances([oldGodMode], [godMode]);
            Assert(oldGodMode.Parameters["show_timer"] == "false" && oldGodMode.Parameters["use_g_god"] == "true",
                "Explicit god mode checkbox choices must survive metadata refresh.");
            var actorBoost = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "actor_boost");
            Assert(actorBoost.DisplayName == "Временно изменить параметры героя",
                "Installed temporary parameters template must not carry the experimental suffix.");
            var oldActorBoost = actorBoost.CloneAsNewInstance();
            oldActorBoost.DisplayName += " (экспериментально)";
            new ActionManifestLoader().ApplyPresetsToInstances([oldActorBoost], [actorBoost]);
            Assert(oldActorBoost.DisplayName == actorBoost.DisplayName,
                "Existing non-custom template names must refresh from the new file metadata.");
            oldActorBoost.HasCustomDisplayName = true;
            oldActorBoost.DisplayName = "Мой бонус";
            new ActionManifestLoader().ApplyPresetsToInstances([oldActorBoost], [actorBoost]);
            Assert(oldActorBoost.DisplayName == "Мой бонус", "Custom template names must be preserved.");
            var legacyMutant = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "spawn_mutant_companion");
            var dementia = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.dementia");
            var weaponMix = dementia.ParameterDefinitions.Single(p => p.Key == "mix_weapon_slots");
            Assert(weaponMix.Kind == ParameterKind.Choice && weaponMix.DefaultValue == "true" &&
                dementia.Parameters["mix_weapon_slots"] == "true" &&
                new ActionViewModel(dementia).Parameters.Single(p => p.Definition.Key == "mix_weapon_slots").IsBooleanChoice,
                "Dementia weapon slots/wheel must be a separate default-ON checkbox.");
            var olderDementia = dementia.CloneAsNewInstance(); olderDementia.Parameters.Remove("mix_weapon_slots");
            new ActionManifestLoader().ApplyPresetsToInstances([olderDementia], [dementia]);
            Assert(olderDementia.Parameters["mix_weapon_slots"] == "true", "Existing instances receive the new default weapon group.");
            olderDementia.Parameters["mix_weapon_slots"] = "false";
            new ActionManifestLoader().ApplyPresetsToInstances([olderDementia], [dementia]);
            Assert(olderDementia.Parameters["mix_weapon_slots"] == "false", "Explicitly disabled weapon group must survive template refresh.");
            var roulette = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.spin_roulette");
            var directSpawnIds = new[] { "spawn_mutants", "spawn_npcs", "spawn_random_mutants", "spawn_random_npcs" };
            foreach (var spawnId in directSpawnIds)
            {
                var spawn = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == spawnId);
                var method = spawn.ParameterDefinitions.Single(p => p.Key == "spawn_point_method");
                var spawnRadius = spawn.ParameterDefinitions.Single(p => p.Key == "radius_meters");
                var methodView = new ActionViewModel(spawn).Parameters.Single(p => p.Definition.Key == method.Key);
                Assert(method.Kind == ParameterKind.Choice && method.Options.SequenceEqual(new[] { "boss_sample", "legacy", "graph", "scan", "coordinates" }) &&
                    method.DefaultValue == "boss_sample" && spawn.Parameters[method.Key] == "boss_sample" &&
                    methodView.IsFlatChoice && methodView.OptionEntries.First().Label == "Случайные точки локации (рекомендуется)" &&
                    methodView.IsSpawnPointMethod && methodView.SpawnPointMethodHint.Contains("не ближе 10 м"),
                    "All four direct-spawn presets need five labelled methods, the boss default and an explanation.");
                Assert(spawnRadius.Maximum == 500 && spawnRadius.Validate("500") is null && spawnRadius.Validate("501") is not null,
                    "Direct-spawn radius limit must be 500 m.");
                var olderSpawn = spawn.CloneAsNewInstance(); olderSpawn.Parameters.Remove(method.Key);
                new ActionManifestLoader().ApplyPresetsToInstances([olderSpawn], [spawn]);
                Assert(olderSpawn.Parameters[method.Key] == "boss_sample", "Older spawn presets must receive the requested boss default.");
                olderSpawn.Parameters[method.Key] = "legacy"; olderSpawn.Parameters["radius_meters"] = "500";
                new ActionManifestLoader().ApplyPresetsToInstances([olderSpawn], [spawn]);
                Assert(olderSpawn.Parameters[method.Key] == "legacy" && olderSpawn.Parameters["radius_meters"] == "500",
                    "Explicit spawn method and radius must survive preset refresh.");
            }
            Assert(loader.Load(gameRoot).Presets.Where(p => !directSpawnIds.Contains(p.HandlerId))
                .All(p => p.ParameterDefinitions.All(d => d.Key != "spawn_point_method")),
                "This iteration must not change other spawn, squad, boss or companion presets.");
            Assert(roulette.DisplayName == "Крутить рулетку" && roulette.CategoryId == "inventory" &&
                roulette.ParameterDefinitions.Count == 3 && roulette.Parameters["count"] == "1" &&
                roulette.Parameters["risk_mode"] == "both" && roulette.Parameters["pause_game"] == "false",
                "Roulette must default to one attempt, both risks and the existing unpaused flow.");
            var rouletteCount = roulette.ParameterDefinitions.Single(d => d.Key == "count");
            var rouletteRisk = roulette.ParameterDefinitions.Single(d => d.Key == "risk_mode");
            Assert(rouletteCount.Kind == ParameterKind.Integer && rouletteCount.Minimum == 1 && rouletteCount.Maximum == 50 &&
                rouletteRisk.Kind == ParameterKind.Choice && rouletteRisk.Options.SequenceEqual(new[] { "both", "low", "high" }) &&
                rouletteRisk.OptionLabels["both"] == "Low и High" && rouletteRisk.OptionLabels["low"] == "Low" &&
                rouletteRisk.OptionLabels["high"] == "High", "Roulette N bounds and labelled risk selector regressed.");
            var riskView = new ActionViewModel(roulette).Parameters.Single(p => p.Definition.Key == "risk_mode");
            Assert(riskView.IsFlatChoice && riskView.OptionEntries.Select(p => p.Label).SequenceEqual(new[] { "Low и High", "Low", "High" }) &&
                riskView.OptionEntries.Select(p => p.Value).SequenceEqual(new[] { "both", "low", "high" }) &&
                riskView.DisplayValue == "Low и High" && !roulette.Description.Contains("Установка:", StringComparison.Ordinal),
                "Flat choices must use per-template labels, not the global equipment dictionary; roulette must not include an installation note.");
            var pauseView = new ActionViewModel(roulette).Parameters.Single(p => p.Definition.Key == "pause_game");
            Assert(pauseView.IsBooleanChoice && !pauseView.BooleanValue,
                "Roulette pause must be a checkbox and remain disabled by default.");
            foreach (var mode in new[] { "both", "low", "high" })
            {
                var savedRoulette = roulette.CloneAsNewInstance();
                savedRoulette.Parameters["count"] = "7"; savedRoulette.Parameters["risk_mode"] = mode;
                savedRoulette.Parameters["pause_game"] = "true";
                new ActionManifestLoader().ApplyPresetsToInstances([savedRoulette], [roulette]);
                Assert(savedRoulette.Parameters["count"] == "7" && savedRoulette.Parameters["risk_mode"] == mode &&
                    savedRoulette.Parameters["pause_game"] == "true",
                    "Template refresh must preserve an explicitly configured series/risk.");
            }
            var olderRoulette = roulette.CloneAsNewInstance();
            olderRoulette.Parameters.Remove("pause_game");
            new ActionManifestLoader().ApplyPresetsToInstances([olderRoulette], [roulette]);
            Assert(olderRoulette.Parameters["pause_game"] == "false", "Existing roulette templates must retain unpaused behavior.");
            var newLife = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.new_life");
            var newLifeDefaults = new Dictionary<string, string>
            {
                ["keep_inventory"] = "false", ["keep_faction"] = "true",
                ["hostile_previous_body"] = "true", ["any_location"] = "false",
                ["quest_previous_body"] = "true", ["leave_companions"] = "true"
            };
            Assert(newLife.DisplayName == "Новая жизнь" && newLife.ParameterDefinitions.Count == 6 &&
                !CatalogRevisionEnforcement.RequiresCatalog(newLife) && !newLife.Description.Contains("Установка:", StringComparison.Ordinal),
                "New Life must expose six options including former-body companions, without a static NPC catalog.");
            foreach (var (key, value) in newLifeDefaults)
            {
                var definition = newLife.ParameterDefinitions.Single(p => p.Key == key);
                Assert(definition.Kind == ParameterKind.Choice && definition.Options.SequenceEqual(new[] { "false", "true" }) &&
                    definition.DefaultValue == value && newLife.Parameters[key] == value &&
                    new ActionViewModel(newLife).Parameters.Single(p => p.Definition.Key == key).IsBooleanChoice,
                    "New Life must expose an ordinary checkbox with the requested default: " + key);
            }
            var customLife = newLife.CloneAsNewInstance();
            foreach (var (key, value) in newLifeDefaults) customLife.Parameters[key] = value == "true" ? "false" : "true";
            new ActionManifestLoader().ApplyPresetsToInstances([customLife], [newLife]);
            Assert(newLifeDefaults.All(pair => customLife.Parameters[pair.Key] != pair.Value),
                "Refreshing New Life must not replace explicit user checkbox values with defaults.");
            var azazel = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.new_life_azazel");
            Assert(azazel.DisplayName == "Новая жизнь (Азазель)" && azazel.ParameterDefinitions.Count == 5 &&
                azazel.Parameters["duration_seconds"] == "180" && azazel.Parameters["any_location"] == "false" &&
                azazel.Parameters["keep_faction"] == "true" && azazel.Parameters["prefer_companions"] == "true" &&
                azazel.Parameters["keep_inventory"] == "false" && !CatalogRevisionEnforcement.RequiresCatalog(azazel),
                "Azazel must expose the agreed independent timer and four options using the live game NPCs.");
            var olderLife = newLife.CloneAsNewInstance();
            olderLife.Parameters.Remove("leave_companions");
            new ActionManifestLoader().ApplyPresetsToInstances([olderLife], [newLife]);
            Assert(olderLife.Parameters["leave_companions"] == "true", "New companion option defaults on for existing instances.");
            var mutantV2 = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.mutant_companion_v2");
            Assert(legacyMutant.DisplayName == "Напарник-мутант (legacy)" && mutantV2.DisplayName == "Напарник-мутант V2",
                "Legacy mutant rename must not rename V2 or change either handler.");
            var namedCompanion = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "spawn_named_companion");
            var specialCompanion = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.special_companion");
            Assert(specialCompanion.DisplayName == "Напарник с именем данатчика (особый визуал)" &&
                specialCompanion.CategoryId == "npc_mutants" && DonationDispatcher.UsesDonorName(specialCompanion.HandlerId),
                "Special companion must have the requested name/category and receive the real donor nickname.");
            Assert(specialCompanion.ParameterDefinitions.Select(d => d.Key).SequenceEqual(namedCompanion.ParameterDefinitions.Select(d => d.Key)) &&
                specialCompanion.Parameters.All(pair => namedCompanion.Parameters[pair.Key] == pair.Value) &&
                specialCompanion.ParameterDefinitions.Single(d => d.Key == "section_id").CatalogKind == CatalogEntryKind.NpcVisual,
                "Special companion must preserve ordinary companion settings/defaults, replacing only the visual catalogue.");
            CatalogEntry[] specialEntries = [
                new() { SectionId = "special_hip", Kind = CatalogEntryKind.NpcVisual, DisplayName = "Хип — actors\\pack\\girl.ogf", IsAvailable = true },
                new() { SectionId = "broken", Kind = CatalogEntryKind.NpcVisual, DisplayName = "Недоступная модель", IsAvailable = false, QuarantineReason = QuarantineReason.InvalidStructure },
                new() { SectionId = "ordinary", Kind = CatalogEntryKind.Npc, DisplayName = "Обычный NPC", IsAvailable = true }
            ];
            var specialCatalog = new CatalogSnapshot(new CatalogRevision("special-rev"), CatalogContentHash.Compute(specialEntries), specialEntries);
            DynamicCatalogBinding.Apply(specialCatalog, [specialCompanion]);
            Assert(specialCompanion.ParameterDefinitions.Single(d => d.Key == "section_id").Options.SequenceEqual(["special_hip"]) &&
                specialCompanion.Parameters["section_id"] == "special_hip", "OGF picker must exclude normal sections and quarantined models.");
            Assert(new CatalogOptionViewModel(specialEntries[0], false, null).LocalizedAndTechnicalName == specialEntries[0].DisplayName,
                "Special visual label must not append an opaque hash after the readable name and OGF path.");
            var visualCommand = new GameCommand { ActionId = specialCompanion.HandlerId, CatalogRevision = specialCatalog.Revision,
                Parameters = new() { ["section_id"] = "special_hip" } };
            Assert(CatalogRevisionEnforcement.Validate(visualCommand, specialCatalog).IsValid, "A current special appearance must pass IPC validation.");
            visualCommand.Parameters["section_id"] = "ordinary";
            Assert(!CatalogRevisionEnforcement.Validate(visualCommand, specialCatalog).IsValid, "An ordinary/story section must not bypass the special appearance selection.");
            specialCompanion.Parameters["random_npc"] = "true"; specialCompanion.Parameters["section_id"] = "";
            visualCommand.Parameters["random_npc"] = "true"; visualCommand.Parameters["section_id"] = "";
            Assert(specialCompanion.Validate().Count == 0 && CatalogRevisionEnforcement.Validate(visualCommand, specialCatalog).IsValid,
                "Random special appearance must work without a fixed selection.");
            Assert(namedCompanion.DisplayName == "Напарник с именем данатчика" &&
                DefaultActionCatalog.CreatePresets().Single(p => p.HandlerId == "spawn_named_companion").DisplayName == namedCompanion.DisplayName,
                "Named companion file/application titles must match without changing its handler.");
            var companionDamage = namedCompanion.ParameterDefinitions.Single(d => d.Key == "incoming_damage_percent");
            Assert(companionDamage.Kind == ParameterKind.Integer && companionDamage.Minimum == 1 && companionDamage.Maximum == 100 &&
                companionDamage.DefaultValue == "100" && namedCompanion.Parameters[companionDamage.Key] == "100",
                "Named NPC companion damage must default to native behavior and allow only 1..100 percent.");
            Assert(mutantV2.ParameterDefinitions.All(d => d.Key != companionDamage.Key), "NPC damage option must not modify the mutant template.");
            var protectedCompanion = namedCompanion.CloneAsNewInstance();
            protectedCompanion.Parameters[companionDamage.Key] = "10";
            new ActionManifestLoader().ApplyPresetsToInstances([protectedCompanion], [namedCompanion]);
            Assert(protectedCompanion.Parameters[companionDamage.Key] == "10", "NPC damage choice must survive template refresh.");
            foreach (var companionPreset in new[] { mutantV2, namedCompanion })
            {
                var nicknameOption = companionPreset.ParameterDefinitions.Single(d => d.Key == "nickname_visibility");
                const string expected = "hide";
                var nicknameView = new ActionViewModel(companionPreset).RegularParameters.Single(p => p.Definition.Key == nicknameOption.Key);
                var nicknameKey = companionPreset.ParameterDefinitions.Single(d => d.Key == "nickname_key");
                var expectedKey = companionPreset.HandlerId == mutantV2.HandlerId ? "NEXT" : "PRIOR";
                Assert(nicknameOption.DisplayName == "3D ник по умолчанию" && nicknameOption.Options.SequenceEqual(new[] { "show", "hide" }) &&
                    nicknameOption.DefaultValue == expected && companionPreset.Parameters[nicknameOption.Key] == expected && !nicknameView.IsBooleanChoice &&
                    nicknameKey.Kind == ParameterKind.KeyboardKey && nicknameKey.DefaultValue == expectedKey && companionPreset.Parameters[nicknameKey.Key] == expectedKey,
                    "Companion 3D nickname needs a default-visibility dropdown and independently rebindable PageDown/PageUp hotkey.");
                var checkboxLegacy = companionPreset.Clone();
                checkboxLegacy.Parameters.Remove("nickname_visibility");checkboxLegacy.Parameters["show_3d_nick"] = "false";
                new ActionManifestLoader().ApplyPresetsToInstances([checkboxLegacy], [companionPreset]);
                Assert(checkboxLegacy.Parameters["nickname_visibility"] == "hide" && !checkboxLegacy.Parameters.ContainsKey("show_3d_nick"),
                    "Changing the nickname checkbox to a dropdown must preserve an explicitly disabled old setting.");
                foreach (var savedKey in new[] { "END", "F6" })
                {
                    var rebound = companionPreset.Clone();
                    rebound.Parameters["nickname_key"] = savedKey;
                    new ActionManifestLoader().ApplyPresetsToInstances([rebound], [companionPreset]);
                    Assert(rebound.Parameters["nickname_key"] == savedKey,
                        "New default nickname keys must not replace an explicit saved binding.");
                }
                foreach (var savedVisibility in new[] { "show", "hide" })
                {
                    var saved = companionPreset.Clone();
                    saved.Parameters["nickname_visibility"] = savedVisibility;
                    new ActionManifestLoader().ApplyPresetsToInstances([saved], [companionPreset]);
                    Assert(saved.Parameters["nickname_visibility"] == savedVisibility,
                        "Hidden-by-default nickname must not replace an explicitly saved visibility setting.");
                }
            }
            var savedLegacyMutant = legacyMutant.CloneAsNewInstance();
            savedLegacyMutant.DisplayName = "Заспавнить мутанта-напарника (экспериментально)";
            savedLegacyMutant.Parameters["radius_meters"] = "42";
            var customLegacyMutant = savedLegacyMutant.CloneAsNewInstance();
            customLegacyMutant.DisplayName = "Мой мутант";customLegacyMutant.HasCustomDisplayName = true;
            new ActionManifestLoader().ApplyPresetsToInstances([savedLegacyMutant, customLegacyMutant], [legacyMutant]);
            Assert(savedLegacyMutant.DisplayName == "Напарник-мутант (legacy)" && savedLegacyMutant.Parameters["radius_meters"] == "42" &&
                customLegacyMutant.DisplayName == "Мой мутант" && customLegacyMutant.Parameters["radius_meters"] == "42",
                "Legacy rename must refresh default names while preserving custom names and settings.");
            var turret = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.stationary_mgun");
            var lifetime = turret.ParameterDefinitions.Single(p => p.Key == "lifetime_seconds");
            Assert(turret.DisplayName == "Стационарный пулемёт" && turret.CategoryId == "world" &&
                lifetime.Key == "lifetime_seconds" && lifetime.Kind == ParameterKind.Integer &&
                lifetime.Minimum == 0 && lifetime.Maximum == 6000 && lifetime.DefaultValue == "60" &&
                turret.Parameters[lifetime.Key] == "60" && !CatalogRevisionEnforcement.RequiresCatalog(turret),
                "Stationary gun must be a standalone native template with editable lifetime 0-6000, default 60.");
            foreach (var validLifetime in new[] { "0", "1", "59", "60", "6000" })
                Assert(lifetime.Validate(validLifetime) is null, "Valid gun lifetime rejected: " + validLifetime);
            foreach (var invalidLifetime in new[] { "-1", "6001", "1.5", "invalid" })
                Assert(lifetime.Validate(invalidLifetime) is not null, "Invalid gun lifetime accepted: " + invalidLifetime);
            var rotation = turret.ParameterDefinitions.Single(p => p.Key == "rotation_speed_dps");
            Assert(turret.ParameterDefinitions.Count == 2 && rotation.Kind == ParameterKind.Choice &&
                rotation.DisplayName == "Скорость вращения, °/сек" && rotation.DefaultValue == "60" &&
                turret.Parameters[rotation.Key] == "60" &&
                rotation.Options.SequenceEqual(new[] { "60", "90", "120" }),
                "Gun must expose 60/90/120 native speed selection, default 60 degrees/sec.");
            foreach (var value in rotation.Options) Assert(rotation.Validate(value) is null, "Valid speed rejected: " + value);
            foreach (var value in new[] { "0", "5", "10", "15", "20", "30", "40", "45", "180", "61", "20.5", "bad" })
                Assert(rotation.Validate(value) is not null, "Invalid native speed accepted: " + value);
            var slowTime = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.slow_time");
            Assert(slowTime.DisplayName == "Замедление времени" && slowTime.CategoryId == "player_state" && slowTime.ParameterDefinitions.Count == 2 &&
                !CatalogRevisionEnforcement.RequiresCatalog(slowTime), "Slow time must be standalone, without a game catalog or MCM.");
            foreach (var (key, initial, min, max) in new[] {
                ("duration_seconds", "30", 1, 3600), ("slowdown_percent", "65", 1, 90) })
            {
                var definition = slowTime.ParameterDefinitions.Single(p => p.Key == key);
                Assert(definition.Kind == ParameterKind.Integer && definition.DefaultValue == initial &&
                    slowTime.Parameters[key] == initial && definition.Minimum == min && definition.Maximum == max &&
                    definition.Validate("0") is not null && definition.Validate(initial) is null,
                    "Slow time must expose validated editable settings: " + key);
            }
            Assert(slowTime.Description.Contains("F5/F9 сохраняют остаток") && slowTime.Description.Contains("включая героя"),
                "Slow-time description must explain persistence and world/player scaling.");
            var aimTeleport = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.aim_teleport");
            Assert(aimTeleport.DisplayName == "Телепортация по прицелу" && aimTeleport.ParameterDefinitions.Count == 2 &&
                aimTeleport.Validate().Count == 0 && !CatalogRevisionEnforcement.RequiresCatalog(aimTeleport),
                "Aim teleport must install without a catalog and expose exactly two editable settings.");
            foreach (var (key, maximum) in new[] { ("repeat_count", 99), ("interval_seconds", 600) })
            {
                var definition = aimTeleport.ParameterDefinitions.Single(p => p.Key == key);
                Assert(definition.Kind == ParameterKind.Integer && definition.DefaultValue == "3" &&
                    aimTeleport.Parameters[key] == "3" && definition.Minimum == 1 && definition.Maximum == maximum,
                    "Aim teleport settings must match the 3/3 defaults and requested bounds: " + key);
                foreach (var accepted in new[] { "1", "3", maximum.ToString() })
                    Assert(definition.Validate(accepted) is null, "Valid aim teleport setting rejected: " + key + "=" + accepted);
                foreach (var rejected in new[] { "0", "-1", "2.5", (maximum + 1).ToString() })
                    Assert(definition.Validate(rejected) is not null, "Invalid aim teleport setting accepted: " + key + "=" + rejected);
            }
            var guitar = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.play_guitar");
            var songs = guitar.ParameterDefinitions.Single();
            Assert(guitar.DisplayName == "Сыграть на гитаре" && songs.Key == "song_sections" &&
                songs.Kind == ParameterKind.MultiChoice && songs.CatalogKind == CatalogEntryKind.GuitarMusic &&
                songs.MinimumSelections == 1 && guitar.Parameters[songs.Key] == "",
                "Guitar must use a live multi-select, not copied vanilla track names.");
            var guitarEntries = new[] {
                new CatalogEntry {SectionId="adb_guitar_custom",Kind=CatalogEntryKind.GuitarMusic,DisplayName="Custom song",IsAvailable=true},
                new CatalogEntry {SectionId="adb_guitar_excluded",Kind=CatalogEntryKind.GuitarMusic,DisplayName="Excluded",IsAvailable=false,QuarantineReason=QuarantineReason.Blacklisted}
            };
            var guitarSnapshot = new CatalogSnapshot(new CatalogRevision("guitar-songs"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(guitarEntries),guitarEntries);
            DynamicCatalogBinding.Apply(guitarSnapshot,[guitar]);
            Assert(songs.Options.SequenceEqual(new[] { "adb_guitar_custom" }) &&
                guitar.Parameters[songs.Key] == "adb_guitar_custom" && songs.Validate("adb_guitar_excluded") is not null,
                "Guitar picker did not bind safe live songs.");
            var burer = loader.Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.burer_form");
            var burerAtlas = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "addon-package/gamedata/textures/ui/anomaly_donation_bridge_burer_form.dds"));
            Assert(burerAtlas.Length == 128 + 512 * 512 * 4 &&
                System.Text.Encoding.ASCII.GetString(burerAtlas, 0, 4) == "DDS " &&
                BitConverter.ToUInt32(burerAtlas, 12) == 512 && BitConverter.ToUInt32(burerAtlas, 16) == 512 &&
                burerAtlas[128 + 3] == 0 && burerAtlas[128 + (128 * 512 + 256) * 4 + 3] == 0,
                "Burer HUD atlas must not contain black text backgrounds.");
            Assert(Enumerable.Range(0, 512 * 256).All(pixel => burerAtlas[128 + pixel * 4 + 3] == 0),
                "Old DDS backdrop must be fully transparent.");
            Assert(burerAtlas[128 + (384 * 512 + 128) * 4 + 3] == 0 &&
                burerAtlas[128 + (384 * 512 + 248) * 4 + 3] > 100,
                "Targeting ring must have a transparent centre and visible anti-aliased outline.");
            var ringPixel = 128 + (384 * 512 + 248) * 4;
            Assert(burerAtlas[ringPixel] == 235 && burerAtlas[ringPixel + 1] == 235 && burerAtlas[ringPixel + 2] == 235,
                "Targeting ring must be white, not gold.");
            // Approved reticle: only two short side arcs. Do not shrink the
            // radius, widen the sprite, restore top/bottom, or add a panel.
            var visibleBrackets = 0;
            for (var y = 256; y < 512; y++)
            for (var x = 0; x < 256; x++)
            {
                var alpha = burerAtlas[128 + (y * 512 + x) * 4 + 3];
                if (alpha == 0) continue;
                visibleBrackets++;
                var dx = Math.Abs(x - 127.5);
                var dy = Math.Abs(y - 383.5);
                var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                var bracketRadius = Math.Sqrt(dx * dx + dy * dy);
                Assert(angle < 16.5 && Math.Abs(bracketRadius - 120) < 3.2,
                    "Reticle must keep 120px radius with only the approved 32.256-degree side arcs.");
                var mirror = 128 + (y * 512 + (255 - x)) * 4 + 3;
                Assert(burerAtlas[mirror] == alpha, "Reticle side brackets must be symmetrical.");
            }
            Assert(visibleBrackets is > 400 and < 1200 &&
                burerAtlas[128 + (264 * 512 + 128) * 4 + 3] == 0 &&
                burerAtlas[128 + (504 * 512 + 128) * 4 + 3] == 0 &&
                burerAtlas[128 + (384 * 512 + 8) * 4 + 3] > 100,
                "Reticle must have visible left/right brackets and fully transparent top/bottom.");
            var burerTextures = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory,
                "addon-package/gamedata/configs/ui/textures_descr/anomaly_donation_bridge_burer_form.xml"));
            Assert(!burerTextures.Descendants("texture").Any(t => (string?)t.Attribute("id") == "adb_burer_hint_panel") &&
                burerTextures.Descendants("texture").Any(t => (string?)t.Attribute("id") == "adb_burer_aim_ring") &&
                burerTextures.Descendants("file").Any(t => (string?)t.Attribute("name") == @"ui\anomaly_donation_bridge_burer_form"),
                "Burer HUD must reference its private atlas without replacing shared game textures.");
            var appearances = burer.ParameterDefinitions.Single(definition => definition.Key == "mutant_sections");
            var burerView = new ActionViewModel(burer);
            Assert(burer.Description.Split('\n').Length == 5 && burer.Description.Length < 350 &&
                burerView.RegularParameters.Select(parameter => parameter.Definition.Key)
                    .SequenceEqual(new[] { "mutant_sections", "duration_seconds", "health_percent", "attack_power_percent", "tele_power_percent", "camera_mode" }) &&
                burerView.AbilityKeyParameters.Select(parameter => parameter.Definition.Key)
                    .SequenceEqual(new[] { "wave_key", "tele_key", "melee_key", "shield_key", "camera_key" }) &&
                burerView.Parameters.Count == 11,
                "Burer description/grouping lost its short paragraphs or changed persistence/order of ability bindings.");
            var telePower = burer.ParameterDefinitions.Single(definition => definition.Key == "tele_power_percent");
            var requestedHealth = burer.ParameterDefinitions.Single(definition => definition.Key == "health_percent");
            Assert(burer.DisplayName == "Превратиться в бюрера" && burer.Description.Contains("свои HP", StringComparison.Ordinal) &&
                telePower.DefaultValue == "150" && telePower.Minimum == 10 && telePower.Maximum == 200 &&
                burer.Parameters[telePower.Key] == "150" && requestedHealth.Validate("100") is null &&
                requestedHealth.Validate("9000") is null && burer.Parameters[requestedHealth.Key] == "300",
                "Burer throw strength/defaults or requested 100-9000% health range are incorrect.");
            var attackPower = burer.ParameterDefinitions.Single(definition => definition.Key == "attack_power_percent");
            Assert(attackPower.DisplayName == "Сила атаки, %" && attackPower.DefaultValue == "200" &&
                burer.Parameters[attackPower.Key] == "200" && attackPower.Minimum == 10 && attackPower.Maximum == 9000 &&
                attackPower.Validate("10") is null && attackPower.Validate("200") is null && attackPower.Validate("9000") is null &&
                attackPower.Validate("0") is not null && attackPower.Validate("9001") is not null,
                "Burer attack strength field/default/range is invalid.");
            Assert(burer.CategoryId == "metamorphosis" && burer.ParameterDefinitions.Count == 11 &&
                appearances.CatalogNativeClasses.SequenceEqual(new[] { "SM_BURER" }) &&
                appearances.Clone().CatalogNativeClasses.SequenceEqual(appearances.CatalogNativeClasses) &&
                burer.Parameters["duration_seconds"] == "60" && CatalogRevisionEnforcement.RequiresCatalog(burer),
                "Burer prototype must use a live class-filtered appearance picker, not fixed source section names.");
            Assert(ActionBrowserViewModel.Categories.Single(category => category.Id == "metamorphosis").Label == "Метаморфоз" &&
                loader.Load(gameRoot).Presets.Where(preset => preset.CategoryId == "metamorphosis")
                    .Select(preset => preset.HandlerId).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "hardcoon.adb.bloodsucker_form", "hardcoon.adb.burer_form", "hardcoon.adb.controller_form", "hardcoon.adb.poltergeist_form", "hardcoon.adb.pseudogiant_form", "hardcoon.adb.tushkano_form" }),
                "Metamorphosis must contain six forms and exclude the archived chimera and psy-dog.");
            foreach (var form in loader.Load(gameRoot).Presets.Where(preset => preset.CategoryId == "metamorphosis"))
            {
                var cameraDefault = form.HandlerId is "hardcoon.adb.tushkano_form" or "hardcoon.adb.poltergeist_form" ? "center" : "side";
                var camera = form.ParameterDefinitions.Single(d => d.Key == "camera_mode");
                var cameraKey = form.ParameterDefinitions.Single(d => d.Key == "camera_key");
                Assert(camera.Kind == ParameterKind.Choice && camera.Options.SequenceEqual(new[] { "side", "center" }) &&
                    camera.OptionLabels["side"] == "Камера сбоку" && camera.OptionLabels["center"] == "По центру" &&
                    camera.DefaultValue == cameraDefault && form.Parameters["camera_mode"] == cameraDefault &&
                    camera.Validate("center") is null && camera.Validate("invalid") is not null &&
                    cameraKey.Kind == ParameterKind.GameInputKey && cameraKey.DefaultValue == "V" &&
                    form.Parameters["camera_key"] == "V" && cameraKey.Validate("G") is null,
                    $"Wrong camera choice/binding/default: {form.HandlerId}");
                var existing = form.Clone(); existing.Parameters["camera_mode"] = "center"; existing.Parameters["camera_key"] = "G";
                new ActionManifestLoader().ApplyPresetsToInstances([existing], [form]);
                Assert(existing.Parameters["camera_mode"] == "center" && existing.Parameters["camera_key"] == "G",
                    $"Saved camera preference was overwritten: {form.HandlerId}");
                var duration = form.ParameterDefinitions.Single(d => d.Key == "duration_seconds");
                Assert(duration.Minimum == 1 && duration.Maximum == 60000 && duration.DefaultValue == "60" &&
                    duration.Validate("60000") is null && duration.Validate("60001") is not null && duration.Validate("0") is not null,
                    $"Wrong metamorphosis duration limits: {form.HandlerId}");
            }
            var controller = loader.Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.controller_form");
            var controllerView = new ActionViewModel(controller);
            var servantLimit = controller.ParameterDefinitions.Single(d => d.Key == "max_servants");
            Assert(controller.DisplayName == "Превратиться в контролёра" && controller.CategoryId == "metamorphosis" &&
                controller.ParameterDefinitions.Count == 11 && controllerView.AbilityKeyParameters.Count() == 5 &&
                controller.Parameters["health_percent"] == "400" && controller.ParameterDefinitions.Single(d => d.Key == "health_percent").DefaultValue == "400" && controller.Parameters["attack_power_percent"] == "200" &&
                controller.Parameters["max_servants"] == "10" && servantLimit.Minimum == 1 && servantLimit.Maximum == 50 &&
                servantLimit.Validate("10") is null && servantLimit.Validate("0") is not null && servantLimit.Validate("51") is not null &&
                controller.Parameters["capture_key"] == "MOUSE_1" && controller.Parameters["aim_key"] == "MOUSE_2" && controller.Parameters["melee_key"] == "F" &&
                controller.Parameters["cycle_key"] == "R" && !controller.ParameterDefinitions.Any(d => d.Key is "panic_key" or "mark_key" or "control_key") &&
                controller.Description.Split('\n').Length == 5 && CatalogRevisionEnforcement.RequiresCatalog(controller),
                "Controller must remain a standalone declarative form with three bindings and a configurable default of ten servants.");
            var savedControllerHealth = controller.Clone();savedControllerHealth.Parameters["health_percent"] = "300";
            var missingControllerHealth = controller.Clone();missingControllerHealth.Parameters.Remove("health_percent");
            new ActionManifestLoader().ApplyPresetsToInstances([savedControllerHealth, missingControllerHealth], [controller]);
            Assert(savedControllerHealth.Parameters["health_percent"] == "300" && missingControllerHealth.Parameters["health_percent"] == "400",
                "Controller HP default must be 400% total without overwriting explicit saved HP.");
            var legacyController = controller.Clone();
            legacyController.Parameters.Remove("capture_key"); legacyController.Parameters.Remove("aim_key");
            legacyController.Parameters["mark_key"] = "MOUSE_1"; legacyController.Parameters["control_key"] = "MOUSE_2";
            new ActionManifestLoader().ApplyPresetsToInstances([legacyController], [controller]);
            Assert(legacyController.Parameters["capture_key"] == "MOUSE_1" && legacyController.Parameters["aim_key"] == "MOUSE_2" &&
                !controller.Description.Contains("общая цель", StringComparison.Ordinal) && !controller.Description.Contains("Без метки", StringComparison.Ordinal),
                "Controller must migrate old bindings without manual-target controls or RMB collisions.");
            var controllerPicker = controller.ParameterDefinitions.Single(d => d.Key == "mutant_sections");
            var controllerEntries = new[] {
                new CatalogEntry {SectionId="custom_controller",Kind=CatalogEntryKind.Mutant,NativeClass="SM_CONTR",DisplayName="Controller",IsAvailable=true},
                new CatalogEntry {SectionId="custom_burer",Kind=CatalogEntryKind.Mutant,NativeClass="SM_BURER",DisplayName="Burer",IsAvailable=true}
            };
            DynamicCatalogBinding.Apply(new CatalogSnapshot(new CatalogRevision("controller-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(controllerEntries), controllerEntries), [controller]);
            Assert(controllerPicker.CatalogNativeClasses.SequenceEqual(new[] { "SM_CONTR" }) &&
                controllerPicker.Options.SequenceEqual(new[] { "custom_controller" }) && controllerPicker.Validate("custom_burer") is not null,
                "Controller picker must exclude other mutant classes.");
            // Exercise archived metadata independently; never include it in shipped effects.
            const string psyDogFile = "anomaly_donation_bridge_effect_hardcoon_adb_psy_dog_form.script";
            var psyArchive = Path.Combine(Directory.GetParent(optionalRoot)!.FullName,
                "deferred-effects", "psy-dog-form", "PDF-001");
            Assert(!shippedFiles.Any(path => Path.GetFileName(path) == psyDogFile) &&
                !optional.SourcePaths.ContainsKey("hardcoon.adb.psy_dog_form"),
                "Archived psy-dog leaked into the application package.");
            var psyGameFile = Path.Combine(gameScripts, psyDogFile);
            File.Copy(Path.Combine(psyArchive, "gamedata", "scripts", psyDogFile), psyGameFile);
            var psyDog = new FilePresetLoader(psyArchive).Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.psy_dog_form");
            File.Delete(psyGameFile);
            var psyView = new ActionViewModel(psyDog);
            Assert(psyDog.DisplayName == "Превратиться в пси-собаку" && psyDog.CategoryId == "metamorphosis" &&
                psyDog.ParameterDefinitions.Count == 9 && psyView.AbilityKeyParameters.Count() == 2 &&
                psyDog.Parameters["health_percent"] == "300" && psyDog.Parameters["attack_power_percent"] == "200" &&
                psyDog.Parameters["phantom_count"] == "4" && psyDog.Parameters["phantom_duration_seconds"] == "20" &&
                psyDog.Parameters["phantom_cooldown_seconds"] == "8" &&
                psyDog.Parameters["melee_key"] == "MOUSE_1" && psyDog.Parameters["phantom_key"] == "MOUSE_2" &&
                psyDog.Description.Split('\n').Length == 5 && CatalogRevisionEnforcement.RequiresCatalog(psyDog),
                "Psy-dog must be a standalone declarative form with configurable bite/temporary doubles.");
            var psyPicker = psyDog.ParameterDefinitions.Single(d => d.Key == "mutant_sections");
            var psyEntries = new[] {
                new CatalogEntry {SectionId="custom_psy",Kind=CatalogEntryKind.Mutant,NativeClass="SM_DOG_P",DisplayName="Psy dog",IsAvailable=true},
                new CatalogEntry {SectionId="ordinary_dog",Kind=CatalogEntryKind.Mutant,NativeClass="SM_P_DOG",DisplayName="Ordinary pseudodog",IsAvailable=true},
                new CatalogEntry {SectionId="native_phantom",Kind=CatalogEntryKind.Mutant,NativeClass="SM_DOG_F",DisplayName="Native phantom",IsAvailable=true}
            };
            DynamicCatalogBinding.Apply(new CatalogSnapshot(new CatalogRevision("psy-dog-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(psyEntries), psyEntries), [psyDog]);
            Assert(psyPicker.CatalogNativeClasses.SequenceEqual(new[] { "SM_DOG_P" }) &&
                psyPicker.Options.SequenceEqual(new[] { "custom_psy" }) &&
                psyPicker.Validate("ordinary_dog") is not null && psyPicker.Validate("native_phantom") is not null,
                "Psy-dog picker leaked ordinary dogs or parent-dependent native phantoms.");
            var bloodsucker = loader.Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.bloodsucker_form");
            var bloodView = new ActionViewModel(bloodsucker);
            Assert(bloodsucker.DisplayName == "Превратиться в кровососа" && bloodsucker.CategoryId == "metamorphosis" &&
                bloodsucker.ParameterDefinitions.Count == 8 && bloodView.RegularParameters.Count() == 5 &&
                bloodView.AbilityKeyParameters.Count() == 3 && bloodsucker.Parameters["health_percent"] == "200" &&
                bloodsucker.ParameterDefinitions.Single(d => d.Key == "health_percent").DefaultValue == "200" &&
                bloodsucker.Parameters["attack_power_percent"] == "200" && bloodsucker.Parameters["melee_key"] == "MOUSE_1" &&
                bloodsucker.Parameters["cloak_key"] == "MOUSE_2" && !bloodsucker.Parameters.ContainsKey("dash_key") &&
                bloodsucker.Description.Split('\n').Length == 5 && CatalogRevisionEnforcement.RequiresCatalog(bloodsucker),
                "Bloodsucker must use the existing declarative editor without changing the EXE.");
            var bloodPicker = bloodsucker.ParameterDefinitions.Single(d => d.Key == "mutant_sections");
            var bloodEntries = new[] {
                new CatalogEntry {SectionId="custom_sucker",Kind=CatalogEntryKind.Mutant,NativeClass="SM_BLOOD",DisplayName="Custom bloodsucker",IsAvailable=true},
                new CatalogEntry {SectionId="bloodsucker_named_dog",Kind=CatalogEntryKind.Mutant,NativeClass="SM_DOG_S",DisplayName="Not a bloodsucker",IsAvailable=true}
            };
            DynamicCatalogBinding.Apply(new CatalogSnapshot(new CatalogRevision("bloodsucker-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(bloodEntries), bloodEntries), [bloodsucker]);
            Assert(bloodPicker.CatalogNativeClasses.SequenceEqual(new[] { "SM_BLOOD" }) &&
                bloodPicker.Options.SequenceEqual(new[] { "custom_sucker" }) &&
                bloodPicker.Validate("bloodsucker_named_dog") is not null &&
                bloodsucker.ParameterDefinitions.Single(d => d.Key == "health_percent").Validate("9000") is null,
                "Bloodsucker picker leaked another class or rejected configurable health.");
            var savedBloodsucker = bloodsucker.Clone();savedBloodsucker.Parameters["health_percent"] = "300";
            new ActionManifestLoader().ApplyPresetsToInstances([savedBloodsucker], [bloodsucker]);
            Assert(savedBloodsucker.Parameters["health_percent"] == "300",
                "The new 200% default must not overwrite a saved Bloodsucker health value.");
            var giant = loader.Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.pseudogiant_form");
            var giantView = new ActionViewModel(giant);
            Assert(giant.DisplayName == "Превратиться в псевдогиганта" && giant.CategoryId == "metamorphosis" &&
                giant.ParameterDefinitions.Count == 8 && giantView.RegularParameters.Count() == 5 &&
                giantView.AbilityKeyParameters.Count() == 3 && giant.Parameters["health_percent"] == "1300" &&
                giant.Parameters["attack_power_percent"] == "200" && giant.Parameters["melee_key"] == "MOUSE_1" &&
                giant.Parameters["stomp_key"] == "MOUSE_2" && CatalogRevisionEnforcement.RequiresCatalog(giant),
                "Standalone giant preset is not supported by the existing declarative editor/catalog contract.");
            var giantPicker = giant.ParameterDefinitions.Single(d => d.Key == "mutant_sections");
            Assert(giantPicker.CatalogNativeClasses.SequenceEqual(new[] { "SM_GIANT" }) &&
                giant.ParameterDefinitions.Single(d => d.Key == "health_percent").DefaultValue == "1300" &&
                giant.ParameterDefinitions.Single(d => d.Key == "health_percent").Validate("9000") is null &&
                giant.ParameterDefinitions.Single(d => d.Key == "attack_power_percent").Validate("350") is null,
                "Giant model class or independent HP/attack ranges are invalid.");
            var giantEntries = new[] {
                new CatalogEntry {SectionId="modded_giant",Kind=CatalogEntryKind.Mutant,NativeClass="SM_GIANT",DisplayName="Custom giant",IsAvailable=true},
                new CatalogEntry {SectionId="pseudogiant_named_dog",Kind=CatalogEntryKind.Mutant,NativeClass="SM_DOG_S",DisplayName="Not a giant",IsAvailable=true}
            };
            var giantSnapshot = new CatalogSnapshot(new CatalogRevision("giant-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(giantEntries), giantEntries);
            DynamicCatalogBinding.Apply(giantSnapshot, [giant]);
            Assert(giantPicker.Options.SequenceEqual(new[] { "modded_giant" }) &&
                giant.Parameters[giantPicker.Key] == "modded_giant" && giantPicker.Validate("pseudogiant_named_dog") is not null,
                "Giant picker leaked other mutant classes.");
            var tushkano = loader.Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.tushkano_form");
            var tushkanoView = new ActionViewModel(tushkano);
            Assert(tushkano.DisplayName == "Превратиться в тушкана" && tushkano.CategoryId == "metamorphosis" &&
                tushkano.ParameterDefinitions.Count == 8 && tushkanoView.RegularParameters.Count() == 6 &&
                tushkanoView.AbilityKeyParameters.Select(p => p.Definition.Key).SequenceEqual(new[] { "melee_key", "camera_key" }) &&
                tushkano.Parameters["health_percent"] == "400" && tushkano.Parameters["attack_power_percent"] == "300" &&
                tushkano.Parameters["jump_height_percent"] == "250" && tushkano.Parameters["melee_key"] == "MOUSE_1" &&
                tushkano.Description.Split('\n').Length == 5 && tushkano.Description.Length < 350 &&
                CatalogRevisionEnforcement.RequiresCatalog(tushkano),
                "Tushkano must use the existing declarative editor with the requested defaults, no EXE changes.");
            var jumpHeight = tushkano.ParameterDefinitions.Single(d => d.Key == "jump_height_percent");
            Assert(jumpHeight.DefaultValue == "250" && jumpHeight.Minimum == 10 && jumpHeight.Maximum == 1000 &&
                jumpHeight.Validate("100") is null && jumpHeight.Validate("300") is null && jumpHeight.Validate("1000") is null &&
                jumpHeight.Validate("0") is not null && jumpHeight.Validate("1001") is not null,
                "Jump height must be an independently editable percentage with a 250% default.");
            var tushkanoPicker = tushkano.ParameterDefinitions.Single(d => d.Key == "mutant_sections");
            var tushkanoEntries = new[] {
                new CatalogEntry {SectionId="custom_rodent",Kind=CatalogEntryKind.Mutant,NativeClass="SM_TUSHK",DisplayName="Custom rodent",IsAvailable=true},
                new CatalogEntry {SectionId="tushkano_named_dog",Kind=CatalogEntryKind.Mutant,NativeClass="SM_DOG_S",DisplayName="Not a rodent",IsAvailable=true}
            };
            var tushkanoSnapshot = new CatalogSnapshot(new CatalogRevision("tushkano-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(tushkanoEntries), tushkanoEntries);
            DynamicCatalogBinding.Apply(tushkanoSnapshot, [tushkano]);
            Assert(tushkanoPicker.CatalogNativeClasses.SequenceEqual(new[] { "SM_TUSHK" }) &&
                tushkanoPicker.Options.SequenceEqual(new[] { "custom_rodent" }) &&
                tushkanoPicker.Validate("tushkano_named_dog") is not null,
                "Tushkano visual picker must be filtered by native class, not section naming.");
            var savedTushkano = tushkano.Clone();savedTushkano.Parameters["jump_height_percent"] = "500";
            savedTushkano.Parameters["health_percent"] = "9000";savedTushkano.Parameters["attack_power_percent"] = "250";
            new ActionManifestLoader().ApplyPresetsToInstances([savedTushkano], [tushkano]);
            Assert(savedTushkano.Parameters["jump_height_percent"] == "500" && savedTushkano.Parameters["health_percent"] == "9000" &&
                savedTushkano.Parameters["attack_power_percent"] == "250",
                "Refreshing templates must preserve the user's Tushkano characteristics.");
            var chimeraArchive = Path.Combine(Directory.GetParent(optionalRoot)!.FullName, "deferred-effects", "chimera-form", "CF-004");
            const string chimeraFile = "anomaly_donation_bridge_effect_hardcoon_adb_chimera_form.script";
            Assert(!shippedFiles.Any(path => Path.GetFileName(path) == chimeraFile) &&
                !optional.SourcePaths.ContainsKey("hardcoon.adb.chimera_form"), "Archived chimera leaked into the application.");
            File.Copy(Path.Combine(chimeraArchive, "gamedata", "scripts", chimeraFile), Path.Combine(gameScripts, chimeraFile));
            var chimera = new FilePresetLoader(chimeraArchive).Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.chimera_form");
            var chimeraView = new ActionViewModel(chimera);
            Assert(chimera.DisplayName == "Превратиться в химеру" && chimera.CategoryId == "metamorphosis" &&
                chimera.ParameterDefinitions.Count == 7 && chimeraView.RegularParameters.Count() == 4 &&
                chimeraView.AbilityKeyParameters.Select(p => p.Definition.Key).SequenceEqual(new[] { "attack_key", "high_arc_key", "boost_key" }) &&
                chimera.Parameters["health_percent"] == "300" && chimera.Parameters["attack_power_percent"] == "200" &&
                chimera.Parameters["attack_key"] == "MOUSE_1" && chimera.Parameters["high_arc_key"] == "MOUSE_2" && chimera.Parameters["boost_key"] == "X" && !chimera.Parameters.ContainsKey("melee_key") &&
                !chimera.Parameters.ContainsKey("leap_key") &&
                chimera.Description.Split('\n').Length == 5 && chimera.Description.Length < 350 &&
                CatalogRevisionEnforcement.RequiresCatalog(chimera),
                "Chimera must expose low LMB/high RMB pounce and X boost bindings, retain HP/attack defaults, and omit the separate melee action.");
            var chimeraPicker = chimera.ParameterDefinitions.Single(d => d.Key == "mutant_sections");
            var chimeraEntries = new[] {
                new CatalogEntry {SectionId="alien_cat_visual",Kind=CatalogEntryKind.Mutant,NativeClass="SM_CHIMS",DisplayName="Custom chimera",IsAvailable=true},
                new CatalogEntry {SectionId="chimera_named_burer",Kind=CatalogEntryKind.Mutant,NativeClass="SM_BURER",DisplayName="Not a chimera",IsAvailable=true}
            };
            var chimeraSnapshot = new CatalogSnapshot(new CatalogRevision("chimera-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(chimeraEntries), chimeraEntries);
            DynamicCatalogBinding.Apply(chimeraSnapshot, [chimera]);
            Assert(chimeraPicker.CatalogNativeClasses.SequenceEqual(new[] { "SM_CHIMS" }) &&
                chimeraPicker.Options.SequenceEqual(new[] { "alien_cat_visual" }) && chimeraPicker.Validate("chimera_named_burer") is not null,
                "Chimera visual picker leaked other mutant classes.");
            var savedChimera = chimera.Clone();savedChimera.Parameters["health_percent"] = "9000";
            savedChimera.Parameters["attack_power_percent"] = "350";savedChimera.Parameters["attack_key"] = "G";
            savedChimera.Parameters["boost_key"] = "H";
            new ActionManifestLoader().ApplyPresetsToInstances([savedChimera], [chimera]);
            Assert(savedChimera.Parameters["health_percent"] == "9000" && savedChimera.Parameters["attack_power_percent"] == "350" &&
                savedChimera.Parameters["attack_key"] == "G" && savedChimera.Parameters["boost_key"] == "H", "Chimera update overwrote user configuration.");
            var legacyChimera = chimera.Clone();legacyChimera.Parameters.Remove("attack_key");
            legacyChimera.Parameters["melee_key"] = "MOUSE_1";legacyChimera.Parameters["leap_key"] = "MOUSE_2";
            legacyChimera.Parameters["health_percent"] = "600";legacyChimera.Parameters.Remove("boost_key");
            new ActionManifestLoader().ApplyPresetsToInstances([legacyChimera], [chimera]);
            Assert(legacyChimera.Parameters["attack_key"] == "MOUSE_1" && legacyChimera.Parameters["boost_key"] == "X" && legacyChimera.Parameters["health_percent"] == "600" &&
                !legacyChimera.Parameters.ContainsKey("melee_key") && !legacyChimera.Parameters.ContainsKey("leap_key"),
                "Old chimera variants must receive the sole LMB binding without changing their health or retaining the old melee field.");
            File.Delete(Path.Combine(gameScripts, chimeraFile)); // isolated archive regression only, never shipped
            var poltergeist = loader.Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.poltergeist_form");
            var poltergeistView = new ActionViewModel(poltergeist);
            Assert(poltergeist.DisplayName == "Превратиться в полтергейста" && poltergeist.CategoryId == "metamorphosis" &&
                poltergeist.ParameterDefinitions.Count == 11 && poltergeistView.RegularParameters.Count() == 7 &&
                poltergeistView.AbilityKeyParameters.Select(p => p.Definition.Key).SequenceEqual(new[] { "anomaly_key", "tele_key", "cycle_key", "camera_key" }) &&
                poltergeist.Parameters["health_percent"] == "500" && poltergeist.Parameters["attack_power_percent"] == "200" &&
                poltergeist.Parameters["tele_power_percent"] == "200" && poltergeist.Parameters["anomaly_lifetime_seconds"] == "10" &&
                poltergeist.Parameters["anomaly_key"] == "MOUSE_1" && poltergeist.Parameters["tele_key"] == "MOUSE_2" &&
                poltergeist.Parameters["cycle_key"] == "R" && poltergeist.Description.Split('\n').Length == 5 &&
                poltergeist.Description.Length < 350 && poltergeist.Description.Contains("шар") && CatalogRevisionEnforcement.RequiresCatalog(poltergeist),
                "Poltergeist must load through the existing declarative form editor with configurable attack/lifetime/keys.");
            var poltergeistPicker = poltergeist.ParameterDefinitions.Single(d => d.Key == "mutant_sections");
            var poltergeistEntries = new[] {
                new CatalogEntry {SectionId="independent_visual",Kind=CatalogEntryKind.Mutant,NativeClass="SM_POLTR",DisplayName="Custom poltergeist",IsAvailable=true},
                new CatalogEntry {SectionId="poltergeist_named_burer",Kind=CatalogEntryKind.Mutant,NativeClass="SM_BURER",DisplayName="Not a poltergeist",IsAvailable=true}
            };
            DynamicCatalogBinding.Apply(new CatalogSnapshot(new CatalogRevision("polter-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(poltergeistEntries), poltergeistEntries), [poltergeist]);
            Assert(poltergeistPicker.CatalogNativeClasses.SequenceEqual(new[] { "SM_POLTR" }) &&
                poltergeistPicker.Options.SequenceEqual(new[] { "independent_visual" }) && poltergeistPicker.Validate("poltergeist_named_burer") is not null,
                "Poltergeist visual picker leaked unrelated mutants.");
            var savedPoltergeist = poltergeist.Clone();savedPoltergeist.Parameters["cycle_key"] = "H";
            savedPoltergeist.Parameters["anomaly_lifetime_seconds"] = "30";savedPoltergeist.Parameters["health_percent"] = "9000";
            savedPoltergeist.Parameters["tele_power_percent"] = "150";
            new ActionManifestLoader().ApplyPresetsToInstances([savedPoltergeist], [poltergeist]);
            Assert(savedPoltergeist.Parameters["cycle_key"] == "H" && savedPoltergeist.Parameters["anomaly_lifetime_seconds"] == "30" &&
                savedPoltergeist.Parameters["health_percent"] == "9000" && savedPoltergeist.Parameters["tele_power_percent"] == "150", "Poltergeist refresh overwrote user settings.");
            var missingPolterDefaults = poltergeist.Clone();missingPolterDefaults.Parameters.Remove("health_percent");missingPolterDefaults.Parameters.Remove("tele_power_percent");
            new ActionManifestLoader().ApplyPresetsToInstances([missingPolterDefaults], [poltergeist]);
            Assert(missingPolterDefaults.Parameters["health_percent"] == "500" && missingPolterDefaults.Parameters["tele_power_percent"] == "200",
                "New poltergeist variants did not receive updated health/throw defaults.");
            var savedBurer = burer.Clone();savedBurer.CategoryId = "player_state";savedBurer.Parameters["duration_seconds"] = "180";
            new ActionManifestLoader().ApplyPresetsToInstances([savedBurer], [burer]);
            Assert(savedBurer.CategoryId == "metamorphosis" && savedBurer.Parameters["duration_seconds"] == "180",
                "Existing burer instances must migrate categories without losing user parameters.");
            savedBurer.Parameters.Remove(attackPower.Key);
            new ActionManifestLoader().ApplyPresetsToInstances([savedBurer], [burer]);
            Assert(savedBurer.Parameters[attackPower.Key] == "200", "Existing variants must receive the new attack default.");
            savedBurer.Parameters[attackPower.Key] = "350";
            new ActionManifestLoader().ApplyPresetsToInstances([savedBurer], [burer]);
            Assert(savedBurer.Parameters[attackPower.Key] == "350", "Manifest update overwrote user attack strength.");
            var formHealth = burer.ParameterDefinitions.Single(definition => definition.Key == "health_percent");
            Assert(formHealth.DefaultValue == "300" && burer.Parameters[formHealth.Key] == "300" &&
                formHealth.Minimum == 10 && formHealth.Maximum == 99999 && formHealth.Validate("300") is null &&
                formHealth.Validate("0") is not null && formHealth.DisplayName == "Запас здоровья формы, %",
                "Burer independent form HP/default is missing.");
            foreach (var (key, initial) in new[] { ("wave_key", "MOUSE_1"), ("tele_key", "MOUSE_2"), ("melee_key", "F"), ("shield_key", "R") })
            {
                var binding = burer.ParameterDefinitions.Single(d => d.Key == key);
                Assert(binding.Kind == ParameterKind.GameInputKey && binding.DefaultValue == initial &&
                    burer.Parameters[key] == initial && binding.Validate(initial) is null &&
                    binding.Validate("MOUSE_5") is null && binding.Validate("F5") is not null &&
                    binding.Validate("Ctrl+F") is not null && binding.Clone().Kind == ParameterKind.GameInputKey,
                    "Burer ability binding/default invalid: " + key);
            }
            var burerEntries = new[] {
                new CatalogEntry {SectionId="custom_model_x",Kind=CatalogEntryKind.Mutant,NativeClass="SM_BURER",Category="modded_species",DisplayName="Custom burer",IsAvailable=true},
                new CatalogEntry {SectionId="burer_named_dog",Kind=CatalogEntryKind.Mutant,NativeClass="SM_DOG_S",Category="burer",DisplayName="Not a burer",IsAvailable=true},
                new CatalogEntry {SectionId="quarantined_burer",Kind=CatalogEntryKind.Mutant,NativeClass="SM_BURER",DisplayName="Excluded",IsAvailable=false,QuarantineReason=QuarantineReason.Blacklisted}
            };
            var burerSnapshot = new CatalogSnapshot(new CatalogRevision("burer-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(burerEntries), burerEntries);
            DynamicCatalogBinding.Apply(burerSnapshot, [burer]);
            Assert(appearances.Options.SequenceEqual(new[] { "custom_model_x" }) &&
                burer.Parameters[appearances.Key] == "custom_model_x" && burer.CatalogRevision == burerSnapshot.Revision,
                "Appearance filtering must use the native class even when a mod changes species and section names.");
            var appearancePicker = new ParameterValueViewModel(burer, appearances, () => { },
                new CatalogRuntimeState(CapabilitySet.Empty, burerSnapshot, false, "Test"));
            Assert(appearancePicker.CatalogOptions.Select(option => option.SectionId)
                    .OrderBy(id => id).SequenceEqual(new[] { "custom_model_x", "quarantined_burer" }),
                "The actual UI picker must exclude non-burer native classes, not merely validate selections later.");
            Assert(appearancePicker.CatalogOptionsView.Cast<CatalogOptionViewModel>().All(option => option.SectionId != "burer_named_dog"),
                "Native-class filter must also apply to visible UI options.");
            var heli = loader.Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.helicopter_boss");
            Assert(heli.DisplayName == "Босс-вертолёт" && heli.ParameterDefinitions.Count == 5,
                "Helicopter template missing or has unexpected parameters.");
            foreach (var (key, initial, min, max) in new[] {
                ("health_percent", "50", 10, 99999), ("arrival_seconds", "10", 0, 60000),
                ("reward_money", "10000", 0, 1000000) })
            {
                var definition = heli.ParameterDefinitions.Single(p => p.Key == key);
                Assert(heli.Parameters[key] == initial && definition.DefaultValue == initial &&
                    definition.Minimum == min && definition.Maximum == max, "Invalid heli parameter: " + key);
            }
            Assert(heli.Parameters["use_rockets"] == "false" &&
                heli.ParameterDefinitions.Single(p => p.Key == "use_rockets").Options.SequenceEqual(new[] { "false", "true" }) &&
                !heli.Description.Contains("Установка:") && CatalogRevisionEnforcement.RequiresCatalog(heli),
                "Helicopter must default to gun-only, omit one-off install notes, and use the live model catalog.");
            var models = heli.ParameterDefinitions.Single(p => p.Key == "helicopter_sections");
            Assert(models.Kind == ParameterKind.MultiChoice && models.CatalogKind == CatalogEntryKind.Helicopter &&
                models.MinimumSelections == 1, "Helicopter model choice must be a real dynamic multi-selection.");
            var heliEntries = new[] {
                new CatalogEntry { SectionId="mod_heli_a",Kind=CatalogEntryKind.Helicopter,DisplayName="Custom A",NativeClass="C_HLCP_S",IsAvailable=true },
                new CatalogEntry { SectionId="mod_heli_b",Kind=CatalogEntryKind.Helicopter,DisplayName="Custom B",NativeClass="C_HLCP_S",IsAvailable=true },
                new CatalogEntry { SectionId="bad_heli",Kind=CatalogEntryKind.Helicopter,DisplayName="Excluded",IsAvailable=false,QuarantineReason=QuarantineReason.Blacklisted }
            };
            var heliSnapshot = new CatalogSnapshot(new CatalogRevision("heli-models"),
                AnomalyDonationBridge.GameIpc.CatalogContentHash.Compute(heliEntries), heliEntries);
            DynamicCatalogBinding.Apply(heliSnapshot, [heli]);
            Assert(models.Options.SequenceEqual(new[] { "mod_heli_a", "mod_heli_b" }) &&
                models.Validate("mod_heli_a,mod_heli_b") is null && models.Validate("bad_heli") is not null,
                "Live helicopter choices must include multiple sections and exclude quarantined models.");
            heli.Parameters[models.Key]="mod_heli_b";
            DynamicCatalogBinding.Apply(heliSnapshot,[heli]);
            Assert(heli.Parameters[models.Key]=="mod_heli_b", "Refreshing the catalog replaced a selected helicopter model.");
            Assert(DonationDispatcher.UsesDonorName(heli.HandlerId) &&
                BridgeRuntime.ManualRetryDonor(new EventHistoryEntry { Donor = "OriginalDonor" }, heli.HandlerId) == "OriginalDonor",
                "Helicopter dispatch/manual repeat lost donor identity.");
            var support = loader.Load(gameRoot).Presets.Single(preset => preset.HandlerId == "hardcoon.adb.helicopter_companion");
            Assert(support.DisplayName == "Вертолёт-напарник" && support.ParameterDefinitions.Count == 5 &&
                CatalogRevisionEnforcement.RequiresCatalog(support), "Support helicopter/catalog contract missing.");
            var patrol = support.ParameterDefinitions.Single(p => p.Key == "duration_seconds");
            var follow = support.ParameterDefinitions.Single(p => p.Key == "follow_distance");
            Assert(patrol.Minimum == 10 && patrol.Maximum == 99999999 && patrol.Validate("99999999") is null &&
                patrol.Validate("9") is not null && patrol.Validate("100000000") is not null &&
                follow.DisplayName == "Расстояние следования за игроком, м" && follow.DefaultValue == "100",
                "Support helicopter duration/following bounds are inconsistent.");
            foreach (var (key, initial) in new[] { ("use_rockets", "false"), ("engine_sound", "true") })
            {
                var p = support.ParameterDefinitions.Single(definition => definition.Key == key);
                Assert(p.Kind == ParameterKind.Choice && p.Options.SequenceEqual(new[] { "false", "true" }) &&
                    p.DefaultValue == initial && support.Parameters[key] == initial &&
                    new ParameterValueViewModel(support, p, () => { }).IsBooleanChoice,
                    "Helicopter switch must render as a checkbox with the agreed default: " + key);
            }
            DynamicCatalogBinding.Apply(heliSnapshot, [support]);
            var supportModels = support.ParameterDefinitions.Single(p => p.Key == "helicopter_sections");
            Assert(supportModels.Options.SequenceEqual(models.Options) && supportModels.Validate("bad_heli") is not null,
                "Support helicopter must use the same scanned/quarantined live models as the boss.");
            var btr = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == BtrPassengerCommands.Handler);
            var btrModels = btr.ParameterDefinitions.Single(p => p.Key == "vehicle_sections");
            var btrTime = btr.ParameterDefinitions.Single(p => p.Key == "duration_seconds");
            var btrFollow = btr.ParameterDefinitions.Single(p => p.Key == "follow_distance");
            var btrExtend = btr.ParameterDefinitions.Single(p => p.Key == "extend_current");
            var btrDrive = btr.ParameterDefinitions.Single(p => p.Key == "drive_key");
            Assert(CatalogRevisionEnforcement.RequiresCatalog(btr) && btrModels.Kind == ParameterKind.MultiChoice &&
                btrModels.CatalogKind == CatalogEntryKind.ArmedVehicle && btrModels.MinimumSelections == 1,
                "BTR model selection must use the scanned armed vehicle catalog.");
            Assert(btrTime.Minimum == 10 && btrTime.Maximum == 99999999 && btrTime.DefaultValue == "60" &&
                btrFollow.Minimum == 5 && btrFollow.Maximum == 100 && btrFollow.DefaultValue == "20" &&
                btrExtend.DefaultValue == "true" && new ParameterValueViewModel(btr, btrExtend, () => { }).IsBooleanChoice,
                "BTR timing, following range and extension defaults are inconsistent.");
            Assert(btrDrive.Kind == ParameterKind.KeyboardKey && btrDrive.DefaultValue == "RCTRL" &&
                btrDrive.Validate("RCTRL") is null && btrDrive.Validate("HOME") is null &&
                btrDrive.Validate("F9") is not null && btrDrive.Validate("LCTRL") is not null,
                "The BTR must accept its default right Ctrl and assignable single keys.");
            var equipmentAmmo = loader.Load(gameRoot).Presets.Single(p => p.HandlerId == "hardcoon.adb.equipment_ammo");
            Assert(equipmentAmmo.DisplayName == "Выдать патроны к снаряжению" && equipmentAmmo.ParameterDefinitions.Count == 4,
                "Equipment ammo template must contain two slots, active-weapon mode and pack count.");
            foreach (var (key, initial) in new[] { ("primary", "true"), ("secondary", "true"), ("hands_only", "false") })
            {
                var definition = equipmentAmmo.ParameterDefinitions.Single(p => p.Key == key);
                Assert(definition.DefaultValue == initial && equipmentAmmo.Parameters[key] == initial &&
                    new ParameterValueViewModel(equipmentAmmo, definition, () => { }).IsBooleanChoice,
                    "Equipment ammo slot/mode must be a checkbox with the agreed default: " + key);
            }
            var packs = equipmentAmmo.ParameterDefinitions.Single(p => p.Key == "ammo_packs");
            Assert(packs.Minimum == 1 && packs.Maximum == 50 && packs.DefaultValue == "1" &&
                packs.Validate("50") is null && packs.Validate("0") is not null && packs.Validate("51") is not null,
                "Equipment ammo pack bounds/default are inconsistent.");
            Assert(!CatalogRevisionEnforcement.RequiresCatalog(equipmentAmmo),
                "Ammo must be discovered in the live weapon, without a client-selected ammunition list.");
            var cleanup = loader.Load(gameRoot).Presets.Single(preset =>
                preset.HandlerId == "hardcoon.adb.cleanup_corpses");
            var radius = cleanup.ParameterDefinitions.Single(definition => definition.Key == "radius_meters");
            var mutants = cleanup.ParameterDefinitions.Single(definition => definition.Key == "delete_mutant_corpses");
            Assert(cleanup.Parameters["radius_meters"] == "100" && radius.DefaultValue == "100" &&
                radius.Minimum == 1 && radius.Maximum == 999 &&
                cleanup.Parameters["delete_mutant_corpses"] == "false" && mutants.DefaultValue == "false" &&
                mutants.Options.SequenceEqual(new[] { "false", "true" }),
                "Corpse cleanup must default to 100m and leave the mutant checkbox disabled.");
            Assert(cleanup.Description.Contains("Может помочь с вылетами, если рядом много трупов.", StringComparison.Ordinal) &&
                !cleanup.Description.Contains("Не гарантирует", StringComparison.OrdinalIgnoreCase),
                "Corpse cleanup must use the updated crash-help description.");
            var thief = loader.Load(gameRoot).Presets.Single(preset =>
                preset.HandlerId == "hardcoon.adb.donor_thief");
            var fan = loader.Load(gameRoot).Presets.Single(preset =>
                preset.HandlerId == "hardcoon.adb.annoying_fan");
            var closeDialogs = fan.ParameterDefinitions.Single(definition => definition.Key == "close_stock_dialogs");
            Assert(fan.Parameters["close_stock_dialogs"] == "false" && closeDialogs.DefaultValue == "false" &&
                closeDialogs.Options.SequenceEqual(new[] { "false", "true" }) &&
                closeDialogs.DisplayName == "Безопасно закрывать штатные диалоги перед обращением",
                "The fan's safe stock-dialog closing checkbox must be visible and disabled by default.");
            var sprint = thief.ParameterDefinitions.Single(definition => definition.Key == "sprint_meters");
            var waitForUi = thief.ParameterDefinitions.Single(definition => definition.Key == "wait_for_ui_close");
            Assert(waitForUi.DisplayName == "Откладывать срабатывание пока открыты диалоговые окна" &&
                waitForUi.Options.SequenceEqual(new[] { "false", "true" }) &&
                waitForUi.DefaultValue == "true" && thief.Parameters["wait_for_ui_close"] == "true",
                "The thief must expose UI waiting as a checkbox enabled by default.");
            var oldThief = thief.Clone();
            oldThief.ParameterDefinitions.RemoveAll(definition => definition.Key == "wait_for_ui_close");
            oldThief.Parameters.Remove("wait_for_ui_close");
            oldThief.Parameters["item_count"] = "7";
            new ActionManifestLoader().ApplyPresetsToInstances([oldThief], [thief]);
            Assert(oldThief.Parameters["wait_for_ui_close"] == "true" && oldThief.Parameters["item_count"] == "7",
                "An existing thief must receive the default UI guard without resetting its settings.");
            var thiefSource = new ActionViewModel(oldThief);
            var thiefEditor = new ActionEditorViewModel(thiefSource, draft =>
            {
                thiefSource.ApplyEditorDraft(draft);
                return Task.CompletedTask;
            });
            var uiToggle = thiefEditor.Draft.Parameters.Single(parameter => parameter.Definition.Key == "wait_for_ui_close");
            Assert(uiToggle.IsBooleanChoice && uiToggle.BooleanValue,
                "The thief's UI guard must render as a checked checkbox.");
            uiToggle.BooleanValue = false;
            Assert(await thiefEditor.SaveAsync(), "Cannot save thief UI guard: " + thiefEditor.Error);
            var savedThief = JsonSerializer.Deserialize<ActionDefinition>(
                JsonSerializer.Serialize(thiefSource.Model, JsonDefaults.Options), JsonDefaults.Options)!;
            new ActionManifestLoader().ApplyPresetsToInstances([savedThief], [thief]);
            Assert(savedThief.Parameters["wait_for_ui_close"] == "false" && savedThief.Parameters["item_count"] == "7",
                "Disabling the thief's UI guard must survive saving and schema refresh.");
            Assert(thief.Parameters["item_count"] == "99" &&
                thief.Parameters["sprint_meters"] == "200" && sprint.DefaultValue == "200" &&
                sprint.Minimum == 1 && sprint.Maximum == 10000 &&
                !thief.Description.Contains("бессмерт", StringComparison.OrdinalIgnoreCase),
                "The shipped thief has wrong defaults or promises immortality.");
            Assert((await manager.InspectAsync(gameRoot)).CanInstall == false,
                "Verified installation still asks to copy scripts.");

            await File.AppendAllTextAsync(Path.Combine(effectsScripts, first),
                "\n-- developer effect update\n");
            var updateStatus = await manager.InspectAsync(gameRoot);
            Assert(updateStatus.CanInstall && updateStatus.InstalledCount == shippedFiles.Count - 1,
                "An updated effect script did not re-enable the unified installation button.");
            await manager.PreflightAsync(gameRoot);
            await manager.InstallAllAsync(gameRoot);
            Assert(await File.ReadAllTextAsync(Path.Combine(gameScripts, first)) ==
                await File.ReadAllTextAsync(Path.Combine(effectsScripts, first)) &&
                !(await manager.InspectAsync(gameRoot)).CanInstall,
                "A managed effect update was not copied or remained pending afterward.");

            await File.AppendAllTextAsync(Path.Combine(gameScripts, first), "\n-- user-owned change\n");
            var changedHash = await File.ReadAllTextAsync(Path.Combine(gameScripts, first));
            try
            {
                await manager.PreflightAsync(gameRoot);
                throw new InvalidOperationException("Preflight accepted a user-modified effect script.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("чужой файл", StringComparison.Ordinal))
            {
                Assert(await File.ReadAllTextAsync(Path.Combine(gameScripts, first)) == changedHash,
                    "Preflight overwrote a user-modified effect script.");
            }
            File.Delete(Path.Combine(gameScripts, first));

            var core = new AddonPackageManager(new AppPaths(Path.Combine(root, "core-data")),
                Path.Combine(AppContext.BaseDirectory, "addon-package"));
            var coreBefore = await core.InspectAsync(gameRoot);
            Assert(coreBefore.State == AddonInstallState.NotInstalled,
                $"Effect scripts must not block initial core installation: {coreBefore.State}: {coreBefore.Message}");
            Assert((await core.InstallOrUpdateAsync(gameRoot)).State == AddonInstallState.Current,
                "Core installation failed alongside separately installed effects.");

            var unifiedGameRoot = Path.Combine(root, "unified-game");
            Directory.CreateDirectory(Path.Combine(unifiedGameRoot, "bin"));
            await File.WriteAllTextAsync(Path.Combine(unifiedGameRoot, "fsgame.ltx"),
                "$game_data$=true|true|$fs_root$|gamedata\\");
            await File.WriteAllTextAsync(Path.Combine(unifiedGameRoot, "bin", "AnomalyDX11.exe"),
                "test engine marker");
            await using (var runtime = new BridgeRuntime(new AppPaths(Path.Combine(root, "unified-data"))))
            {
                var settings = new SettingsViewModel(runtime) { GamePath = unifiedGameRoot };
                await settings.RefreshAddonStatusAsync();
                await settings.RefreshEffectsStatusAsync();
                Assert(settings.CanInstallBridge,
                    "The unified button is disabled when core and effect scripts are missing.");
                await settings.InstallBridgeAsync();
                Assert((await runtime.InspectAddonAsync(unifiedGameRoot)).State == AddonInstallState.Current &&
                    (await runtime.InspectEffectsAsync(unifiedGameRoot, string.Empty)).InstalledCount == 70 &&
                    !settings.CanInstallBridge,
                    "The unified operation did not install both core and all 70 effect scripts.");
                Assert(!(await runtime.InspectBridgeAsync(unifiedGameRoot, string.Empty)).CanInstall,
                    "Header update button must be hidden for a verified current installation.");
                var effectTarget = Path.Combine(unifiedGameRoot, "gamedata", "scripts", first);
                File.Delete(effectTarget);
                Assert((await runtime.InspectBridgeAsync(unifiedGameRoot, string.Empty)).CanInstall,
                    "Missing effect did not reveal the header update shortcut.");
                var armed = runtime.Settings.Armed;
                var repaired = await runtime.InstallBridgeAsync(unifiedGameRoot, string.Empty);
                Assert(File.Exists(effectTarget) && !repaired.CanInstall && runtime.Settings.Armed == armed,
                    "Shared header installer did not clear the pending repair or changed reception.");
                var mo2Root = Path.Combine(root, "mo2-target");
                Directory.CreateDirectory(mo2Root);
                Assert((await runtime.InspectBridgeAsync(unifiedGameRoot, mo2Root)).CanInstall,
                    "Header ignored the configured MO2 target.");
                var mo2Installed = await runtime.InstallBridgeAsync(unifiedGameRoot, mo2Root);
                Assert(File.Exists(Path.Combine(mo2Root, "gamedata", "scripts", first)) && !mo2Installed.CanInstall,
                    "Header did not install effects into the configured MO2 target.");
                try
                {
                    await runtime.InspectBridgeAsync(Path.Combine(root, "not-a-game"), string.Empty);
                    throw new Exception("Invalid game path was accepted by the header checker.");
                }
                catch (InvalidOperationException) { }
            }

            foreach (var state in Enum.GetValues<AddonInstallState>())
            foreach (var effectsPending in new[] { false, true })
            {
                var status = new BridgeInstallationStatus(new AddonInstallationStatus(state, "core"),
                    new EffectScriptInstallationStatus(54, effectsPending ? 53 : 54, effectsPending, "effects"));
                var expected = (state is AddonInstallState.Current or AddonInstallState.NotInstalled or AddonInstallState.UpdateAvailable) &&
                    (state != AddonInstallState.Current || effectsPending);
                Assert(status.CanInstall == expected, "Shared header/settings update decision is wrong: " + state);
            }

            File.Delete(Path.Combine(effectsScripts, first));
            Assert(loader.Load(gameRoot).Presets.Count == shippedFiles.Count - 1,
                "Deleting a source script did not remove its preset.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string FindOptionalRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "optional-effects");
                if (File.Exists(Path.Combine(current.FullName, "version.json")) &&
                    Directory.Exists(candidate)) return candidate;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Optional effect sources not found.");
    }
}
