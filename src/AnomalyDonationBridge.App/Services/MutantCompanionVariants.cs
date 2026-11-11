using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

/// <summary>Private spawn aliases derived from the selected game's merged, validated catalog.</summary>
internal static class MutantCompanionVariants
{
    internal const string RelativePath = "gamedata/configs/mod_system_anomaly_donation_bridge_mutant_v2.ltx";
    internal const string UiPath = "gamedata/configs/ui/anomaly_donation_bridge_mutant_v2.xml";
    internal const string RelationsPath = "gamedata/configs/mod_system_zzzzzzzz_adb_mutant_v2_relations.ltx";
    private static readonly Regex SafeSection = new("^[a-zA-Z0-9_.-]{1,256}$", RegexOptions.CultureInvariant);
    internal static readonly IReadOnlySet<string> Classes = new HashSet<string>(StringComparer.Ordinal)
    {
        "SM_BLOOD", "SM_BOARW", "SM_BURER", "SM_CAT_S", "SM_CHIMS", "SM_CONTR",
        "SM_DOG_S", "SM_FLESH", "SM_IZLOM", "SM_GIANT", "SM_POLTR", "SM_P_DOG",
        "SM_DOG_P", "SM_RAT", "SM_SNORK", "SM_TUSHK", "SM_ZOMBI"
    };

    internal static string Alias(string section) => "adb_mc2_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(section.ToLowerInvariant())))[..24].ToLowerInvariant();

    internal static byte[] Generate(CatalogSnapshot? catalog)
    {
        var prepared = MonsterCommunityCatalog.Parse(catalog?.MonsterCommunities ?? "").Count > 0;
        var text = new StringBuilder("; Anomaly Donation Bridge mutant V2. Generated; do not edit.\n" +
            "; Savegames containing these objects require this addon and the original mod sections.\n" +
            "[adb_mc2_no_steps]\n\n" +
            // SPPInfo identity from CameraManager: suppress presence-only PP,
            // without replacing a native class or removing its attacks.
            "[adb_mc2_presence_neutral]\n" +
            "duality_h = 0\nduality_v = 0\nblur = 0\ngray = 0\n" +
            "noise_intensity = 0\nnoise_grain = 1\nnoise_fps = 30\n" +
            "color_base = 0.5,0.5,0.5\ncolor_gray = 0.333,0.333,0.333\ncolor_add = 0,0,0\n" +
            "time = 1\ntime_attack = 0.1\ntime_release = 0.5\n\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Retain definitions even if an existing source becomes quarantined:
        // deleting its alias would invalidate a save containing an older pet.
        // Actual spawning still requires the original section to be available.
        foreach (var entry in (catalog?.Entries ?? []).Where(e => e.Kind == CatalogEntryKind.Mutant &&
                     Classes.Contains(e.NativeClass) && SafeSection.IsMatch(e.SectionId))
                     .OrderBy(e => e.SectionId, StringComparer.Ordinal))
        {
            var source = entry.SectionId.ToLowerInvariant();
            var alias = Alias(source);
            if (!seen.Add(alias)) throw new InvalidDataException("Повтор приватной секции мутанта V2.");
            text.Append('[').Append(alias).Append("]:").Append(source).Append('\n');
            text.Append("adb_mc2_source = ").Append(source).Append('\n');
            text.Append("adb_mc2_native_class = ").Append(entry.NativeClass).Append('\n');
            // Bootstrap aliases keep old saves loadable while the first game
            // session gathers merged species metadata. They are NOT spawn-ready.
            text.Append(prepared ? "adb_mc2_profile = 5\nspecies = adb_mc2_companion\n" : "adb_mc2_profile = 4\n");
            text.Append("script_binding = bind_monster.bind\n");
            foreach (var aura in new[] { "psy", "radiation", "fire", "base" })
            {
                foreach (var key in new[] { "linear_factor", "quadratic_factor", "max_power", "max_distance" })
                    text.Append(aura).Append('_').Append(key).Append(" = 0\n");
                text.Append(aura).Append("_enable_for_dead = false\n");
                text.Append(aura).Append("_pp_highest_at = 1\n");
            }
            if (entry.NativeClass == "SM_GIANT") text.Append("step_params = adb_mc2_no_steps\n");
            // Keep SM_DOG_P, Phantoms_Count and phantom_section inherited. Only
            // the independent presence overlay receives a neutral PP profile.
            if (entry.NativeClass == "SM_DOG_P") text.Append("aura_effector = adb_mc2_presence_neutral\n");
            // Detection drives native poltergeist attacks as well as its PP.
            // Mute ONLY the visual PP; leave detection, flames and telekinesis
            // inherited. Native special attacks can explicitly target Actor().
            if (entry.NativeClass == "SM_POLTR")
                text.Append("detection_pp_effector_name =\nflame_scan_effector_section = adb_mc2_presence_neutral\n");
            text.Append('\n');
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    internal static byte[]? GenerateRelations(CatalogSnapshot? catalog)
    {
        var species = MonsterCommunityCatalog.Parse(catalog?.MonsterCommunities ?? "");
        if (species.Count == 0) return null;
        // DLTX CSV insertion APPENDS (including repeated -1/0 values). Never
        // replace the merged original rows, their order, teams or old cells.
        // The scanner strips our previous extension before exporting metadata,
        // so reinstalling replaces this file without accumulating columns.
        var text = new StringBuilder("; ADB V2 native combat extension, generated from this game's live catalog.\n" +
            "; Original-to-original relationships are unchanged. Restart the game after installation.\n" +
            "[adb_mc2_relations]\nprofile = 5\nmembers = ");
        text.AppendJoin(',', species.Select(s => s.Name));
        text.Append("\n\n![monster_communities]\n>communities = adb_mc2_companion,255\n\n![monster_relations]\n");
        foreach (var s in species)
            text.Append('>').Append(s.Name).Append(" = ").Append(s.HasHumanUsers ? -1 : s.ActorRelation).Append('\n');
        // Native useful() must admit a hostile human/mutant BEFORE calling Lua.
        // Our per-object filter then rejects the actor, pets, allies, neutrals,
        // passive mode, unsolicited defense and targets outside the order leash.
        text.Append("adb_mc2_companion = ");
        text.AppendJoin(',', species.Select(s => s.Name == "actor" ? 0 : -1));
        text.Append(",1\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
