using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace AnomalyDonationBridge.Core;

public enum EngineProfile
{
    Unknown,
    Anomaly153Vanilla,
    XRayMonolithStandard,
    XRayMonolithMtTest
}

public enum CatalogEntryKind
{
    Mutant,
    Npc,
    StalkerSquad,
    MutantSquad,
    Item,
    Anomaly,
    Faction,
    Level,
    Helicopter,
    GuitarMusic,
    ArmedVehicle,
    NpcVisual
}

public enum CatalogStrength
{
    Unclassified,
    Lite,
    Normal,
    Strong
}

public enum QuarantineReason
{
    None,
    StoryOrUnique,
    NonTradeable,
    Scripted,
    InvalidStructure,
    Blacklisted,
    MissingRequiredFields,
    UnsupportedClass
}

/// <summary>
/// Identifies one complete catalog produced during the current game session.
/// The value is opaque; consumers must only compare it using ordinal equality.
/// </summary>
public readonly record struct CatalogRevision
{
    private static readonly Regex Format = new(
        "^[A-Za-z0-9._:-]{1,128}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public CatalogRevision(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Format.IsMatch(value))
        {
            throw new ArgumentException(
                "Ревизия каталога должна содержать от 1 до 128 ASCII-символов: буквы, цифры, '.', '_', ':' или '-'.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value ?? string.Empty;

    public static bool TryParse(string? value, out CatalogRevision revision)
    {
        if (!string.IsNullOrWhiteSpace(value) && Format.IsMatch(value))
        {
            revision = new CatalogRevision(value);
            return true;
        }

        revision = default;
        return false;
    }
}

public sealed class CapabilitySet
{
    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.Ordinal);

    public static CapabilitySet Empty { get; } = new();

    public EngineProfile Profile { get; init; } = EngineProfile.Unknown;
    public string EngineVersion { get; init; } = string.Empty;
    public IReadOnlySet<string> CapabilityIds { get; init; } = EmptySet;
    public IReadOnlySet<string> SupportedHandlerIds { get; init; } = EmptySet;

    public bool SupportsCapability(string capabilityId) =>
        CapabilityIds.Contains(capabilityId ?? string.Empty);

    public bool SupportsHandler(string handlerId) =>
        SupportedHandlerIds.Contains(handlerId ?? string.Empty);

    public static CapabilitySet Create(
        EngineProfile profile,
        string? engineVersion,
        IEnumerable<string>? capabilityIds,
        IEnumerable<string>? supportedHandlerIds)
    {
        var capabilities = NormalizeIds(capabilityIds, "возможности");
        var handlers = NormalizeIds(supportedHandlerIds, "обработчика");
        return new CapabilitySet
        {
            Profile = profile,
            EngineVersion = (engineVersion ?? string.Empty).Trim(),
            CapabilityIds = capabilities,
            SupportedHandlerIds = handlers
        };
    }

    private static HashSet<string> NormalizeIds(IEnumerable<string>? values, string label)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            if (!Validation.ActionId().IsMatch(value))
            {
                throw new ArgumentException($"Недопустимый идентификатор {label}: {value}");
            }

            if (!result.Add(value))
            {
                throw new ArgumentException($"Повторяющийся идентификатор {label}: {value}");
            }
        }

        return result;
    }
}

public sealed record CatalogEntry
{
    public required string SectionId { get; init; }
    public required CatalogEntryKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public string Category { get; init; } = string.Empty;
    public CatalogStrength Strength { get; init; } = CatalogStrength.Unclassified;
    public string Faction { get; init; } = string.Empty;
    public string NativeClass { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
    public QuarantineReason QuarantineReason { get; init; }

    // Older saved catalogs could mark a freshly created torrid zone available.
    // Preserve the wire/catalog hash, but never turn that stale entry into a
    // random choice or allow it through command validation.
    public bool IsNativeSpawnUnsafe => Kind == CatalogEntryKind.Anomaly &&
        string.Equals(Category, "ZS_TORRD", StringComparison.OrdinalIgnoreCase);
    public bool IsQuarantined => !IsAvailable || QuarantineReason != QuarantineReason.None ||
        IsNativeSpawnUnsafe;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SectionId) || SectionId.Length > 256 ||
            SectionId.Any(character => character is '\t' or '\r' or '\n' or ','))
        {
            throw new InvalidDataException($"Некорректная секция каталога: {SectionId}");
        }

        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 1024)
        {
            throw new InvalidDataException($"У секции {SectionId} отсутствует допустимое название.");
        }

        if (Category.Length > 256 || Faction.Length > 128 || NativeClass is null || NativeClass.Length > 32 ||
            NativeClass.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new InvalidDataException($"Метаданные секции {SectionId} превышают допустимую длину.");
        }

        if (IsAvailable && QuarantineReason != QuarantineReason.None)
        {
            throw new InvalidDataException($"Доступная секция {SectionId} не может иметь причину карантина.");
        }

        if (!IsAvailable && QuarantineReason == QuarantineReason.None)
        {
            throw new InvalidDataException($"Недоступная секция {SectionId} должна иметь причину карантина.");
        }
    }
}

