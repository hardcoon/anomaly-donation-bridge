using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AnomalyDonationBridge.Core;

public enum TriggerComparator
{
    Exact,
    GreaterOrEqual
}

public enum ParameterKind
{
    Integer,
    Decimal,
    Percent,
    Choice,
    ItemChoice,
    MultiChoice,
    KeyboardKey,
    GameInputKey
}

public enum QueueMode
{
    Skip,
    WaitForDuration,
    WaitIndefinitely
}

public enum GameResultStatus
{
    Executed,
    Deferred,
    Rejected,
    Uncertain
}

public enum DispatchState
{
    Queued,
    Sent,
    Executed,
    Deferred,
    Rejected,
    Uncertain,
    Skipped,
    Cancelled,
    Expired,
    Duplicate,
    NoRule
}

public sealed class ItemSpawnEntry
{
    public string ItemId { get; set; } = string.Empty;
    public int Count { get; set; } = 1;

    public ItemSpawnEntry Clone() => new() { ItemId = ItemId, Count = Count };
}

public sealed class SpawnGroupEntry
{
    public string VariantId { get; set; } = string.Empty;
    public string Strength { get; set; } = "any";
    public int Count { get; set; } = 1;

    public SpawnGroupEntry Clone() => new()
    {
        VariantId = VariantId,
        Strength = Strength,
        Count = Count
    };
}

public static class ItemBundleCodec
{
    public const string HandlerId = "spawn_items";
    public const string ParameterKey = "items";
    // The row count is intentionally unlimited. The encoded payload, rather
    // than an arbitrary UI limit, keeps the complete 16 KiB command bounded.
    public const int MaximumSerializedLength = 4_000;

    public static string Serialize(IEnumerable<ItemSpawnEntry> entries) => string.Join(';',
        entries.Select(entry =>
            $"{entry.ItemId.ToLowerInvariant()}*{entry.Count.ToString(CultureInfo.InvariantCulture)}"));

    public static IReadOnlyList<ItemSpawnEntry>? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumSerializedLength) return null;
        var result = new List<ItemSpawnEntry>();
        foreach (var row in value.Split(';'))
        {
            var fields = row.Split('*');
            if (fields.Length != 2 || !Validation.ItemSection().IsMatch(fields[0]) ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count is < 1 or > 50)
            {
                return null;
            }
            result.Add(new ItemSpawnEntry { ItemId = fields[0], Count = count });
        }
        return result.Count > 0 ? result : null;
    }
}

public static class SpawnGroupBundleCodec
{
    public const string ParameterKey = "groups";
    public const int MaximumSerializedLength = 4_000;

    public static bool Supports(string handlerId) => handlerId is
        "spawn_mutants" or "spawn_npcs" or "spawn_npc_squads" or "spawn_mutant_squads";

    public static string Serialize(IEnumerable<SpawnGroupEntry> entries) => string.Join(';',
        entries.Select(entry =>
            $"{entry.VariantId.ToLowerInvariant()}*any*{entry.Count.ToString(CultureInfo.InvariantCulture)}"));

    public static IReadOnlyList<SpawnGroupEntry>? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumSerializedLength) return null;
        var result = new List<SpawnGroupEntry>();
        foreach (var row in value.Split(';'))
        {
            var fields = row.Split('*');
            if (fields.Length != 3 || !Validation.ItemSection().IsMatch(fields[0]) ||
                !AllowedStrengths.Contains(fields[1], StringComparer.OrdinalIgnoreCase) ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count is < 1 or > 99)
            {
                return null;
            }
            result.Add(new SpawnGroupEntry { VariantId = fields[0], Strength = "any", Count = count });
        }
        return result.Count > 0 ? result : null;
    }

    public static IReadOnlyList<string> AllowedStrengths { get; } =
        ["any", "lite", "normal", "strong", "unclassified"];
}

public sealed class ParameterDefinition
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ParameterKind Kind { get; set; }
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public List<string> Options { get; set; } = [];
    public Dictionary<string, List<string>> OptionGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OptionLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DefaultValue { get; set; } = string.Empty;
    public CatalogEntryKind? CatalogKind { get; set; }
    public List<string> CatalogCategories { get; set; } = [];
    public List<string> CatalogNativeClasses { get; set; } = [];
    public int MinimumSelections { get; set; }

    public ParameterDefinition Clone() => new()
    {
        Key = Key,
        DisplayName = DisplayName,
        Kind = Kind,
        Minimum = Minimum,
        Maximum = Maximum,
        Options = [.. Options],
        OptionGroups = OptionGroups.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase),
        OptionLabels = new Dictionary<string, string>(OptionLabels, StringComparer.OrdinalIgnoreCase),
        DefaultValue = DefaultValue,
        CatalogKind = CatalogKind,
        CatalogCategories = [.. CatalogCategories],
        CatalogNativeClasses = [.. CatalogNativeClasses],
        MinimumSelections = MinimumSelections
    };

    public string? Validate(string? value)
    {
        value ??= string.Empty;
        if (!Validation.ParameterKey().IsMatch(Key))
        {
            return $"Недопустимый ключ параметра: {Key}";
        }

        if (Kind is ParameterKind.KeyboardKey or ParameterKind.GameInputKey)
            return GameKeyboardKeys.IsValid(value, Kind == ParameterKind.GameInputKey) ||
                   Kind == ParameterKind.KeyboardKey && DefaultValue == "RCTRL" && value == DefaultValue ? null :
                $"Для «{DisplayName}» нажмите одну клавишу. Esc, F5, F9 и модификаторы зарезервированы.";

        if (Kind is ParameterKind.Choice or ParameterKind.ItemChoice)
        {
            if (CatalogKind is not null)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return $"Для параметра «{DisplayName}» не выбрана секция из каталога игры";
                }

                return Options.Contains(value, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : $"Секция «{value}» для параметра «{DisplayName}» отсутствует в свежем безопасном каталоге";
            }
            return Options.Contains(value, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"Параметр «{DisplayName}» должен быть одним из: {string.Join(", ", Options)}";
        }

        if (Kind == ParameterKind.MultiChoice)
        {
            var selected = SelectedOptions(value);
            var minimumSelections = CatalogKind is not null
                ? Math.Max(1, MinimumSelections)
                : Math.Max(0, MinimumSelections);
            if (selected.Count < minimumSelections)
            {
                return CatalogKind is not null
                    ? $"Для параметра «{DisplayName}» не выбрана ни одна секция из каталога игры"
                    : $"Для параметра «{DisplayName}» нужно выбрать хотя бы {minimumSelections}";
            }
            if (selected.Count != selected.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                return $"Параметр «{DisplayName}» содержит повторяющиеся значения";
            }
            // Instance references are resolved against the current profile when
            // dispatching; editor labels/options are not a persisted catalogue.
            if (ActiveTemplateSelection.IsParameter(this))
                return selected.All(id => Validation.InstanceId().IsMatch(id))
                    ? null : "Список активных шаблонов содержит недопустимый идентификатор";
            var unknown = selected.FirstOrDefault(option =>
                !Options.Contains(option, StringComparer.OrdinalIgnoreCase));
            return unknown is null
                ? null
                : $"Параметр «{DisplayName}» содержит недопустимое значение: {unknown}";
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return $"Параметр «{DisplayName}» должен быть числом";
        }

        if (Kind == ParameterKind.Integer && decimal.Truncate(number) != number)
        {
            return $"Параметр «{DisplayName}» должен быть целым числом";
        }

        if (Minimum is { } minimum && number < minimum)
        {
            return $"Параметр «{DisplayName}» не может быть меньше {minimum}";
        }

        if (Maximum is { } maximum && number > maximum)
        {
            return $"Параметр «{DisplayName}» не может быть больше {maximum}";
        }

        return null;
    }

    public IReadOnlyList<string> SelectedOptions(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}

