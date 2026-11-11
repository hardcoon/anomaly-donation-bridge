using System.Globalization;
using System.IO.Pipes;
using System.Text;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.ProtocolTests;

internal static partial class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("CAPABILITIES grammar", TestCapabilitiesAsync),
            ("weighted voting snapshot grammar and 200 choices", TestVotingGameWireAsync),
            ("voting native-pipe presentation/query/cancel", TestVotingGamePipeAsync),
            ("transactional catalog and SHA-256", TestCatalogAssemblyAsync),
            ("command revision and 16 KiB bound", TestCommandRevisionAsync),
            ("extension command injects fresh revision", TestExtensionFreshRevisionAsync),
            ("extension command rejects stale revision", TestExtensionStaleRevisionAsync),
            ("reserved revision cannot bypass model", TestReservedRevisionParameterAsync),
            ("dynamic binding and 41 built-in templates", TestDynamicBindingAndPresetsAsync),
            ("selected random never falls back to the preset catalogue", TestRandomAllItemOptInAsync),
            ("preflight rejects before SENT", TestPreflightRejectionAsync),
            ("death/reload waits for fresh catalog without losing queued effect", TestCatalogReloadQueueAsync),
            ("named-pipe session", TestNamedPipeSessionAsync),
            ("heartbeat suspension preserves session", TestHeartbeatSuspensionAsync),
            ("stopped game loop blocks command delivery", TestStoppedGameLoopBlocksDeliveryAsync),
            ("unanswered command has a terminal watchdog", TestCommandResultWatchdogAsync),
            ("cached catalog identity", TestCachedCatalogIdentityAsync),
            ("large catalog pipe burst", TestLargeCatalogPipeBurstAsync),
            ("malformed UTF-8 reconnect", TestMalformedUtf8ReconnectAsync)
        };

        foreach (var test in tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
                return 1;
            }
        }

        Console.WriteLine($"PASS {tests.Length} protocol tests");
        return 0;
    }

    private static Task TestCapabilitiesAsync()
    {
        var line = string.Join('\t',
            "CAPABILITIES",
            PipeProtocol.PercentEncode("anomaly_1_5_3_vanilla"),
            PipeProtocol.PercentEncode("1.0.5.6"),
            PipeProtocol.PercentEncode("dynamic_catalog;thirst_adapter"),
            PipeProtocol.PercentEncode("add_radiation;spawn_mutants"));
        var message = IsType<CapabilitiesMessage>(PipeProtocol.ParseInbound(line));
        Equal(EngineProfile.Anomaly153Vanilla, message.Capabilities.Profile, "engine profile");
        Equal("1.0.5.6", message.Capabilities.EngineVersion, "engine version");
        True(message.Capabilities.SupportsCapability("dynamic_catalog"), "dynamic catalog capability");
        True(message.Capabilities.SupportsHandler("spawn_mutants"), "dynamic handler capability");
        Equal("PING\tprobe%20id", PipeProtocol.Ping("probe id"), "correlated ping encoding");
        Equal("probe id", IsType<PongMessage>(PipeProtocol.ParseInbound(
            "PONG\tprobe%20id")).ProbeId, "correlated pong decoding");
        Throws<InvalidDataException>(() => PipeProtocol.ParseInbound(
            "CAPABILITIES\tunknown\t1\tduplicate%3Bduplicate\tadd_radiation"),
            "duplicate capabilities rejected");
        return Task.CompletedTask;
    }

    private static Task TestCatalogAssemblyAsync()
    {
        var entries = TestEntries();
        var revision = new CatalogRevision("session-1:catalog-1");
        var hash = CatalogContentHash.Compute(entries);
        Equal(64, hash.Length, "SHA-256 length");
        True(hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "SHA-256 lowercase hex");
        Equal(
            "0\tm_dog\tmutant\tDog\tMutants\tlite\t\t1\tnone\n",
            CatalogContentHash.CanonicalEntryLine(0, entries[0]),
            "canonical catalog line");

        var assembler = new CatalogSnapshotAssembler();
        assembler.Begin(new CatalogBeginMessage(revision, entries.Count, hash));
        for (var index = 0; index < entries.Count; index++)
        {
            assembler.Add(new CatalogEntryMessage(revision, index, entries[index]));
        }
        var snapshot = assembler.End(new CatalogEndMessage(revision, entries.Count, hash));
        Equal(entries.Count, snapshot.Entries.Count, "snapshot count");
        True(snapshot.ContainsAvailable(CatalogEntryKind.Mutant, "m_dog"), "safe entry available");
        True(!snapshot.ContainsAvailable(CatalogEntryKind.Item, "quest_item"), "quarantined entry unavailable");
        var parsedLevel = IsType<CatalogEntryMessage>(PipeProtocol.ParseInbound(
            "CATALOG_ENTRY\tsession-1:catalog-1\t0\tadb_level_l02_garbage\tlevel\tGarbage\tlocations\tunclassified\tl02_garbage\t1\tnone"));
        Equal(CatalogEntryKind.Level, parsedLevel.Entry.Kind,
            "cross-level catalog kind survives IPC parsing");
        var parsedHeli = IsType<CatalogEntryMessage>(PipeProtocol.ParseInbound(
            "CATALOG_ENTRY\tsession-1:catalog-1\t0\tmod_heli_custom\thelicopter\tCustom rotor\thelicopters\tunclassified\t\t1\tnone\tC_HLCP_S"));
        Equal(CatalogEntryKind.Helicopter, parsedHeli.Entry.Kind, "helicopter catalog kind survives IPC parsing");
        Equal("C_HLCP_S", parsedHeli.Entry.NativeClass, "native helicopter type survives IPC parsing");
        var parsedVehicle = IsType<CatalogEntryMessage>(PipeProtocol.ParseInbound(
            "CATALOG_ENTRY\tsession-1:catalog-1\t0\tveh_btr\tarmed_vehicle\tBTR\tarmed_vehicles\tunclassified\t\t1\tnone\tSCRPTCAR"));
        Equal(CatalogEntryKind.ArmedVehicle, parsedVehicle.Entry.Kind, "armed vehicle catalog kind survives IPC parsing");
        var parsedVisual = IsType<CatalogEntryMessage>(PipeProtocol.ParseInbound(
            "CATALOG_ENTRY\tsession-1:catalog-1\t0\tadb_visual_test\tnpc_visual\tactors%5Cmodel.ogf\tactors\tunclassified\t\t1\tnone"));
        Equal(CatalogEntryKind.NpcVisual, parsedVisual.Entry.Kind, "human visual catalog kind survives IPC parsing");
        True(CatalogContentHash.CanonicalEntryLine(0, parsedVisual.Entry).Contains("\tnpc_visual\t"), "human visual kind participates in catalogue hash");
        True(CatalogContentHash.CanonicalEntryLine(0, parsedVehicle.Entry).Contains("\tarmed_vehicle\t"),
            "armed vehicle kind included in canonical catalog hash");
        var parsedGuitar = IsType<CatalogEntryMessage>(PipeProtocol.ParseInbound(
            "CATALOG_ENTRY\tsession-1:catalog-1\t0\tadb_guitar_custom\tguitar_music\tCustom song\tguitar_music\tunclassified\t\t1\tnone"));
        Equal(CatalogEntryKind.GuitarMusic,parsedGuitar.Entry.Kind,"guitar song kind survives IPC parsing");
        True(CatalogContentHash.CanonicalEntryLine(0,parsedGuitar.Entry).Contains("\tguitar_music\t"),"guitar catalog hash token");
        True(CatalogContentHash.CanonicalEntryLine(0, parsedHeli.Entry).Contains("\thelicopter\t"),
            "helicopter kind included in canonical catalog hash");

        assembler.Begin(new CatalogBeginMessage(revision, 1, CatalogContentHash.Compute(entries.Take(1))));
        Throws<InvalidDataException>(() => assembler.Add(new CatalogEntryMessage(revision, 1, entries[0])),
            "out-of-order entry rejected");
        assembler.Reset();

        assembler.Begin(new CatalogBeginMessage(revision, entries.Count, new string('0', 64)));
        for (var index = 0; index < entries.Count; index++)
        {
            assembler.Add(new CatalogEntryMessage(revision, index, entries[index]));
        }
        Throws<InvalidDataException>(
            () => assembler.End(new CatalogEndMessage(revision, entries.Count, new string('0', 64))),
            "content hash mismatch rejected");
        True(!assembler.IsReceiving, "failed END resets transaction");
        return Task.CompletedTask;
    }

    private static Task TestCommandRevisionAsync()
    {
        var snapshot = Snapshot("session-1:rev-1");
        var command = DynamicCommand(snapshot.Revision, "m_dog");
        Equal(CatalogCommandValidationResult.Valid, CatalogRevisionEnforcement.Validate(command, snapshot),
            "current revision accepted");
        True(CatalogRevisionEnforcement.Validate(command, null) is { IsValid: false, IsTransient: true },
            "missing live catalog is temporary, never a permanent rejection");

        var line = PipeProtocol.Command(command);
        var fields = line.Split('\t');
        Equal(7, fields.Length, "COMMAND remains seven fields");
        var parameters = PipeProtocol.ParseStructuredParameters(PipeProtocol.PercentDecode(fields[6]));
        Equal(snapshot.Revision.Value, parameters[CatalogRevisionEnforcement.RevisionParameterKey],
            "wire command carries catalog revision");
        command.DonorName = "Донатчик";
        command.ActionName = "Эффект";
        command.BatchIndex = 2;
        command.BatchSize = 3;
        command.SaveBeforeBatch = true;
        command.SaveAfterBatch = true;
        command.ShowGameEffectLog = false;
        command.ShowPdaEffectMessage = true;
        var contextualFields = PipeProtocol.Command(command).Split('\t');
        Equal(13, contextualFields.Length, "saving COMMAND carries context and retention");
        Equal("50", contextualFields[12], "default service save retention serialized");
        command.EffectSaveRetentionCount = 7;
        Equal("7", PipeProtocol.Command(command).Split('\t')[12], "custom retention serialized");
        command.EffectSaveRetentionCount = 0;
        Equal("0", PipeProtocol.Command(command).Split('\t')[12], "unlimited retention serialized");
        command.EffectSaveRetentionCount = -1;
        Throws<InvalidDataException>(() => PipeProtocol.Command(command), "invalid retention rejected");
        command.EffectSaveRetentionCount = 50;
        Equal("Донатчик", PipeProtocol.PercentDecode(contextualFields[7]), "donor survives wire encoding");
        Equal("Эффект", PipeProtocol.PercentDecode(contextualFields[8]), "action name survives wire encoding");
        Equal("2", contextualFields[9], "batch index serialized");
        Equal("3", contextualFields[10], "batch size serialized");
        Equal("15", contextualFields[11], "save, centered-message and PDA flags serialized");
        Equal("m_dog*any*1", parameters[SpawnGroupBundleCodec.ParameterKey],
            "wire command carries selected group bundle");

        command.CatalogRevision = new CatalogRevision("session-1:old");
        True(CatalogRevisionEnforcement.Validate(command, snapshot) is { IsValid: false, IsTransient: false }, "stale revision rejected");
        command.CatalogRevision = snapshot.Revision;
        command.Parameters[SpawnGroupBundleCodec.ParameterKey] = "quest_item*any*1";
        True(CatalogRevisionEnforcement.Validate(command, snapshot) is { IsValid: false, IsTransient: false }, "wrong/quarantined kind rejected");

        command.Parameters[SpawnGroupBundleCodec.ParameterKey] = "m_dog*lite*1";
        command.Parameters["oversized"] = new string('x', PipeProtocol.MaximumLineBytes);
        Throws<InvalidDataException>(() => PipeProtocol.Command(command), "oversized command rejected");
        Throws<InvalidDataException>(
            () => PipeProtocol.ParseInbound(new string('x', PipeProtocol.MaximumLineBytes)),
            "oversized inbound line rejected");
        Equal("GET_CATALOG", PipeProtocol.GetCatalog(), "catalog request grammar");
        Equal("GET_CATALOG_INFO", PipeProtocol.GetCatalogInfo(), "catalog identity request grammar");
        var catalogInfo = IsType<CatalogInfoMessage>(PipeProtocol.ParseInbound(
            $"CATALOG_INFO\tsession-2%3A1\t9\t{new string('a', 64)}"));
        Equal(new CatalogRevision("session-2:1"), catalogInfo.Revision, "catalog identity revision");
        Equal(9, catalogInfo.EntryCount, "catalog identity count");
        return Task.CompletedTask;
    }

    private static async Task TestExtensionFreshRevisionAsync()
    {
        var snapshot = Snapshot("session-1:extension-current");
        var command = ExtensionCommand(snapshot.Revision);
        var transport = new CatalogTransport(snapshot);
        var validation = transport.Prepare(command);
        True(validation.IsValid, "extension handler accepts the current modeled revision");
        await transport.SendAsync(command);
        Equal(command, Single(transport.Commands), "fresh extension command reaches the transport");

        var fields = PipeProtocol.Command(command).Split('\t');
        Equal(7, fields.Length, "extension COMMAND keeps the stable wire shape");
        var parameters = PipeProtocol.ParseStructuredParameters(PipeProtocol.PercentDecode(fields[6]));
        Equal(snapshot.Revision.Value, parameters[CatalogRevisionEnforcement.RevisionParameterKey],
            "extension COMMAND receives the reserved revision parameter");
        Equal("rain", parameters["weather"], "extension parameters are preserved");
    }

    private static Task TestExtensionStaleRevisionAsync()
    {
        var snapshot = Snapshot("session-1:extension-current");
        var command = ExtensionCommand(new CatalogRevision("session-1:extension-stale"));
        var validation = CatalogRevisionEnforcement.Validate(command, snapshot);
        True(!validation.IsValid && !validation.IsTransient, "extension handler rejects a stale modeled revision");
        True(validation.Reason.Contains("устаревшей", StringComparison.OrdinalIgnoreCase),
            "stale extension rejection explains the revision mismatch");
        return Task.CompletedTask;
    }

    private static Task TestReservedRevisionParameterAsync()
    {
        var snapshot = Snapshot("session-1:extension-current");
        var command = ExtensionCommand(null);
        command.Parameters[CatalogRevisionEnforcement.RevisionParameterKey] = snapshot.Revision.Value;

        var validation = CatalogRevisionEnforcement.Validate(command, snapshot);
        True(!validation.IsValid, "manual reserved parameter is rejected during preflight");
        True(validation.Reason.Contains("зарезервированным", StringComparison.OrdinalIgnoreCase),
            "reserved parameter rejection is explicit");
        Throws<InvalidDataException>(() => PipeProtocol.Command(command),
            "wire serializer also rejects a manual revision without the modeled property");
        return Task.CompletedTask;
    }

    private static Task TestDynamicBindingAndPresetsAsync()
    {
        var presets = DefaultActionCatalog.CreatePresets();
        Equal(41, presets.Count, "template count");
        Equal(41, presets.Select(preset => preset.HandlerId).Distinct(StringComparer.Ordinal).Count(),
            "handler IDs unique");
        True(presets.All(preset => preset.ScriptRelativePath.StartsWith(
                "gamedata\\scripts\\anomaly_donation_bridge_", StringComparison.Ordinal)),
            "all script paths are namespaced Anomaly paths");
        True(presets.All(preset => !preset.ScriptRelativePath.Contains("pf_donation", StringComparison.Ordinal)),
            "IX-Ray action paths removed");

        var snapshot = Snapshot("session-1:binding");
        DynamicCatalogBinding.Apply(snapshot, presets);
        var mutant = presets.Single(preset => preset.HandlerId == "spawn_mutants");
        Equal("m_dog", mutant.SpawnGroups.Single().VariantId,
            "first blank composite row gets the first safe entry");
        var selectedNpcSquads = presets.Single(preset => preset.HandlerId == "spawn_npc_squads");
        Equal("s_bandit", selectedNpcSquads.SpawnGroups.Single().VariantId,
            "selected squad bundle binds the first safe real squad");
        var selectedSquadCommand = new GameCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            DonationId = Guid.NewGuid().ToString("N"),
            ActionId = "spawn_npc_squads",
            AmountMinor = 100,
            Currency = "RUB",
            CatalogRevision = snapshot.Revision,
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                [SpawnGroupBundleCodec.ParameterKey] = "s_bandit*any*2",
                ["radius_meters"] = "25",
                ["force_hostile"] = "true"
            }
        };
        Equal(CatalogCommandValidationResult.Valid,
            CatalogRevisionEnforcement.Validate(selectedSquadCommand, snapshot),
            "exact NPC squad bundle passes live catalog preflight");
        Equal(snapshot.Revision, mutant.CatalogRevision, "binding stamps revision");
        var companion = presets.Single(preset => preset.HandlerId == "spawn_named_companion");
        companion.Parameters["random_npc"] = "true";
        companion.Parameters["section_id"] = string.Empty;
        True(!companion.Validate().Any(error => error.Contains("не выбрана секция", StringComparison.Ordinal)),
            "random companion still requires a manually selected NPC section");
        True(companion.Parameters["use_player_faction"] == "true",
            "named companion does not default to the player's faction");
        var mutantCompanion = presets.Single(preset => preset.HandlerId == "spawn_mutant_companion");
        Equal("Напарник-мутант (legacy)", mutantCompanion.DisplayName,
            "legacy mutant companion has the same title in the compiled fallback catalog");
        Equal("m_dog", mutantCompanion.Parameters["mutant_sections"],
            "mutant companion allowlist binds the current safe mutant catalog");
        True(!mutantCompanion.Parameters.ContainsKey("random_mutant") &&
            mutantCompanion.ParameterDefinitions.All(definition =>
                !string.Equals(definition.Key, "random_mutant", StringComparison.OrdinalIgnoreCase)),
            "mutant companion always chooses randomly without a redundant checkbox");
        True(DonationDispatcher.UsesDonorName(mutantCompanion.HandlerId),
            "mutant companion receives the real donor name");
        var donorBoss = presets.Single(preset => preset.HandlerId == "spawn_donor_boss");
        Equal(snapshot.Revision, donorBoss.CatalogRevision,
            "donor boss is bound to the live NPC catalog revision");
        True(DonationDispatcher.UsesDonorName(donorBoss.HandlerId),
            "donor boss receives the real donor name");
        True(DonationDispatcher.UsesDonorName("hardcoon.adb.donor_thief"),
            "donor thief receives the real donor name, including manual retries");
        var donorBossCommand = new GameCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            DonationId = Guid.NewGuid().ToString("N"),
            ActionId = donorBoss.HandlerId,
            AmountMinor = 100,
            Currency = "RUB",
            CatalogRevision = snapshot.Revision,
            Parameters = new(donorBoss.EffectiveParameters(), StringComparer.OrdinalIgnoreCase)
        };
        Equal(CatalogCommandValidationResult.Valid,
            CatalogRevisionEnforcement.Validate(donorBossCommand, snapshot),
            "donor boss accepts a live catalog containing a safe NPC");
        var squads = presets.Single(preset => preset.HandlerId == "spawn_random_hostile_squad");
        Equal("s_bandit,s_monolith", squads.Parameters["squad_sections"],
            "squad allowlist defaults to every safe current section");
        True(!squads.Parameters["squad_sections"].Contains("s_story", StringComparison.Ordinal),
            "quarantined squad not selectable");
        var factions = presets.Single(preset => preset.HandlerId == "change_player_faction");
        Equal("adb_faction_actor_bandit,adb_faction_actor_stalker",
            factions.Parameters["faction_communities"],
            "player faction checklist binds every current safe faction");
        var items = presets.Single(preset => preset.HandlerId == "spawn_items");
        var itemSections = items.ParameterDefinitions.Single(definition =>
            definition.Key == "section_id");
        True(itemSections.Options.Contains(DefaultActionCatalog.RandomWeaponSectionId,
                StringComparer.Ordinal),
            "live weapon catalog adds the random weapon choice");
        Equal("Случайное оружие",
            itemSections.OptionLabels[DefaultActionCatalog.RandomWeaponSectionId],
            "random weapon choice has a readable label");
        True(itemSections.Options.Contains(DefaultActionCatalog.RandomOutfitSectionId,
                StringComparer.Ordinal),
            "live outfit catalog adds the random outfit choice");
        Equal("Случайная броня",
            itemSections.OptionLabels[DefaultActionCatalog.RandomOutfitSectionId],
            "random outfit choice has a readable label");
        True(itemSections.Options.Contains(DefaultActionCatalog.RandomHelmetSectionId,
                StringComparer.Ordinal),
            "live helmet catalog adds the random helmet choice");
        Equal("Случайный шлем",
            itemSections.OptionLabels[DefaultActionCatalog.RandomHelmetSectionId],
            "random helmet choice has a readable label");
        True(itemSections.Options.Contains(DefaultActionCatalog.RandomArtifactSectionId,
                StringComparer.Ordinal),
            "live artifact catalog adds the random artifact choice");
        Equal("Случайный артефакт",
            itemSections.OptionLabels[DefaultActionCatalog.RandomArtifactSectionId],
            "random artifact choice has a readable label");
        var randomWeaponCommand = new GameCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            DonationId = Guid.NewGuid().ToString("N"),
            ActionId = "spawn_items",
            AmountMinor = 100,
            Currency = "RUB",
            CatalogRevision = snapshot.Revision,
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                [ItemBundleCodec.ParameterKey] = DefaultActionCatalog.RandomWeaponSectionId + "*1",
                ["weapon_options"] = "equip"
            }
        };
        Equal(CatalogCommandValidationResult.Valid,
            CatalogRevisionEnforcement.Validate(randomWeaponCommand, snapshot),
            "random weapon sentinel is accepted only against a live weapon catalog");
        randomWeaponCommand.Parameters[ItemBundleCodec.ParameterKey] =
            DefaultActionCatalog.RandomOutfitSectionId + "*1;" +
            DefaultActionCatalog.RandomHelmetSectionId + "*1;" +
            DefaultActionCatalog.RandomArtifactSectionId + "*1";
        Equal(CatalogCommandValidationResult.Valid,
            CatalogRevisionEnforcement.Validate(randomWeaponCommand, snapshot),
            "random outfit, helmet and artifact sentinels pass live catalog preflight");
        var anomaly = presets.Single(preset => preset.HandlerId == "spawn_anomaly");
        var anomalySections = anomaly.ParameterDefinitions.Single(definition =>
            definition.Key == "section_id");
        True(anomalySections.Options.Contains(DefaultActionCatalog.RandomAnomalySectionId,
                StringComparer.Ordinal),
            "live anomaly catalog adds the random anomaly choice");
        Equal("Спавнить один случайный тип аномалии",
            anomalySections.OptionLabels[DefaultActionCatalog.RandomAnomalySectionId],
            "random anomaly choice has a readable label");
        True(anomalySections.Options.Contains(DefaultActionCatalog.EachRandomAnomalySectionId,
                StringComparer.Ordinal),
            "live anomaly catalog adds the per-anomaly random choice");
        Equal("Каждая аномалия будет случайной",
            anomalySections.OptionLabels[DefaultActionCatalog.EachRandomAnomalySectionId],
            "per-anomaly random choice has a readable label");
        var anomalyCount = anomaly.ParameterDefinitions.Single(definition =>
            definition.Key == "count");
        Equal(1m, anomalyCount.Minimum, "anomaly ring count minimum");
        Equal(100m, anomalyCount.Maximum, "anomaly ring count maximum");
        var anomalyLifetime = anomaly.ParameterDefinitions.Single(definition =>
            definition.Key == "lifetime_seconds");
        Equal(60000m, anomalyLifetime.Maximum, "temporary anomaly lifetime maximum");
        True(anomaly.ParameterDefinitions.All(definition =>
                definition.Key != "randomization_options"),
            "per-anomaly randomization no longer uses a separate checkbox");
        var randomAnomalyCommand = new GameCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            DonationId = Guid.NewGuid().ToString("N"),
            ActionId = "spawn_anomaly",
            AmountMinor = 100,
            Currency = "RUB",
            CatalogRevision = snapshot.Revision,
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = DefaultActionCatalog.EachRandomAnomalySectionId,
                ["distance_meters"] = "20",
                ["count"] = "4",
                ["lifetime_seconds"] = "60"
            }
        };
        Equal(CatalogCommandValidationResult.Valid,
            CatalogRevisionEnforcement.Validate(randomAnomalyCommand, snapshot),
            "per-anomaly random sentinel is accepted against a live anomaly catalog");

        mutant.SpawnGroups.Single().VariantId = "removed_by_mod_change";
        DynamicCatalogBinding.Apply(snapshot, presets);
        Equal("removed_by_mod_change", mutant.SpawnGroups.Single().VariantId,
            "stale saved section preserved");
        True(mutant.Validate().Count > 0, "stale saved section invalidates action");

        var randomAll = presets.Single(preset => preset.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId);
        var included = randomAll.ParameterDefinitions.Single(definition =>
            definition.Key == DefaultActionCatalog.RandomAllSelectionParameterKey);
        Equal(0, included.Options.Count, "instance choices are bound by the current profile, not the game catalog");
        True(!included.Options.Contains("spawn_items", StringComparer.Ordinal),
            "item grant is not in default multiselect");

        var liveLevels = new CatalogSnapshot(
            new CatalogRevision("session-1:levels"), new string('d', 64),
            [
                new CatalogEntry
                {
                    SectionId = "adb_level_l02_garbage", Kind = CatalogEntryKind.Level,
                    DisplayName = "Свалка", Category = "locations",
                    Faction = "l02_garbage", IsAvailable = true
                }
            ]);
        var levelPreset = DefaultActionCatalog.CreatePresets()
            .Single(preset => preset.HandlerId == "teleport_random_level");
        DynamicCatalogBinding.Apply(liveLevels, [levelPreset]);
        var levelDefinition = levelPreset.ParameterDefinitions.Single(definition =>
            definition.Key == "level_sections");
        Equal(CatalogEntryKind.Level, levelDefinition.CatalogKind,
            "level multiselect binds to the live game-level catalog");
        Equal("adb_level_l02_garbage", levelPreset.Parameters["level_sections"],
            "current location checkbox is selected from the live catalog");
        var levelOptions = levelPreset.ParameterDefinitions.Single(definition =>
            definition.Key == "level_options");
        Equal(ParameterKind.MultiChoice, levelOptions.Kind,
            "visited-level restriction is exposed as a checkbox");
        Equal("Телепортировать только в посещённые локации",
            levelOptions.OptionLabels["visited_only"],
            "visited-level restriction is localized");
        var levelCommand = new GameCommand
        {
            CommandId = "level-cmd", DonationId = "level-donation",
            ActionId = "teleport_random_level", AmountMinor = 100,
            Currency = "RUB", CatalogRevision = liveLevels.Revision,
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["level_sections"] = "adb_level_l02_garbage",
                ["level_options"] = "visited_only"
            }
        };
        Equal(CatalogCommandValidationResult.Valid,
            CatalogRevisionEnforcement.Validate(levelCommand, liveLevels),
            "selected cross-level destination passes fresh-catalog preflight");
        levelCommand.Parameters["level_sections"] =
            "adb_level_excluded,adb_level_l02_garbage";
        Equal(CatalogCommandValidationResult.Valid,
            CatalogRevisionEnforcement.Validate(levelCommand, liveLevels),
            "excluded locations are filtered when another safe destination remains");
        levelCommand.Parameters["level_sections"] = "adb_level_unknown";
        True(!CatalogRevisionEnforcement.Validate(levelCommand, liveLevels).IsValid,
            "teleport is blocked when exclusions leave no safe destination");
        return Task.CompletedTask;
    }

    private static async Task TestRandomAllItemOptInAsync()
    {
        var snapshot = Snapshot("session-1:random-items");
        var presets = DefaultActionCatalog.CreatePresets();
        DynamicCatalogBinding.Apply(snapshot, presets);
        var randomAll = presets.Single(preset => preset.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
            .CloneAsNewInstance();
        randomAll.CooldownSeconds = 0;
        randomAll.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = string.Empty;
        var item = presets.Single(preset => preset.HandlerId == "spawn_items").Clone();
        item.CooldownSeconds = 0;
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };

        var rejectedHistory = new InMemoryHistorySink();
        var rejectedDispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), rejectedHistory)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        Equal(
            DispatchState.Rejected,
            await rejectedDispatcher.EnqueueActionAsync(
                Donation(100m), randomAll, policy, availableActions: [randomAll], availablePresets: [item]),
            "empty instance selection rejects");

        Equal(
            DispatchState.Rejected,
            await rejectedDispatcher.EnqueueActionAsync(
                Donation(101m), randomAll, policy, availableActions: [randomAll], availablePresets: [item]),
            "supplied presets do not replace missing selected instances");
        Equal(0, rejectedHistory.Entries.Count(entry => entry.State == DispatchState.Queued),
            "unselected preset is never queued");
    }

    private static async Task TestPreflightRejectionAsync()
    {
        var action = DefaultActionCatalog.CreatePresets().Single(preset => preset.HandlerId == "add_radiation")
            .CloneAsNewInstance();
        action.CooldownSeconds = 0;
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };
        Equal(DispatchState.Queued, await dispatcher.EnqueueActionAsync(Donation(1m), action, policy),
            "static action queued");
        var transport = new RejectingTransport();
        var emitted = await dispatcher.DrainAsync(transport, policy);
        Equal(0, transport.SendCount, "rejected preflight never touches transport send");
        Equal(DispatchState.Rejected, Single(emitted).State, "preflight failure is deterministic rejection");
        True(history.Entries.Any(entry => entry.State == DispatchState.Rejected),
            "preflight rejection recorded in history");
    }

    private static async Task TestCatalogReloadQueueAsync()
    {
        const string handler = "hardcoon.adb.mutant_companion_v2";
        var pipeName = $"{PipeProtocol.PipeName}.CatalogQueue.{Guid.NewGuid():N}";
        await using var server = new NamedPipeGameServer("test", pipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var snapshot = Snapshot("reload-queue:1");
        CatalogInfoMessage? info = null;
        server.CatalogInfoChanged += value => Volatile.Write(ref info, value);
        var result = new TaskCompletionSource<GameExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ResultReceived += value => result.TrySetResult(value);
        server.Start();

        async Task<NamedPipeClientStream> ConnectAsync()
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(token);
            return pipe;
        }
        async Task<string> ReadAsync(StreamReader reader, StreamWriter writer)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException("Catalog queue fixture closed.");
                if (line == "PING") { await writer.WriteLineAsync("PONG"); continue; }
                return line;
            }
        }
        async Task HandshakeAsync(StreamReader reader, StreamWriter writer)
        {
            await writer.WriteLineAsync("HELLO\t1\ttest\t1234");
            True((await ReadAsync(reader, writer)).StartsWith("WELCOME\t", StringComparison.Ordinal), "reload welcome");
            await writer.WriteLineAsync($"CAPABILITIES\tanomaly_1_5_3_vanilla\t1.5.3\tcorrelated_ping%3Bcatalog_identity\t{handler}");
            Equal("GET_CATALOG_INFO", await ReadAsync(reader, writer), "reload requests identity, not a forced scan");
            await writer.WriteLineAsync("STATUS\tready\tready");
            await WaitUntilAsync(() => server.IsGameReady, token);
        }
        async Task AdoptAsync(StreamWriter writer)
        {
            Volatile.Write(ref info, null);
            await writer.WriteLineAsync($"CATALOG_INFO\t{snapshot.Revision.Value}\t{snapshot.Entries.Count}\t{snapshot.ContentHash}");
            await WaitUntilAsync(() => Volatile.Read(ref info) is not null, token);
            True(server.TryAdoptCachedCatalog(snapshot, info!), "matching cached catalog becomes live without a rescan");
        }

        using var first = await ConnectAsync();
        using var firstReader = new StreamReader(first, new UTF8Encoding(false), false, leaveOpen: true);
        var firstWriter = new StreamWriter(first, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await HandshakeAsync(firstReader, firstWriter);
        await AdoptAsync(firstWriter);
        await WriteStatusAsync(firstWriter, "waiting", "actor_dead");

        var action = DefaultActionCatalog.CreatePresets().Single(preset => preset.HandlerId == "spawn_mutant_companion").CloneAsNewInstance();
        action.HandlerId = handler;
        action.CooldownSeconds = 0;
        DynamicCatalogBinding.Apply(snapshot, [action]);
        action.Parameters["mutant_sections"] = "m_dog";
        var donation = Donation(1m);
        donation.Username = "Донатер после загрузки";
        var ledger = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(ledger, history) { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy();
        Equal(DispatchState.Queued, await dispatcher.EnqueueActionAsync(donation, action, policy), "V2 queued while actor is dead");
        var commandId = Single(history.Entries).CommandId;
        await dispatcher.DrainAsync(server, policy, token);
        Equal(1, dispatcher.Count, "death keeps V2 queued");
        firstWriter.Dispose();
        firstReader.Dispose();
        first.Dispose();
        await WaitUntilAsync(() => !server.IsConnected, token);

        using var second = await ConnectAsync();
        using var reader = new StreamReader(second, new UTF8Encoding(false), false, leaveOpen: true);
        using var writer = new StreamWriter(second, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await HandshakeAsync(reader, writer);
        True(server.Catalog is null, "reconnected game has not restored its catalog yet");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Equal(0, (await dispatcher.DrainAsync(server, policy, token)).Count, "waiting for catalog is not a rejection or SENT");
            Equal(1, dispatcher.Count, "ready actor without live catalog must not consume queued V2");
        }
        Equal(DispatchState.Queued, (await ledger.FindCommandAsync(donation.Id, commandId))!.State, "catalog wait preserves queued ledger");
        Equal(1, history.Entries.Count, "catalog wait does not spam history");
        await AdoptAsync(writer);

        // Catalog can disappear after successful dispatcher preflight, while the
        // transport is waiting for its correlated readiness response.
        var racedDrain = dispatcher.DrainAsync(server, policy, token);
        var probe = await ReadAsync(reader, writer);
        True(probe.StartsWith("PING\t", StringComparison.Ordinal), "readiness probe before V2 send");
        await server.RequestCatalogAsync(token);
        Equal("GET_CATALOG", await ReadAsync(reader, writer), "explicit catalog refresh starts");
        await writer.WriteLineAsync("PONG\t" + probe[5..]);
        await racedDrain;
        Equal(1, dispatcher.Count, "catalog disappearance during probe restores unsent V2 to queue");
        Equal(0, server.AwaitingResultCount, "unsent V2 is not in flight");
        True(history.Entries.All(entry => entry.State == DispatchState.Queued), "catalog race is not uncertain or rejected");

        await writer.WriteLineAsync($"CATALOG_BEGIN\t{snapshot.Revision.Value}\t{snapshot.Entries.Count}\t{snapshot.ContentHash}");
        await WaitUntilAsync(() => server.IsCatalogSynchronizing, token);
        await dispatcher.DrainAsync(server, policy, token);
        Equal(1, dispatcher.Count, "partial catalog cannot consume queued V2");
        for (var index = 0; index < snapshot.Entries.Count; index++)
            await writer.WriteLineAsync(CatalogEntryWire(snapshot.Revision, index, snapshot.Entries[index]));
        await writer.WriteLineAsync($"CATALOG_END\t{snapshot.Revision.Value}\t{snapshot.Entries.Count}\t{snapshot.ContentHash}");
        await WaitUntilAsync(() => server.Catalog is not null && !server.IsCatalogSynchronizing, token);
        var drain = dispatcher.DrainAsync(server, policy, token);
        string command;
        while (true)
        {
            command = await ReadAsync(reader, writer);
            if (!command.StartsWith("PING\t", StringComparison.Ordinal)) break;
            await writer.WriteLineAsync("PONG\t" + command[5..]);
        }
        await drain;
        var fields = command.Split('\t');
        Equal("COMMAND", fields[0], "fresh catalog releases queued V2");
        Equal(commandId, fields[1], "reload retains original command ID");
        var parameters = PipeProtocol.ParseStructuredParameters(PipeProtocol.PercentDecode(fields[6]));
        Equal(donation.Username, parameters["donor_name"], "reload keeps donor name");
        Equal("m_dog", parameters["mutant_sections"], "reload keeps selected mutant");
        await writer.WriteLineAsync($"RESULT\t{commandId}\texecuted\tok");
        await dispatcher.CompleteAsync(await result.Task.WaitAsync(token), token);
        await dispatcher.DrainAsync(server, policy, token);
        Equal(0, dispatcher.Count, "success consumes queued V2 once");
        Equal(1, history.Entries.Count(entry => entry.State == DispatchState.Sent), "only one send reaches game");
        Equal(1, history.Entries.Count(entry => entry.State == DispatchState.Executed), "only one execution recorded");

        async Task WriteStatusAsync(StreamWriter target, string state, string reason)
        {
            await target.WriteLineAsync($"STATUS\t{state}\t{reason}");
            await WaitUntilAsync(() => !server.IsGameReady, token);
        }
    }

    private static async Task TestNamedPipeSessionAsync()
    {
        var pipeName = $"{PipeProtocol.PipeName}.ProtocolTests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        await using var server = new NamedPipeGameServer("1.0.0", pipeName);
        var catalogReceived = new TaskCompletionSource<CatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultReceived = new TaskCompletionSource<GameExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CatalogChanged += snapshot =>
        {
            if (snapshot is not null) catalogReceived.TrySetResult(snapshot);
        };
        server.ResultReceived += result => resultReceived.TrySetResult(result);
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t1234");
        True((await reader.ReadLineAsync(timeout.Token))!.StartsWith("WELCOME\t1\t", StringComparison.Ordinal),
            "server welcomes addon");
        var beforeCapabilities = server.Prepare(new GameCommand
        {
            DonationId = Guid.NewGuid().ToString("N"),
            ActionId = "add_radiation",
            AmountMinor = 100,
            Currency = "RUB"
        });
        True(!beforeCapabilities.IsValid && beforeCapabilities.IsTransient &&
            beforeCapabilities.Reason.Contains("не сообщила", StringComparison.OrdinalIgnoreCase),
            "command is rejected until capabilities are reported");

        await writer.WriteLineAsync(string.Join('\t',
            "CAPABILITIES",
            "anomaly_1_5_3_vanilla",
            "1.0.5.6",
            PipeProtocol.PercentEncode("dynamic_catalog;catalog_identity;correlated_ping"),
            PipeProtocol.PercentEncode("spawn_mutants;add_radiation")));
        Equal("GET_CATALOG_INFO", await reader.ReadLineAsync(timeout.Token),
            "server requests only the lightweight identity after capabilities");

        var entry = TestEntries()[0];
        var entries = new[] { entry };
        var revision = new CatalogRevision("pipe-session:1");
        var hash = CatalogContentHash.Compute(entries);
        await writer.WriteLineAsync($"CATALOG_INFO\t{revision.Value}\t1\t{hash}");
        await server.RequestCatalogAsync(timeout.Token);
        Equal("GET_CATALOG", await reader.ReadLineAsync(timeout.Token),
            "full catalog is requested only by an explicit refresh");
        await writer.WriteLineAsync($"CATALOG_BEGIN\t{revision.Value}\t1\t{hash}");
        await writer.WriteLineAsync(CatalogEntryWire(revision, 0, entry));
        await writer.WriteLineAsync($"CATALOG_END\t{revision.Value}\t1\t{hash}");
        var received = await catalogReceived.Task.WaitAsync(timeout.Token);
        Equal(revision, received.Revision, "server publishes only complete snapshot");
        Equal(EngineProfile.Anomaly153Vanilla, server.Capabilities.Profile, "server exposes profile");

        await writer.WriteLineAsync("STATUS\tready\tready");
        await WaitUntilAsync(() => server.IsGameReady, timeout.Token);
        var command = DynamicCommand(revision, "m_dog");
        var oversized = DynamicCommand(revision, "m_dog");
        oversized.Parameters["oversized"] = new string('x', PipeProtocol.MaximumLineBytes);
        var oversizedValidation = server.Prepare(oversized);
        True(!oversizedValidation.IsValid &&
            oversizedValidation.Reason.Contains("16 КиБ", StringComparison.Ordinal),
            "oversized command is rejected during preflight before SENT");

        var commandLine = await SendCommandWithReadinessProbeAsync(
            server, command, reader, writer, timeout.Token);
        True(commandLine is not null && commandLine.StartsWith("COMMAND\t", StringComparison.Ordinal),
            "dynamic command crosses pipe");
        var commandParameters = PipeProtocol.ParseStructuredParameters(
            PipeProtocol.PercentDecode(commandLine!.Split('\t')[6]));
        Equal(revision.Value, commandParameters[CatalogRevisionEnforcement.RevisionParameterKey],
            "pipe command includes catalog revision");

        await writer.WriteLineAsync($"RESULT\t{command.CommandId}\texecuted\tok");
        var result = await resultReceived.Task.WaitAsync(timeout.Token);
        Equal(GameResultStatus.Executed, result.Status, "pipe result delivered");

        await writer.WriteLineAsync("CAPABILITIES\tunknown\tunsupported\tnamed_pipe\t");
        await WaitUntilAsync(
            () => server.Capabilities.EngineVersion == "unsupported" &&
                server.Capabilities.SupportedHandlerIds.Count == 0,
            timeout.Token);
        var unsupported = new GameCommand
        {
            DonationId = Guid.NewGuid().ToString("N"),
            ActionId = "add_radiation",
            AmountMinor = 100,
            Currency = "RUB"
        };
        var unsupportedValidation = server.Prepare(unsupported);
        True(!unsupportedValidation.IsValid &&
            unsupportedValidation.Reason.Contains("не объявил", StringComparison.OrdinalIgnoreCase),
            "authoritative empty handler list rejects every action");
    }

    private static async Task TestHeartbeatSuspensionAsync()
    {
        var pipeName = $"{PipeProtocol.PipeName}.HeartbeatTests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        await using var server = new NamedPipeGameServer(
            "1.0.0",
            pipeName,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100));
        var suspended = new TaskCompletionSource<GameConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource<GameConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sawSuspension = false;
        server.ConnectionChanged += snapshot =>
        {
            if (snapshot.Connected && !snapshot.Ready && snapshot.Reason == "game_suspended")
            {
                sawSuspension = true;
                suspended.TrySetResult(snapshot);
            }
            else if (sawSuspension && snapshot.Connected && snapshot.Ready && snapshot.Reason == "ok")
            {
                resumed.TrySetResult(snapshot);
            }
        };
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(
            client, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t9123");
        True((await reader.ReadLineAsync(timeout.Token))?.StartsWith(
            "WELCOME\t1\t", StringComparison.Ordinal) == true,
            "server welcomes heartbeat test addon");
        await writer.WriteLineAsync(
            "CAPABILITIES\tanomaly_1_5_3_vanilla\t1.5.3\tnamed_pipe%3Bcorrelated_ping\tadd_radiation");
        await writer.WriteLineAsync("STATUS\tready\tok");
        await WaitUntilAsync(() => server.IsGameReady, timeout.Token);

        var paused = await suspended.Task.WaitAsync(timeout.Token);
        True(paused.Connected, "heartbeat silence preserves the pipe session");
        True(!paused.Ready && server.IsConnected && !server.IsGameReady,
            "heartbeat silence pauses dispatch without disconnecting");

        await writer.WriteLineAsync("PONG");
        var active = await resumed.Task.WaitAsync(timeout.Token);
        True(active.Connected && active.Ready && server.IsGameReady,
            "first inbound record resumes the existing session without HELLO");
    }

    private static async Task TestStoppedGameLoopBlocksDeliveryAsync()
    {
        var pipeName = $"{PipeProtocol.PipeName}.StoppedLoopTests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        await using var server = new NamedPipeGameServer(
            "1.0.0",
            pipeName,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100));
        var suspended = new TaskCompletionSource<GameConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConnectionChanged += snapshot =>
        {
            if (snapshot.Connected && !snapshot.Ready && snapshot.Reason == "game_suspended")
            {
                suspended.TrySetResult(snapshot);
            }
        };
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(
            client, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t9124");
        _ = await reader.ReadLineAsync(timeout.Token);
        await writer.WriteLineAsync(
            "CAPABILITIES\tanomaly_1_5_3_vanilla\t1.5.3\tnamed_pipe%3Bcorrelated_ping\tadd_radiation");
        await writer.WriteLineAsync("STATUS\tready\tok");
        await WaitUntilAsync(() => server.IsGameReady, timeout.Token);

        var stalledCommand = new GameCommand
        {
            CommandId = "stalled-command",
            DonationId = "stalled-donation",
            ActionId = "add_radiation",
            AmountMinor = 100,
            Currency = "RUB",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["percent"] = "25" }
        };
        var stalledSend = server.SendAsync(stalledCommand, timeout.Token);
        var probe = await reader.ReadLineAsync(timeout.Token);
        True(probe?.StartsWith("PING\t", StringComparison.Ordinal) == true,
            "transport probes the actual addon loop before sending");
        var rejected = false;
        try
        {
            await stalledSend;
        }
        catch (GameNotReadyBeforeSendException)
        {
            rejected = true;
        }
        await suspended.Task.WaitAsync(timeout.Token);
        True(rejected && server.AwaitingResultCount == 0,
            "a stopped game loop cannot consume a command from the queue");

        await writer.WriteLineAsync("STATUS\tready\tok");
        await WaitUntilAsync(() => server.IsGameReady, timeout.Token);
        var resumedCommand = new GameCommand
        {
            CommandId = "resumed-command",
            DonationId = "resumed-donation",
            ActionId = stalledCommand.ActionId,
            AmountMinor = stalledCommand.AmountMinor,
            Currency = stalledCommand.Currency,
            Parameters = new(stalledCommand.Parameters, StringComparer.OrdinalIgnoreCase)
        };
        var commandLine = await SendCommandWithReadinessProbeAsync(
            server, resumedCommand, reader, writer, timeout.Token);
        True(commandLine.StartsWith("COMMAND\tresumed-command", StringComparison.Ordinal),
            "a responsive addon loop accepts the command regardless of foreground window");
        await writer.WriteLineAsync("RESULT\tresumed-command\texecuted\tok");
    }

    private static async Task TestCommandResultWatchdogAsync()
    {
        var pipeName = $"{PipeProtocol.PipeName}.WatchdogTests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        await using var server = new NamedPipeGameServer(
            "1.0.0",
            pipeName,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(100));
        var result = new TaskCompletionSource<GameExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ResultReceived += value => result.TrySetResult(value);
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(
            client, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t9125");
        _ = await reader.ReadLineAsync(timeout.Token);
        await writer.WriteLineAsync(
            "CAPABILITIES\tanomaly_1_5_3_vanilla\t1.5.3\tnamed_pipe%3Bcorrelated_ping\tadd_radiation");
        await writer.WriteLineAsync("STATUS\tready\tok");
        await WaitUntilAsync(() => server.IsGameReady, timeout.Token);
        var watchdogCommand = new GameCommand
        {
            CommandId = "watchdog-command",
            DonationId = "watchdog-donation",
            ActionId = "add_radiation",
            AmountMinor = 100,
            Currency = "RUB",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["percent"] = "25" }
        };
        _ = await SendCommandWithReadinessProbeAsync(
            server, watchdogCommand, reader, writer, timeout.Token);

        var terminal = await result.Task.WaitAsync(timeout.Token);
        Equal(GameResultStatus.Uncertain, terminal.Status,
            "missing result becomes terminal instead of waiting forever");
        True(terminal.Reason.Contains("автоматический повтор запрещён", StringComparison.Ordinal),
            "watchdog preserves at-most-once semantics");
        Equal(0, server.AwaitingResultCount, "watchdog clears the in-flight command");
    }

    private static async Task TestCachedCatalogIdentityAsync()
    {
        var pipeName = $"{PipeProtocol.PipeName}.CatalogIdentityTests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        await using var server = new NamedPipeGameServer("1.0.6", pipeName);
        var identityReceived = new TaskCompletionSource<CatalogInfoMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogReceived = new TaskCompletionSource<CatalogSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.CatalogInfoChanged += value =>
        {
            if (value is not null) identityReceived.TrySetResult(value);
        };
        server.CatalogChanged += value =>
        {
            if (value is not null) catalogReceived.TrySetResult(value);
        };
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(
            client, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("HELLO\t1\t1.0.3\t2468");
        _ = await reader.ReadLineAsync(timeout.Token);
        await writer.WriteLineAsync(
            "CAPABILITIES\tanomaly_1_5_3_vanilla\t1.5.3\t" +
            "dynamic_catalog%3Bcatalog_identity\tspawn_mutants");
        Equal("GET_CATALOG_INFO", await reader.ReadLineAsync(timeout.Token),
            "identity-capable addon avoids an automatic full transfer");

        var entries = new[] { TestEntries()[0] };
        var liveRevision = new CatalogRevision("game-session:current");
        const string communities = "actor,255,1,0;arbitrary_mod_species,77,-1,0";
        var hash = CatalogContentHash.Compute(entries, communities);
        await writer.WriteLineAsync($"CATALOG_INFO\t{liveRevision.Value}\t1\t{hash}");
        var identity = await identityReceived.Task.WaitAsync(timeout.Token);
        var cached = new CatalogSnapshot(
            new CatalogRevision("cached-session:old"), hash, entries, DateTimeOffset.UtcNow.AddHours(-1), communities);
        True(server.TryAdoptCachedCatalog(cached, identity),
            "matching SHA-256 cache is promoted without downloading entries");
        var adopted = await catalogReceived.Task.WaitAsync(timeout.Token);
        Equal(liveRevision, adopted.Revision, "adopted cache receives the current game revision");
        Equal(hash, adopted.ContentHash, "adopted cache preserves verified content hash");
        Equal(communities, adopted.MonsterCommunities, "adopted cache preserves native species metadata");
        var alteredMetadata = new CatalogSnapshot(new CatalogRevision("altered-metadata"), hash, entries,
            monsterCommunities: communities.Replace("77", "78"));
        True(!server.TryAdoptCachedCatalog(alteredMetadata, identity), "metadata tampering cannot reuse the claimed content hash");

        var tampered = new CatalogSnapshot(
            new CatalogRevision("cached-session:tampered"),
            new string('b', 64),
            entries);
        True(!server.TryAdoptCachedCatalog(tampered, identity),
            "mismatched cache hash cannot become live");
    }

    private static async Task TestMalformedUtf8ReconnectAsync()
    {
        var pipeName = $"{PipeProtocol.PipeName}.Utf8Tests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        await using var server = new NamedPipeGameServer("1.0.0", pipeName);
        var protocolError = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ProtocolError += exception => protocolError.TrySetResult(exception);
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var malformed = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await malformed.ConnectAsync(timeout.Token);
            await malformed.WriteAsync(new byte[] { 0xFF, (byte)'\n' }, timeout.Token);
            await malformed.FlushAsync(timeout.Token);
            var error = await protocolError.Task.WaitAsync(timeout.Token);
            True(error is InvalidDataException &&
                error.Message.Contains("UTF-8", StringComparison.OrdinalIgnoreCase),
                "malformed raw UTF-8 is reported as a protocol error");
        }

        await using var recovered = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await recovered.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(
            recovered, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(
            recovered, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t5678");
        True((await reader.ReadLineAsync(timeout.Token))?.StartsWith(
            "WELCOME\t1\t", StringComparison.Ordinal) == true,
            "server accepts a fresh session after malformed UTF-8");
    }

    private static async Task TestLargeCatalogPipeBurstAsync()
    {
        var pipeName = $"{PipeProtocol.PipeName}.LargeCatalogTests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        await using var server = new NamedPipeGameServer("1.0.6", pipeName);
        var catalogReceived = new TaskCompletionSource<CatalogSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progressKnown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CatalogChanged += snapshot =>
        {
            if (snapshot is not null) catalogReceived.TrySetResult(snapshot);
        };
        server.CatalogSyncProgressChanged += progress =>
        {
            if (progress.IsSynchronizing && progress.TotalCount == 3600)
                progressKnown.TrySetResult(true);
            if (!progress.IsSynchronizing && progress.ReceivedCount == 3600)
                progressCompleted.TrySetResult(true);
        };
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(
            client, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(
            client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("HELLO\t1\t1.0.2\t4321");
        True((await reader.ReadLineAsync(timeout.Token))?.StartsWith(
            "WELCOME\t1\t", StringComparison.Ordinal) == true,
            "server welcomes large-catalog addon");
        await writer.WriteLineAsync(string.Join('\t',
            "CAPABILITIES",
            "anomaly_1_5_3_vanilla",
            "1.5.3",
            PipeProtocol.PercentEncode("dynamic_catalog"),
            PipeProtocol.PercentEncode("spawn_mutants")));
        await WaitUntilAsync(
            () => server.Capabilities.SupportsCapability("dynamic_catalog"), timeout.Token);
        await server.RequestCatalogAsync(timeout.Token);
        Equal("GET_CATALOG", await reader.ReadLineAsync(timeout.Token),
            "manual refresh requests the large catalog");

        const int entryCount = 3600;
        var entries = Enumerable.Range(0, entryCount)
            .Select(index => Entry(
                $"test_section_{index:D5}",
                CatalogEntryKind.Item,
                $"Тестовый предмет {index:D5}",
                "Тестовая категория",
                CatalogStrength.Unclassified))
            .ToArray();
        var revision = new CatalogRevision("large-catalog:1");
        var hash = CatalogContentHash.Compute(entries);
        var payload = new StringBuilder(entryCount * 180);
        payload.Append("CATALOG_BEGIN\t")
            .Append(revision.Value).Append('\t')
            .Append(entryCount.ToString(CultureInfo.InvariantCulture)).Append('\t')
            .Append(hash).Append('\n');
        for (var index = 0; index < entries.Length; index++)
        {
            payload.Append(CatalogEntryWire(revision, index, entries[index])).Append('\n');
        }
        payload.Append("CATALOG_END\t")
            .Append(revision.Value).Append('\t')
            .Append(entryCount.ToString(CultureInfo.InvariantCulture)).Append('\t')
            .Append(hash).Append('\n');

        var bytes = Encoding.UTF8.GetBytes(payload.ToString());
        await client.WriteAsync(bytes, timeout.Token);
        await client.FlushAsync(timeout.Token);
        var received = await catalogReceived.Task.WaitAsync(timeout.Token);
        await progressKnown.Task.WaitAsync(timeout.Token);
        await progressCompleted.Task.WaitAsync(timeout.Token);
        Equal(entryCount, received.Entries.Count,
            "server receives a vanilla-sized catalog without starving the pipe");
        Equal(revision, received.Revision, "large catalog revision");
    }

    private static List<CatalogEntry> TestEntries() =>
    [
        Entry("m_dog", CatalogEntryKind.Mutant, "Dog", "Mutants", CatalogStrength.Lite),
        Entry("npc_bandit", CatalogEntryKind.Npc, "Bandit", "Bandits", CatalogStrength.Normal, "bandit"),
        Entry("s_bandit", CatalogEntryKind.StalkerSquad, "Bandit squad", "Bandits", CatalogStrength.Lite, "bandit"),
        Entry("s_monolith", CatalogEntryKind.StalkerSquad, "Monolith squad", "Monolith", CatalogStrength.Strong, "monolith"),
        Entry("m_pack", CatalogEntryKind.MutantSquad, "Dog pack", "Mutants", CatalogStrength.Normal),
        Entry("medkit", CatalogEntryKind.Item, "Medkit", "Medicine", CatalogStrength.Unclassified),
        Entry("wpn_pm", CatalogEntryKind.Item, "PM", "weapons", CatalogStrength.Unclassified),
        Entry("novice_outfit", CatalogEntryKind.Item, "Novice suit", "outfits", CatalogStrength.Unclassified),
        Entry("helm_m40", CatalogEntryKind.Item, "M40", "helmets", CatalogStrength.Unclassified),
        Entry("af_fire", CatalogEntryKind.Item, "Fireball", "artefacts", CatalogStrength.Unclassified),
        Entry("zone_electric", CatalogEntryKind.Anomaly, "Electra", "Electric", CatalogStrength.Normal),
        Entry("adb_faction_actor_bandit", CatalogEntryKind.Faction, "Bandits", "Factions",
            CatalogStrength.Unclassified, "actor_bandit"),
        Entry("adb_faction_actor_stalker", CatalogEntryKind.Faction, "Loners", "Factions",
            CatalogStrength.Unclassified, "actor_stalker"),
        Entry("s_story", CatalogEntryKind.StalkerSquad, "Story squad", "Story", CatalogStrength.Strong,
            "stalker", false, QuarantineReason.StoryOrUnique),
        Entry("quest_item", CatalogEntryKind.Item, "Quest item", "Quest", CatalogStrength.Unclassified,
            string.Empty, false, QuarantineReason.StoryOrUnique)
    ];

    private static CatalogEntry Entry(
        string section,
        CatalogEntryKind kind,
        string name,
        string category,
        CatalogStrength strength,
        string faction = "",
        bool available = true,
        QuarantineReason reason = QuarantineReason.None) => new()
    {
        SectionId = section,
        Kind = kind,
        DisplayName = name,
        Category = category,
        Strength = strength,
        Faction = faction,
        IsAvailable = available,
        QuarantineReason = reason
    };

    private static CatalogSnapshot Snapshot(string revision)
    {
        var entries = TestEntries();
        return new CatalogSnapshot(new CatalogRevision(revision), CatalogContentHash.Compute(entries), entries);
    }

    private static GameCommand DynamicCommand(CatalogRevision revision, string section) => new()
    {
        CommandId = Guid.NewGuid().ToString("N"),
        DonationId = Guid.NewGuid().ToString("N"),
        ActionId = "spawn_mutants",
        AmountMinor = 100,
        Currency = "RUB",
        CatalogRevision = revision,
        Parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            [SpawnGroupBundleCodec.ParameterKey] = section + "*any*1"
        }
    };

    private static GameCommand ExtensionCommand(CatalogRevision? revision) => new()
    {
        CommandId = Guid.NewGuid().ToString("N"),
        DonationId = Guid.NewGuid().ToString("N"),
        ActionId = "extension_weather",
        AmountMinor = 100,
        Currency = "RUB",
        CatalogRevision = revision,
        Parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["weather"] = "rain"
        }
    };

    private static string CatalogEntryWire(CatalogRevision revision, int index, CatalogEntry entry)
    {
        static string Kind(CatalogEntryKind value) => value switch
        {
            CatalogEntryKind.Mutant => "mutant",
            CatalogEntryKind.Npc => "npc",
            CatalogEntryKind.StalkerSquad => "stalker_squad",
            CatalogEntryKind.MutantSquad => "mutant_squad",
            CatalogEntryKind.Item => "item",
            CatalogEntryKind.Anomaly => "anomaly",
            CatalogEntryKind.Faction => "faction",
            CatalogEntryKind.Level => "level",
            CatalogEntryKind.Helicopter => "helicopter",
            CatalogEntryKind.ArmedVehicle => "armed_vehicle",
            CatalogEntryKind.GuitarMusic => "guitar_music",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        static string Strength(CatalogStrength value) => value.ToString().ToLowerInvariant();
        static string Reason(QuarantineReason value) => value switch
        {
            QuarantineReason.None => "none",
            QuarantineReason.StoryOrUnique => "story_unique",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        return string.Join('\t',
            "CATALOG_ENTRY",
            PipeProtocol.PercentEncode(revision.Value),
            index.ToString(CultureInfo.InvariantCulture),
            PipeProtocol.PercentEncode(entry.SectionId),
            Kind(entry.Kind),
            PipeProtocol.PercentEncode(entry.DisplayName),
            PipeProtocol.PercentEncode(entry.Category),
            Strength(entry.Strength),
            PipeProtocol.PercentEncode(entry.Faction),
            entry.IsAvailable ? "1" : "0",
            Reason(entry.QuarantineReason));
    }

    private static DonationEvent Donation(decimal amount) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Username = "protocol-test",
        Amount = amount,
        Currency = "RUB",
        CreatedAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow
    };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
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

    private sealed class CatalogTransport(CatalogSnapshot snapshot) : IGameCommandTransport, IGameCommandPreflight
    {
        public bool IsConnected => true;
        public bool IsGameReady => true;
        public List<GameCommand> Commands { get; } = [];
        public CatalogCommandValidationResult Prepare(GameCommand command) =>
            CatalogRevisionEnforcement.Validate(command, snapshot);
        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingTransport : IGameCommandTransport, IGameCommandPreflight
    {
        public bool IsConnected => true;
        public bool IsGameReady => true;
        public int SendCount { get; private set; }
        public CatalogCommandValidationResult Prepare(GameCommand command) =>
            CatalogCommandValidationResult.Invalid("stale catalog test");
        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private static T IsType<T>(object value)
    {
        if (value is T typed) return typed;
        throw new InvalidOperationException($"Expected {typeof(T).Name}, actual {value.GetType().Name}");
    }

    private static T Single<T>(IEnumerable<T> values) => values.Single();
    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
