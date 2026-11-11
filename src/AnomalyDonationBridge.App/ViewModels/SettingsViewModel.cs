using System.Collections.ObjectModel;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.DonationAlerts;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed class ActivePresetHotkeyViewModel : ObservableObject
{
    private readonly IReadOnlyList<ActionDefinition> _actions;
    private string _presetNumberText;
    private string _hotkey;

    public ActivePresetHotkeyViewModel(ActivePresetHotkey source, IReadOnlyList<ActionDefinition> actions)
    {
        Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id;
        _actions = actions;
        _presetNumberText = source.PresetNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _hotkey = source.Hotkey ?? string.Empty;
    }

    public string Id { get; }
    public string PresetNumberText
    {
        get => _presetNumberText;
        set
        {
            if (!SetProperty(ref _presetNumberText, value)) return;
            RaisePropertyChanged(nameof(PresetLabel));
        }
    }
    public string Hotkey { get => _hotkey; set => SetProperty(ref _hotkey, value); }
    public string PresetLabel => TryGetPresetNumber(out var number) && number <= _actions.Count
        ? _actions[number - 1].DisplayName
        : "Активный шаблон с таким номером не найден";

    public bool TryGetPresetNumber(out int number) =>
        int.TryParse(PresetNumberText, out number) && number is >= 1 and <= 10_000;
}

public sealed class CatalogExclusionRowViewModel : ObservableObject
{
    private bool _isExcluded;

    public CatalogExclusionRowViewModel(CatalogEntry entry, bool isExcluded)
    {
        Kind = entry.Kind;
        SectionId = entry.SectionId;
        DisplayName = entry.DisplayName;
        Category = entry.Category;
        _isExcluded = isExcluded;
    }

    public CatalogEntryKind Kind { get; }
    public string SectionId { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public string KindLabel => Kind switch
    {
        CatalogEntryKind.Mutant => "Мутант",
        CatalogEntryKind.Npc => "NPC",
        CatalogEntryKind.StalkerSquad => "Отряд NPC",
        CatalogEntryKind.MutantSquad => "Отряд мутантов",
        CatalogEntryKind.Item => "Предмет",
        CatalogEntryKind.Anomaly => "Аномалия",
        CatalogEntryKind.Faction => "Группировка",
        CatalogEntryKind.Level => "Локация",
        CatalogEntryKind.Helicopter => "Вертолёт",
        CatalogEntryKind.ArmedVehicle => "Вооружённый транспорт",
        CatalogEntryKind.NpcVisual => "Внешность NPC",
        CatalogEntryKind.GuitarMusic => "Гитарная композиция",
        _ => Kind.ToString()
    };
    public bool IsExcluded { get => _isExcluded; set => SetProperty(ref _isExcluded, value); }
}

public sealed record CatalogExclusionKindFilterOption(CatalogEntryKind? Kind, string Label);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly BridgeRuntime _runtime;
    private string _gamePath;
    private string _effectMo2ModPath;
    private string _editorPath;
    private string _anonymousDonorName;
    private string _clientId;
    private string _expectedAccountCode;
    private QueueMode _queueMode;
    private int _waitMinutes;
    private int _maximumQueuedEvents;
    private int _globalCooldownSeconds;
    private int _fortuneWheelIdleMinutes;
    private bool _saveBeforeDonationEffects;
    private bool _saveAfterDonationEffects;
    private int _effectSaveRetentionCount;
    private bool _showGameEffectLog;
    private bool _showPdaEffectMessage;
    private bool _limitDonationAlertsApiRequests;
    private bool _limitDonateXApiRequests;
    private bool _minimizeToTrayOnClose;
    private bool _restartAfterCrash;
    private bool _hideUnavailableCatalogSections;
    private string _toggleDonationProcessingHotkey;
    private string _repeatLastDonationHotkey;
    private string _status = string.Empty;
    private bool _busy;
    private AddonInstallationStatus? _addonStatus;
    private EffectScriptInstallationStatus? _effectsStatus;
    private string _catalogExclusionSearch = string.Empty;
    private bool _showOnlyExcludedCatalogEntries = true;
    private CatalogExclusionKindFilterOption _selectedCatalogExclusionKindFilter;

