using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Persistence;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = SettingsNormalizer.CurrentSchemaVersion;
    public string GamePath { get; set; } = string.Empty;
    public string EffectMo2ModPath { get; set; } = string.Empty;
    public string EditorPath { get; set; } = "notepad.exe";
    public string AnonymousDonorName { get; set; } = SettingsNormalizer.DefaultAnonymousDonorName;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool RestartAfterCrash { get; set; } = true;
    public bool HideUnavailableCatalogSections { get; set; } = true;
    public bool UseLightTheme { get; set; }
    public string ToggleDonationProcessingHotkey { get; set; } = string.Empty;
    public string RepeatLastDonationHotkey { get; set; } = string.Empty;
    public List<ActivePresetHotkey> ActivePresetHotkeys { get; set; } = [];
    public bool Armed { get; set; }
    public string DonationAlertsClientId { get; set; } = string.Empty;
    public long? DonationAlertsUserId { get; set; }
    public string DonationAlertsAccountCode { get; set; } = string.Empty;
    public string DonationAlertsAccountName { get; set; } = string.Empty;
    public string ExpectedAccountCode { get; set; } = string.Empty;
    public bool LimitDonationAlertsApiRequests { get; set; } = true;
    public bool InitialDonationSnapshotCompleted { get; set; }
    public string LastDonationId { get; set; } = string.Empty;
    public DateTimeOffset? LastDonationCreatedAt { get; set; }
    public string DonateXUserId { get; set; } = string.Empty;
    public string DonateXAccountName { get; set; } = string.Empty;
    public bool LimitDonateXApiRequests { get; set; } = true;
    public bool DonateXInitialDonationSnapshotCompleted { get; set; }
    public string LastDonateXDonationId { get; set; } = string.Empty;
    public DateTimeOffset? LastDonateXDonationCreatedAt { get; set; }
    public string YouTubeChatVideoUrl { get; set; } = string.Empty;
    public bool YouTubeChatEnabled { get; set; }
    public QueuePolicy QueuePolicy { get; set; } = new();
    public int GlobalCooldownSeconds { get; set; } = 5;
    public int FortuneWheelIdleMinutes { get; set; }
    public bool SaveBeforeDonationEffects { get; set; }
    public bool SaveAfterDonationEffects { get; set; }
    public int EffectSaveRetentionCount { get; set; } = 50;
    public bool ShowGameEffectLog { get; set; } = true;
    public bool ShowPdaEffectMessage { get; set; }
    public List<CatalogExclusion> CatalogExclusions { get; set; } = [];
    public SafeZoneSettings SafeZone { get; set; } = new();
    public List<string> ObservedCurrencies { get; set; } = ["RUB", "USD", "EUR", "UAH", "KZT", "BYN", "BRL", "TRY"];
    public List<ActionDefinition> Actions { get; set; } = [];
    public List<ActionDefinition> UserPresets { get; set; } = [];
    public string ActiveProfileId { get; set; } = string.Empty;
    public List<DonationProfile> Profiles { get; set; } = [];
}

public sealed class ActivePresetHotkey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int PresetNumber { get; set; } = 1;
    public string Hotkey { get; set; } = string.Empty;
}

public sealed class CatalogExclusion
{
    public CatalogEntryKind Kind { get; set; }
    public string SectionId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DonationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Основной";
    public List<ActionDefinition> Actions { get; set; } = [];
    public List<ActionDefinition> UserPresets { get; set; } = [];
    public List<ActivePresetHotkey> ActivePresetHotkeys { get; set; } = [];
}

