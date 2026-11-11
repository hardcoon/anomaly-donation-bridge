namespace AnomalyDonationBridge.Core;

/// <summary>References configured instances, never a handler's generated defaults.</summary>
public static class ActiveTemplateSelection
{
    // Keep the handler ID so existing instances receive the new editor on upgrade.
    public const string HandlerId = DonationDispatcher.RandomAllPresetsHandlerId;
    public const string ParameterKey = "included_actions";
    public const string DisplayName = "Рандом по выбранным активным шаблонам";
    public const string Description = "Случайно запускает один из отмеченных шаблонов раздела «Активные» текущего профиля с его сохранёнными настройками. Отключённые и удалённые варианты не участвуют.";

    public static bool IsParameter(ParameterDefinition definition) =>
        definition.Kind == ParameterKind.MultiChoice && definition.Key == ParameterKey;

    public static bool IsCandidate(ActionDefinition action) =>
        action.HandlerId is not DonationDispatcher.RandomActiveHandlerId and not HandlerId;

    public static ParameterDefinition CreateDefinition() => new()
    {
        Key = ParameterKey, DisplayName = "Участвуют в случайном выборе",
        Kind = ParameterKind.MultiChoice, MinimumSelections = 1
    };

    public static HashSet<string> SelectedIds(ActionDefinition action) =>
        (action.Parameters.GetValueOrDefault(ParameterKey) ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.Ordinal);

    public static void BindEditor(ActionDefinition action, IEnumerable<ActionDefinition> activeActions)
    {
        if (action.HandlerId != HandlerId) return;
        var definition = CreateDefinition();
        var index = 0;
        foreach (var candidate in activeActions)
        {
            index++;
            if (!IsCandidate(candidate) || candidate.Id == action.Id || definition.OptionLabels.ContainsKey(candidate.Id)) continue;
            definition.Options.Add(candidate.Id);
            definition.OptionLabels[candidate.Id] = $"№{index} · {candidate.DisplayName}" +
                (candidate.IsActive ? string.Empty : " (отключён)");
        }
        // Keep explicit references visible/removable when an instance was deleted
        // or a user preset was copied to a different profile. Never substitute it.
        foreach (var id in SelectedIds(action).Where(id => !definition.OptionLabels.ContainsKey(id)))
        {
            definition.Options.Add(id);
            definition.OptionLabels[id] = $"Недоступный шаблон · {id[..Math.Min(8, id.Length)]} (не участвует)";
        }
        action.ParameterDefinitions = [definition];
        action.Parameters.Remove("included_presets");
        action.Parameters.Remove("include_items");
        action.Parameters.TryAdd(ParameterKey, string.Empty);
    }
}