public sealed class CatalogSnapshot
{
    private readonly IReadOnlyDictionary<(CatalogEntryKind Kind, string SectionId), CatalogEntry> _entriesByKey;

    public CatalogSnapshot(
        CatalogRevision revision,
        string contentHash,
        IEnumerable<CatalogEntry> entries,
        DateTimeOffset? receivedAt = null,
        string monsterCommunities = "")
    {
        if (revision.IsEmpty)
        {
            throw new ArgumentException("Ревизия каталога не задана.", nameof(revision));
        }

        if (!IsValidContentHash(contentHash))
        {
            throw new ArgumentException(
                "Хеш каталога должен быть SHA-256 в виде 64 строчных шестнадцатеричных символов.",
                nameof(contentHash));
        }

        var materialized = entries?.ToList() ?? throw new ArgumentNullException(nameof(entries));
        if (materialized.Count > 100_000)
        {
            throw new InvalidDataException("Каталог содержит больше 100 000 записей.");
        }

        var index = new Dictionary<(CatalogEntryKind, string), CatalogEntry>();
        foreach (var entry in materialized)
        {
            entry.Validate();
            if (!index.TryAdd((entry.Kind, entry.SectionId), entry))
            {
                throw new InvalidDataException(
                    $"Секция {entry.SectionId} типа {entry.Kind} передана в каталоге повторно.");
            }
        }

        _ = MonsterCommunityCatalog.Parse(monsterCommunities);
        MonsterCommunities = monsterCommunities;
        Revision = revision;
        ContentHash = contentHash;
        ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow;
        Entries = new ReadOnlyCollection<CatalogEntry>(materialized);
        _entriesByKey = new ReadOnlyDictionary<(CatalogEntryKind, string), CatalogEntry>(index);
    }

    public CatalogRevision Revision { get; }
    public string ContentHash { get; }
    public DateTimeOffset ReceivedAt { get; }
    public IReadOnlyList<CatalogEntry> Entries { get; }
    public string MonsterCommunities { get; }

    public IEnumerable<CatalogEntry> AvailableEntries(CatalogEntryKind kind) =>
        Entries.Where(entry => entry.Kind == kind && !entry.IsQuarantined);

    public bool TryGetEntry(CatalogEntryKind kind, string sectionId, out CatalogEntry entry) =>
        _entriesByKey.TryGetValue((kind, sectionId), out entry!);

    public bool ContainsAvailable(CatalogEntryKind kind, string sectionId) =>
        TryGetEntry(kind, sectionId, out var entry) && !entry.IsQuarantined;

