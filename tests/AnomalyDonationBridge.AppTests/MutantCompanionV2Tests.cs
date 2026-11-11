using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class MutantCompanionV2Tests
{
    private const string Handler = "hardcoon.adb.mutant_companion_v2";
    private const string Communities = "mod_beast,77,-2,0;actor,255,1,0;human,255,0,1;dog,43,-1,0";
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    internal static async Task RunAsync()
    {
        var definition = new ParameterDefinition { Key = "menu_key", DisplayName = "Menu", Kind = ParameterKind.KeyboardKey, DefaultValue = "HOME" };
        Check(definition.Validate("HOME") is null && definition.Validate("F6") is null, "Valid game key rejected.");
        foreach (var key in new[] { "F5", "F9", "ESCAPE", "LCONTROL", "Ctrl+Home", "199", "bad", "MOUSE_1" })
            Check(definition.Validate(key) is not null, "Unsafe/noncanonical game key accepted: " + key);
        Check(definition.Clone().Kind == ParameterKind.KeyboardKey, "Key type lost in clone.");
        Check(DonationDispatcher.UsesDonorName(Handler), "V2 omitted from donor-name dispatch/repeat.");
        Check(BridgeRuntime.ManualRetryDonor(new EventHistoryEntry { Donor = "Донатер" }, Handler) == "Донатер", "Manual repeat lost donor.");
        VerifyUiPackage();
        var effectText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "effects/gamedata/scripts/anomaly_donation_bridge_effect_hardcoon_adb_mutant_companion_v2.script"));
        Check(effectText.Contains("Множитель начального здоровья (100 = ×1, 300 = ×3)") &&
            !effectText.Contains("Запас здоровья"), "Initial health multiplier is still confused with current HP.");
        using var preset = JsonDocument.Parse(effectText.Split("ADB_PRESET_V1", 2)[1].Split("ADB_PRESET_END", 2)[0]);
        var parameters = preset.RootElement.GetProperty("parameters");
        var definitions = preset.RootElement.GetProperty("parameterDefinitions").EnumerateArray().ToArray();
        foreach (var (key, value) in new[] { ("health_percent", "300"), ("follow_distance", "15") })
            Check(parameters.GetProperty(key).GetString() == value &&
                definitions.Single(p => p.GetProperty("key").GetString() == key).GetProperty("defaultValue").GetString() == value,
                "V2 template defaults disagree with parameter definitions: " + key);

        var entries = new[] { Mutant("gamma_custom_giant", "SM_GIANT"), Mutant("psy_dog", "SM_DOG_P"),
            Mutant("mod_poltergeist", "SM_POLTR"), Mutant("unknown_mutant", "SM_UNK"),
            Mutant("mod_burer", "SM_BURER"), Mutant("old_pet_dog", "SM_DOG_S") with
                { IsAvailable=false,QuarantineReason=QuarantineReason.Blacklisted } };
        var snapshot = new CatalogSnapshot(new CatalogRevision("mc2-test"), CatalogContentHash.Compute(entries, Communities), entries,
            monsterCommunities: Communities);
        var generated = Encoding.UTF8.GetString(MutantCompanionVariants.Generate(snapshot));
        Check(generated.Contains($"[{MutantCompanionVariants.Alias(entries[0].SectionId)}]:gamma_custom_giant"), "Modded section not inherited.");
        Check(generated.Contains("radiation_max_power = 0") && generated.Contains("step_params = adb_mc2_no_steps"), "Aura/step isolation missing.");
        Check(generated.Contains("aura_effector = adb_mc2_presence_neutral") &&
            generated.Contains("detection_pp_effector_name =\n"), "Presence-only profiles missing.");
        Check(!generated.Contains("]:unknown_mutant"), "Unknown native class received unsafe alias.");
        Check(!generated.Contains("tele_max_distance") && generated.Contains("]:old_pet_dog"),
            "Burer attacks were disabled or savegame aliases removed.");
        VerifyAttackInheritance();
        VerifyLegacyCatalogMetadata(snapshot);
        VerifyNativeRelations(snapshot);
        Check(preset.RootElement.GetProperty("description").GetString()!.Contains("\n\nУстановка:") &&
            preset.RootElement.GetProperty("description").GetString()!.Contains("повторно обновить аддон"),
            "Template does not explain the two-stage installation.");
        Check(!effectText.Contains("[ADB] mutant V2 repath:") && !effectText.Contains("[ADB] mutant V2 manual refresh:") &&
            !effectText.Contains("[ADB] mutant V2 in-place physics reset:") && effectText.Contains("[ADB] mutant V2 registration:") &&
            effectText.Contains("[ADB] mutant V2 menu:"), "Routine V2 traces remain or real error reporting was removed.");
        Check(MutantCompanionVariants.Alias("PSY_DOG") == MutantCompanionVariants.Alias("psy_dog"), "Alias is not stable.");
        var wire = "CATALOG_ENTRY\tmc2-test\t0\tgamma_custom_giant\tmutant\tGiant\tgiant\tnormal\tmonster\t1\tnone\tSM_GIANT";
        var parsed = (CatalogEntryMessage)PipeProtocol.ParseInbound(wire);
        Check(parsed.Entry.NativeClass == "SM_GIANT", "Extended catalog wire field lost.");
        var legacy = (CatalogEntryMessage)PipeProtocol.ParseInbound(wire[..wire.LastIndexOf('\t')]);
        Check(legacy.Entry.NativeClass == "", "Legacy catalog changed.");
        Check(CatalogContentHash.CanonicalEntryLine(0, parsed.Entry).EndsWith("\tSM_GIANT\n"), "Metadata is not authenticated by hash.");

        var root = Path.Combine(Environment.GetEnvironmentVariable("ADB_TEST_ROOT") ?? Path.GetTempPath(), "mc2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(Path.Combine(game, "bin"));
            await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "; mock game");
            var paths = new AppPaths(Path.Combine(root, "data"));
            var package = Path.Combine(root, "package");
            var script = "gamedata/scripts/anomaly_donation_bridge_mc2_test.script";
            await WritePackage(package, script, "return 1");
            var manager = new AddonPackageManager(paths, package);
            var legacySnapshot = new CatalogSnapshot(new CatalogRevision("legacy"), CatalogContentHash.Compute(entries), entries);
            await manager.InstallOrUpdateAsync(game,mutantCatalog: legacySnapshot);
            Check(!File.Exists(Path.Combine(game,MutantCompanionVariants.RelationsPath)) &&
                (await File.ReadAllTextAsync(Path.Combine(game,MutantCompanionVariants.RelativePath))).Contains("adb_mc2_profile = 4"),
                "First bootstrap install guessed game-specific relationships.");
            Check((await manager.InstallOrUpdateAsync(game, mutantCatalog: snapshot)).State == AddonInstallState.Current, "V2 install failed.");
            var variantPath = Path.Combine(game, MutantCompanionVariants.RelativePath);
            var installed = await File.ReadAllBytesAsync(variantPath);
            Check(installed.SequenceEqual(MutantCompanionVariants.Generate(snapshot)), "Installed alias bytes mismatch.");
            Check((await manager.InspectAsync(game, mutantCatalog: snapshot)).State == AddonInstallState.Current, "Install did not settle.");
            Check((await manager.InspectAsync(game)).State == AddonInstallState.Current, "Missing cache must retain verified aliases.");
            await manager.InstallOrUpdateAsync(game);
            Check(installed.SequenceEqual(await File.ReadAllBytesAsync(variantPath)), "Aliases were erased without catalog.");

            // A managed profile-1 installation must regenerate, not retain the
            // old class replacement / ability zeros under the same stable IDs.
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var relationsPath = Path.Combine(game, MutantCompanionVariants.RelationsPath);
            Check((await File.ReadAllBytesAsync(relationsPath)).SequenceEqual(MutantCompanionVariants.GenerateRelations(snapshot)!),
                "Live merged species did not generate a native relation extension.");
            await manager.InstallOrUpdateAsync(game, mutantCatalog: legacySnapshot);
            Check(installed.SequenceEqual(await File.ReadAllBytesAsync(variantPath)), "Existing native-class catalog required an extra game scan.");
            var oldBytes = Encoding.UTF8.GetBytes(generated.Replace("adb_mc2_profile = 5", "adb_mc2_profile = 1") +
                "; old prototype restricted attacks\n");
            await File.WriteAllBytesAsync(variantPath, oldBytes);
            var oldReceipt = JsonSerializer.Deserialize<AddonInstallationReceipt>(
                await File.ReadAllTextAsync(paths.AddonInstallationReceiptFile), jsonOptions)!;
            oldReceipt.Files[MutantCompanionVariants.RelativePath] = Convert.ToHexString(SHA256.HashData(oldBytes));
            await File.WriteAllTextAsync(paths.AddonInstallationReceiptFile, JsonSerializer.Serialize(oldReceipt, jsonOptions));
            Check((await manager.InspectAsync(game, mutantCatalog: snapshot)).State != AddonInstallState.Current,
                "Old attack-restricted profiles were reported current.");
            Check((await manager.InstallOrUpdateAsync(game, mutantCatalog: snapshot)).State == AddonInstallState.Current &&
                installed.SequenceEqual(await File.ReadAllBytesAsync(variantPath)), "Managed old profile migration failed.");

            // Incomplete/legacy metadata must preserve BOTH save-dependent files.
            var profile3 = Encoding.UTF8.GetBytes(generated.Replace("adb_mc2_profile = 5", "adb_mc2_profile = 3"));
            var installedRelations = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(MutantCompanionVariants.GenerateRelations(snapshot)!)
                .Replace("profile = 5", "profile = 3"));
            await File.WriteAllBytesAsync(variantPath, profile3);
            await File.WriteAllBytesAsync(relationsPath, installedRelations);
            oldReceipt = JsonSerializer.Deserialize<AddonInstallationReceipt>(await File.ReadAllTextAsync(paths.AddonInstallationReceiptFile),jsonOptions)!;
            oldReceipt.Files[MutantCompanionVariants.RelativePath]=Convert.ToHexString(SHA256.HashData(profile3));
            oldReceipt.Files[MutantCompanionVariants.RelationsPath]=Convert.ToHexString(SHA256.HashData(installedRelations));
            await File.WriteAllTextAsync(paths.AddonInstallationReceiptFile,JsonSerializer.Serialize(oldReceipt,jsonOptions));
            await manager.InstallOrUpdateAsync(game);
            Check(profile3.SequenceEqual(await File.ReadAllBytesAsync(variantPath)) &&
                installedRelations.SequenceEqual(await File.ReadAllBytesAsync(relationsPath)), "Missing catalog removed a species still used by old aliases.");
            await manager.InstallOrUpdateAsync(game,mutantCatalog: legacySnapshot);
            Check(profile3.SequenceEqual(await File.ReadAllBytesAsync(variantPath)) &&
                installedRelations.SequenceEqual(await File.ReadAllBytesAsync(relationsPath)), "Legacy catalog split the two-file installation.");

            // Late failure after an earlier managed file was replaced: all prior bytes and receipt survive.
            var receipt = await File.ReadAllBytesAsync(paths.AddonInstallationReceiptFile);
            var sourceScript = Path.Combine(game, script);
            var before = await File.ReadAllBytesAsync(sourceScript);
            await WritePackage(package, script, "return 2");
            // Fail the final receipt write, AFTER alias regeneration and legacy
            // matrix replacement. This must restore the two-file old installation.
            var blockedReceipt = paths.AddonInstallationReceiptFile + ".tmp";
            Directory.CreateDirectory(blockedReceipt);
            var failed = false;
            var changedRelations = Communities.Replace("77,-2", "77,-1");
            var changedEntries = entries.Append(Mutant("new_custom_mutant", "SM_DOG_S")).ToArray();
            var changedSnapshot = new CatalogSnapshot(new CatalogRevision("changed-relations"),
                CatalogContentHash.Compute(changedEntries, changedRelations), changedEntries, monsterCommunities: changedRelations);
            try { await manager.InstallOrUpdateAsync(game, mutantCatalog: changedSnapshot); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
            finally { Directory.Delete(blockedReceipt); }
            Check(failed, "Late install failure was not exercised.");
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(sourceScript)), "Partial install was not rolled back.");
            Check(installedRelations.SequenceEqual(await File.ReadAllBytesAsync(relationsPath)), "Failed install damaged private relations.");
            Check(profile3.SequenceEqual(await File.ReadAllBytesAsync(variantPath)), "Failed install damaged savegame aliases.");
            Check(receipt.SequenceEqual(await File.ReadAllBytesAsync(paths.AddonInstallationReceiptFile)), "Failed install changed receipt.");
            await manager.InstallOrUpdateAsync(game, mutantCatalog: snapshot);
            Check(installed.SequenceEqual(await File.ReadAllBytesAsync(variantPath)) &&
                MutantCompanionVariants.GenerateRelations(snapshot)!.SequenceEqual(await File.ReadAllBytesAsync(relationsPath)),
                "Native combat profile/matrix did not migrate together.");
            await File.AppendAllTextAsync(variantPath, "; user change");
            Check((await manager.InspectAsync(game, mutantCatalog: snapshot)).State == AddonInstallState.Conflict, "Modified aliases silently overwritten.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifyAttackInheritance()
    {
        // Check every supported native type, with arbitrary mod section names.
        // Presence fields / footstep events and our private native community only.
        var entries = MutantCompanionVariants.Classes.OrderBy(c => c, StringComparer.Ordinal)
            .Select((c, i) => Mutant("unrelated_mod_section_" + i, c)).ToArray();
        var snapshot = new CatalogSnapshot(new CatalogRevision("native-attacks"), CatalogContentHash.Compute(entries, Communities), entries,
            monsterCommunities: Communities);
        var text = Encoding.UTF8.GetString(MutantCompanionVariants.Generate(snapshot));
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                var name = line[1..line.IndexOf(']')];
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                sections.Add(name, current);
            }
            else
            {
                var equals = line.IndexOf('=');
                Check(equals > 0 && current is not null, "Malformed private presence config.");
                current!.Add(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }
        }
        var common = new HashSet<string>(["adb_mc2_source", "adb_mc2_native_class", "adb_mc2_profile", "script_binding", "species"], StringComparer.Ordinal);
        foreach (var aura in new[] { "psy", "radiation", "fire", "base" })
            foreach (var field in new[] { "linear_factor", "quadratic_factor", "max_power", "max_distance", "enable_for_dead", "pp_highest_at" })
                common.Add(aura + "_" + field);
        foreach (var entry in entries)
        {
            var alias = MutantCompanionVariants.Alias(entry.SectionId);
            Check(text.Contains($"[{alias}]:{entry.SectionId}\n"), "Native source inheritance missing: " + entry.NativeClass);
            var values = sections[alias];
            var allowed = new HashSet<string>(common, StringComparer.Ordinal);
            if (entry.NativeClass == "SM_GIANT") allowed.Add("step_params");
            if (entry.NativeClass == "SM_DOG_P") allowed.Add("aura_effector");
            if (entry.NativeClass == "SM_POLTR") allowed.UnionWith(["detection_pp_effector_name", "flame_scan_effector_section"]);
            Check(values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(allowed), "Unexpected native combat override: " + entry.NativeClass);
            Check(values["adb_mc2_profile"] == "5" && values["species"] == MonsterCommunityCatalog.Companion,
                "Native attack profile is not assigned its private community.");
            foreach (var aura in new[] { "psy", "radiation", "fire", "base" })
                foreach (var field in new[] { "linear_factor", "quadratic_factor", "max_power", "max_distance" })
                    Check(values[aura + "_" + field] == "0", "Presence aura restored by mistake.");
        }
        var pp = sections["adb_mc2_presence_neutral"];
        foreach (var key in new[] { "duality_h", "duality_v", "blur", "gray", "noise_intensity" })
            Check(pp[key] == "0", "Neutral presence overlay has an active effect: " + key);
        Check(pp["color_base"] == "0.5,0.5,0.5" && pp["color_gray"] == "0.333,0.333,0.333" &&
            pp["color_add"] == "0,0,0" && pp["noise_grain"] == "1" && pp["noise_fps"] == "30",
            "Presence overlay differs from engine SPPInfo identity or has invalid noise parameters.");
        Check(sections["adb_mc2_no_steps"].Count == 0 && !text.Contains("!["), "Global/original sections were patched.");
    }

    private static void VerifyLegacyCatalogMetadata(CatalogSnapshot snapshot)
    {
        var text = Encoding.UTF8.GetString(MutantCompanionVariants.Generate(snapshot));
        Check(!text.Contains("![") && text.Contains("\nspecies = adb_mc2_companion"), "Native source sections were overwritten.");
        var legacy = new CatalogSnapshot(snapshot.Revision,CatalogContentHash.Compute(snapshot.Entries),snapshot.Entries);
        var bootstrap = Encoding.UTF8.GetString(MutantCompanionVariants.Generate(legacy));
        Check(!bootstrap.Contains("species =") && bootstrap.Contains("adb_mc2_profile = 4") &&
            MutantCompanionVariants.GenerateRelations(legacy) is null, "Legacy metadata guessed a vanilla species matrix.");
        foreach (var invalid in new[] { "dog,43,-1,0", "actor,255,1,0;actor,255,1,0", "actor,255,1,0;x\n![actor],4,0,1",
                     "actor,255,1,0;dog,256,0,0", "actor,255,1,0;dog,4,2,0", "actor,255,1,0;adb_mc2_companion,255,0,0" })
        {
            var rejected=false;
            try { MonsterCommunityCatalog.Parse(invalid); } catch (InvalidDataException) { rejected=true; }
            Check(rejected, "Unsafe/incomplete species metadata accepted: " + invalid);
        }
        var begin = (CatalogBeginMessage)PipeProtocol.ParseInbound("CATALOG_BEGIN\tmc2-test\t" + snapshot.Entries.Count +
            "\t" + snapshot.ContentHash + "\t" + PipeProtocol.PercentEncode(Communities));
        var assembler = new CatalogSnapshotAssembler();
        assembler.Begin(begin);
        for(var i=0; i<snapshot.Entries.Count; i++) assembler.Add(new CatalogEntryMessage(begin.Revision,i,snapshot.Entries[i]));
        var result=assembler.End(new CatalogEndMessage(begin.Revision,snapshot.Entries.Count,snapshot.ContentHash));
        Check(result.MonsterCommunities==Communities, "Transactional catalog lost species metadata.");
        assembler.Begin(begin with { MonsterCommunities=Communities.Replace("77", "78") });
        for(var i=0; i<snapshot.Entries.Count; i++) assembler.Add(new CatalogEntryMessage(begin.Revision,i,snapshot.Entries[i]));
        var failed=false;
        try { assembler.End(new CatalogEndMessage(begin.Revision,snapshot.Entries.Count,snapshot.ContentHash)); }
        catch(InvalidDataException) { failed=true; }
        Check(failed && !assembler.IsReceiving, "Species metadata was not authenticated by catalog hash.");
        var document=CatalogCacheDocument.Create(Path.GetTempPath(),CapabilitySet.Empty,snapshot);
        var json=JsonSerializer.Serialize(document);
        Check(JsonSerializer.Deserialize<CatalogCacheDocument>(json)!.ToSnapshot().MonsterCommunities==Communities,
            "Offline cache lost native species metadata.");
        Check(CatalogExclusionFilter.Apply(snapshot,new[] { (snapshot.Entries[0].Kind,snapshot.Entries[0].SectionId) }).MonsterCommunities==Communities,
            "Exclusions erased native species metadata.");
    }

    private static void VerifyNativeRelations(CatalogSnapshot snapshot)
    {
        var species = MonsterCommunityCatalog.Parse(snapshot.MonsterCommunities);
        // Model the inspected CInifile DLTX CSV append operation with repeated
        // values. No vanilla rows/ordering are assumed or copied by the addon.
        var original = species.Select((_,i) => Enumerable.Range(0,species.Count)
            .Select(j => (i+j)%4-2).ToArray()).ToArray();
        var rows = species.Select((s,i) => (s.Name, Values: original[i].ToList()))
            .ToDictionary(p => p.Name,p => p.Values,StringComparer.Ordinal);
        var communities = species.SelectMany(s => new[] { s.Name,s.Team.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList();
        var generated = Encoding.UTF8.GetString(MutantCompanionVariants.GenerateRelations(snapshot)!);
        var section = "";
        foreach (var raw in generated.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length==0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') || line.StartsWith("![")) { section=line;continue; }
            var parts=line.Split('=',2);var key=parts[0].Trim();var value=parts[1].Trim();
            if (section=="![monster_communities]")
            {
                Check(key==">communities", "Original community order/teams replaced.");
                communities.AddRange(value.Split(','));
            }
            if (section!="![monster_relations]") continue;
            var numbers=value.Split(',').Select(int.Parse).ToList();
            if (key.StartsWith('>')) rows[key[1..]].AddRange(numbers);
            else
            {
                Check(key==MonsterCommunityCatalog.Companion && !rows.ContainsKey(key), "Original relation row overwritten.");
                rows.Add(key,numbers);
            }
        }
        Check(communities.Count==(species.Count+1)*2 && communities[^2]==MonsterCommunityCatalog.Companion && communities[^1]=="255",
            "Private community was not appended exactly once.");
        for(var i=0;i<species.Count;i++)
        {
            var s=species[i];var row=rows[s.Name];
            Check(communities[i*2]==s.Name && communities[i*2+1]==s.Team.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                row.Count==species.Count+1 && row.Take(species.Count).SequenceEqual(original[i]), "Original native matrix changed.");
            Check(row[^1]==(s.HasHumanUsers ? -1 : s.ActorRelation), "Incoming relation does not match actor/human policy.");
            Check(rows[MonsterCommunityCatalog.Companion][i]==(s.Name=="actor" ? 0 : -1), "Native useful() still excludes a modded species.");
        }
        Check(rows[MonsterCommunityCatalog.Companion].Count==species.Count+1 && rows[MonsterCommunityCatalog.Companion][^1]==1,
            "Private pets are enemies or matrix dimensions invalid.");
        Check(MutantCompanionVariants.GenerateRelations(snapshot)!.SequenceEqual(MutantCompanionVariants.GenerateRelations(snapshot)!),
            "Rebuilding native relation config is not deterministic.");
    }

    private static void VerifyUiPackage()
    {
        var package = Path.Combine(AppContext.BaseDirectory, "addon-package");
        var ui = XDocument.Load(Path.Combine(package, "gamedata/configs/ui/anomaly_donation_bridge_mutant_v2.xml")).Root!;
        double Number(XElement node, string name) => (double)node.Attribute(name)!;
        var panel = ui.Element("panel")!;
        var buttons = new[] { "follow", "stay", "rest", "assist", "defend", "passive", "less", "reset", "more", "refresh", "close" }
            .Select(key => ui.Element(key)!).ToArray();
        Check(buttons.All(button => button is not null && button.Element("texture")?.Value == "adb_mc2_button"), "Menu leaked stock decorative buttons.");
        Check(panel.Element("texture")?.Value == "adb_mc2_panel" && ui.Element("details") is null, "Menu still has stock background or joined details text.");
        foreach (var node in buttons.Concat(new[] { ui.Element("health_value")!, ui.Element("order_value")!, ui.Element("combat_value")! }))
        {
            Check(Number(node, "x") >= Number(panel, "x") && Number(node, "y") >= Number(panel, "y") &&
                Number(node, "x") + Number(node, "width") <= Number(panel, "x") + Number(panel, "width") &&
                Number(node, "y") + Number(node, "height") <= Number(panel, "y") + Number(panel, "height"), "Control outside panel.");
        }
        Check(buttons.Take(3).Select(b => Number(b, "y")).Distinct().Count() == 1 &&
            buttons.Take(3).Select(b => Number(b, "width")).Distinct().Count() == 1, "Command grid is misaligned.");
        foreach (var start in new[] { 0, 6 })
            Check(buttons.Skip(start).Take(3).Select(b => Number(b, "y")).Distinct().Count() == 1 &&
                buttons.Skip(start).Take(3).Select(b => Number(b, "width")).Distinct().Count() == 1, "Command group grid is misaligned.");
        Check(buttons.Skip(3).Take(3).Select(b => Number(b, "y")).Distinct().Count() == 1 &&
            Number(buttons[5], "width") > Number(buttons[3], "width"), "Long combat label lacks space.");
        Check(Number(ui.Element("list")!, "x") + Number(ui.Element("list")!, "width") < Number(ui.Element("column_line")!, "x"), "List overlaps state column.");
        Check(Number(ui.Element("list")!, "height") >= 11 * Number(ui.Element("list")!, "item_height") &&
            Number(ui.Element("previous_page")!, "y") >= Number(ui.Element("list")!, "y") + Number(ui.Element("list")!, "height") &&
            Number(ui.Element("next_page")!, "x") + Number(ui.Element("next_page")!, "width") <=
            Number(ui.Element("list")!, "x") + Number(ui.Element("list")!, "width"), "Paged party list overflows or pagination overlaps rows.");
        Check(buttons.Take(10).All(b => Number(b, "x") >= Number(ui.Element("status_heading")!, "x")) &&
            Number(buttons[10], "y") < Number(ui.Element("header_line")!, "y") && ui.Element("hint") is null,
            "Controls escaped state column or close is not a top-right cross.");
        Check(ui.Element("health_value")!.Attribute("y")!.Value != ui.Element("order_value")!.Attribute("y")!.Value &&
            ui.Element("order_value")!.Attribute("y")!.Value != ui.Element("combat_value")!.Attribute("y")!.Value, "Status fields share a line.");
        var descriptor = XDocument.Load(Path.Combine(package, "gamedata/configs/ui/textures_descr/anomaly_donation_bridge_mutant_v2.xml"));
        Check(descriptor.Descendants("texture").Count() == 22, "Own neutral/colored atlas descriptors missing.");
        var atlas = File.ReadAllBytes(Path.Combine(package, "gamedata/textures/ui/anomaly_donation_bridge_mutant_v2.dds"));
        Check(atlas.Length == 128 + 1024 * 128 * 4 && Encoding.ASCII.GetString(atlas, 0, 4) == "DDS " &&
            BitConverter.ToUInt32(atlas, 12) == 128 && BitConverter.ToUInt32(atlas, 16) == 1024, "Invalid DDS atlas.");
        Check(atlas[128] == 0 && atlas[129] == 0 && atlas[130] == 0 && atlas[131] == 190, "Panel is not plain translucent black.");
        foreach (var palette in new[] { "green", "orange", "red", "blue" })
            foreach (var state in new[] { "e", "h", "t", "d" })
                Check(descriptor.Descendants("texture").Any(t => (string?)t.Attribute("id") == $"adb_mc2_button_{palette}_{state}"), "An active button state is missing.");
    }

    private static CatalogEntry Mutant(string section, string native) => new()
    {
        SectionId = section,
        NativeClass = native,
        Kind = CatalogEntryKind.Mutant,
        DisplayName = "Giant",
        Category = "giant",
        Faction = "monster",
        Strength = CatalogStrength.Normal,
        IsAvailable = true,
        QuarantineReason = QuarantineReason.None
    };

    private static async Task WritePackage(string root, string script, string content)
    {
        var files = new List<AddonPackageFile>();
        foreach (var pair in new[] { (script, content), (MutantCompanionVariants.UiPath, "<w/>") })
        {
            var path = Path.Combine(root, pair.Item1); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, pair.Item2, new UTF8Encoding(false));
            files.Add(new AddonPackageFile { Path = pair.Item1, Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) });
        }
        var manifest = new AddonPackageManifest
        {
            SchemaVersion = 1,
            Product = AddonPackageManager.ProductId,
            AddonVersion = AddonPackageManager.ShippedAddonVersion,
            ProtocolVersion = "1",
            PipeName = AddonPackageManager.PipeName,
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(root, "addon-manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