public sealed class TriggerRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public TriggerComparator Comparator { get; set; } = TriggerComparator.Exact;
    public decimal Amount { get; set; } = 1m;
    public string Currency { get; set; } = "RUB";

    public TriggerRule Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Comparator = Comparator,
        Amount = Amount,
        Currency = Currency
    };
}

public sealed class ActionDefinition
{
    public string Id { get; set; } = NewInstanceId();
    public string HandlerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool HasCustomDisplayName { get; set; }
    public string Description { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string ScriptRelativePath { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool StrictSequence { get; set; }
    public int? GroupDelaySeconds { get; set; }
    public int CooldownSeconds { get; set; } = 3;
    public List<ParameterDefinition> ParameterDefinitions { get; set; } = [];
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ItemSpawnEntry> ItemSpawns { get; set; } = [];
    public List<SpawnGroupEntry> SpawnGroups { get; set; } = [];
    public List<TriggerRule> Triggers { get; set; } = [];
    public CatalogRevision? CatalogRevision { get; set; }

    public ActionDefinition Clone() => new()
    {
        Id = Id,
        HandlerId = HandlerId,
        DisplayName = DisplayName,
        HasCustomDisplayName = HasCustomDisplayName,
        Description = Description,
        CategoryId = CategoryId,
        ScriptRelativePath = ScriptRelativePath,
        IsActive = IsActive,
        StrictSequence = StrictSequence,
        GroupDelaySeconds = GroupDelaySeconds,
        CooldownSeconds = CooldownSeconds,
        ParameterDefinitions = ParameterDefinitions.Select(definition => definition.Clone()).ToList(),
        Parameters = new Dictionary<string, string>(Parameters, StringComparer.OrdinalIgnoreCase),
        ItemSpawns = ItemSpawns.Select(entry => entry.Clone()).ToList(),
        SpawnGroups = SpawnGroups.Select(entry => entry.Clone()).ToList(),
        Triggers = Triggers.Select(trigger => trigger.Clone()).ToList(),
        CatalogRevision = CatalogRevision
    };

    public ActionDefinition CloneAsNewInstance()
    {
        var clone = Clone();
        clone.Id = NewInstanceId();
        clone.IsActive = true;
        foreach (var trigger in clone.Triggers)
        {
            trigger.Id = Guid.NewGuid().ToString("N");
        }
        return clone;
    }

    public static string NewInstanceId() => Guid.NewGuid().ToString("N");

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Validation.InstanceId().IsMatch(Id))
        {
            errors.Add($"Недопустимый идентификатор экземпляра действия: {Id}");
        }

        if (!Validation.ActionId().IsMatch(HandlerId))
        {
            errors.Add($"Недопустимый идентификатор игрового обработчика: {HandlerId}");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            errors.Add($"У действия {Id} отсутствует название");
        }

        if (CooldownSeconds < 0 || CooldownSeconds > 86_400)
        {
            errors.Add($"Пауза после срабатывания для «{DisplayName}» должна быть от 0 до 86 400 секунд");
        }
        if (GroupDelaySeconds is < 0 or > 86_400)
        {
            errors.Add("Задержка между эффектами группы должна быть от 0 до 86 400 секунд");
        }

        foreach (var definition in ParameterDefinitions)
        {
            if ((HandlerId is "spawn_named_companion" or "hardcoon.adb.special_companion") &&
                string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase) &&
                Parameters.TryGetValue("random_npc", out var randomNpc) &&
                string.Equals(randomNpc, "true", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Parameters.TryGetValue(definition.Key, out var value);
            var error = definition.Validate(value ?? definition.DefaultValue);
            if (error is not null)
            {
                errors.Add(error);
            }
        }

        if (string.Equals(HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal))
        {
            if (ItemSpawns.Count == 0) errors.Add($"{DisplayName}: добавьте хотя бы один предмет");
            var itemDefinition = ParameterDefinitions.FirstOrDefault(definition =>
                string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase));
            for (var index = 0; index < ItemSpawns.Count; index++)
            {
                var entry = ItemSpawns[index];
                if (!Validation.ItemSection().IsMatch(entry.ItemId) ||
                    itemDefinition?.Validate(entry.ItemId) is not null)
                {
                    errors.Add($"{DisplayName}: недопустимый предмет в строке {index + 1}");
                }
                if (entry.Count is < 1 or > 50)
                    errors.Add($"{DisplayName}: количество в строке {index + 1} должно быть от 1 до 50");
            }
            if (ItemBundleCodec.Serialize(ItemSpawns).Length > ItemBundleCodec.MaximumSerializedLength)
                errors.Add($"{DisplayName}: список предметов слишком велик для передачи игре");
            if (ItemSpawns.Sum(entry => entry.Count) > 500)
                errors.Add($"{DisplayName}: за одно срабатывание можно выдать не более 500 предметов");
        }

        if (SpawnGroupBundleCodec.Supports(HandlerId))
        {
            if (SpawnGroups.Count == 0) errors.Add($"{DisplayName}: добавьте хотя бы одну группу");
            var variantDefinition = ParameterDefinitions.FirstOrDefault(definition =>
                string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase));
            for (var index = 0; index < SpawnGroups.Count; index++)
            {
                var entry = SpawnGroups[index];
                if (!Validation.ItemSection().IsMatch(entry.VariantId) ||
                    variantDefinition?.Validate(entry.VariantId) is not null)
                    errors.Add($"{DisplayName}: недопустимая секция в строке {index + 1}");
                if (entry.Count is < 1 or > 99)
                    errors.Add($"{DisplayName}: количество в строке {index + 1} должно быть от 1 до 99");
            }
            if (SpawnGroupBundleCodec.Serialize(SpawnGroups).Length > SpawnGroupBundleCodec.MaximumSerializedLength)
                errors.Add($"{DisplayName}: список групп слишком велик для передачи игре");
            if (SpawnGroups.Sum(entry => entry.Count) > 200)
                errors.Add($"{DisplayName}: за одно срабатывание можно создать не более 200 существ");
        }

        if (HandlerId == "hardcoon.adb.wandering_trader" &&
            new[] { "weapons", "ammo", "helmets", "armor", "artifacts", "medicine", "food", "devices" }
                .All(key => Parameters.TryGetValue(key + "_enabled", out var enabled) && enabled == "false"))
            errors.Add("Ассортимент: включите хотя бы одну категорию товаров");

        ValidateOrderedRange(errors, "minimum_count", "maximum_count", "количества");
        ValidateOrderedRange(errors, "minimum_meters", "maximum_meters", "дальности");

        foreach (var trigger in Triggers)
        {
            errors.AddRange(Validation.ValidateTrigger(trigger).Select(error => $"{DisplayName}: {error}"));
        }

