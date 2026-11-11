using System.Globalization;
using System.Text;

namespace AnomalyDonationBridge.Core;

public static class ActivePresetTextExporter
{
    public static string Build(IEnumerable<ActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var lines = actions.Select((action, index) =>
        {
            var prices = action.Triggers
                .Where(trigger => trigger.Enabled)
                .Select(trigger =>
                    (trigger.Comparator == TriggerComparator.Exact ? string.Empty : "от ") +
                    $"{trigger.Amount.ToString("0.##", CultureInfo.InvariantCulture)} {trigger.Currency}")
                .ToList();
            return $"№{index + 1} {action.DisplayName.TrimEnd(' ', '=')}: " +
                (prices.Count == 0 ? "цена не назначена" : string.Join(", ", prices));
        });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
