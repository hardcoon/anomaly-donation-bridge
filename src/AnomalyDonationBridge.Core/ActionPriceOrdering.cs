namespace AnomalyDonationBridge.Core;

public static class ActionPriceOrdering
{
    // An unconfigured new member must not reset an existing group's explicit 0.
    public static int ResolveGroupDelay(IEnumerable<ActionDefinition> actions) =>
        Math.Clamp(actions.Select(action => action.GroupDelaySeconds).Max() ?? 1, 0, 86_400);

    public static string? GroupKey(ActionDefinition action)
    {
        var rules = action.Triggers.Where(trigger => trigger.Enabled)
            .Select(trigger => string.Join('|', (int)trigger.Comparator,
                trigger.Amount.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
                trigger.Currency.Trim().ToUpperInvariant()))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return rules.Length == 0 ? null : string.Join(';', rules);
    }

    public static List<ActionDefinition> MoveWithinGroup(IReadOnlyList<ActionDefinition> actions,
        string groupKey, string sourceId, string targetId, bool after)
    {
        var slots = actions.Select((action, index) => (action, index))
            .Where(item => GroupKey(item.action) == groupKey).ToList();
        var members = slots.Select(item => item.action).ToList();
        var source = members.SingleOrDefault(action => action.Id == sourceId);
        if (source is null || !members.Any(action => action.Id == targetId))
            throw new InvalidOperationException("Состав группы изменился. Повторите перетаскивание.");
        if (sourceId == targetId) return actions.ToList();
        members.Remove(source);
        members.Insert(members.FindIndex(action => action.Id == targetId) + (after ? 1 : 0), source);
        var result = actions.ToList();
        for (var index = 0; index < slots.Count; index++) result[slots[index].index] = members[index];
        return result;
    }

    public static List<ActionDefinition> SortByMinimumAssignedAmount(
        IEnumerable<ActionDefinition> actions,
        bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var priced = actions
            .Select((action, index) => new
            {
                Action = action,
                OriginalIndex = index,
                MinimumAmount = action.Triggers
                    .Where(trigger => trigger.Enabled)
                    .Select(trigger => (decimal?)trigger.Amount)
                    .Min()
            });
        var ordered = descending
            ? priced.OrderBy(item => item.MinimumAmount.HasValue ? 0 : 1)
                .ThenByDescending(item => item.MinimumAmount)
                .ThenBy(item => item.OriginalIndex)
            : priced.OrderBy(item => item.MinimumAmount.HasValue ? 0 : 1)
                .ThenBy(item => item.MinimumAmount)
                .ThenBy(item => item.OriginalIndex);
        return ordered
            .Select(item => item.Action)
            .ToList();
    }
}
