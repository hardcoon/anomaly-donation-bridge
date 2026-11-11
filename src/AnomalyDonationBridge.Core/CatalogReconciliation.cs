namespace AnomalyDonationBridge.Core;

public static class CatalogReconciliation
{
    public static void RetainDeclaredActions(
        IList<ActionDefinition> catalog,
        IReadOnlySet<string> declaredActionIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(declaredActionIds);
        for (var index = catalog.Count - 1; index >= 0; index--)
        {
            if (!declaredActionIds.Contains(catalog[index].HandlerId))
            {
                catalog.RemoveAt(index);
            }
        }
    }

    public static string ResolveSavedParameterValue(
        ParameterDefinition definition,
        IReadOnlyDictionary<string, string> savedParameters)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(savedParameters);
        if (!savedParameters.TryGetValue(definition.Key, out var saved))
        {
            return definition.DefaultValue;
        }

        // Persisted dynamic parameters intentionally do not carry the runtime
        // catalog payload. Preserve their opaque section IDs until the current
        // game catalog is applied and can validate them authoritatively.
        if (ActiveTemplateSelection.IsParameter(definition) ||
            definition.CatalogKind is not null && definition.Options.Count == 0)
        {
            var selected = definition.SelectedOptions(saved)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join(',', selected);
        }

        if (definition.Kind == ParameterKind.MultiChoice)
        {
            var selected = definition.SelectedOptions(saved)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return string.Join(',', definition.Options.Where(selected.Contains));
        }

        return definition.Validate(saved) is null ? saved : definition.DefaultValue;
    }
}