    public SettingsViewModel(BridgeRuntime runtime)
    {
        _runtime = runtime;
        InitializeYouTubeChat();
        CatalogExclusionKindFilters =
        [
            new(null, "Фильтр: все"),
            new(CatalogEntryKind.Item, "Предметы"),
            new(CatalogEntryKind.Npc, "NPC"),
            new(CatalogEntryKind.Mutant, "Мутанты"),
            new(CatalogEntryKind.StalkerSquad, "Отряды NPC"),
            new(CatalogEntryKind.MutantSquad, "Отряды мутантов"),
            new(CatalogEntryKind.Anomaly, "Аномалии"),
            new(CatalogEntryKind.Level, "Локации"),
            new(CatalogEntryKind.Helicopter, "Вертолёты"),
            new(CatalogEntryKind.ArmedVehicle, "Вооружённый транспорт"),
        new(CatalogEntryKind.NpcVisual, "Внешность NPC"),
            new(CatalogEntryKind.GuitarMusic, "Гитарные композиции"),
            new(CatalogEntryKind.Faction, "Группировки")
        ];
        _selectedCatalogExclusionKindFilter = CatalogExclusionKindFilters[0];
        var settings = runtime.Settings;
        SafeZone = new SafeZoneEditorViewModel(settings.SafeZone, runtime.CatalogState.Catalog?.Entries ?? [],
            runtime.Presets.Concat(settings.Actions).Concat(settings.UserPresets));
        _gamePath = settings.GamePath;
        _effectMo2ModPath = settings.EffectMo2ModPath;
        _editorPath = settings.EditorPath;
        _anonymousDonorName = settings.AnonymousDonorName;
        _clientId = settings.DonationAlertsClientId;
        _expectedAccountCode = settings.ExpectedAccountCode;
        _queueMode = settings.QueuePolicy.Mode;
        _waitMinutes = settings.QueuePolicy.WaitMinutes;
        _maximumQueuedEvents = settings.QueuePolicy.MaximumQueuedEvents;
        _globalCooldownSeconds = settings.GlobalCooldownSeconds;
        _fortuneWheelIdleMinutes = settings.FortuneWheelIdleMinutes;
        _saveBeforeDonationEffects = settings.SaveBeforeDonationEffects;
        _saveAfterDonationEffects = settings.SaveAfterDonationEffects;
        _effectSaveRetentionCount = settings.EffectSaveRetentionCount;
        _showGameEffectLog = settings.ShowGameEffectLog;
        _showPdaEffectMessage = settings.ShowPdaEffectMessage;
        _limitDonationAlertsApiRequests = settings.LimitDonationAlertsApiRequests;
        _limitDonateXApiRequests = settings.LimitDonateXApiRequests;
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _restartAfterCrash = settings.RestartAfterCrash;
        _hideUnavailableCatalogSections = settings.HideUnavailableCatalogSections;
        _toggleDonationProcessingHotkey = settings.ToggleDonationProcessingHotkey;
        _repeatLastDonationHotkey = settings.RepeatLastDonationHotkey;
        ActivePresetHotkeys = new ObservableCollection<ActivePresetHotkeyViewModel>(
            settings.ActivePresetHotkeys.Select(binding => new ActivePresetHotkeyViewModel(binding, settings.Actions)));
        var saved = settings.CatalogExclusions.ToDictionary(
            entry => (entry.Kind, entry.SectionId), entry => entry);
        var rows = (runtime.CatalogState.Catalog?.Entries ?? [])
            .Select(entry => new CatalogExclusionRowViewModel(
                entry, saved.ContainsKey((entry.Kind, entry.SectionId))))
            .ToList();
        foreach (var exclusion in settings.CatalogExclusions.Where(exclusion =>
                     rows.All(row => row.Kind != exclusion.Kind ||
                         !string.Equals(row.SectionId, exclusion.SectionId, StringComparison.Ordinal))))
        {
            rows.Add(new CatalogExclusionRowViewModel(new CatalogEntry
            {
                Kind = exclusion.Kind,
                SectionId = exclusion.SectionId,
                DisplayName = exclusion.DisplayName,
                Category = exclusion.Category,
                IsAvailable = false,
                QuarantineReason = QuarantineReason.Blacklisted
            }, true));
        }
        CatalogEntries = new ObservableCollection<CatalogExclusionRowViewModel>(rows
            .OrderBy(row => row.Kind)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        foreach (var row in CatalogEntries)
            SubscribeCatalogRow(row);
    }

    public SafeZoneEditorViewModel SafeZone { get; }
    public IReadOnlyList<QueueMode> QueueModes { get; } = Enum.GetValues<QueueMode>();
    public string RedirectUri => DonationAlertsEndpoints.RedirectUri;
    public string Scopes => DonationAlertsEndpoints.Scopes;
    public string ApplicationVersion => GitHubReleaseUpdateChecker.CurrentApplicationVersion;
    public string SecretHint => string.IsNullOrWhiteSpace(_runtime.Secrets.ClientSecret) ? "Секрет ещё не сохранён" : "Секрет сохранён в DPAPI; оставьте поле пустым, чтобы использовать его";
    public string AccountLabel => string.IsNullOrWhiteSpace(_runtime.Settings.DonationAlertsAccountCode)
        ? "Аккаунт не подключён"
        : _runtime.AccountMatchesExpected
            ? $"✓ {_runtime.Settings.DonationAlertsAccountName} ({_runtime.Settings.DonationAlertsAccountCode})"
            : $"⚠ Подключён {_runtime.Settings.DonationAlertsAccountCode}, ожидался {_runtime.Settings.ExpectedAccountCode}";
    public string DonateXTokenHint => _runtime.Secrets.HasDonateXToken
        ? "Токен сохранён в DPAPI. Оставьте поле пустым, чтобы проверить сохранённое подключение."
        : "Токен ещё не сохранён.";
    public string DonateXAccountLabel => _runtime.DonateXAccountConfigured
        ? $"✓ {_runtime.Settings.DonateXAccountName}"
        : "Аккаунт DonateX не подключён";
    public string DataDirectory => _runtime.DataDirectory;
    public string EffectsDirectory => _runtime.EffectsDirectory;
    public string EffectMo2ModPath
    {
        get => _effectMo2ModPath;
        set
        {
            if (!SetProperty(ref _effectMo2ModPath, value)) return;
            _effectsStatus = null;
            RaisePropertyChanged(nameof(EffectsStatus));
            RaisePropertyChanged(nameof(CanInstallEffects));
            RaisePropertyChanged(nameof(CanInstallBridge));
        }
    }
    public string GamePath
    {
        get => _gamePath;
        set
        {
            if (!SetProperty(ref _gamePath, value)) return;
            _addonStatus = null;
            _effectsStatus = null;
            RaisePropertyChanged(nameof(AddonStatus));
            RaisePropertyChanged(nameof(CanInstallAddon));
            RaisePropertyChanged(nameof(EffectsStatus));
            RaisePropertyChanged(nameof(CanInstallEffects));
            RaisePropertyChanged(nameof(CanInstallBridge));
        }
    }
    public string EditorPath { get => _editorPath; set => SetProperty(ref _editorPath, value); }
    public string AnonymousDonorName { get => _anonymousDonorName; set => SetProperty(ref _anonymousDonorName, value); }
    public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }
    public string ExpectedAccountCode { get => _expectedAccountCode; set => SetProperty(ref _expectedAccountCode, value); }
    public QueueMode QueueMode { get => _queueMode; set { if (SetProperty(ref _queueMode, value)) RaisePropertyChanged(nameof(WaitControlsEnabled)); } }
    public int WaitMinutes { get => _waitMinutes; set => SetProperty(ref _waitMinutes, value); }
    public int MaximumQueuedEvents { get => _maximumQueuedEvents; set => SetProperty(ref _maximumQueuedEvents, value); }
    public int GlobalCooldownSeconds { get => _globalCooldownSeconds; set => SetProperty(ref _globalCooldownSeconds, value); }
    public int FortuneWheelIdleMinutes { get => _fortuneWheelIdleMinutes; set => SetProperty(ref _fortuneWheelIdleMinutes, value); }
    public bool SaveBeforeDonationEffects { get => _saveBeforeDonationEffects; set => SetProperty(ref _saveBeforeDonationEffects, value); }
    public bool SaveAfterDonationEffects { get => _saveAfterDonationEffects; set => SetProperty(ref _saveAfterDonationEffects, value); }
    public int EffectSaveRetentionCount { get => _effectSaveRetentionCount; set => SetProperty(ref _effectSaveRetentionCount, value); }
    public bool ShowGameEffectLog { get => _showGameEffectLog; set => SetProperty(ref _showGameEffectLog, value); }
    public bool ShowPdaEffectMessage { get => _showPdaEffectMessage; set => SetProperty(ref _showPdaEffectMessage, value); }
    public bool LimitDonationAlertsApiRequests { get => _limitDonationAlertsApiRequests; set => SetProperty(ref _limitDonationAlertsApiRequests, value); }
    public bool LimitDonateXApiRequests { get => _limitDonateXApiRequests; set => SetProperty(ref _limitDonateXApiRequests, value); }
    public bool MinimizeToTrayOnClose { get => _minimizeToTrayOnClose; set => SetProperty(ref _minimizeToTrayOnClose, value); }
    public bool RestartAfterCrash { get => _restartAfterCrash; set => SetProperty(ref _restartAfterCrash, value); }
    public bool HideUnavailableCatalogSections { get => _hideUnavailableCatalogSections; set => SetProperty(ref _hideUnavailableCatalogSections, value); }
    public string ToggleDonationProcessingHotkey { get => _toggleDonationProcessingHotkey; set => SetProperty(ref _toggleDonationProcessingHotkey, value); }
    public string RepeatLastDonationHotkey { get => _repeatLastDonationHotkey; set => SetProperty(ref _repeatLastDonationHotkey, value); }
    public ObservableCollection<ActivePresetHotkeyViewModel> ActivePresetHotkeys { get; }
    public ObservableCollection<CatalogExclusionRowViewModel> CatalogEntries { get; }
    public IReadOnlyList<CatalogExclusionKindFilterOption> CatalogExclusionKindFilters { get; }
    public IEnumerable<CatalogExclusionRowViewModel> FilteredCatalogEntries
    {
        get
        {
            var search = CatalogExclusionSearch.Trim();
            return CatalogEntries.Where(row =>
                (!ShowOnlyExcludedCatalogEntries || row.IsExcluded) &&
                (SelectedCatalogExclusionKindFilter.Kind is null ||
                    row.Kind == SelectedCatalogExclusionKindFilter.Kind) &&
                (search.Length == 0 ||
                    row.SectionId.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    row.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    row.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    row.KindLabel.Contains(search, StringComparison.CurrentCultureIgnoreCase)));
        }
    }
    public string CatalogExclusionSearch
    {
        get => _catalogExclusionSearch;
        set
        {
            if (SetProperty(ref _catalogExclusionSearch, value))
                RaisePropertyChanged(nameof(FilteredCatalogEntries));
        }
    }
    public bool ShowOnlyExcludedCatalogEntries
    {
        get => _showOnlyExcludedCatalogEntries;
        set
        {
            if (!SetProperty(ref _showOnlyExcludedCatalogEntries, value)) return;
            RaisePropertyChanged(nameof(CatalogExclusionScopeLabel));
            RaisePropertyChanged(nameof(FilteredCatalogEntries));
        }
    }
    public string CatalogExclusionScopeLabel => ShowOnlyExcludedCatalogEntries
        ? "Исключённые"
        : "Все секции";
    public CatalogExclusionKindFilterOption SelectedCatalogExclusionKindFilter
    {
        get => _selectedCatalogExclusionKindFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedCatalogExclusionKindFilter, value))
                RaisePropertyChanged(nameof(FilteredCatalogEntries));
        }
    }
    public int CatalogExclusionCount => CatalogEntries.Count(row => row.IsExcluded);

    public void AddManualCatalogExclusion(
        CatalogEntryKind kind,
        string sectionId,
        string? displayName)
    {
        sectionId = (sectionId ?? string.Empty).Trim();
        if (sectionId.Length is < 1 or > 160 ||
            sectionId.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '.' or '-')))
            throw new InvalidOperationException(
                "ID секции должен содержать от 1 до 160 латинских букв, цифр, точек, дефисов или подчёркиваний.");
        var existing = CatalogEntries.FirstOrDefault(row => row.Kind == kind &&
            string.Equals(row.SectionId, sectionId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IsExcluded = true;
            Status = $"Секция {sectionId} уже есть в каталоге и отмечена для исключения.";
            return;
        }
        var name = string.IsNullOrWhiteSpace(displayName) ? sectionId : displayName.Trim();
        if (name.Length > 1024) name = name[..1024];
        var row = new CatalogExclusionRowViewModel(new CatalogEntry
        {
            Kind = kind,
            SectionId = sectionId,
            DisplayName = name,
            IsAvailable = false,
            QuarantineReason = QuarantineReason.Blacklisted
        }, true);
        SubscribeCatalogRow(row);
        CatalogEntries.Add(row);
        RaisePropertyChanged(nameof(FilteredCatalogEntries));
        RaisePropertyChanged(nameof(CatalogExclusionCount));
        Status = $"Секция {sectionId} добавлена. Нажмите «Сохранить», чтобы применить исключение.";
    }

    private void SubscribeCatalogRow(CatalogExclusionRowViewModel row)
    {
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CatalogExclusionRowViewModel.IsExcluded))
            {
                RaisePropertyChanged(nameof(CatalogExclusionCount));
                RaisePropertyChanged(nameof(FilteredCatalogEntries));
            }
        };
    }
    public bool WaitControlsEnabled => QueueMode == QueueMode.WaitForDuration;
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string AddonStatus => _addonStatus?.Message ?? "Проверка аддона выполняется автоматически.";
    public bool CanInstallAddon => !Busy && _addonStatus?.CanInstall == true;
    public string EffectsStatus => _effectsStatus?.Message ?? "Шаблоны ещё не проверены.";
    public bool CanInstallEffects => !Busy && _effectsStatus?.CanInstall == true;
    public bool CanInstallBridge => !Busy && _addonStatus is not null && _effectsStatus is not null &&
        new BridgeInstallationStatus(_addonStatus, _effectsStatus).CanInstall;

    public async Task RefreshAddonStatusAsync()
    {
        var gameError = _runtime.ValidateGamePath(GamePath);
        if (!string.IsNullOrEmpty(gameError))
        {
            _addonStatus = new AddonInstallationStatus(AddonInstallState.NotInstalled, gameError);
        }
        else
        {
            _addonStatus = await _runtime.InspectAddonAsync(GamePath);
        }
        RaisePropertyChanged(nameof(AddonStatus));
        RaisePropertyChanged(nameof(CanInstallAddon));
        RaisePropertyChanged(nameof(CanInstallBridge));
    }

    public async Task InstallBridgeAsync()
    {
        if (Busy) return;
        Busy = true;
        RaisePropertyChanged(nameof(CanInstallBridge));
        try
        {
            var gameError = _runtime.ValidateGamePath(GamePath);
            if (!string.IsNullOrEmpty(gameError)) throw new InvalidOperationException(gameError);
            var addon = await _runtime.InspectAddonAsync(GamePath);
            if (addon.State is not (AddonInstallState.Current or AddonInstallState.NotInstalled or AddonInstallState.UpdateAvailable))
                throw new InvalidOperationException(addon.Message);
            await _runtime.PreflightEffectsAsync(GamePath, EffectMo2ModPath);
            await SaveAsync();
            var result = await _runtime.InstallBridgeAsync(GamePath, EffectMo2ModPath);
            _addonStatus = result.Addon;
            _effectsStatus = result.Effects;
            Status = result.Message + " Полностью перезапустите игру.";
            RaisePropertyChanged(nameof(AddonStatus));
            RaisePropertyChanged(nameof(EffectsStatus));
        }
        finally
        {
            Busy = false;
            RaisePropertyChanged(nameof(CanInstallAddon));
            RaisePropertyChanged(nameof(CanInstallEffects));
            RaisePropertyChanged(nameof(CanInstallBridge));
        }
    }

    public async Task RefreshEffectsStatusAsync()
    {
        _effectsStatus = await _runtime.InspectEffectsAsync(GamePath, EffectMo2ModPath);
        RaisePropertyChanged(nameof(EffectsStatus));
        RaisePropertyChanged(nameof(CanInstallEffects));
        RaisePropertyChanged(nameof(CanInstallBridge));
    }

    public async Task InstallEffectsAsync()
    {
        await SaveAsync();
        Busy = true;
        RaisePropertyChanged(nameof(CanInstallEffects));
        try
        {
            _effectsStatus = await _runtime.InstallEffectsAsync(GamePath, EffectMo2ModPath);
            Status = _effectsStatus.Message + " Для регистрации новых Lua-файлов перезапустите игру.";
            RaisePropertyChanged(nameof(EffectsStatus));
        }
        finally
        {
            Busy = false;
            RaisePropertyChanged(nameof(CanInstallEffects));
        }
    }

    public async Task SaveAsync()
    {
        var gameError = _runtime.ValidateGamePath(GamePath);
        if (!string.IsNullOrEmpty(gameError)) throw new InvalidOperationException(gameError);
        await _runtime.SelectGamePathAsync(GamePath);
        if (!string.IsNullOrWhiteSpace(EffectMo2ModPath) && !Directory.Exists(EffectMo2ModPath))
            throw new DirectoryNotFoundException("Папка мода MO2 не найдена.");
        _runtime.Settings.EffectMo2ModPath = string.IsNullOrWhiteSpace(EffectMo2ModPath)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(EffectMo2ModPath));
        _runtime.Settings.EditorPath = string.IsNullOrWhiteSpace(EditorPath) ? "notepad.exe" : EditorPath.Trim();
        _runtime.Settings.AnonymousDonorName = SettingsNormalizer.NormalizeAnonymousDonorName(AnonymousDonorName);
        AnonymousDonorName = _runtime.Settings.AnonymousDonorName;
        _runtime.Settings.DonationAlertsClientId = ClientId.Trim();
        _runtime.Settings.ExpectedAccountCode = ExpectedAccountCode.Trim().ToLowerInvariant();
        _runtime.Settings.QueuePolicy.Mode = QueueMode.WaitIndefinitely;
        _runtime.Settings.QueuePolicy.WaitMinutes = Math.Clamp(WaitMinutes, 1, 24 * 60);
        _runtime.Settings.QueuePolicy.MaximumQueuedEvents = Math.Clamp(MaximumQueuedEvents, 1, 10_000);
        _runtime.Settings.GlobalCooldownSeconds = Math.Clamp(GlobalCooldownSeconds, 0, 300);
        _runtime.Settings.FortuneWheelIdleMinutes = Math.Clamp(FortuneWheelIdleMinutes, 0, 10_080);
        _runtime.Settings.SaveBeforeDonationEffects = SaveBeforeDonationEffects;
        _runtime.Settings.SaveAfterDonationEffects = SaveAfterDonationEffects;
        _runtime.Settings.EffectSaveRetentionCount = Math.Clamp(EffectSaveRetentionCount, 0, 10_000);
        _runtime.Settings.ShowGameEffectLog = ShowGameEffectLog;
        _runtime.Settings.ShowPdaEffectMessage = ShowPdaEffectMessage;
        _runtime.Settings.LimitDonationAlertsApiRequests = LimitDonationAlertsApiRequests;
        _runtime.Settings.LimitDonateXApiRequests = LimitDonateXApiRequests;
        _runtime.Settings.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        _runtime.Settings.RestartAfterCrash = RestartAfterCrash;
        _runtime.Settings.HideUnavailableCatalogSections = HideUnavailableCatalogSections;
        _runtime.Settings.CatalogExclusions = CatalogEntries
            .Where(row => row.IsExcluded)
            .Select(row => new CatalogExclusion
            {
                Kind = row.Kind,
                SectionId = row.SectionId,
                DisplayName = row.DisplayName,
                Category = row.Category,
                AddedAt = _runtime.Settings.CatalogExclusions.FirstOrDefault(existing =>
                    existing.Kind == row.Kind &&
                    string.Equals(existing.SectionId, row.SectionId, StringComparison.Ordinal))?.AddedAt
                    ?? DateTimeOffset.UtcNow
            }).ToList();
        ApplyHotkeySettings();
        var warnings = _runtime.RefreshActionManifest();
        await _runtime.SaveSettingsAsync();
        Status = warnings.Count == 0 ? "Настройки сохранены." : "Сохранено с предупреждением: " + string.Join("; ", warnings);
    }

    public ActivePresetHotkeyViewModel AddActivePresetHotkey(int? presetNumber = null)
    {
        var number = presetNumber ?? Enumerable.Range(1, Math.Max(1, _runtime.Settings.Actions.Count))
            .FirstOrDefault(candidate => ActivePresetHotkeys.All(binding =>
                !binding.TryGetPresetNumber(out var existing) || existing != candidate));
        if (number < 1) number = 1;
        var binding = new ActivePresetHotkeyViewModel(new ActivePresetHotkey
        {
            PresetNumber = number
        }, _runtime.Settings.Actions);
        ActivePresetHotkeys.Add(binding);
        return binding;
    }

    public void RemoveActivePresetHotkey(ActivePresetHotkeyViewModel binding) =>
        ActivePresetHotkeys.Remove(binding);

    public async Task ConnectAsync(string secret, Action<Uri> openBrowser)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            await SaveWithoutGameRequirementAsync();
            Status = "Ожидание авторизации в DonationAlerts…";
            await _runtime.ConnectDonationAlertsAsync(ClientId, secret, openBrowser);
            Status = _runtime.AccountMatchesExpected
                ? "DonationAlerts подключён: " + _runtime.Settings.DonationAlertsAccountCode
                : $"Внимание: подключён {_runtime.Settings.DonationAlertsAccountCode}, ожидался {_runtime.Settings.ExpectedAccountCode}. Обработка выключена.";
            RaisePropertyChanged(nameof(AccountLabel));
            RaisePropertyChanged(nameof(SecretHint));
        }
        finally { Busy = false; }
    }

    public async Task DisconnectAsync(bool forgetSecret)
    {
        await _runtime.DisconnectDonationAlertsAsync(forgetSecret);
        Status = "Аккаунт отключён.";
        if (forgetSecret) ClientId = string.Empty;
        RaisePropertyChanged(nameof(AccountLabel));
        RaisePropertyChanged(nameof(SecretHint));
    }

    public async Task ConnectDonateXAsync(string externalToken)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            _runtime.Settings.LimitDonateXApiRequests = LimitDonateXApiRequests;
            await _runtime.SaveSettingsAsync();
            var token = string.IsNullOrWhiteSpace(externalToken)
                ? _runtime.Secrets.DonateXExternalToken
                : externalToken.Trim();
            Status = "Проверка токена и подключение DonateX…";
            await _runtime.ConnectDonateXAsync(token);
            Status = "DonateX подключён: " + _runtime.Settings.DonateXAccountName;
            RaisePropertyChanged(nameof(DonateXAccountLabel));
            RaisePropertyChanged(nameof(DonateXTokenHint));
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task DisconnectDonateXAsync()
    {
        await _runtime.DisconnectDonateXAsync();
        Status = "DonateX отключён.";
        RaisePropertyChanged(nameof(DonateXAccountLabel));
        RaisePropertyChanged(nameof(DonateXTokenHint));
    }

    public Task ExportAsync(string path) => _runtime.ExportRulesAsync(path);
    public async Task ImportAsync(string path)
    {
        await _runtime.ImportRulesAsync(path);
        Status = "Правила импортированы. Секреты не затрагивались.";
    }

    private async Task SaveWithoutGameRequirementAsync()
    {
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("Введите Client ID.");
        _runtime.Settings.DonationAlertsClientId = ClientId.Trim();
        _runtime.Settings.AnonymousDonorName = SettingsNormalizer.NormalizeAnonymousDonorName(AnonymousDonorName);
        AnonymousDonorName = _runtime.Settings.AnonymousDonorName;
        _runtime.Settings.ExpectedAccountCode = ExpectedAccountCode.Trim().ToLowerInvariant();
        _runtime.Settings.QueuePolicy.Mode = QueueMode.WaitIndefinitely;
        _runtime.Settings.QueuePolicy.WaitMinutes = Math.Clamp(WaitMinutes, 1, 24 * 60);
        _runtime.Settings.QueuePolicy.MaximumQueuedEvents = Math.Clamp(MaximumQueuedEvents, 1, 10_000);
        _runtime.Settings.GlobalCooldownSeconds = Math.Clamp(GlobalCooldownSeconds, 0, 300);
        _runtime.Settings.FortuneWheelIdleMinutes = Math.Clamp(FortuneWheelIdleMinutes, 0, 10_080);
        _runtime.Settings.SaveBeforeDonationEffects = SaveBeforeDonationEffects;
        _runtime.Settings.SaveAfterDonationEffects = SaveAfterDonationEffects;
        _runtime.Settings.EffectSaveRetentionCount = Math.Clamp(EffectSaveRetentionCount, 0, 10_000);
        _runtime.Settings.ShowGameEffectLog = ShowGameEffectLog;
        _runtime.Settings.ShowPdaEffectMessage = ShowPdaEffectMessage;
        _runtime.Settings.LimitDonationAlertsApiRequests = LimitDonationAlertsApiRequests;
        _runtime.Settings.LimitDonateXApiRequests = LimitDonateXApiRequests;
        _runtime.Settings.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        _runtime.Settings.RestartAfterCrash = RestartAfterCrash;
        _runtime.Settings.HideUnavailableCatalogSections = HideUnavailableCatalogSections;
        ApplyHotkeySettings();
        await _runtime.SaveSettingsAsync();
    }

    private void ApplyHotkeySettings()
    {
        _runtime.Settings.SafeZone = SafeZone.ToSettings();
        var toggle = NormalizeHotkey(ToggleDonationProcessingHotkey, "включения и отключения обработки донатов");
        var repeat = NormalizeHotkey(RepeatLastDonationHotkey, "повтора последнего доната");
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddUniqueHotkey(used, toggle, "включения и отключения обработки донатов");
        AddUniqueHotkey(used, repeat, "повтора последнего доната");
        var presetBindings = new List<ActivePresetHotkey>();
        foreach (var row in ActivePresetHotkeys)
        {
            if (!row.TryGetPresetNumber(out var presetNumber) || presetNumber > _runtime.Settings.Actions.Count)
                throw new InvalidOperationException($"Укажите номер существующего активного шаблона от 1 до {_runtime.Settings.Actions.Count}.");
            var hotkey = NormalizeHotkey(row.Hotkey, $"активации шаблона №{presetNumber}");
            AddUniqueHotkey(used, hotkey, $"активации шаблона №{presetNumber}");
            row.Hotkey = hotkey;
            presetBindings.Add(new ActivePresetHotkey
            {
                Id = row.Id,
                PresetNumber = presetNumber,
                Hotkey = hotkey
            });
        }
        ToggleDonationProcessingHotkey = toggle;
        RepeatLastDonationHotkey = repeat;
        _runtime.Settings.ToggleDonationProcessingHotkey = toggle;
        _runtime.Settings.RepeatLastDonationHotkey = repeat;
        _runtime.Settings.ActivePresetHotkeys = presetBindings;
    }

    private static void AddUniqueHotkey(Dictionary<string, string> used, string hotkey, string actionName)
    {
        if (hotkey.Length == 0) return;
        if (used.TryGetValue(hotkey, out var existing))
            throw new InvalidOperationException($"Сочетание {hotkey} уже назначено для {existing}; выберите другое для {actionName}.");
        used[hotkey] = actionName;
    }

    private static string NormalizeHotkey(string? value, string actionName)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (GlobalHotkeyGesture.TryNormalize(value, out var normalized, out var error)) return normalized;
        throw new InvalidOperationException($"Неверная горячая клавиша для {actionName}: {error}.");
    }
}
