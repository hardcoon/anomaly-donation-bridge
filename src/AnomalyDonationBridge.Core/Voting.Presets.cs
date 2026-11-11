namespace AnomalyDonationBridge.Core;

internal static class VotingPresets
{
    // A voting option holds an already configured effect, not a catalog editor.
    // Keep its chosen values and validation rules, rather than another copy of
    // every model/item from the game in every current and historical round.
    // The dispatcher still rebinds the winner to the live catalog before use.
    internal static ActionDefinition? Snapshot(ActionDefinition? source)
    {
        if (source is null) return null;
        var snapshot = source.Clone();
        foreach (var definition in snapshot.ParameterDefinitions.Where(item => item.CatalogKind is not null))
        {
            var value = snapshot.Parameters.GetValueOrDefault(definition.Key, definition.DefaultValue);
            var selected = definition.SelectedOptions(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (definition.Key.Equals("section_id", StringComparison.OrdinalIgnoreCase))
            {
                selected.UnionWith(snapshot.ItemSpawns.Select(item => item.ItemId));
                selected.UnionWith(snapshot.SpawnGroups.Select(group => group.VariantId));
            }
            // Intersection is intentional: never make a previously invalid
            // selection valid merely by putting it in the compact snapshot.
            definition.Options = definition.Options.Where(selected.Contains).ToList();
            definition.OptionGroups.Clear();
            definition.OptionLabels = definition.OptionLabels.Where(pair => selected.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        return snapshot;
    }
}