    public static bool IsValidContentHash(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record CatalogCommandValidationResult(bool IsValid, string Reason)
{
    public bool IsTransient { get; init; }
    public static CatalogCommandValidationResult Valid { get; } = new(true, string.Empty);
    public static CatalogCommandValidationResult Invalid(string reason) => new(false, reason);
    public static CatalogCommandValidationResult Awaiting(string reason) => new(false, reason) { IsTransient = true };
}

public static class CatalogExclusionFilter
{
    public static CatalogSnapshot Apply(
        CatalogSnapshot source,
        IEnumerable<(CatalogEntryKind Kind, string SectionId)> exclusions)
    {
        ArgumentNullException.ThrowIfNull(source);
        var excluded = exclusions
            .Where(value => !string.IsNullOrWhiteSpace(value.SectionId))
            .ToHashSet();
        if (excluded.Count == 0) return source;
        return new CatalogSnapshot(
            source.Revision,
            source.ContentHash,
            source.Entries.Select(entry => excluded.Contains((entry.Kind, entry.SectionId))
                ? entry with { IsAvailable = false, QuarantineReason = QuarantineReason.Blacklisted }
                : entry),
            source.ReceivedAt, source.MonsterCommunities);
    }
}

public static class HistoryCatalogSelection
{
    public static IReadOnlyList<CatalogEntry> Resolve(
        EventHistoryEntry history,
        CatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(catalog);
        var action = history.ActionSnapshot;
        if (action is null) return [];

        var result = new Dictionary<(CatalogEntryKind Kind, string SectionId), CatalogEntry>();
        void Add(CatalogEntryKind kind, string? sectionId)
        {
            sectionId = (sectionId ?? string.Empty).Trim();
            if (catalog.TryGetEntry(kind, sectionId, out var entry))
                result.TryAdd((entry.Kind, entry.SectionId), entry);
        }
        void AddRandomItems(string category)
        {
            foreach (var entry in catalog.Entries.Where(entry =>
                         entry.Kind == CatalogEntryKind.Item &&
                         !entry.IsQuarantined &&
                         string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase)))
                result.TryAdd((entry.Kind, entry.SectionId), entry);
        }

        if (string.Equals(action.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal))
        {
            foreach (var item in action.ItemSpawns)
            {
                if (string.Equals(item.ItemId, DefaultActionCatalog.RandomWeaponSectionId, StringComparison.Ordinal))
                    AddRandomItems("weapons");
                else if (string.Equals(item.ItemId, DefaultActionCatalog.RandomOutfitSectionId, StringComparison.Ordinal))
                    AddRandomItems("outfits");
                else if (string.Equals(item.ItemId, DefaultActionCatalog.RandomHelmetSectionId, StringComparison.Ordinal))
                    AddRandomItems("helmets");
                else if (string.Equals(item.ItemId, DefaultActionCatalog.RandomArtifactSectionId, StringComparison.Ordinal))
                    AddRandomItems("artefacts");
                else Add(CatalogEntryKind.Item, item.ItemId);
            }
        }
        else if (SpawnGroupBundleCodec.Supports(action.HandlerId))
        {
            var kind = action.HandlerId switch
            {
                "spawn_mutants" => CatalogEntryKind.Mutant,
                "spawn_npcs" => CatalogEntryKind.Npc,
                "spawn_npc_squads" => CatalogEntryKind.StalkerSquad,
                "spawn_mutant_squads" => CatalogEntryKind.MutantSquad,
                _ => (CatalogEntryKind?)null
            };
            if (kind is { } actualKind)
                foreach (var group in action.SpawnGroups) Add(actualKind, group.VariantId);
        }

        foreach (var definition in action.ParameterDefinitions.Where(definition =>
                     definition.CatalogKind is not null))
        {
            if (((string.Equals(action.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal) &&
                  action.ItemSpawns.Count > 0) ||
                 (SpawnGroupBundleCodec.Supports(action.HandlerId) && action.SpawnGroups.Count > 0)) &&
                string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = action.Parameters.TryGetValue(definition.Key, out var saved)
                ? saved
                : definition.DefaultValue;
            foreach (var section in (value ?? string.Empty).Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (section is DefaultActionCatalog.RandomAnomalySectionId or
                    DefaultActionCatalog.EachRandomAnomalySectionId)
                {
                    foreach (var entry in catalog.Entries.Where(entry =>
                                 entry.Kind == CatalogEntryKind.Anomaly && !entry.IsQuarantined))
                        result.TryAdd((entry.Kind, entry.SectionId), entry);
                }
                else Add(definition.CatalogKind!.Value, section);
            }
        }

        return result.Values
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.SectionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public static class CatalogRevisionEnforcement
{
    public const string RevisionParameterKey = "catalog_revision";

    private static readonly IReadOnlyDictionary<string, SelectionRule> Rules =
        new Dictionary<string, SelectionRule>(StringComparer.Ordinal)
        {
            ["spawn_mutants"] = new(CatalogEntryKind.Mutant, SpawnGroupBundleCodec.ParameterKey, false),
            ["spawn_npcs"] = new(CatalogEntryKind.Npc, SpawnGroupBundleCodec.ParameterKey, false),
            ["spawn_npc_squads"] = new(CatalogEntryKind.StalkerSquad, SpawnGroupBundleCodec.ParameterKey, false),
            ["spawn_mutant_squads"] = new(CatalogEntryKind.MutantSquad, SpawnGroupBundleCodec.ParameterKey, false),
            ["spawn_random_hostile_squad"] = new(CatalogEntryKind.StalkerSquad, "squad_sections", true),
            ["spawn_random_mutant_pack"] = new(CatalogEntryKind.MutantSquad, "squad_sections", true),
            ["spawn_random_npcs"] = new(CatalogEntryKind.Npc, "npc_sections", true),
            ["spawn_random_mutants"] = new(CatalogEntryKind.Mutant, "mutant_sections", true),
            ["spawn_mutant_companion"] = new(CatalogEntryKind.Mutant, "mutant_sections", true),
            ["hardcoon.adb.mutant_companion_v2"] = new(CatalogEntryKind.Mutant, "mutant_sections", true),
            ["hardcoon.adb.helicopter_boss"] = new(CatalogEntryKind.Helicopter, "helicopter_sections", true),
            ["hardcoon.adb.helicopter_companion"] = new(CatalogEntryKind.Helicopter, "helicopter_sections", true),
            ["hardcoon.adb.wandering_trader"] = new(CatalogEntryKind.Npc, "visuals", true),
            ["hardcoon.adb.btr_companion"] = new(CatalogEntryKind.ArmedVehicle, "vehicle_sections", true),
            ["hardcoon.adb.play_guitar"] = new(CatalogEntryKind.GuitarMusic, "song_sections", true),
            ["hardcoon.adb.burer_form"] = new(CatalogEntryKind.Mutant, "mutant_sections", true),
            ["spawn_donor_boss"] = new(CatalogEntryKind.Npc, "npc_sections", true),
            ["hardcoon.adb.special_companion"] = new(CatalogEntryKind.NpcVisual, "section_id", false),
            ["hardcoon.adb.annoying_fan"] = new(CatalogEntryKind.Npc, "npc_sections", true),
            ["teleport_random_level"] = new(CatalogEntryKind.Level, "level_sections", true),
            ["spawn_items"] = new(CatalogEntryKind.Item, ItemBundleCodec.ParameterKey, false),
            ["spawn_anomaly"] = new(CatalogEntryKind.Anomaly, "section_id", false),
            ["change_player_faction"] = new(CatalogEntryKind.Faction, "faction_communities", true)
        };

    public static bool RequiresCatalog(string handlerId) => Rules.ContainsKey(handlerId);

    public static bool RequiresCatalog(ActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Rules.ContainsKey(action.HandlerId) ||
               action.ParameterDefinitions.Any(definition => definition.CatalogKind is not null);
    }

    public static CatalogCommandValidationResult Validate(
        GameCommand command,
        CatalogSnapshot? currentCatalog)
    {
        ArgumentNullException.ThrowIfNull(command);
        var hasBuiltInRule = Rules.TryGetValue(command.ActionId, out var rule);
        if (command.Parameters.TryGetValue(RevisionParameterKey, out var parameterRevision))
        {
            if (command.CatalogRevision is not { } modeledRevision)
            {
                return CatalogCommandValidationResult.Invalid(
                    "catalog_revision является зарезервированным параметром протокола.");
            }

            if (!string.Equals(parameterRevision, modeledRevision.Value, StringComparison.Ordinal))
            {
                return CatalogCommandValidationResult.Invalid(
                    "Параметр catalog_revision конфликтует с моделью команды.");
            }
        }

        if (!hasBuiltInRule && command.CatalogRevision is null)
        {
            return CatalogCommandValidationResult.Valid;
        }

        if (currentCatalog is null)
        {
            return CatalogCommandValidationResult.Awaiting("Свежий каталог игры ещё не получен.");
        }

        if (command.CatalogRevision is not { } supplied)
        {
            return CatalogCommandValidationResult.Invalid(
                "Команда не содержит ревизию каталога, на основании которого она создана.");
        }

        if (supplied != currentCatalog.Revision)
        {
            return CatalogCommandValidationResult.Invalid("Команда создана для устаревшей ревизии каталога.");
        }

        // Extension handlers declare their dynamic parameter schema in the
        // application manifest. Their selected values have already passed the
        // current ActionDefinition options; the IPC layer still enforces that
        // the snapshot revision is live before sending them to Lua.
        if (!hasBuiltInRule)
        {
            return CatalogCommandValidationResult.Valid;
        }

        // Some built-ins choose one random entry from the live safe catalog in
        // Lua and therefore have no user-facing section parameter.  They still
        // require the exact current revision and at least one usable entry.
        if (string.IsNullOrEmpty(rule!.ParameterKey))
        {
            return currentCatalog.AvailableEntries(rule.Kind).Any()
                ? CatalogCommandValidationResult.Valid
                : CatalogCommandValidationResult.Invalid(
                    "В текущем безопасном каталоге нет NPC для случайного выбора.");
        }

        if (command.ActionId == "hardcoon.adb.special_companion" &&
            command.Parameters.TryGetValue("random_npc", out var randomVisual) &&
            string.Equals(randomVisual, "true", StringComparison.OrdinalIgnoreCase))
            return currentCatalog.AvailableEntries(CatalogEntryKind.NpcVisual).Any()
                ? CatalogCommandValidationResult.Valid
                : CatalogCommandValidationResult.Invalid("В каталоге нет доступных особых внешностей NPC.");

        if (!command.Parameters.TryGetValue(rule.ParameterKey, out var rawSelection))
        {
            return CatalogCommandValidationResult.Invalid(
                $"В команде отсутствует динамический параметр {rule.ParameterKey}.");
        }

        if (string.Equals(command.ActionId, "spawn_items", StringComparison.Ordinal))
        {
            var items = ItemBundleCodec.TryParse(rawSelection);
            if (items is null) return CatalogCommandValidationResult.Invalid("Набор предметов повреждён или слишком велик.");
            foreach (var item in items)
            {
                if (string.Equals(item.ItemId, DefaultActionCatalog.RandomWeaponSectionId, StringComparison.Ordinal))
                {
                    if (!currentCatalog.AvailableEntries(CatalogEntryKind.Item).Any(IsWeaponEntry))
                        return CatalogCommandValidationResult.Invalid("В текущем безопасном каталоге нет оружия для случайной выдачи.");
                }
                else if (string.Equals(item.ItemId, DefaultActionCatalog.RandomOutfitSectionId, StringComparison.Ordinal))
                {
                    if (!currentCatalog.AvailableEntries(CatalogEntryKind.Item).Any(IsOutfitEntry))
                        return CatalogCommandValidationResult.Invalid("В текущем безопасном каталоге нет брони для случайной выдачи.");
                }
                else if (string.Equals(item.ItemId, DefaultActionCatalog.RandomHelmetSectionId, StringComparison.Ordinal))
                {
                    if (!currentCatalog.AvailableEntries(CatalogEntryKind.Item).Any(IsHelmetEntry))
                        return CatalogCommandValidationResult.Invalid("В текущем безопасном каталоге нет шлемов для случайной выдачи.");
                }
                else if (string.Equals(item.ItemId, DefaultActionCatalog.RandomArtifactSectionId, StringComparison.Ordinal))
                {
                    if (!currentCatalog.AvailableEntries(CatalogEntryKind.Item).Any(IsArtifactEntry))
                        return CatalogCommandValidationResult.Invalid("В текущем безопасном каталоге нет артефактов для случайной выдачи.");
                }
                else if (!currentCatalog.ContainsAvailable(CatalogEntryKind.Item, item.ItemId))
                    return CatalogCommandValidationResult.Invalid($"Секция {item.ItemId} отсутствует в текущем безопасном каталоге.");
            }
            return CatalogCommandValidationResult.Valid;
        }

        if (SpawnGroupBundleCodec.Supports(command.ActionId))
        {
            var groups = SpawnGroupBundleCodec.TryParse(rawSelection);
            if (groups is null) return CatalogCommandValidationResult.Invalid("Состав групп повреждён или слишком велик.");
            foreach (var group in groups)
            {
                if (!currentCatalog.TryGetEntry(rule!.Kind, group.VariantId, out var entry) || entry.IsQuarantined)
                    return CatalogCommandValidationResult.Invalid($"Секция {group.VariantId} отсутствует в текущем безопасном каталоге.");
            }
            return CatalogCommandValidationResult.Valid;
        }

        var sections = rule.Multiple
            ? rawSelection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [rawSelection.Trim()];
        if (sections.Length == 0 || sections.Any(string.IsNullOrWhiteSpace))
        {
            return CatalogCommandValidationResult.Invalid("Не выбрана игровая секция.");
        }

        if (sections.Distinct(StringComparer.Ordinal).Count() != sections.Length)
        {
            return CatalogCommandValidationResult.Invalid("Список секций содержит повторения.");
        }

        if (string.Equals(command.ActionId, "teleport_random_level", StringComparison.Ordinal))
        {
            return sections.Any(section => currentCatalog.ContainsAvailable(CatalogEntryKind.Level, section))
                ? CatalogCommandValidationResult.Valid
                : CatalogCommandValidationResult.Invalid(
                    "Среди выбранных локаций не осталось доступных после применения исключений.");
        }

        foreach (var section in sections)
        {
            if (string.Equals(command.ActionId, "spawn_items", StringComparison.Ordinal) &&
                string.Equals(section, DefaultActionCatalog.RandomWeaponSectionId, StringComparison.Ordinal))
            {
                if (!currentCatalog.AvailableEntries(CatalogEntryKind.Item).Any(IsWeaponEntry))
                {
                    return CatalogCommandValidationResult.Invalid(
                        "В текущем безопасном каталоге нет оружия для случайной выдачи.");
                }

                continue;
            }

            if (string.Equals(command.ActionId, "spawn_anomaly", StringComparison.Ordinal) &&
                section is DefaultActionCatalog.RandomAnomalySectionId or
                    DefaultActionCatalog.EachRandomAnomalySectionId)
            {
                if (!currentCatalog.AvailableEntries(CatalogEntryKind.Anomaly).Any())
                {
                    return CatalogCommandValidationResult.Invalid(
                        "В текущем безопасном каталоге нет аномалий для случайного выбора.");
                }

                continue;
            }

            if (!currentCatalog.ContainsAvailable(rule.Kind, section))
            {
                return CatalogCommandValidationResult.Invalid(
                    $"Секция {section} отсутствует в текущем безопасном каталоге.");
            }
        }

        return CatalogCommandValidationResult.Valid;
    }

    private static bool IsWeaponEntry(CatalogEntry entry) =>
        string.Equals(entry.Category, "weapons", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutfitEntry(CatalogEntry entry) =>
        string.Equals(entry.Category, "outfits", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelmetEntry(CatalogEntry entry) =>
        string.Equals(entry.Category, "helmets", StringComparison.OrdinalIgnoreCase);

    private static bool IsArtifactEntry(CatalogEntry entry) =>
        string.Equals(entry.Category, "artefacts", StringComparison.OrdinalIgnoreCase);

    public static void StampCurrentRevision(GameCommand command, CatalogSnapshot currentCatalog)
    {
        command.CatalogRevision = currentCatalog.Revision;
        var validation = Validate(command, currentCatalog);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Reason);
        }
    }

    private sealed record SelectionRule(CatalogEntryKind Kind, string ParameterKey, bool Multiple);
}

public static class DynamicCatalogBinding
{
    public static void Apply(CatalogSnapshot snapshot, IEnumerable<ActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(actions);

        foreach (var action in actions)
        {
            var dynamicDefinitions = action.ParameterDefinitions
                .Where(definition => definition.CatalogKind is not null)
                .ToList();
            if (CatalogRevisionEnforcement.RequiresCatalog(action))
            {
                action.CatalogRevision = snapshot.Revision;
            }

            foreach (var definition in dynamicDefinitions)
            {
                var entries = snapshot.AvailableEntries(definition.CatalogKind!.Value)
                    .Where(entry => definition.CatalogNativeClasses.Count == 0 ||
                        definition.CatalogNativeClasses.Contains(
                            entry.NativeClass, StringComparer.OrdinalIgnoreCase))
                    .Where(entry => definition.CatalogCategories.Count == 0 ||
                        definition.CatalogCategories.Contains(
                            entry.Category, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.Category, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(entry => entry.SectionId, StringComparer.Ordinal)
                    .ToList();

                definition.Options = entries.Select(entry => entry.SectionId).ToList();
                definition.OptionLabels = entries.ToDictionary(
                    entry => entry.SectionId,
                    entry => entry.DisplayName,
                    StringComparer.OrdinalIgnoreCase);
                definition.OptionGroups = entries
                    .GroupBy(entry => string.IsNullOrWhiteSpace(entry.Category) ? "Без категории" : entry.Category,
                        StringComparer.CurrentCultureIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(entry => entry.SectionId).ToList(),
                        StringComparer.CurrentCultureIgnoreCase);

                var isItemSpawner = string.Equals(
                    action.HandlerId, "spawn_items", StringComparison.Ordinal) &&
                    string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase);
                if (isItemSpawner)
                {
                    AddRandomItemChoice(definition, entries, "artefacts",
                        DefaultActionCatalog.RandomArtifactSectionId, "Случайный артефакт");
                    AddRandomItemChoice(definition, entries, "helmets",
                        DefaultActionCatalog.RandomHelmetSectionId, "Случайный шлем");
                    AddRandomItemChoice(definition, entries, "outfits",
                        DefaultActionCatalog.RandomOutfitSectionId, "Случайная броня");
                    AddRandomItemChoice(definition, entries, "weapons",
                        DefaultActionCatalog.RandomWeaponSectionId, "Случайное оружие");
                }

                var isAnomalySpawner = string.Equals(
                    action.HandlerId, "spawn_anomaly", StringComparison.Ordinal) &&
                    string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase);
                if (isAnomalySpawner && entries.Count > 0)
                {
                    definition.Options.Insert(0, DefaultActionCatalog.EachRandomAnomalySectionId);
                    definition.Options.Insert(0, DefaultActionCatalog.RandomAnomalySectionId);
                    definition.OptionLabels[DefaultActionCatalog.RandomAnomalySectionId] =
                        "Спавнить один случайный тип аномалии";
                    definition.OptionLabels[DefaultActionCatalog.EachRandomAnomalySectionId] =
                        "Каждая аномалия будет случайной";
                    definition.OptionGroups["Случайная"] =
                    [
                        DefaultActionCatalog.RandomAnomalySectionId,
                        DefaultActionCatalog.EachRandomAnomalySectionId
                    ];
                }

                var fallback = definition.Kind == ParameterKind.MultiChoice && definition.CatalogKind != CatalogEntryKind.NpcVisual &&
                    !(action.HandlerId == "hardcoon.adb.wandering_trader" && definition.Key == "visuals")
                    ? string.Join(',', definition.Options)
                    : entries.FirstOrDefault()?.SectionId ?? string.Empty;
                definition.DefaultValue = fallback;

                if (action.Parameters.TryGetValue(definition.Key, out var selected) &&
                    !string.IsNullOrWhiteSpace(selected))
                {
                    // Preserve both valid selections and stale saved identifiers.
                    // A stale non-empty value must remain visible and make the
                    // action invalid instead of silently changing its effect.
                    continue;
                }

                action.Parameters[definition.Key] = fallback;
            }

            // Composite presets keep their executable rows outside the legacy
            // parameter dictionary. Populate only genuinely blank rows after a
            // live catalog arrives; never replace a non-empty stale selection.
            if (string.Equals(action.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal) &&
                action.Parameters.TryGetValue("section_id", out var itemFallback))
            {
                foreach (var row in action.ItemSpawns.Where(row => string.IsNullOrWhiteSpace(row.ItemId)))
                    row.ItemId = itemFallback;
            }
            if (SpawnGroupBundleCodec.Supports(action.HandlerId) &&
                action.Parameters.TryGetValue("section_id", out var groupFallback))
            {
                foreach (var row in action.SpawnGroups.Where(row => string.IsNullOrWhiteSpace(row.VariantId)))
                    row.VariantId = groupFallback;
            }
        }
    }

    private static void AddRandomItemChoice(
        ParameterDefinition definition,
        IReadOnlyCollection<CatalogEntry> entries,
        string sourceCategory,
        string sectionId,
        string label)
    {
        if (!entries.Any(entry => string.Equals(
                entry.Category, sourceCategory, StringComparison.OrdinalIgnoreCase))) return;
        definition.Options.Insert(0, sectionId);
        definition.OptionLabels[sectionId] = label;
        definition.OptionGroups[label] = [sectionId];
    }
}