        var duplicates = Triggers.Where(rule => rule.Enabled)
            .Where(rule => Money.TryNormalizeCurrency(rule.Currency, out _))
            .GroupBy(rule => new
            {
                rule.Comparator,
                rule.Amount,
                Currency = Money.TryNormalizeCurrency(rule.Currency, out var currency) ? currency : string.Empty
            })
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicates)
        {
            errors.Add($"{DisplayName}: повторяющееся условие {duplicate.Key.Comparator} {duplicate.Key.Amount} {duplicate.Key.Currency}");
        }

        return errors;
    }

    private void ValidateOrderedRange(
        ICollection<string> errors,
        string minimumKey,
        string maximumKey,
        string rangeName)
    {
        if (!ParameterDefinitions.Any(definition =>
                string.Equals(definition.Key, minimumKey, StringComparison.OrdinalIgnoreCase)) ||
            !ParameterDefinitions.Any(definition =>
                string.Equals(definition.Key, maximumKey, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var minimumText = Parameters.TryGetValue(minimumKey, out var minimumValue)
            ? minimumValue
            : ParameterDefinitions.First(definition =>
                string.Equals(definition.Key, minimumKey, StringComparison.OrdinalIgnoreCase)).DefaultValue;
        var maximumText = Parameters.TryGetValue(maximumKey, out var maximumValue)
            ? maximumValue
            : ParameterDefinitions.First(definition =>
                string.Equals(definition.Key, maximumKey, StringComparison.OrdinalIgnoreCase)).DefaultValue;
        if (decimal.TryParse(minimumText, NumberStyles.Number, CultureInfo.InvariantCulture, out var minimum) &&
            decimal.TryParse(maximumText, NumberStyles.Number, CultureInfo.InvariantCulture, out var maximum) &&
            minimum > maximum)
        {
            errors.Add($"{DisplayName}: минимум {rangeName} не может быть больше максимума");
        }
    }

    public IReadOnlyDictionary<string, string> EffectiveParameters()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal) && ItemSpawns.Count > 0)
        {
            result[ItemBundleCodec.ParameterKey] = ItemBundleCodec.Serialize(ItemSpawns);
            foreach (var definition in ParameterDefinitions.Where(definition =>
                         definition.Key is not "section_id" and not "count"))
            {
                result[definition.Key] = Parameters.TryGetValue(definition.Key, out var value)
                    ? value
                    : definition.DefaultValue;
            }
            return result;
        }
        if (SpawnGroupBundleCodec.Supports(HandlerId) && SpawnGroups.Count > 0)
        {
            result[SpawnGroupBundleCodec.ParameterKey] = SpawnGroupBundleCodec.Serialize(SpawnGroups);
            foreach (var definition in ParameterDefinitions.Where(definition =>
                         definition.Key is not "section_id" and not "strength" and not "count"))
            {
                result[definition.Key] = Parameters.TryGetValue(definition.Key, out var value)
                    ? value
                    : definition.DefaultValue;
            }
            return result;
        }
        foreach (var definition in ParameterDefinitions)
        {
            result[definition.Key] = Parameters.TryGetValue(definition.Key, out var value)
                ? value
                : definition.DefaultValue;
        }

        return result;
    }
}

public sealed class DonationEvent
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "RUB";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    public long AmountMinor => Money.ToMinor(Amount, Currency);
}

public sealed class RuleMatch
{
    public required ActionDefinition Action { get; init; }
    public required TriggerRule Trigger { get; init; }
    public ActionDefinition? SourceAction { get; init; }
    public string? HistoryActionId { get; init; }
}