public sealed class SecretBundle
{
    public string ClientSecret { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string SocketConnectionToken { get; set; } = string.Empty;
    public string DonateXExternalToken { get; set; } = string.Empty;
    public string YouTubeApiKey { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAt { get; set; }

    public bool HasOAuthTokens => !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(RefreshToken);
    public bool HasDonateXToken => !string.IsNullOrWhiteSpace(DonateXExternalToken);
    public bool HasYouTubeApiKey => !string.IsNullOrWhiteSpace(YouTubeApiKey);

    public void ClearTokens()
    {
        AccessToken = string.Empty;
        RefreshToken = string.Empty;
        SocketConnectionToken = string.Empty;
        AccessTokenExpiresAt = default;
    }
}

public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public string SettingsBackupFile => Path.Combine(RootDirectory, "settings.json.bak");
    public string SecretsFile => Path.Combine(RootDirectory, "secrets.dpapi");
    public string HistoryFile => Path.Combine(RootDirectory, "history.jsonl");
    public string ProcessedDonationsFile => Path.Combine(RootDirectory, "processed-donations.jsonl");
    public string PendingQueueFile => Path.Combine(RootDirectory, "pending-queue.json");
    public string LogFile => Path.Combine(RootDirectory, "bridge.log");
    public string CatalogCacheFile => Path.Combine(RootDirectory, "catalog-cache.json");
    public string AddonInstallationReceiptFile => Path.Combine(RootDirectory, "addon-installation.json");
    public string AddonBackupsDirectory => Path.Combine(RootDirectory, "addon-backups");
    public string ExtensionsDirectory => Path.Combine(RootDirectory, "extensions");
    public string EffectInstallationReceiptFile => Path.Combine(RootDirectory, "effect-installation.json");
    public string EffectBackupsDirectory => Path.Combine(RootDirectory, "effect-backups");

    public static AppPaths CreateDefault()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "AnomalyDonationBridge"));
    }

    public void EnsureExists() => Directory.CreateDirectory(RootDirectory);
}

public static class SettingsNormalizer
{
    // Provider integrations only add optional JSON fields, so the persisted schema remains
    // compatible with 1.0.76 and other schema-13 builds. Version 14 was briefly
    // emitted by 1.0.77; accept it and normalize it back to 13.
    public const int CurrentSchemaVersion = 13;
    public const int MaximumReadableSchemaVersion = 14;
    public const int ProfileNameMaxLength = 80;
    public const int AnonymousDonorNameMaxLength = 64;
    public const string DefaultAnonymousDonorName = "Анонимный донатчик";
    private static readonly HashSet<string> RetiredBuiltInHandlerIds =
        new(StringComparer.Ordinal) { "drop_outfit" };

