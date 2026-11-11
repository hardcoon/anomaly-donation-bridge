using System.Text.RegularExpressions;

namespace AnomalyDonationBridge.Persistence;

public enum SafeZoneBlockedAction { Skip, Wait }

public sealed class SafeZoneRule
{
    public string LevelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool BlockAll { get; set; } = true;
    public List<string> HandlerIds { get; set; } = [];
}

public sealed class SafeZoneSettings
{
    public bool Enabled { get; set; }
    public SafeZoneBlockedAction BlockedAction { get; set; }
    public List<SafeZoneRule> Rules { get; set; } = [];

    public static SafeZoneSettings Normalize(SafeZoneSettings? source)
    {
        source ??= new();
        if (!Enum.IsDefined(source.BlockedAction))
            throw new InvalidDataException("Неизвестное действие при блокировке безопасной зоной.");
        if ((source.Rules?.Count ?? 0) > 256)
            throw new InvalidDataException("Безопасная зона: не более 256 локаций.");
        var result = new SafeZoneSettings { Enabled = source.Enabled, BlockedAction = source.BlockedAction };
        foreach (var rule in source.Rules ?? [])
        {
            if (rule is null) continue;
            var level = (rule.LevelName ?? string.Empty).Trim().ToLowerInvariant();
            if (!Regex.IsMatch(level, "^[a-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Безопасная зона: недопустимое имя локации.");
            var handlers = (rule.HandlerIds ?? []).Select(id => (id ?? string.Empty).Trim())
                .Distinct(StringComparer.Ordinal).ToList();
            if (handlers.Count > 1024 || handlers.Any(id =>
                    !Regex.IsMatch(id, "^[a-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)))
                throw new InvalidDataException("Безопасная зона: недопустимый список эффектов.");
            var existing = result.Rules.FirstOrDefault(item => item.LevelName == level);
            if (existing is not null)
            {
                // Duplicate configuration must never weaken a restriction.
                existing.BlockAll |= rule.BlockAll;
                existing.HandlerIds = existing.HandlerIds.Concat(handlers).Distinct(StringComparer.Ordinal).ToList();
                if (existing.HandlerIds.Count > 1024) throw new InvalidDataException("Безопасная зона: слишком много эффектов.");
                continue;
            }
            var displayName = (rule.DisplayName ?? string.Empty).Trim();
            if (displayName.Length > 256 || displayName.Any(char.IsControl)) displayName = level;
            result.Rules.Add(new SafeZoneRule
            {
                LevelName = level, DisplayName = displayName.Length == 0 ? level : displayName,
                BlockAll = rule.BlockAll, HandlerIds = handlers
            });
        }
        return result;
    }
}