public sealed class GameCommand
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public string DonationId { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "RUB";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CatalogRevision? CatalogRevision { get; set; }
    public string DonorName { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public int BatchIndex { get; set; } = 1;
    public int BatchSize { get; set; } = 1;
    public bool SaveBeforeBatch { get; set; }
    public bool SaveAfterBatch { get; set; }
    public int EffectSaveRetentionCount { get; set; } = 50;
    public bool ShowGameEffectLog { get; set; } = true;
    public bool ShowPdaEffectMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool RequireSafeZoneCheck { get; set; }
    public string SafeZoneRevision { get; set; } = string.Empty;
    public string SourceHandlerId { get; set; } = string.Empty;
}

public sealed class EffectExecutionOptions
{
    public bool SaveBeforeBatch { get; init; }
    public bool SaveAfterBatch { get; init; }
    public int EffectSaveRetentionCount { get; init; } = 50;
    public bool ShowGameEffectLog { get; init; } = true;
    public bool ShowPdaEffectMessage { get; init; }
}

public sealed class GameExecutionResult
{
    public string CommandId { get; set; } = string.Empty;
    public GameResultStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QueuePolicy
{
    public QueueMode Mode { get; set; } = QueueMode.WaitIndefinitely;
    public int WaitMinutes { get; set; } = 5;
    public int MaximumQueuedEvents { get; set; } = 100;

    public QueuePolicy Clone() => new()
    {
        Mode = Mode,
        WaitMinutes = WaitMinutes,
        MaximumQueuedEvents = MaximumQueuedEvents
    };

    public TimeSpan? GetLifetime() => Mode switch
    {
        // Skip is decided from transport readiness during Drain; a ready game must still execute immediately.
        QueueMode.Skip => null,
        QueueMode.WaitForDuration => TimeSpan.FromMinutes(Math.Clamp(WaitMinutes, 1, 24 * 60)),
        QueueMode.WaitIndefinitely => null,
        _ => TimeSpan.Zero
    };
}

public sealed class EventHistoryEntry
{
    // Keep the actual donor/title for donor-named effects and exact manual
    // replays; only the history presentation labels this synthetic source.
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayDonor => DonationId?.StartsWith("voting:", StringComparison.Ordinal) == true ? "Голосование" : Donor;
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DonationCreatedAt { get; set; }
    public string DonationId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string Donor { get; set; } = string.Empty;
    public string DonationMessage { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string HandlerId { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public ActionDefinition? ActionSnapshot { get; set; }
    public DispatchState State { get; set; }
    public string Detail { get; set; } = string.Empty;
    public bool CanRetryManually { get; set; }
}

public sealed class ProcessedDonationRecord
{
    public string DonationId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public DispatchState State { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class Money
{
    private static readonly IReadOnlyDictionary<string, int> KnownMinorDigits =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["RUB"] = 2,
            ["USD"] = 2,
            ["EUR"] = 2,
            ["UAH"] = 2,
            ["KZT"] = 2,
            ["BYN"] = 2,
            ["GBP"] = 2
        };

    public static string NormalizeCurrency(string currency)
    {
        var normalized = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!Validation.Currency().IsMatch(normalized))
        {
            throw new ArgumentException("Код валюты должен состоять ровно из трёх латинских букв", nameof(currency));
        }

        return normalized;
    }

    public static int MinorDigits(string currency) => KnownMinorDigits.TryGetValue(NormalizeCurrency(currency), out var digits) ? digits : 2;

    public static bool TryNormalizeCurrency(string? currency, out string normalized)
    {
        try
        {
            normalized = NormalizeCurrency(currency ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    public static long ToMinor(decimal amount, string currency)
    {
        var factor = DecimalPower(10, MinorDigits(currency));
        return checked(decimal.ToInt64(decimal.Round(amount * factor, 0, MidpointRounding.AwayFromZero)));
    }

    public static decimal FromMinor(long minor, string currency)
    {
        var factor = DecimalPower(10, MinorDigits(currency));
        return minor / factor;
    }

    private static decimal DecimalPower(decimal value, int exponent)
    {
        var result = 1m;
        for (var index = 0; index < exponent; index++)
        {
            result *= value;
        }

        return result;
    }
}

public static partial class Validation
{
    [GeneratedRegex("^[a-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    public static partial Regex ActionId();

    [GeneratedRegex("^[a-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    public static partial Regex InstanceId();

    [GeneratedRegex("^[a-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    public static partial Regex ParameterKey();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    public static partial Regex Currency();

    [GeneratedRegex("^[a-z0-9_@.-]{1,128}$", RegexOptions.CultureInvariant)]
    public static partial Regex ItemSection();

    public const int UserPresetNameMaxLength = 80;

    public static bool TryNormalizeUserPresetName(string? value, out string normalized)
    {
        normalized = Regex.Replace((value ?? string.Empty).Trim(), "\\s+", " ");
        return normalized.Length is > 0 and <= UserPresetNameMaxLength;
    }

    public static IReadOnlyList<string> ValidateTrigger(TriggerRule trigger)
    {
        var errors = new List<string>();
        if (trigger.Amount < 0m || trigger.Amount > 1_000_000_000m)
        {
            errors.Add("сумма должна быть от 0 до 1 000 000 000");
        }

        try
        {
            trigger.Currency = Money.NormalizeCurrency(trigger.Currency);
        }
        catch (ArgumentException exception)
        {
            errors.Add(exception.Message);
        }

        if (string.IsNullOrWhiteSpace(trigger.Id))
        {
            errors.Add("отсутствует идентификатор условия");
        }

        return errors;
    }
}

public static class RuleMatcher
{
    public static RuleMatch? Match(DonationEvent donation, IEnumerable<ActionDefinition> actions)
        => MatchAll(donation, actions).FirstOrDefault();

    public static IReadOnlyList<RuleMatch> MatchAll(DonationEvent donation, IEnumerable<ActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(donation);
        ArgumentNullException.ThrowIfNull(actions);

        var currency = Money.NormalizeCurrency(donation.Currency);
        var candidates = actions
            .Where(action => action.IsActive)
            .SelectMany(action => action.Triggers
                .Where(trigger => trigger.Enabled)
                .Where(trigger => Money.TryNormalizeCurrency(trigger.Currency, out var triggerCurrency) && string.Equals(triggerCurrency, currency, StringComparison.Ordinal))
                .Where(trigger => trigger.Comparator == TriggerComparator.Exact
                    ? donation.Amount == trigger.Amount
                    : donation.Amount >= trigger.Amount)
                .Select(trigger => new RuleMatch { Action = action, Trigger = trigger }))
            .ToList();

        if (candidates.Count == 0) return [];
        var comparator = candidates.Any(match => match.Trigger.Comparator == TriggerComparator.Exact)
            ? TriggerComparator.Exact
            : TriggerComparator.GreaterOrEqual;
        var matchingComparator = candidates.Where(match => match.Trigger.Comparator == comparator).ToList();
        var amount = comparator == TriggerComparator.Exact
            ? donation.Amount
            : matchingComparator.Max(match => match.Trigger.Amount);
        // LINQ preserves the input action order, which is the stable order shown
        // in the Active tab and persisted in settings.json.
        return matchingComparator.Where(match => match.Trigger.Amount == amount).ToList();
    }
}

public static class CatalogValidation
{
    public static IReadOnlyList<string> Validate(IEnumerable<ActionDefinition> actions)
    {
        var materialized = actions.ToList();
        var errors = materialized.SelectMany(action => action.Validate()).ToList();
        var duplicateInstanceIds = materialized
            .Where(action => action.IsActive && !string.IsNullOrWhiteSpace(action.Id))
            .GroupBy(action => action.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateInstanceIds)
        {
            errors.Add($"Идентификатор экземпляра {duplicate.Key} назначен нескольким активным действиям");
        }
        return errors;
    }
}

public static class DefaultActionCatalog
{
    public const string RandomAllSelectionParameterKey = ActiveTemplateSelection.ParameterKey;
    public const string RandomWeaponSectionId = "__adb_random_weapon__";
    public const string RandomOutfitSectionId = "__adb_random_outfit__";
    public const string RandomHelmetSectionId = "__adb_random_helmet__";
    public const string RandomArtifactSectionId = "__adb_random_artifact__";
    public const string RandomAnomalySectionId = "__adb_random_anomaly__";
    public const string EachRandomAnomalySectionId = "__adb_each_random_anomaly__";
    private const string AddonScriptRoot = "gamedata\\scripts\\";
    private const string ActorActionsScript = AddonScriptRoot + "anomaly_donation_bridge_actions_actor.script";
    private const string InventoryActionsScript = AddonScriptRoot + "anomaly_donation_bridge_actions_inventory.script";
    private const string SpawnActionsScript = AddonScriptRoot + "anomaly_donation_bridge_actions_spawn.script";
    private static readonly string[] PresetDisplayOrder =
    [
        "add_radiation", "drop_active_weapon", "block_inventory", "spawn_mutants", "spawn_npcs",
        "spawn_npc_squads", "spawn_mutant_squads",
        "spawn_random_hostile_squad", "spawn_random_mutant_pack",
        "spawn_random_npcs", "spawn_random_mutants", "spawn_named_companion",
        "spawn_mutant_companion", "spawn_donor_boss", "kill_companions", "change_health",
        "change_needs", "spawn_items", "change_money", "spawn_anomaly", "get_drunk",
        "trigger_emission", "give_random_stash", "tell_joke", "sleep_hours", "remove_equipment", "damage_equipment", "god_mode",
        "drop_random_items", "teleport_random", "teleport_random_level",
        "delete_recent_saves", "kill_nearby_npcs", DonationDispatcher.RandomActiveHandlerId,
        DonationDispatcher.RandomAllPresetsHandlerId, "use_random_consumable",
        "change_player_faction", "change_player_name", "actor_boost", "actor_parameters", "hide_hud"
    ];

    public static List<ActionDefinition> Create() => CreateDefaultActiveInstances();

    public static List<ActionDefinition> CreateDefaultActiveInstances()
    {
        var presets = CreatePresets().ToDictionary(action => action.HandlerId, StringComparer.Ordinal);
        return
        [
            Activate(presets["add_radiation"], Exact(1m)),
            Activate(presets["drop_active_weapon"], Exact(2m)),
            Activate(presets["spawn_mutants"], Exact(3m))
        ];
    }

    public static List<ActionDefinition> CreatePresets() => ConfigureRandomAllPresetSelection(OrderForDisplay(
    [
        new()
        {
            Id = "add_radiation",
            HandlerId = "add_radiation",
            DisplayName = "Добавить радиацию",
            Description = "Изменяет уровень радиационного заражения актёра.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("percent", "Изменение, %", ParameterKind.Percent, -100m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["percent"] = "25" }
        },
        new()
        {
            Id = "drop_active_weapon",
            HandlerId = "drop_active_weapon",
            DisplayName = "Выбить оружие",
            Description = "Выбивает текущее оружие один раз либо не даёт удерживать оружие в руках в течение выбранного времени.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_drop_active_weapon.script",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("wait_seconds", "Ждать оружие в руках, сек.", ParameterKind.Integer, 0m, 300m, "30"),
                Number("duration_seconds", "Выбрасывать оружие в течение, сек. (0 — один раз)", ParameterKind.Integer, 0m, 600m, "0")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["wait_seconds"] = "30",
                ["duration_seconds"] = "0"
            }
        },
        new()
        {
            Id = "block_inventory",
            HandlerId = "block_inventory",
            DisplayName = "Заблокировать инвентарь",
            Description = "Штатным callback Anomaly запрещает открывать обычный инвентарь на выбранное время. Торговля, ремонт и обыск не блокируются.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_block_inventory.script",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("duration_seconds", "Время блокировки, сек.", ParameterKind.Integer, 10m, 600m, "30")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["duration_seconds"] = "30"
            }
        },
        new()
        {
            Id = "spawn_mutants",
            HandlerId = "spawn_mutants",
            DisplayName = "Заспавнить мутантов",
            Description = "Создаёт выбранных мутантов из свежего безопасного каталога установленной игры.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogChoice("section_id", "Мутант", CatalogEntryKind.Mutant),
                Number("count", "Количество", ParameterKind.Integer, 1m, 99m, "3"),
                BooleanChoice("random_group", "Создавать случайную группу из добавленных", false),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 500m, "25"),
                SpawnPointMethodChoice()
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = string.Empty,
                ["count"] = "3",
                ["random_group"] = "false",
                ["radius_meters"] = "25", ["spawn_point_method"] = "boss_sample"
            },
            SpawnGroups = [new SpawnGroupEntry { VariantId = string.Empty, Strength = "any", Count = 3 }]
        },
        new()
        {
            Id = "spawn_npcs",
            HandlerId = "spawn_npcs",
            DisplayName = "Заспавнить NPC",
            Description = "Создаёт выбранных NPC из свежего безопасного каталога установленной игры.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogChoice("section_id", "NPC", CatalogEntryKind.Npc),
                Number("count", "Количество", ParameterKind.Integer, 1m, 99m, "1"),
                BooleanChoice("random_group", "Создавать случайную группу из добавленных", false),
                BooleanChoice("use_donor_name", "Спавнить NPC с ником донатера", false),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 500m, "25"),
                SpawnPointMethodChoice()
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = string.Empty,
                ["count"] = "1",
                ["random_group"] = "false",
                ["use_donor_name"] = "false",
                ["radius_meters"] = "25", ["spawn_point_method"] = "boss_sample"
            },
            SpawnGroups = [new SpawnGroupEntry { VariantId = string.Empty, Strength = "any", Count = 1 }]
        },
        new()
        {
            Id = "spawn_npc_squads",
            HandlerId = "spawn_npc_squads",
            DisplayName = "Спавн отряда NPC (вызывает проблемы)",
            Description = "Создаёт выбранные полноценные сквады NPC прямо возле персонажа.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogChoice("section_id", "Отряд NPC", CatalogEntryKind.StalkerSquad),
                Number("count", "Количество отрядов", ParameterKind.Integer, 1m, 99m, "1"),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 100m, "25"),
                BooleanChoice("force_hostile", "Всегда враждебны игроку", true)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = string.Empty,
                ["count"] = "1",
                ["radius_meters"] = "25",
                ["force_hostile"] = "true"
            },
            SpawnGroups = [new SpawnGroupEntry { VariantId = string.Empty, Strength = "any", Count = 1 }]
        },
        new()
        {
            Id = "spawn_named_companion",
            HandlerId = "spawn_named_companion",
            DisplayName = "Напарник с именем данатчика",
            Description = "Создаёт выбранного или случайного NPC как настоящего напарника, даёт ему имя донатчика и сохраняет в отряде при переходах между локациями. Получаемый урон можно уменьшить: 100% — обычный, 50% — половина, 10% — десятая часть.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_spawn_named_companion.script",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogChoice("section_id", "NPC-напарник", CatalogEntryKind.Npc),
                BooleanChoice("random_npc", "Случайный NPC (выбирает любого из списка)", false),
                BooleanChoice("use_player_faction", "Спавнить напарника группировкой игрока", true),
                new() { Key = "nickname_visibility", DisplayName = "3D ник по умолчанию", Kind = ParameterKind.Choice,
                    Options = ["show", "hide"], DefaultValue = "hide", OptionLabels = new() { ["show"] = "Показывать", ["hide"] = "Не показывать" } },
                new() { Key = "nickname_key", DisplayName = "Переключить 3D ники (по умолчанию PageUp)", Kind = ParameterKind.KeyboardKey, DefaultValue = "PRIOR" },
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 100m, "25"),
                Number("incoming_damage_percent", "Получаемый урон, %", ParameterKind.Integer, 1m, 100m, "100")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = string.Empty,
                ["random_npc"] = "false",
                ["use_player_faction"] = "true",
                ["nickname_visibility"] = "hide",
                ["nickname_key"] = "PRIOR",
                ["radius_meters"] = "25",
                ["incoming_damage_percent"] = "100"
            }
        },
        new()
        {
            Id = "spawn_mutant_companion",
            HandlerId = "spawn_mutant_companion",
            DisplayName = "Напарник-мутант (legacy)",
            Description = "Создаёт одного настоящего мутанта без симуляционного отряда. Мутант получает имя донатчика, следует за игроком, помогает в бою и переходит между локациями.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_spawn_mutant_companion.script",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogMultiChoice("mutant_sections", "Допустимые мутанты", CatalogEntryKind.Mutant),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["mutant_sections"] = string.Empty,
                ["radius_meters"] = "25"
            }
        },
        new()
        {
            Id = "spawn_donor_boss",
            HandlerId = "spawn_donor_boss",
            DisplayName = "Донат-босс",
            Description = "Создаёт на текущей локации одиночного NPC с именем донатчика. NPC принадлежит штатной враждебной группировке арены и преследует игрока.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_spawn_donor_boss.script",
            CategoryId = "npc_mutants",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogMultiChoice("npc_sections", "Допустимые варианты", CatalogEntryKind.Npc),
                Number("reward_money", "Награда за убийство, RU", ParameterKind.Integer, 0m, 100000000m, "10000"),
                Number("health_percent", "Здоровье босса, %", ParameterKind.Integer, 100m, 1000000m, "3000"),
                Number("lives", "Количество жизней", ParameterKind.Integer, 1m, 100m, "1"),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 1000m, "300"),
                BooleanChoice("always_follow", "Всегда следовать за игроком", true)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["npc_sections"] = string.Empty,
                ["reward_money"] = "10000",
                ["health_percent"] = "3000",
                ["lives"] = "1",
                ["radius_meters"] = "300",
                ["always_follow"] = "true"
            }
        },
        new()
        {
            Id = "change_player_name",
            HandlerId = "change_player_name",
            DisplayName = "Сменить имя героя на имя донатчика",
            Description = "Штатно меняет имя персонажа на имя донатчика. Новое имя хранится в сохранении игры.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_change_player_name.script",
            CategoryId = "player_state",
            CooldownSeconds = 3
        },
        new()
        {
            Id = "spawn_mutant_squads",
            HandlerId = "spawn_mutant_squads",
            DisplayName = "Спавн отряда мутантов (вызывает проблемы)",
            Description = "Создаёт выбранные полноценные сквады мутантов прямо возле персонажа.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogChoice("section_id", "Отряд мутантов", CatalogEntryKind.MutantSquad),
                Number("count", "Количество отрядов", ParameterKind.Integer, 1m, 99m, "1"),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 100m, "25"),
                BooleanChoice("force_hostile", "Всегда враждебны игроку", true)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = string.Empty,
                ["count"] = "1",
                ["radius_meters"] = "25",
                ["force_hostile"] = "true"
            },
            SpawnGroups = [new SpawnGroupEntry { VariantId = string.Empty, Strength = "any", Count = 1 }]
        },
        new()
        {
            Id = "spawn_random_hostile_squad",
            HandlerId = "spawn_random_hostile_squad",
            DisplayName = "Случайный сквад NPC (вызывает проблемы)",
            Description = "Создаёт настоящий отряд выбранной группировки прямо возле персонажа.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogMultiChoice("squad_sections", "Разрешённые отряды", CatalogEntryKind.StalkerSquad),
                Number("minimum_count", "Минимум бойцов", ParameterKind.Integer, 1m, 99m, "1"),
                Number("maximum_count", "Максимум бойцов", ParameterKind.Integer, 1m, 99m, "10"),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 100m, "25"),
                BooleanChoice("force_hostile", "Всегда враждебны игроку", true)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["squad_sections"] = string.Empty,
                ["minimum_count"] = "1",
                ["maximum_count"] = "10",
                ["radius_meters"] = "25",
                ["force_hostile"] = "true"
            }
        },
        new()
        {
            Id = "spawn_random_mutant_pack",
            HandlerId = "spawn_random_mutant_pack",
            DisplayName = "Случайный сквад мутантов (вызывает проблемы)",
            Description = "Создаёт один из настоящих отрядов мутантов текущей сборки игры.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogMultiChoice("squad_sections", "Разрешённые стаи", CatalogEntryKind.MutantSquad),
                Number("minimum_count", "Минимум мутантов", ParameterKind.Integer, 1m, 99m, "1"),
                Number("maximum_count", "Максимум мутантов", ParameterKind.Integer, 1m, 99m, "10"),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 100m, "25"),
                BooleanChoice("force_hostile", "Всегда враждебны игроку", true)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["squad_sections"] = string.Empty,
                ["minimum_count"] = "1",
                ["maximum_count"] = "10",
                ["radius_meters"] = "25",
                ["force_hostile"] = "true"
            }
        },
        new()
        {
            Id = "spawn_random_npcs",
            HandlerId = "spawn_random_npcs",
            DisplayName = "Случайные NPC (без сквада)",
            Description = "Выбирает NPC из разрешённых секций и создаёт отдельных персонажей, а не симуляционный отряд.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogMultiChoice("npc_sections", "Разрешённые NPC", CatalogEntryKind.Npc),
                Number("minimum_count", "Минимум NPC", ParameterKind.Integer, 1m, 99m, "1"),
                Number("maximum_count", "Максимум NPC", ParameterKind.Integer, 1m, 99m, "10"),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 500m, "25"),
                SpawnPointMethodChoice(),
                BooleanChoice("use_donor_name", "Спавнить NPC с ником донатера", false),
                BooleanChoice("force_hostile", "Всегда враждебны игроку", true)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["npc_sections"] = string.Empty,
                ["minimum_count"] = "1", ["maximum_count"] = "10",
                ["radius_meters"] = "25", ["spawn_point_method"] = "boss_sample", ["use_donor_name"] = "false",
                ["force_hostile"] = "true"
            }
        },
        new()
        {
            Id = "spawn_random_mutants",
            HandlerId = "spawn_random_mutants",
            DisplayName = "Случайные мутанты (без сквада)",
            Description = "Выбирает мутанта из разрешённых секций и создаёт отдельных существ, а не симуляционный отряд.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogMultiChoice("mutant_sections", "Разрешённые мутанты", CatalogEntryKind.Mutant),
                Number("minimum_count", "Минимум мутантов", ParameterKind.Integer, 1m, 99m, "1"),
                Number("maximum_count", "Максимум мутантов", ParameterKind.Integer, 1m, 99m, "10"),
                Number("radius_meters", "Радиус появления, м", ParameterKind.Integer, 10m, 500m, "25"),
                SpawnPointMethodChoice(),
                BooleanChoice("force_hostile", "Всегда враждебны игроку", true)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["mutant_sections"] = string.Empty,
                ["minimum_count"] = "1", ["maximum_count"] = "10",
                ["radius_meters"] = "25", ["spawn_point_method"] = "boss_sample", ["force_hostile"] = "true"
            }
        },
        new()
        {
            Id = "change_health",
            HandlerId = "change_health",
            DisplayName = "Изменить здоровье",
            Description = "Добавляет или отнимает процент здоровья.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("percent", "Изменение, %", ParameterKind.Percent, -100m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["percent"] = "25" }
        },
        new()
        {
            Id = "change_needs",
            HandlerId = "change_needs",
            DisplayName = "Изменить голод и жажду",
            Description = "Положительное значение утоляет голод или жажду, отрицательное усиливает, ноль не меняет шкалу.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("hunger_percent", "Голод, % (+ утолить / − усилить)", ParameterKind.Percent, -100m, 100m, "25"),
                Number("thirst_percent", "Жажда, % (+ утолить / − усилить)", ParameterKind.Percent, -100m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["hunger_percent"] = "25",
                ["thirst_percent"] = "25"
            }
        },
        new()
        {
            Id = "spawn_items",
            HandlerId = "spawn_items",
            DisplayName = "Выдать предметы в инвентарь",
            Description = "Добавляет выбранные предметы из безопасных категорий игрового спавнера.",
            ScriptRelativePath = InventoryActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogChoice("section_id", "Предмет", CatalogEntryKind.Item, ParameterKind.ItemChoice),
                Number("count", "Количество", ParameterKind.Integer, 1m, 50m, "1"),
                new ParameterDefinition
                {
                    Key = "weapon_options",
                    DisplayName = "Для оружия",
                    Kind = ParameterKind.MultiChoice,
                    Options = ["equip", "add_ammo"],
                    OptionLabels = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["equip"] = "Брать выданное оружие в руки",
                        ["add_ammo"] = "При выдаче оружия добавить патроны"
                    },
                    DefaultValue = string.Empty,
                    MinimumSelections = 0
                },
                Number("ammo_packs", "Пачек патронов на каждое оружие", ParameterKind.Integer, 1m, 50m, "1")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = string.Empty,
                ["count"] = "1",
                ["weapon_options"] = string.Empty,
                ["ammo_packs"] = "1"
            },
            ItemSpawns = [new ItemSpawnEntry { ItemId = string.Empty, Count = 1 }]
        },
        new()
        {
            Id = "spawn_anomaly",
            HandlerId = "spawn_anomaly",
            DisplayName = "Создать временную аномалию",
            Description = "Создаёт вокруг героя равномерное кольцо временных аномалий выбранного либо случайного типа.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogChoice("section_id", "Аномалия", CatalogEntryKind.Anomaly),
                Number("distance_meters", "Радиус спавна, м", ParameterKind.Integer, 0m, 100m, "7"),
                Number("count", "Количество аномалий", ParameterKind.Integer, 1m, 100m, "6"),
                Number("lifetime_seconds", "Время жизни, сек.", ParameterKind.Integer, 10m, 60000m, "60")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["section_id"] = string.Empty,
                ["distance_meters"] = "7",
                ["count"] = "6",
                ["lifetime_seconds"] = "60"
            }
        },
        new()
        {
            Id = "trigger_emission",
            HandlerId = "trigger_emission",
            DisplayName = "Вызвать выброс",
            Description = "Запускает штатный выброс на текущей локации, если он доступен и ещё не идёт.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3
        },
        new()
        {
            Id = "give_random_stash",
            HandlerId = "give_random_stash",
            DisplayName = "Выдать случайный тайник",
            Description = "Штатно выдаёт наводку на случайный доступный тайник, наполняет его, ставит метку и сообщает фактическую локацию в КПК.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_give_random_stash.script",
            CategoryId = "world",
            CooldownSeconds = 3
        },
        new()
        {
            Id = "tell_joke",
            HandlerId = "tell_joke",
            DisplayName = "Рассказать анекдот",
            Description = "Находит реальные звуковые реплики анекдотов в установленной игре и модах и проигрывает случайную реплику от лица героя.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_tell_joke.script",
            CooldownSeconds = 3
        },
        new()
        {
            Id = "sleep_hours",
            HandlerId = "sleep_hours",
            DisplayName = "Заставить персонажа поспать",
            Description = "Запускает штатное затемнение и пробуждение, сдвигает время и применяет обычные последствия сна.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("hours", "Продолжительность сна, часов", ParameterKind.Integer, 1m, 24m, "6"),
                BooleanChoice("alternative_sleep",
                    "Альтернативный способ сна (если обычный вариант не работает)", false)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["hours"] = "6",
                ["alternative_sleep"] = "false"
            }
        },
        new()
        {
            Id = "change_money",
            HandlerId = "change_money",
            DisplayName = "Изменить деньги",
            Description = "Положительная сумма добавляет игровые деньги, отрицательная — отнимает.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("amount", "Сумма, ₽ (+/−)", ParameterKind.Integer, -1_000_000m, 1_000_000m, "1000")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["amount"] = "1000" }
        },
        new()
        {
            Id = "get_drunk",
            HandlerId = "get_drunk",
            DisplayName = "Наложить эффект опьянения",
            Description = "Включает штатный эффект опьянения на выбранное время без предметов.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("duration_seconds", "Длительность опьянения, сек.", ParameterKind.Integer, 5m, 300m, "30")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["duration_seconds"] = "30" }
        },
        new()
        {
            Id = "remove_equipment",
            HandlerId = "remove_equipment",
            DisplayName = "Снять экипировку",
            Description = "Штатно снимает броню, шлем или оба предмета и оставляет их в инвентаре.",
            ScriptRelativePath = InventoryActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Choice("target", "Что снять", "both", "outfit", "helmet", "both")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["target"] = "both" }
        },
        new()
        {
            Id = "damage_equipment",
            HandlerId = "damage_equipment",
            DisplayName = "Повредить экипировку",
            Description = "Уменьшает текущее состояние брони, шлема или обоих предметов на выбранный процент.",
            ScriptRelativePath = InventoryActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Choice("target", "Что повредить", "outfit", "outfit", "helmet", "both"),
                Number("percent", "Уменьшить состояние на, %", ParameterKind.Percent, 1m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["target"] = "outfit",
                ["percent"] = "25"
            }
        },
        new()
        {
            Id = "god_mode",
            HandlerId = "god_mode",
            DisplayName = "Год-мод",
            Description = "Временно блокирует входящий урон. Раз в секунду снимает радиацию и кровотечение, восстанавливает пси-здоровье до 100%.\n\nЭффект сохраняется при быстрой загрузке и переходах между локациями, но не возобновляется после выхода из игры. Можно отключить таймер на экране или выбрать альтернативный способ через g_god.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_god_mode.script",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("duration_seconds", "Длительность неуязвимости, сек.", ParameterKind.Integer, 5m, 600m, "30"),
                BooleanChoice("show_timer", "Показывать таймер внизу экрана", true),
                BooleanChoice("use_g_god", "Альтернативный старый способ — g_god", false)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["duration_seconds"] = "30", ["show_timer"] = "true", ["use_g_god"] = "false"
            }
        },
        new()
        {
            Id = "drop_random_items",
            HandlerId = "drop_random_items",
            DisplayName = "Выбросить случайный предмет",
            Description = "Выбрасывает указанное число случайных безопасных предметов из любых типов и стаков; если их меньше — выбрасывает все доступные.",
            ScriptRelativePath = InventoryActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("count", "Количество предметов", ParameterKind.Integer, 1m, 999m, "1")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["count"] = "1" }
        },
        new()
        {
            Id = "actor_boost",
            HandlerId = "actor_boost",
            DisplayName = "Временно изменить параметры героя",
            Description = "На указанное время увеличивает или уменьшает высоту прыжка, скорость бега и переносимый вес, затем возвращает прежние значения. Эффект хранится только в памяти текущего процесса игры: переживает F9 и переходы, но не возобновляется после выхода или вылета. Конфиги не меняются. Рост скорости ограничен безопасным пределом.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_actor_boost.script",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("jump_percent", "Высота прыжка, %", ParameterKind.Integer, -90m, 300m, "50"),
                Number("speed_percent", "Скорость перемещения, %", ParameterKind.Integer, -90m, 50m, "50"),
                Number("carry_weight_kg", "Переносимый вес, кг", ParameterKind.Integer, -500m, 500m, "50"),
                Number("duration_seconds", "Длительность, сек.", ParameterKind.Integer, 5m, 3600m, "60")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["jump_percent"] = "50", ["speed_percent"] = "50",
                ["carry_weight_kg"] = "50", ["duration_seconds"] = "60"
            }
        },
        new()
        {
            Id = "actor_parameters",
            HandlerId = "actor_parameters",
            DisplayName = "Постоянно изменить параметры героя",
            Description = "Навсегда для текущего сохранения увеличивает или уменьшает высоту прыжка, скорость и переносимый вес. Повторные срабатывания складываются, но рост скорости ограничен безопасным пределом.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_actor_parameters.script",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("jump_percent", "Высота прыжка, %", ParameterKind.Integer, -90m, 300m, "50"),
                Number("speed_percent", "Скорость перемещения, %", ParameterKind.Integer, -90m, 50m, "50"),
                Number("carry_weight_kg", "Переносимый вес, кг", ParameterKind.Integer, -500m, 500m, "50")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["jump_percent"] = "50", ["speed_percent"] = "50",
                ["carry_weight_kg"] = "50"
            }
        },
        new()
        {
            Id = "hide_hud",
            HandlerId = "hide_hud",
            DisplayName = "Временно отключить HUD",
            Description = "Штатной консольной командой скрывает HUD на выбранное время, затем возвращает прежнее состояние.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_hide_hud.script",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("duration_seconds", "Длительность, сек.", ParameterKind.Integer, 5m, 600m, "30")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["duration_seconds"] = "30" }
        },
        new()
        {
            Id = "teleport_random",
            HandlerId = "teleport_random",
            DisplayName = "Случайный телепорт",
            Description = "Перемещает персонажа на безопасную вершину текущей локации.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("minimum_meters", "Минимум, м", ParameterKind.Integer, 10m, 2000m, "25"),
                Number("maximum_meters", "Максимум, м", ParameterKind.Integer, 10m, 2000m, "100")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["minimum_meters"] = "25",
                ["maximum_meters"] = "100"
            }
        },
        new()
        {
            Id = "teleport_random_level",
            HandlerId = "teleport_random_level",
            DisplayName = "Телепортация на случайную локацию",
            Description = "Выбирает одну из отмеченных локаций и точку её игровой AI-сети, затем переносит персонажа как при переходе между локациями.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogMultiChoice("level_sections", "Доступные локации для телепортации", CatalogEntryKind.Level),
                new ParameterDefinition
                {
                    Key = "level_options",
                    DisplayName = "Ограничения",
                    Kind = ParameterKind.MultiChoice,
                    Options = ["visited_only"],
                    OptionLabels = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["visited_only"] = "Телепортировать только в посещённые локации"
                    },
                    MinimumSelections = 0,
                    DefaultValue = string.Empty
                }
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["level_sections"] = string.Empty,
                ["level_options"] = string.Empty
            }
        },
        new()
        {
            Id = "delete_recent_saves",
            HandlerId = "delete_recent_saves",
            DisplayName = "Удалить последние N сохранений",
            Description = "Убирает выбранное число последних сохранений из списка загрузки; 999 означает все. Комплект файлов можно восстановить из savedgames\\ADB_Removed.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("count", "Количество последних сохранений", ParameterKind.Integer, 1m, 999m, "1")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["count"] = "1" }
        },
        new()
        {
            Id = "kill_nearby_npcs",
            HandlerId = "kill_nearby_npcs",
            DisplayName = "Убить ближайших NPC",
            Description = "Убивает всех живых NPC в заданном радиусе от игрока. Важные NPC и мутанты защищены, пока не разрешены отдельно.",
            ScriptRelativePath = SpawnActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("radius_meters", "Радиус от игрока, м", ParameterKind.Integer, 1m, 999m, "25"),
                BooleanChoice("kill_important_npcs", "Убивать важных NPC", false),
                BooleanChoice("kill_mutants", "Убивать мутантов", false),
                BooleanChoice("only_enemies", "Убивать только врагов", false)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["radius_meters"] = "25", ["kill_important_npcs"] = "false",
                ["kill_mutants"] = "false", ["only_enemies"] = "false"
            }
        },
        new()
        {
            Id = "kill_companions",
            HandlerId = "kill_companions",
            DisplayName = "Убить напарников",
            Description = "Убивает выбранное число случайных обычных NPC-напарников. Значение 99 убивает всех; экспериментальные мутанты-напарники не затрагиваются.",
            ScriptRelativePath = AddonScriptRoot + "anomaly_donation_bridge_effect_kill_companions.script",
            CategoryId = "npc_mutants",
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                Number("count", "Количество напарников", ParameterKind.Integer, 1m, 99m, "1")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["count"] = "1" }
        },
        new()
        {
            Id = DonationDispatcher.RandomActiveHandlerId,
            HandlerId = DonationDispatcher.RandomActiveHandlerId,
            DisplayName = "Рандомный по активным шаблонам",
            Description = "При срабатывании выбирает один из остальных доступных активных вариантов.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3
        },
        new()
        {
            Id = DonationDispatcher.RandomAllPresetsHandlerId,
            HandlerId = DonationDispatcher.RandomAllPresetsHandlerId,
            DisplayName = ActiveTemplateSelection.DisplayName,
            Description = ActiveTemplateSelection.Description,
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3
        },
        new()
        {
            Id = "use_random_consumable",
            HandlerId = "use_random_consumable",
            DisplayName = "Использовать случайный расходник",
            Description = "Штатно использует случайную еду, напиток или медицинский предмет и расходует одно применение.",
            ScriptRelativePath = InventoryActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                new ParameterDefinition
                {
                    Key = "sources",
                    DisplayName = "Откуда брать предмет",
                    Kind = ParameterKind.MultiChoice,
                    Options = ["hotbar", "inventory"],
                    OptionLabels = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["hotbar"] = "Брать предметы с хотбара",
                        ["inventory"] = "Брать предметы из инвентаря"
                    },
                    DefaultValue = "hotbar,inventory",
                    MinimumSelections = 1
                }
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["sources"] = "hotbar,inventory"
            }
        },
        new()
        {
            Id = "change_player_faction",
            HandlerId = "change_player_faction",
            DisplayName = "Изменить группировку игрока",
            Description = "Случайно выбирает одну из отмеченных группировок текущей сборки и меняет группировку персонажа.",
            ScriptRelativePath = ActorActionsScript,
            CooldownSeconds = 3,
            ParameterDefinitions =
            [
                CatalogMultiChoice("faction_communities", "Возможные группировки", CatalogEntryKind.Faction)
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["faction_communities"] = string.Empty
            }
        }
    ]));

    private static List<ActionDefinition> ConfigureRandomAllPresetSelection(List<ActionDefinition> catalog)
    {
        RefreshRandomAllPresetSelection(catalog);
        return catalog;
    }

    private static List<ActionDefinition> OrderForDisplay(List<ActionDefinition> catalog)
    {
        var order = PresetDisplayOrder
            .Select((handlerId, index) => (handlerId, index))
            .ToDictionary(item => item.handlerId, item => item.index, StringComparer.Ordinal);
        return catalog
            .OrderBy(action => order.TryGetValue(action.HandlerId, out var index) ? index : int.MaxValue)
            .ThenBy(action => action.HandlerId, StringComparer.Ordinal)
            .ToList();
    }

    public static void RefreshRandomAllPresetSelection(IList<ActionDefinition> catalog)
    {
        var randomAll = catalog.FirstOrDefault(action =>
            string.Equals(action.HandlerId, DonationDispatcher.RandomAllPresetsHandlerId, StringComparison.Ordinal));
        if (randomAll is null)
        {
            return;
        }

        randomAll.ParameterDefinitions = [ActiveTemplateSelection.CreateDefinition()];
        randomAll.Parameters.Remove("included_presets");
        randomAll.Parameters.Remove("include_items");
        randomAll.Parameters.TryAdd(ActiveTemplateSelection.ParameterKey, string.Empty);
    }
    private static ActionDefinition Activate(ActionDefinition preset, params TriggerRule[] triggers)
    {
        var instance = preset.Clone();
        instance.Id = ActionDefinition.NewInstanceId();
        instance.IsActive = true;
        instance.Triggers = [.. triggers];
        return instance;
    }

    private static TriggerRule Exact(decimal amount) => new()
    {
        Comparator = TriggerComparator.Exact,
        Amount = amount,
        Currency = "RUB"
    };

    private static ParameterDefinition Number(string key, string name, ParameterKind kind, decimal min, decimal max, string defaultValue) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = kind,
        Minimum = min,
        Maximum = max,
        DefaultValue = defaultValue
    };

    private static ParameterDefinition Choice(string key, string name, string defaultValue, params string[] options) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = ParameterKind.Choice,
        Options = [.. options],
        DefaultValue = defaultValue
    };

    private static ParameterDefinition SpawnPointMethodChoice() => new()
    {
        Key = "spawn_point_method", DisplayName = "Способ поиска места появления", Kind = ParameterKind.Choice,
        DefaultValue = "boss_sample", Options = ["boss_sample", "legacy", "graph", "scan", "coordinates"],
        OptionLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["boss_sample"] = "Случайные точки локации (рекомендуется)", ["legacy"] = "По прямой от игрока",
            ["graph"] = "С обходом препятствий", ["scan"] = "Полный поиск по локации",
            ["coordinates"] = "Случайные координаты"
        }
    };

    private static ParameterDefinition BooleanChoice(string key, string name, bool defaultValue) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = ParameterKind.Choice,
        Options = ["false", "true"],
        OptionLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["false"] = "Выключено",
            ["true"] = "Включено"
        },
        DefaultValue = defaultValue ? "true" : "false"
    };

    private static ParameterDefinition CatalogChoice(
        string key,
        string name,
        CatalogEntryKind catalogKind,
        ParameterKind kind = ParameterKind.Choice) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = kind,
        CatalogKind = catalogKind,
        DefaultValue = string.Empty
    };

    private static ParameterDefinition CatalogMultiChoice(
        string key,
        string name,
        CatalogEntryKind catalogKind) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = ParameterKind.MultiChoice,
        CatalogKind = catalogKind,
        DefaultValue = string.Empty
    };

    private static ParameterDefinition Item(string key, string name, string defaultValue, params string[] options) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = ParameterKind.ItemChoice,
        Options = [.. options],
        DefaultValue = defaultValue
    };

    private static ParameterDefinition GroupedItem(
        string key,
        string name,
        string defaultValue,
        params (string Group, string[] Options)[] groups) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = ParameterKind.ItemChoice,
        Options = groups.SelectMany(group => group.Options).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        OptionGroups = groups.ToDictionary(
            group => group.Group,
            group => group.Options.ToList(),
            StringComparer.OrdinalIgnoreCase),
        DefaultValue = defaultValue
    };
}

public sealed class CurrencyCatalog
{
    private static readonly string[] Defaults = ["RUB", "USD", "EUR", "UAH", "KZT", "BYN", "BRL", "TRY"];
    private readonly HashSet<string> _currencies = new(Defaults, StringComparer.Ordinal);

    public ReadOnlyCollection<string> All => _currencies.OrderBy(value => value, StringComparer.Ordinal).ToList().AsReadOnly();

    public bool AddObserved(string currency) => _currencies.Add(Money.NormalizeCurrency(currency));
}