    public static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        var sourceSchemaVersion = settings.SchemaVersion <= 0 ? 1 : settings.SchemaVersion;
        if (sourceSchemaVersion > MaximumReadableSchemaVersion)
        {
            throw new InvalidDataException($"Версия settings.json {sourceSchemaVersion} новее поддерживаемой версии {MaximumReadableSchemaVersion}.");
        }
        settings.SchemaVersion = CurrentSchemaVersion;
        settings.GamePath ??= string.Empty;
        settings.EffectMo2ModPath ??= string.Empty;
        settings.EditorPath = string.IsNullOrWhiteSpace(settings.EditorPath) ? "notepad.exe" : settings.EditorPath.Trim();
        settings.AnonymousDonorName = NormalizeAnonymousDonorName(settings.AnonymousDonorName);
        settings.ToggleDonationProcessingHotkey = (settings.ToggleDonationProcessingHotkey ?? string.Empty).Trim();
        settings.RepeatLastDonationHotkey = (settings.RepeatLastDonationHotkey ?? string.Empty).Trim();
        settings.DonationAlertsClientId ??= string.Empty;
        settings.DonationAlertsAccountCode ??= string.Empty;
        settings.DonationAlertsAccountName ??= string.Empty;
        settings.LastDonationId ??= string.Empty;
        settings.DonateXUserId ??= string.Empty;
        settings.DonateXAccountName ??= string.Empty;
        settings.LastDonateXDonationId ??= string.Empty;
        settings.YouTubeChatVideoUrl ??= string.Empty;
        settings.GlobalCooldownSeconds = Math.Clamp(settings.GlobalCooldownSeconds, 0, 300);
        settings.FortuneWheelIdleMinutes = Math.Clamp(settings.FortuneWheelIdleMinutes, 0, 10_080);
        settings.EffectSaveRetentionCount = Math.Clamp(settings.EffectSaveRetentionCount, 0, 10_000);
        settings.CatalogExclusions = NormalizeCatalogExclusions(settings.CatalogExclusions);
        settings.SafeZone = SafeZoneSettings.Normalize(settings.SafeZone);
        settings.ExpectedAccountCode = (settings.ExpectedAccountCode ?? string.Empty).Trim().ToLowerInvariant();
        settings.QueuePolicy ??= new QueuePolicy();
        if (!Enum.IsDefined(settings.QueuePolicy.Mode))
        {
            throw new InvalidDataException("settings.json содержит неизвестный режим очереди.");
        }
        // Schema 5 guarantees delivery: legacy Skip/TTL values remain readable
        // but are migrated to an unlimited persistent queue.
        settings.QueuePolicy.Mode = QueueMode.WaitIndefinitely;
        settings.QueuePolicy.WaitMinutes = Math.Clamp(settings.QueuePolicy.WaitMinutes, 1, 24 * 60);
        settings.QueuePolicy.MaximumQueuedEvents = Math.Clamp(settings.QueuePolicy.MaximumQueuedEvents, 1, 10_000);
        settings.ObservedCurrencies = (settings.ObservedCurrencies ?? [])
            .Select(value =>
            {
                try { return Money.NormalizeCurrency(value); }
                catch (ArgumentException) { return null; }
            })
            .Where(value => value is not null)
            .Cast<string>()
            .Concat(["RUB", "USD", "EUR", "UAH", "KZT", "BYN", "BRL", "TRY"])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        NormalizeProfiles(settings, sourceSchemaVersion);
        var usedTriggerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in settings.Profiles.SelectMany(profile =>
                     profile.Actions.Concat(profile.UserPresets)))
        {
            action.ParameterDefinitions ??= [];
            action.Parameters = action.Parameters is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(action.Parameters, StringComparer.OrdinalIgnoreCase);
            action.ItemSpawns ??= [];
            MigrateLegacyItemBundle(action);
            action.SpawnGroups ??= [];
            MigrateLegacySpawnGroupBundle(action);
            if (SpawnGroupBundleCodec.Supports(action.HandlerId))
            {
                foreach (var group in action.SpawnGroups) group.Strength = "any";
            }
            RemoveObsoleteSpawnStrength(action);
            MigrateAnomalyRandomizationChoice(action);
            AddDonorBossNpcSelection(action);
            action.Triggers ??= [];
            foreach (var trigger in action.Triggers)
            {
                if (string.IsNullOrWhiteSpace(trigger.Id) || !usedTriggerIds.Add(trigger.Id))
                {
                    do
                    {
                        trigger.Id = Guid.NewGuid().ToString("N");
                    }
                    while (!usedTriggerIds.Add(trigger.Id));
                }
                if (Money.TryNormalizeCurrency(trigger.Currency, out var currency)) trigger.Currency = currency;
            }
        }
        return settings;
    }

    /// <summary>
    /// Removes runtime catalog payloads before writing settings or a portable
    /// rules file. Only the selected opaque section IDs are persisted.
    /// </summary>
    public static AppSettings CreatePersistenceSnapshot(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SynchronizeActiveProfile(settings);
        var actions = settings.Actions.Select(CreatePersistenceAction).ToList();
        var userPresets = (settings.UserPresets ?? []).Select(CreatePersistenceAction).ToList();
        var profiles = settings.Profiles.Select(profile => new DonationProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Actions = profile.Actions.Select(CreatePersistenceAction).ToList(),
            UserPresets = profile.UserPresets.Select(CreatePersistenceAction).ToList(),
            ActivePresetHotkeys = CloneHotkeys(profile.ActivePresetHotkeys)
        }).ToList();
        return new AppSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            GamePath = settings.GamePath,
            EffectMo2ModPath = settings.EffectMo2ModPath,
            EditorPath = settings.EditorPath,
            AnonymousDonorName = NormalizeAnonymousDonorName(settings.AnonymousDonorName),
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
            RestartAfterCrash = settings.RestartAfterCrash,
            HideUnavailableCatalogSections = settings.HideUnavailableCatalogSections,
            UseLightTheme = settings.UseLightTheme,
            ToggleDonationProcessingHotkey = settings.ToggleDonationProcessingHotkey,
            RepeatLastDonationHotkey = settings.RepeatLastDonationHotkey,
            ActivePresetHotkeys = settings.ActivePresetHotkeys.Select(binding => new ActivePresetHotkey
            {
                Id = binding.Id,
                PresetNumber = binding.PresetNumber,
                Hotkey = binding.Hotkey
            }).ToList(),
            Armed = settings.Armed,
            DonationAlertsClientId = settings.DonationAlertsClientId,
            DonationAlertsUserId = settings.DonationAlertsUserId,
            DonationAlertsAccountCode = settings.DonationAlertsAccountCode,
            DonationAlertsAccountName = settings.DonationAlertsAccountName,
            ExpectedAccountCode = settings.ExpectedAccountCode,
            LimitDonationAlertsApiRequests = settings.LimitDonationAlertsApiRequests,
            InitialDonationSnapshotCompleted = settings.InitialDonationSnapshotCompleted,
            LastDonationId = settings.LastDonationId,
            LastDonationCreatedAt = settings.LastDonationCreatedAt,
            DonateXUserId = settings.DonateXUserId,
            DonateXAccountName = settings.DonateXAccountName,
            LimitDonateXApiRequests = settings.LimitDonateXApiRequests,
            DonateXInitialDonationSnapshotCompleted = settings.DonateXInitialDonationSnapshotCompleted,
            LastDonateXDonationId = settings.LastDonateXDonationId,
            LastDonateXDonationCreatedAt = settings.LastDonateXDonationCreatedAt,
            YouTubeChatVideoUrl = settings.YouTubeChatVideoUrl,
            YouTubeChatEnabled = settings.YouTubeChatEnabled,
            QueuePolicy = settings.QueuePolicy.Clone(),
            GlobalCooldownSeconds = settings.GlobalCooldownSeconds,
            FortuneWheelIdleMinutes = settings.FortuneWheelIdleMinutes,
            SaveBeforeDonationEffects = settings.SaveBeforeDonationEffects,
            SaveAfterDonationEffects = settings.SaveAfterDonationEffects,
            EffectSaveRetentionCount = Math.Clamp(settings.EffectSaveRetentionCount, 0, 10_000),
            ShowGameEffectLog = settings.ShowGameEffectLog,
            ShowPdaEffectMessage = settings.ShowPdaEffectMessage,
            CatalogExclusions = settings.CatalogExclusions.Select(CloneCatalogExclusion).ToList(),
            SafeZone = SafeZoneSettings.Normalize(settings.SafeZone),
            ObservedCurrencies = settings.ObservedCurrencies.ToList(),
            Actions = actions,
            UserPresets = userPresets,
            ActiveProfileId = settings.ActiveProfileId,
            Profiles = profiles
        };
    }

    public static string NormalizeAnonymousDonorName(string? value)
    {
        var safe = string.Concat((value ?? string.Empty)
            .Select(character => char.IsControl(character) ? ' ' : character));
        var normalized = string.Join(' ', safe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return DefaultAnonymousDonorName;
        if (normalized.Length <= AnonymousDonorNameMaxLength) return normalized;
        normalized = normalized[..AnonymousDonorNameMaxLength].TrimEnd();
        if (normalized.Length > 0 && char.IsHighSurrogate(normalized[^1]))
            normalized = normalized[..^1];
        return normalized.Length == 0 ? DefaultAnonymousDonorName : normalized;
    }

    public static CatalogExclusion CloneCatalogExclusion(CatalogExclusion source) => new()
    {
        Kind = source.Kind,
        SectionId = source.SectionId,
        DisplayName = source.DisplayName,
        Category = source.Category,
        AddedAt = source.AddedAt
    };

    public static List<CatalogExclusion> NormalizeCatalogExclusions(
        IEnumerable<CatalogExclusion>? exclusions)
    {
        var result = new List<CatalogExclusion>();
        var used = new HashSet<(CatalogEntryKind Kind, string SectionId)>();
        foreach (var source in exclusions ?? [])
        {
            if (!Enum.IsDefined(source.Kind)) continue;
            var sectionId = (source.SectionId ?? string.Empty).Trim();
            if (sectionId.Length is < 1 or > 256 ||
                sectionId.Any(character => character is '\t' or '\r' or '\n' or ',' or '=' or '[' or ']'))
                continue;
            if (!used.Add((source.Kind, sectionId))) continue;
            var displayName = NormalizeCatalogText(source.DisplayName, sectionId, 1024);
            var category = NormalizeCatalogText(source.Category, string.Empty, 256);
            result.Add(new CatalogExclusion
            {
                Kind = source.Kind,
                SectionId = sectionId,
                DisplayName = displayName,
                Category = category,
                AddedAt = source.AddedAt == default ? DateTimeOffset.UtcNow : source.AddedAt
            });
        }
        return result
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.SectionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeCatalogText(string? value, string fallback, int maximumLength)
    {
        var normalized = new string((value ?? string.Empty)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray()).Trim();
        if (normalized.Length == 0) normalized = fallback;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    public static void SynchronizeActiveProfile(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var active = settings.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, settings.ActiveProfileId, StringComparison.Ordinal));
        if (active is null) return;
        active.Actions = settings.Actions ?? [];
        active.UserPresets = settings.UserPresets ?? [];
        active.ActivePresetHotkeys = settings.ActivePresetHotkeys ?? [];
    }

    public static bool TryNormalizeProfileName(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim();
        return normalized.Length is >= 1 and <= ProfileNameMaxLength;
    }

    public static List<ActionDefinition> CreatePortableActions(IEnumerable<ActionDefinition> actions) =>
        actions.Select(CreatePersistenceAction).ToList();

    private static ActionDefinition CreatePersistenceAction(ActionDefinition source)
    {
        var action = source.Clone();
        action.CatalogRevision = null;
        foreach (var definition in action.ParameterDefinitions.Where(definition =>
                     definition.CatalogKind is not null || ActiveTemplateSelection.IsParameter(definition)))
        {
            definition.Options.Clear();
            definition.OptionGroups.Clear();
            definition.OptionLabels.Clear();
            definition.DefaultValue = string.Empty;
        }
        return action;
    }

    private static void NormalizeProfiles(AppSettings settings, int sourceSchemaVersion)
    {
        var sourceProfiles = settings.Profiles ?? [];
        var migratedHotkeys = NormalizeHotkeys(settings.ActivePresetHotkeys);
        if (sourceSchemaVersion < 8 || sourceProfiles.Count == 0)
        {
            var migratedActions = NormalizeActiveInstances(settings.Actions ?? [], sourceSchemaVersion);
            var migratedUserPresets = NormalizeUserPresets(
                settings.UserPresets ?? [],
                migratedActions.Select(action => action.Id),
                sourceSchemaVersion);
            var migrated = new DonationProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Основной",
                Actions = migratedActions,
                UserPresets = migratedUserPresets,
                ActivePresetHotkeys = migratedHotkeys
            };
            settings.Profiles = [migrated];
            settings.ActiveProfileId = migrated.Id;
            settings.Actions = migrated.Actions;
            settings.UserPresets = migrated.UserPresets;
            settings.ActivePresetHotkeys = migrated.ActivePresetHotkeys;
            return;
        }

        var normalizedProfiles = new List<DonationProfile>(sourceProfiles.Count);
        var usedProfileIds = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceProfiles)
        {
            if (source is null) continue;
            var sourceWasActive = string.Equals(
                (source.Id ?? string.Empty).Trim(),
                (settings.ActiveProfileId ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
            var id = (source.Id ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0 || !usedProfileIds.Add(id))
            {
                do { id = Guid.NewGuid().ToString("N"); }
                while (!usedProfileIds.Add(id));
            }

            var name = TryNormalizeProfileName(source.Name, out var normalizedName)
                ? normalizedName
                : "Профиль";
            var baseName = name;
            for (var suffix = 2; !usedNames.Add(name); suffix++)
            {
                var suffixText = $" {suffix}";
                var prefixLength = Math.Max(1, ProfileNameMaxLength - suffixText.Length);
                name = baseName[..Math.Min(baseName.Length, prefixLength)] + suffixText;
            }

            var actions = NormalizeActiveInstances(source.Actions ?? [], sourceSchemaVersion);
            var userPresets = NormalizeUserPresets(
                source.UserPresets ?? [],
                actions.Select(action => action.Id),
                sourceSchemaVersion);
            normalizedProfiles.Add(new DonationProfile
            {
                Id = id,
                Name = name,
                Actions = actions,
                UserPresets = userPresets,
                ActivePresetHotkeys = sourceSchemaVersion < 9
                    ? sourceWasActive ? CloneHotkeys(migratedHotkeys) : []
                    : NormalizeHotkeys(source.ActivePresetHotkeys)
            });
        }

        if (normalizedProfiles.Count == 0)
        {
            normalizedProfiles.Add(new DonationProfile { Name = "Основной" });
        }

        settings.Profiles = normalizedProfiles;
        var active = normalizedProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, settings.ActiveProfileId, StringComparison.Ordinal))
            ?? normalizedProfiles[0];
        settings.ActiveProfileId = active.Id;
        settings.Actions = active.Actions;
        settings.UserPresets = active.UserPresets;
        settings.ActivePresetHotkeys = active.ActivePresetHotkeys;
    }

    private static List<ActivePresetHotkey> NormalizeHotkeys(IEnumerable<ActivePresetHotkey>? source)
    {
        var result = new List<ActivePresetHotkey>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in source ?? [])
        {
            if (raw is null) continue;
            var id = (raw.Id ?? string.Empty).Trim();
            if (id.Length == 0 || !usedIds.Add(id))
            {
                do { id = Guid.NewGuid().ToString("N"); }
                while (!usedIds.Add(id));
            }
            result.Add(new ActivePresetHotkey
            {
                Id = id,
                PresetNumber = Math.Clamp(raw.PresetNumber, 1, 10_000),
                Hotkey = (raw.Hotkey ?? string.Empty).Trim()
            });
        }
        return result;
    }

    private static List<ActivePresetHotkey> CloneHotkeys(IEnumerable<ActivePresetHotkey>? source) =>
        (source ?? []).Select(binding => new ActivePresetHotkey
        {
            Id = binding.Id,
            PresetNumber = binding.PresetNumber,
            Hotkey = binding.Hotkey
        }).ToList();

    private static List<ActionDefinition> NormalizeActiveInstances(List<ActionDefinition> saved, int sourceSchemaVersion)
    {
        var result = new List<ActionDefinition>();
        var usedInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        var presets = sourceSchemaVersion < 6
            ? DefaultActionCatalog.CreatePresets()
                .ToDictionary(action => action.HandlerId, StringComparer.Ordinal)
            : new Dictionary<string, ActionDefinition>(StringComparer.Ordinal);

        for (var savedIndex = 0; savedIndex < saved.Count; savedIndex++)
        {
            var savedAction = saved[savedIndex];
            if (savedAction is null)
            {
                throw new InvalidDataException($"settings.json содержит пустое действие actions[{savedIndex}].");
            }
            NormalizeSavedActionShape(savedAction, savedIndex);

            // Schema v1 stored the preset catalog and used IsActive as a tab flag.
            // Schema v2+ stores active instances only.
            var isLegacyPresetCatalog = sourceSchemaVersion < 2;
            if (isLegacyPresetCatalog && !savedAction.IsActive)
            {
                continue;
            }

            var legacyHandlerId = isLegacyPresetCatalog
                ? savedAction.Id
                : savedAction.HandlerId;
            var handlerId = NormalizeLegacyHandlerId(legacyHandlerId);
            if (RetiredBuiltInHandlerIds.Contains(handlerId))
            {
                continue;
            }
            if (!Validation.ActionId().IsMatch(handlerId))
            {
                if (!isLegacyPresetCatalog)
                {
                    throw new InvalidDataException(
                        $"У активного варианта {savedAction.Id} отсутствует допустимый HandlerId.");
                }
                continue;
            }

            var instanceId = NormalizeInstanceId(savedAction.Id, usedInstanceIds);
            var instance = sourceSchemaVersion < 6 && presets.TryGetValue(handlerId, out var preset)
                ? ResolveAgainstPreset(savedAction, preset)
                : savedAction.Clone();
            instance.Id = instanceId;
            instance.HandlerId = handlerId;
            instance.IsActive = true;
            result.Add(instance);
        }

        return result;
    }

    private static void NormalizeSavedActionShape(ActionDefinition action, int actionIndex)
    {
        if (action.GroupDelaySeconds is { } delay) action.GroupDelaySeconds = Math.Clamp(delay, 0, 86_400);
        action.DisplayName ??= string.Empty;
        action.Description ??= string.Empty;
        action.ScriptRelativePath ??= string.Empty;
        action.ParameterDefinitions ??= [];
        action.Parameters = action.Parameters is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : action.Parameters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        action.ItemSpawns ??= [];
        action.SpawnGroups ??= [];
        action.Triggers ??= [];

        for (var parameterIndex = 0; parameterIndex < action.ParameterDefinitions.Count; parameterIndex++)
        {
            var definition = action.ParameterDefinitions[parameterIndex];
            if (definition is null)
            {
                throw new InvalidDataException(
                    $"settings.json содержит пустой параметр actions[{actionIndex}].parameterDefinitions[{parameterIndex}].");
            }
            if (!Enum.IsDefined(definition.Kind) ||
                definition.CatalogKind is { } catalogKind && !Enum.IsDefined(catalogKind))
            {
                throw new InvalidDataException(
                    $"settings.json содержит неизвестный kind/catalogKind у actions[{actionIndex}].parameterDefinitions[{parameterIndex}].");
            }
            definition.Key ??= string.Empty;
            definition.DisplayName ??= string.Empty;
            definition.DefaultValue ??= string.Empty;
            definition.Options ??= [];
            definition.OptionGroups ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            definition.OptionLabels ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (definition.OptionGroups.Any(pair => pair.Value is null))
            {
                throw new InvalidDataException(
                    $"settings.json содержит пустую группу options у actions[{actionIndex}].parameterDefinitions[{parameterIndex}].");
            }
        }

        for (var triggerIndex = 0; triggerIndex < action.Triggers.Count; triggerIndex++)
        {
            var trigger = action.Triggers[triggerIndex];
            if (trigger is null)
            {
                throw new InvalidDataException(
                    $"settings.json содержит пустое условие actions[{actionIndex}].triggers[{triggerIndex}].");
            }
            if (!Enum.IsDefined(trigger.Comparator))
            {
                throw new InvalidDataException(
                    $"settings.json содержит неизвестный comparator у actions[{actionIndex}].triggers[{triggerIndex}].");
            }
            trigger.Id ??= string.Empty;
            trigger.Currency ??= string.Empty;
        }
    }

    private static ActionDefinition ResolveAgainstPreset(ActionDefinition saved, ActionDefinition preset)
    {
        var resolved = preset.Clone();
        if (saved.HasCustomDisplayName && Validation.TryNormalizeUserPresetName(saved.DisplayName, out var displayName))
        {
            resolved.DisplayName = displayName;
            resolved.HasCustomDisplayName = true;
        }
        resolved.CooldownSeconds = saved.CooldownSeconds;
        resolved.StrictSequence = saved.StrictSequence;
        resolved.GroupDelaySeconds = saved.GroupDelaySeconds is { } delay ? Math.Clamp(delay, 0, 86_400) : null;
        resolved.Triggers = (saved.Triggers ?? []).Select(trigger => trigger.Clone()).ToList();
        resolved.ItemSpawns = (saved.ItemSpawns ?? []).Select(entry => entry.Clone()).ToList();
        resolved.SpawnGroups = (saved.SpawnGroups ?? []).Select(entry => entry.Clone()).ToList();
        foreach (var definition in resolved.ParameterDefinitions)
        {
            if (saved.Parameters?.TryGetValue(definition.Key, out var value) == true &&
                (definition.CatalogKind is not null && !string.IsNullOrWhiteSpace(value) || definition.Validate(value) is null))
            {
                resolved.Parameters[definition.Key] = value;
            }
        }
        MigrateLegacyItemBundle(resolved);
        MigrateLegacySpawnGroupBundle(resolved);
        return resolved;
    }

    private static List<ActionDefinition> NormalizeUserPresets(
        List<ActionDefinition> saved,
        IEnumerable<string> reservedIds,
        int sourceSchemaVersion)
    {
        var result = new List<ActionDefinition>();
        var usedIds = new HashSet<string>(reservedIds, StringComparer.Ordinal);
        var presets = sourceSchemaVersion < 6
            ? DefaultActionCatalog.CreatePresets()
                .ToDictionary(action => action.HandlerId, StringComparer.Ordinal)
            : new Dictionary<string, ActionDefinition>(StringComparer.Ordinal);
        for (var index = 0; index < saved.Count; index++)
        {
            var savedPreset = saved[index];
            if (savedPreset is null) continue;
            NormalizeSavedActionShape(savedPreset, index);
            var handlerId = NormalizeLegacyHandlerId(savedPreset.HandlerId);
            if (!Validation.ActionId().IsMatch(handlerId)) continue;
            var normalized = sourceSchemaVersion < 6 && presets.TryGetValue(handlerId, out var preset)
                ? ResolveAgainstPreset(savedPreset, preset)
                : savedPreset.Clone();
            if (Validation.TryNormalizeUserPresetName(savedPreset.DisplayName, out var displayName))
                normalized.DisplayName = displayName;
            normalized.HasCustomDisplayName = true;
            normalized.Id = NormalizeInstanceId(savedPreset.Id, usedIds);
            normalized.HandlerId = handlerId;
            normalized.IsActive = false;
            result.Add(normalized);
        }
        return result;
    }

    private static void MigrateLegacyItemBundle(ActionDefinition action)
    {
        if (!string.Equals(action.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal) ||
            action.ItemSpawns.Count > 0) return;
        var item = action.Parameters.TryGetValue("section_id", out var savedItem) ? savedItem : string.Empty;
        var countText = action.Parameters.TryGetValue("count", out var savedCount) ? savedCount : "1";
        if (!int.TryParse(countText, out var count) || count is < 1 or > 50) count = 1;
        action.ItemSpawns.Add(new ItemSpawnEntry { ItemId = item, Count = count });
    }

    private static void MigrateLegacySpawnGroupBundle(ActionDefinition action)
    {
        if (!SpawnGroupBundleCodec.Supports(action.HandlerId) || action.SpawnGroups.Count > 0) return;
        var section = action.Parameters.TryGetValue("section_id", out var savedSection) ? savedSection : string.Empty;
        var countText = action.Parameters.TryGetValue("count", out var savedCount) ? savedCount : "1";
        if (!int.TryParse(countText, out var count) || count is < 1 or > 99)
            count = action.HandlerId == "spawn_mutants" ? 3 : 1;
        action.SpawnGroups.Add(new SpawnGroupEntry { VariantId = section, Strength = "any", Count = count });
    }

    private static void RemoveObsoleteSpawnStrength(ActionDefinition action)
    {
        if (action.HandlerId is not (
            "spawn_mutants" or "spawn_npcs" or
            "spawn_npc_squads" or "spawn_mutant_squads" or
            "spawn_random_hostile_squad" or "spawn_random_mutant_pack" or
            "spawn_random_npcs" or "spawn_random_mutants")) return;
        action.Parameters.Remove("strength");
        action.ParameterDefinitions.RemoveAll(definition =>
            string.Equals(definition.Key, "strength", StringComparison.OrdinalIgnoreCase));
    }

    private static void MigrateAnomalyRandomizationChoice(ActionDefinition action)
    {
        if (!string.Equals(action.HandlerId, "spawn_anomaly", StringComparison.Ordinal)) return;
        if (action.Parameters.TryGetValue("section_id", out var section) &&
            string.Equals(section, DefaultActionCatalog.RandomAnomalySectionId, StringComparison.Ordinal) &&
            action.Parameters.TryGetValue("randomization_options", out var legacyOptions) &&
            legacyOptions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("each_random", StringComparer.OrdinalIgnoreCase))
        {
            action.Parameters["section_id"] = DefaultActionCatalog.EachRandomAnomalySectionId;
        }
        action.Parameters.Remove("randomization_options");
        action.ParameterDefinitions.RemoveAll(definition =>
            string.Equals(definition.Key, "randomization_options", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddDonorBossNpcSelection(ActionDefinition action)
    {
        if (!string.Equals(action.HandlerId, "spawn_donor_boss", StringComparison.Ordinal) ||
            action.ParameterDefinitions.Any(definition =>
                string.Equals(definition.Key, "npc_sections", StringComparison.OrdinalIgnoreCase))) return;
        action.ParameterDefinitions.Insert(0, new ParameterDefinition
        {
            Key = "npc_sections",
            DisplayName = "Допустимые варианты",
            Kind = ParameterKind.MultiChoice,
            CatalogKind = CatalogEntryKind.Npc,
            DefaultValue = string.Empty
        });
        action.Parameters["npc_sections"] = string.Empty;
    }

    private static string NormalizeLegacyHandlerId(string? handlerId)
    {
        var normalized = (handlerId ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "spawn_dogs_3" ? "spawn_mutants" : normalized;
    }

    private static string NormalizeInstanceId(string? instanceId, ISet<string> usedInstanceIds)
    {
        var normalized = (instanceId ?? string.Empty).Trim().ToLowerInvariant();
        if (!Validation.InstanceId().IsMatch(normalized) || !usedInstanceIds.Add(normalized))
        {
            do
            {
                normalized = ActionDefinition.NewInstanceId();
            }
            while (!usedInstanceIds.Add(normalized));
        }
        return normalized;
    }
}
