using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

/// <summary>
/// Reconciles persisted action instances with the application-owned preset schema.
/// Anomaly discovers the addon scripts itself; there is no addon.init or action
/// manifest that should be treated as an editable source of authority.
/// </summary>
public sealed class ActionManifestLoader
{
    public const string AddonRelativeRoot = "gamedata\\scripts";
    public const string EntryScriptRelativePath = AddonRelativeRoot + "\\anomaly_donation_bridge_main.script";

    public IReadOnlyList<string> MergeFromGame(string gamePath, IList<ActionDefinition> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(gamePath)) return [];

        // The compiled schema remains authoritative. A script may be visible
        // only inside MO2's VFS, so physical absence in the game directory is
        // not treated as an incompatibility. The live pipe is authoritative.
        return [];
    }

    public IReadOnlyList<string> ApplyPresetsToInstances(
        IList<ActionDefinition> instances,
        IReadOnlyList<ActionDefinition> presets)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(presets);
        var warnings = new List<string>();
        var presetsByHandler = presets
            .Where(preset => Validation.ActionId().IsMatch(preset.HandlerId))
            .GroupBy(preset => preset.HandlerId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var instance in instances)
        {
            if (!presetsByHandler.TryGetValue(instance.HandlerId, out var preset))
            {
                warnings.Add($"Для сохранённого экземпляра {instance.Id} не установлен скрипт {instance.HandlerId}");
                continue;
            }

            var savedParameters = instance.Parameters is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(instance.Parameters, StringComparer.OrdinalIgnoreCase);
            if (preset.HandlerId is "spawn_named_companion" or "hardcoon.adb.mutant_companion_v2" &&
                !savedParameters.ContainsKey("nickname_visibility") &&
                savedParameters.TryGetValue("show_3d_nick", out var oldNicknameFlag) &&
                bool.TryParse(oldNicknameFlag, out var wasNicknameVisible))
                savedParameters["nickname_visibility"] = wasNicknameVisible ? "show" : "hide";
            var wasActive = instance.IsActive;
            var customName = instance.HasCustomDisplayName &&
                             Validation.TryNormalizeUserPresetName(instance.DisplayName, out var normalizedName)
                ? normalizedName
                : null;
            instance.DisplayName = customName ?? preset.DisplayName;
            instance.HasCustomDisplayName = customName is not null;
            instance.Description = preset.Description;
            instance.CategoryId = preset.CategoryId;
            instance.ScriptRelativePath = preset.ScriptRelativePath;
            instance.ParameterDefinitions = preset.ParameterDefinitions.Select(definition => definition.Clone()).ToList();
            instance.Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in instance.ParameterDefinitions)
            {
                instance.Parameters[definition.Key] =
                    CatalogReconciliation.ResolveSavedParameterValue(definition, savedParameters);
            }
            instance.IsActive = wasActive;
        }

        return warnings;
    }
}
