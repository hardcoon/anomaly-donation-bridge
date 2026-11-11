using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.DonationAlerts;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly BridgeRuntime _runtime;
    private readonly SemaphoreSlim _themeChangeGate = new(1, 1);
    private string _donationStatus = "Не подключён";
    private string _gameStatus = "Ожидание игры";
    private bool _donationConnected;
    private bool _gameConnected;
    private bool _gameReady;
    private string _catalogStatus = "Каталог игры ещё не получен";
    private bool _isCatalogSynchronizing;
    private bool _isCatalogProgressIndeterminate;
    private double _catalogProgressPercent;
    private string _catalogProgressText = string.Empty;
    private string _extensionStatus = string.Empty;
    private EventHistoryEntry? _selectedHistory;
    private IReadOnlyList<EventHistoryEntry> _selectedHistoryEntries = [];
    internal bool IsUpdatingHistory { get; private set; }
    private string _notice = string.Empty;
    private bool _isRefreshingEvents;
    private bool _restoringViewPreferences;
    private bool _showTemplateDescriptions = true;
    private readonly DispatcherTimer _queueTimer;
    private bool _isRefreshingQueueStatus;
    private int _fileScanTicks;
    private string _queueStatusText = string.Empty;
    private string _templateSearchText = string.Empty;
    private int _selectedSectionIndex;
    private bool _isRefreshingBridge;
    private bool _isInstallingBridge;
    private BridgeInstallationStatus? _bridgeInstallation;
    private int _bridgeStateRevision;

    public MainViewModel(BridgeRuntime runtime)
    {
        _runtime = runtime;
        ActiveBrowser = new ActionBrowserViewModel(ActiveActions, groupEqualPrices: true);
        UserPresetBrowser = new ActionBrowserViewModel(UserPresets);
        PresetBrowser = new ActionBrowserViewModel(Presets);
        ActiveBrowser.PreferencesChanged += SaveViewPreferences;
        UserPresetBrowser.PreferencesChanged += SaveViewPreferences;
        PresetBrowser.PreferencesChanged += SaveViewPreferences;
        CodeCommand = new RelayCommand(parameter => Run(() => _runtime.OpenActionCode(((ActionViewModel)parameter!).Model)));
        TestCommand = new AsyncRelayCommand(
            parameter => TestActionAsync((ActionViewModel)parameter!),
            parameter => parameter is ActionViewModel action && action.CanTest,
            allowConcurrentExecutions: true);
        RunAsDonorCommand = new AsyncRelayCommand(
            parameter => RunAsDonorAsync((ActionViewModel)parameter!),
            parameter => parameter is ActionViewModel { CanTest: true });
        TestPriceGroupCommand = new AsyncRelayCommand(
            parameter => TestPriceGroupAsync((ActivePriceGroupViewModel)parameter!),
            parameter => parameter is ActivePriceGroupViewModel { CanTest: true },
            allowConcurrentExecutions: true);
        AddFromPresetCommand = new AsyncRelayCommand(
            parameter => AddFromPresetAsync((ActionViewModel)parameter!),
            parameter => parameter is ActionViewModel { IsUserPreset: true });
        SaveAsUserPresetCommand = new AsyncRelayCommand(parameter => SaveAsUserPresetAsync((ActionViewModel)parameter!));
        RenameUserPresetCommand = new AsyncRelayCommand(parameter => RenameUserPresetAsync((ActionViewModel)parameter!));
        DeleteUserPresetCommand = new AsyncRelayCommand(parameter => DeleteUserPresetAsync((ActionViewModel)parameter!));
        DeleteActiveCommand = new AsyncRelayCommand(parameter => DeleteActiveAsync((ActionViewModel)parameter!));
        AddPresetHotkeyCommand = new AsyncRelayCommand(
            parameter => OpenPresetHotkeySettingsAsync((ActionViewModel)parameter!),
            parameter => parameter is ActionViewModel { IsPreset: false });
        AddTriggerCommand = new AsyncRelayCommand(parameter => EditTriggerAsync((ActionViewModel)parameter!, null));
        AddPriceGroupTriggerCommand = new AsyncRelayCommand(
            parameter => EditPriceGroupTriggerAsync((ActivePriceGroupViewModel)parameter!));
        RenamePriceGroupCommand = new RelayCommand(
            parameter => RenamePriceGroup((ActivePriceGroupViewModel)parameter!));
        EditTriggerCommand = new AsyncRelayCommand(parameter => EditTriggerAsync(((TriggerRuleViewModel)parameter!).Owner, (TriggerRuleViewModel)parameter!));
        DeleteTriggerCommand = new AsyncRelayCommand(parameter => DeleteTriggerAsync((TriggerRuleViewModel)parameter!));
        RetryCommand = new AsyncRelayCommand(
            () => RunAsync(RetrySelectedHistoryAsync), CanRetrySelected);
        ApplyQueuedImmediatelyCommand = new AsyncRelayCommand(
            () => RunAsync(ApplySelectedQueuedImmediatelyAsync),
            () => SelectedHistoryEntries.Any(_runtime.CanApplyQueuedImmediately));
        RemoveSelectedQueuedCommand = new AsyncRelayCommand(
            () => RunAsync(RemoveSelectedQueuedAsync),
            () => SelectedHistoryEntries.Any(_runtime.CanRemoveFromQueue));
        AddHistoryExclusionCommand = new AsyncRelayCommand(
            AddSelectedHistoryToExclusionsAsync,
            CanAddSelectedHistoryToExclusions);
        RemoveQueuedCommand = new AsyncRelayCommand(
            parameter => RunAsync(() => RemoveQueuedAsync((EventHistoryEntry)parameter!)),
            CanRemoveQueued);
        SortActiveByPriceCommand = new AsyncRelayCommand(
            parameter => SortActiveByPriceAsync(string.Equals(parameter as string, "descending", StringComparison.Ordinal)),
            _ => ActiveActions.Count > 1);
        ExportActivePresetsCommand = new AsyncRelayCommand(ExportActivePresetsAsync, () => ActiveActions.Count > 0);
        SaveCommand = new AsyncRelayCommand(() => SaveActionsAsync());
        SaveActionCommand = new AsyncRelayCommand(parameter => SaveActionsAsync(parameter as ActionViewModel));
        RefreshCatalogCommand = new AsyncRelayCommand(
            () => RunAsync(() => _runtime.RefreshCatalogAsync()),
            () => CanRefreshCatalog);
        RefreshEventsCommand = new AsyncRelayCommand(RefreshEventsAsync);
        ClearQueueCommand = new AsyncRelayCommand(ClearQueueAsync);
        InstallBridgeCommand = new AsyncRelayCommand(
            () => RunAsync(InstallBridgeAsync), () => ShowBridgeUpdate && !IsInstallingBridge);

        _queueTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _queueTimer.Tick += async (_, _) =>
        {
            RaisePropertyChanged(nameof(ProcessingStatus));
            await RefreshQueueStatusAsync();
            try
            {
                var actionName = await _runtime.RunFortuneWheelIfDueAsync();
                if (!string.IsNullOrWhiteSpace(actionName))
                    Notice = $"Колесо фортуны запустило шаблон «{actionName}».";
            }
            catch (Exception exception)
            {
                Notice = "Колесо фортуны не смогло запустить шаблон: " + exception.Message;
            }
            if (++_fileScanTicks % 5 == 0)
            {
                _runtime.RefreshActionManifestIfFilesChanged();
                await RefreshBridgeUpdateAsync();
            }
        };

        _runtime.GameConnectionChanged += snapshot => OnUi(() => ApplyGameSnapshot(snapshot));
        _runtime.DonationConnectionChanged += state => OnUi(() => ApplyDonationState(state));
        _runtime.HistoryAdded += entry => OnUi(() => AddHistory(entry));
        _runtime.SettingsChanged += () => OnUi(() =>
        {
            RefreshFromSettings();
            _ = RefreshBridgeUpdateAsync();
        });
        _runtime.CatalogStateChanged += state => OnUi(() => ApplyCatalogState(state));
        _runtime.CatalogSyncProgressChanged += progress =>
            OnUi(() => ApplyCatalogProgress(progress));
    }

    public ObservableCollection<ActionViewModel> ActiveActions { get; } = [];
    public ObservableCollection<ActionViewModel> UserPresets { get; } = [];
    public ObservableCollection<ActionViewModel> Presets { get; } = [];
    public ActionBrowserViewModel ActiveBrowser { get; }
    public ActionBrowserViewModel UserPresetBrowser { get; }
    public ActionBrowserViewModel PresetBrowser { get; }
    public string TemplateSearchText
    {
        get => _templateSearchText;
        set
        {
            if (!SetProperty(ref _templateSearchText, value ?? string.Empty)) return;
            ActiveBrowser.SearchText = UserPresetBrowser.SearchText = PresetBrowser.SearchText = _templateSearchText;
            RaisePropertyChanged(nameof(HasTemplateSearchText));
        }
    }
    public bool HasTemplateSearchText => !string.IsNullOrWhiteSpace(TemplateSearchText);
    public bool HasQueueStatus => !string.IsNullOrWhiteSpace(QueueStatusText);
    public bool ShowTemplateSearch => SelectedSectionIndex is >= 0 and < 3;
    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set
        {
            if (!SetProperty(ref _selectedSectionIndex, value)) return;
            RaisePropertyChanged(nameof(ShowTemplateSearch));
            RaisePropertyChanged(nameof(TemplateSearchHint));
        }
    }
    public string TemplateSearchHint => SelectedSectionIndex switch
    {
        0 => "Найти в активных…",
        1 => "Найти в пользовательских…",
        _ => "Найти в шаблонах…"
    };
    public void ClearTemplateSearch() => TemplateSearchText = string.Empty;
    public bool ShowTemplateDescriptions
    {
        get => _showTemplateDescriptions;
        set
        {
            if (SetProperty(ref _showTemplateDescriptions, value)) SaveViewPreferences();
        }
    }
    public ObservableCollection<EventHistoryEntry> History { get; } = [];
    public ObservableCollection<string> Currencies { get; } = [];
    public Func<ActionViewModel, TriggerRuleViewModel?, TriggerEditorResult?>? ShowTriggerEditor { get; set; }
    public Func<string, string, string?>? PromptForPresetName { get; set; }
    public Func<ActionViewModel, DonorLaunchInput?>? PromptForDonorLaunch { get; set; }
    public Func<string?>? SelectActiveExportPath { get; set; }
    public Func<Task>? ShowSettings { get; set; }
    public Func<int, Task>? ShowPresetHotkeySettings { get; set; }
    public Func<IReadOnlyList<CatalogEntry>, IReadOnlyList<CatalogEntry>?>? SelectCatalogExclusions { get; set; }

    public ICommand CodeCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand RunAsDonorCommand { get; }
    public ICommand TestPriceGroupCommand { get; }
    public ICommand AddFromPresetCommand { get; }
    public ICommand SaveAsUserPresetCommand { get; }
    public ICommand RenameUserPresetCommand { get; }
    public ICommand DeleteUserPresetCommand { get; }
    public ICommand DeleteActiveCommand { get; }
    public ICommand AddPresetHotkeyCommand { get; }
    public ICommand AddTriggerCommand { get; }
    public ICommand AddPriceGroupTriggerCommand { get; }
    public ICommand RenamePriceGroupCommand { get; }
    public ICommand EditTriggerCommand { get; }
    public ICommand DeleteTriggerCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ApplyQueuedImmediatelyCommand { get; }
    public ICommand RemoveSelectedQueuedCommand { get; }
    public ICommand AddHistoryExclusionCommand { get; }
    public ICommand RemoveQueuedCommand { get; }
    public ICommand SortActiveByPriceCommand { get; }
    public ICommand ExportActivePresetsCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveActionCommand { get; }
    public ICommand RefreshCatalogCommand { get; }
    public ICommand RefreshEventsCommand { get; }
    public ICommand ClearQueueCommand { get; }
    public ICommand InstallBridgeCommand { get; }
    public bool ShowBridgeUpdate => _bridgeInstallation?.CanInstall == true;
    public string BridgeUpdateButtonText => IsInstallingBridge ? "Обновление…" : "Обновить эффекты и аддон";
    public string BridgeUpdateHint => _bridgeInstallation?.Message ?? string.Empty;
    public bool IsInstallingBridge
    {
        get => _isInstallingBridge;
        private set
        {
            if (!SetProperty(ref _isInstallingBridge, value)) return;
            RaisePropertyChanged(nameof(BridgeUpdateButtonText));
            (InstallBridgeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string DonationStatus { get => _donationStatus; private set => SetProperty(ref _donationStatus, value); }
    public string GameStatus { get => _gameStatus; private set => SetProperty(ref _gameStatus, value); }
    public bool DonationConnected { get => _donationConnected; private set => SetProperty(ref _donationConnected, value); }
    public bool GameConnected { get => _gameConnected; private set => SetProperty(ref _gameConnected, value); }
    public bool GameReady { get => _gameReady; private set => SetProperty(ref _gameReady, value); }
    public string CatalogStatus { get => _catalogStatus; private set => SetProperty(ref _catalogStatus, value); }
    public bool IsCatalogSynchronizing
    {
        get => _isCatalogSynchronizing;
        private set
        {
            if (!SetProperty(ref _isCatalogSynchronizing, value)) return;
            RaisePropertyChanged(nameof(CanRefreshCatalog));
            if (RefreshCatalogCommand is AsyncRelayCommand command) command.RaiseCanExecuteChanged();
        }
    }
    public bool IsCatalogProgressIndeterminate
    {
        get => _isCatalogProgressIndeterminate;
        private set => SetProperty(ref _isCatalogProgressIndeterminate, value);
    }
    public double CatalogProgressPercent
    {
        get => _catalogProgressPercent;
        private set => SetProperty(ref _catalogProgressPercent, value);
    }
    public string CatalogProgressText
    {
        get => _catalogProgressText;
        private set => SetProperty(ref _catalogProgressText, value);
    }
    public string ExtensionStatus { get => _extensionStatus; private set => SetProperty(ref _extensionStatus, value); }
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }
    public bool IsRefreshingEvents
    {
        get => _isRefreshingEvents;
        private set
        {
            if (!SetProperty(ref _isRefreshingEvents, value)) return;
            RaisePropertyChanged(nameof(RefreshEventsButtonText));
        }
    }
    public string RefreshEventsButtonText => IsRefreshingEvents ? "Обновление…" : "Обновить";
    public string QueueStatusText
    {
        get => _queueStatusText;
        private set
        {
            if (!SetProperty(ref _queueStatusText, value)) return;
            RaisePropertyChanged(nameof(HasQueueStatus));
        }
    }
    public string DataDirectory => _runtime.DataDirectory;
    public bool IsLightTheme => _runtime.UiPreferences.UseLightTheme ?? _runtime.Settings.UseLightTheme;
    public bool IsBlackTheme => !IsLightTheme;
    public string ThemeColor => ThemeManager.NormalizeColor(_runtime.UiPreferences.ThemeColor);
    public bool IsRedTheme => ThemeColor == "red";
    public bool IsGreenTheme => ThemeColor == "green";
    public bool IsBlueTheme => ThemeColor == "blue";
    public bool IsYellowTheme => ThemeColor == "yellow";
    public bool CanArm => _runtime.CanArmDonationProcessing;
    public string ProcessingStatus => (Armed ? "Эффекты донатов включены" : "Эффекты донатов отключены") + "\n" + YouTubeVotingStatus;
    private string YouTubeVotingStatus
    {
        get
        {
            if (_runtime.VotingError.Length > 0) return "Голоса YouTube: ошибка голосования";
            if (!_runtime.Voting.Automation.Enabled || _runtime.Voting.YouTubeRoundId(_runtime.Settings.ActiveProfileId) is null)
                return "Голоса YouTube: приём в голосовании выключен";
            return _runtime.YouTubeChat.State switch
            {
                YouTubeChatState.Connected => "Голоса YouTube обрабатываются",
                YouTubeChatState.Connecting or YouTubeChatState.Retrying => "Голоса YouTube: ожидание подключения",
                YouTubeChatState.Ended => "Голоса YouTube: трансляция завершена",
                YouTubeChatState.Error => "Голоса YouTube: ошибка подключения",
                _ => "Голоса YouTube: чат не подключён"
            };
        }
    }
    public bool CanRefreshCatalog => GameConnected && _runtime.CanRefreshCatalog && !IsCatalogSynchronizing;
    public string ArmHint => !_runtime.ProcessingIntegrationConfigured
        ? "Сначала подключите DonationAlerts, DonateX или чат YouTube в настройках"
        : !_runtime.CanArmDonationProcessing
            ? $"Подключён аккаунт {_runtime.Settings.DonationAlertsAccountCode}, ожидался {_runtime.Settings.ExpectedAccountCode}"
            : "Включить или приостановить обычные эффекты донатов. Голосования работают независимо.";

    public bool Armed
    {
        get => _runtime.Settings.Armed;
        set
        {
            if (_runtime.Settings.Armed == value) return;
            if (value && !_runtime.ProcessingIntegrationConfigured)
            {
                ShowError("Сначала подключите DonationAlerts, DonateX или чат YouTube в настройках.");
                RaisePropertyChanged();
                return;
            }
            if (value && !_runtime.CanArmDonationProcessing)
            {
                ShowError($"Подключён аккаунт {_runtime.Settings.DonationAlertsAccountCode}, ожидался {_runtime.Settings.ExpectedAccountCode}. Проверьте настройки.");
                RaisePropertyChanged();
                return;
            }
            _runtime.Settings.Armed = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ProcessingStatus));
            _ = RunAsync(() => _runtime.SetArmedAsync(value));
        }
    }

    public EventHistoryEntry? SelectedHistory
    {
        get => _selectedHistory;
        set => SetHistorySelection(value is null ? [] : [value]);
    }

    public IReadOnlyList<EventHistoryEntry> SelectedHistoryEntries => _selectedHistoryEntries;

    public void SetHistorySelection(IEnumerable<EventHistoryEntry> entries)
    {
        if (IsUpdatingHistory) return;
        _selectedHistoryEntries = entries.Distinct().ToArray();
        _selectedHistory = _selectedHistoryEntries.FirstOrDefault();
        RaisePropertyChanged(nameof(SelectedHistory));
        RaisePropertyChanged(nameof(SelectedHistoryEntries));
        RefreshHistoryCommands();
    }

    private void RefreshHistoryCommands()
    {
        foreach (var command in new[] { RetryCommand, ApplyQueuedImmediatelyCommand, RemoveSelectedQueuedCommand,
                     AddHistoryExclusionCommand, RemoveQueuedCommand }.OfType<AsyncRelayCommand>())
            command.RaiseCanExecuteChanged();
    }

    private void RestoreHistorySelection(IReadOnlyList<EventHistoryEntry> selected)
    {
        SetHistorySelection(History.Where(item => selected.Any(previous =>
            EventHistoryProjection.IsSameLogicalEvent(previous, item))));
    }

    public async Task InitializeAsync()
    {
        var history = await _runtime.InitializeAsync();
        _restoringViewPreferences = true;
        var preferences = _runtime.UiPreferences;
        ShowTemplateDescriptions = preferences.ShowTemplateDescriptions;
        ActiveBrowser.SelectCategory(preferences.ActiveCategory, preferences.ActivePlayerSubcategory);
        ActiveBrowser.Columns = preferences.ActiveColumns;
        ActiveBrowser.AutomaticColumns = preferences.AutomaticActiveColumns;
        ActiveBrowser.CompactMode = preferences.CompactActiveActions;
        UserPresetBrowser.SelectCategory(preferences.UserPresetCategory, preferences.UserPresetPlayerSubcategory);
        UserPresetBrowser.Columns = preferences.UserPresetColumns;
        UserPresetBrowser.AutomaticColumns = preferences.AutomaticUserPresetColumns;
        UserPresetBrowser.CompactMode = preferences.CompactUserPresets;
        PresetBrowser.SelectCategory(preferences.PresetCategory, preferences.PresetPlayerSubcategory);
        PresetBrowser.Columns = preferences.PresetColumns;
        PresetBrowser.AutomaticColumns = preferences.AutomaticPresetColumns;
        PresetBrowser.CompactMode = preferences.CompactPresets;
        _restoringViewPreferences = false;
        foreach (var entry in EventHistoryProjection.LatestPerCommand(history)) History.Add(entry);
        CatalogStatus = _runtime.CatalogState.Status;
        ExtensionStatus = _runtime.ExtensionStatus;
        RefreshFromSettings();
        _queueTimer.Start();
        await RefreshQueueStatusAsync();
        await RefreshBridgeUpdateAsync();
    }

    public async Task OpenSettingsAsync()
    {
        if (ShowSettings is not null) await ShowSettings();
        RefreshFromSettings();
        await RefreshBridgeUpdateAsync();
    }

    public async Task RefreshBridgeUpdateAsync()
    {
        if (_isRefreshingBridge || IsInstallingBridge) return;
        _isRefreshingBridge = true;
        var revision = _bridgeStateRevision;
        var gamePath = _runtime.Settings.GamePath;
        var mo2Path = _runtime.Settings.EffectMo2ModPath;
        try
        {
            var status = await _runtime.InspectBridgeAsync(gamePath, mo2Path);
            // A folder/profile may have changed while files were being read.
            if (revision == _bridgeStateRevision)
                _bridgeInstallation = gamePath == _runtime.Settings.GamePath && mo2Path == _runtime.Settings.EffectMo2ModPath
                    ? status : null;
        }
        catch (Exception)
        {
            // Unknown/invalid installation state is not an available update.
            // The full diagnostic remains accessible in Settings.
            if (revision == _bridgeStateRevision) _bridgeInstallation = null;
        }
        finally
        {
            _isRefreshingBridge = false;
            RaiseBridgeUpdateChanged();
        }
    }

    public async Task InstallBridgeAsync()
    {
        if (IsInstallingBridge || !ShowBridgeUpdate) return;
        ++_bridgeStateRevision;
        IsInstallingBridge = true;
        try
        {
            _bridgeInstallation = await _runtime.InstallBridgeAsync(
                _runtime.Settings.GamePath, _runtime.Settings.EffectMo2ModPath);
            Notice = _bridgeInstallation.Message + " Полностью перезапустите игру.";
            RaiseBridgeUpdateChanged();
        }
        finally
        {
            IsInstallingBridge = false;
            RaiseBridgeUpdateChanged();
            await RefreshBridgeUpdateAsync();
        }
    }

    private void RaiseBridgeUpdateChanged()
    {
        RaisePropertyChanged(nameof(ShowBridgeUpdate));
        RaisePropertyChanged(nameof(BridgeUpdateHint));
        (InstallBridgeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public async Task ToggleDonationProcessingFromHotkeyAsync()
    {
        var enabled = !_runtime.Settings.Armed;
        await _runtime.SetArmedAsync(enabled).ConfigureAwait(false);
        OnUi(() => Notice = enabled
            ? "Обработка донатов включена горячей клавишей."
            : "Обработка донатов остановлена горячей клавишей.");
    }

    public async Task RepeatLastDonationFromHotkeyAsync()
    {
        var state = await _runtime.RepeatLastDonationAsync().ConfigureAwait(false);
        OnUi(() => Notice = state switch
        {
            DispatchState.Queued => "Последний донат повторён: подходящие шаблоны добавлены в очередь.",
            DispatchState.NoRule => "Последний донат повторён, но для его суммы и валюты нет активных шаблонов.",
            DispatchState.Rejected => "Последний донат повторён, но настройки подходящих шаблонов недопустимы.",
            _ => $"Повтор последнего доната: {state}."
        });
    }

    public async Task ActivateActivePresetFromHotkeyAsync(int presetNumber)
    {
        var displayName = await _runtime.ActivateActivePresetAsync(presetNumber).ConfigureAwait(false);
        OnUi(() => Notice = $"Горячая клавиша запустила шаблон №{presetNumber} «{displayName}».");
    }

    public void ReportHotkeyMessage(string message) => OnUi(() => Notice = message);

    private async Task OpenPresetHotkeySettingsAsync(ActionViewModel action)
    {
        if (action.IsPreset || action.DisplayOrdinal < 1) return;
        if (ShowPresetHotkeySettings is not null)
            await ShowPresetHotkeySettings(action.DisplayOrdinal);
    }

    private async void SaveViewPreferences()
    {
        if (_restoringViewPreferences) return;
        var namesByProfile = _runtime.UiPreferences.ActivePriceGroupNamesByProfile.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        if (ActiveBrowser.PriceGroupNames.Count == 0)
            namesByProfile.Remove(_runtime.ActiveProfile.Id);
        else
            namesByProfile[_runtime.ActiveProfile.Id] = ActiveBrowser.PriceGroupNames.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var preferences = new UiPreferences
        {
            ShowTemplateDescriptions = ShowTemplateDescriptions,
            ActiveCategory = ActiveBrowser.SelectedCategory.Id,
            ActiveColumns = ActiveBrowser.Columns,
            AutomaticActiveColumns = ActiveBrowser.AutomaticColumns,
            CompactActiveActions = ActiveBrowser.CompactMode,
            ActivePriceGroupNamesByProfile = namesByProfile,
            UserPresetCategory = UserPresetBrowser.SelectedCategory.Id,
            UserPresetColumns = UserPresetBrowser.Columns,
            AutomaticUserPresetColumns = UserPresetBrowser.AutomaticColumns,
            CompactUserPresets = UserPresetBrowser.CompactMode,
            PresetCategory = PresetBrowser.SelectedCategory.Id,
            PresetColumns = PresetBrowser.Columns,
            AutomaticPresetColumns = PresetBrowser.AutomaticColumns,
            CompactPresets = PresetBrowser.CompactMode
        };
        // Merge only presentation fields: a queued browser save must not
        // overwrite a newer color/background selection (or vice versa).
        try { await _runtime.UpdateUiPreferencesAsync(current => preferences with
        {
            ThemeColor = current.ThemeColor,
            UseLightTheme = current.UseLightTheme,
            AutomaticPriceCurrencies = current.AutomaticPriceCurrencies
        }); }
        catch (Exception exception) { Notice = "Не удалось запомнить вид списка: " + exception.Message; }
    }

    public ActionEditorViewModel CreateActionEditor(ActionViewModel source) => new(source, async draft =>
    {
        if (source.IsBuiltInPreset)
        {
            var current = Presets.FirstOrDefault(action => action.HandlerId == source.HandlerId)
                ?? throw new InvalidOperationException("Этот шаблон больше не доступен.");
            current.ApplyEditorDraft(draft);
            Notice = "Настройки коробочного шаблона сохранены. Теперь сохраните его в пользовательские шаблоны.";
        }
        else
        {
            source.ApplyEditorDraft(draft);
            await _runtime.SaveActionParametersAsync(source.Id, draft.Model);
            Notice = source.IsUserPreset
                ? "Параметры пользовательского шаблона сохранены."
                : "Параметры и пауза активного варианта сохранены.";
        }
    }, source.HandlerId == ActorBoostRecovery.HandlerId ? () => _runtime.ResetActorBoostAsync() : null,
        link => _runtime.OpenTemplateSource(source.HandlerId, link), _runtime.Settings.Actions);

    public Task SetLightThemeAsync(bool useLightTheme) => ChangeThemeAsync(null, useLightTheme);
    public Task SetThemeColorAsync(string color) => ChangeThemeAsync(color, null);
    public Task SetThemeAsync(string color, bool useLightTheme) => ChangeThemeAsync(color, useLightTheme);

    private async Task ChangeThemeAsync(string? color, bool? useLightTheme)
    {
        await _themeChangeGate.WaitAsync();
        var previousColor = ThemeColor;
        var previousLight = IsLightTheme;
        try
        {
            var selectedColor = color is null ? previousColor : ThemeManager.NormalizeColor(color);
            var selectedLight = useLightTheme ?? previousLight;
            if (selectedColor == previousColor && selectedLight == previousLight) return;
            ThemeManager.Apply(selectedLight, selectedColor);
            await _runtime.UpdateUiPreferencesAsync(current => current with
            {
                ThemeColor = selectedColor,
                UseLightTheme = selectedLight
            });
        }
        catch
        {
            ThemeManager.Apply(previousLight, previousColor);
            throw;
        }
        finally
        {
            RaiseThemeProperties();
            _themeChangeGate.Release();
        }
    }

    private void RaiseThemeProperties()
    {
        foreach (var name in new[] { nameof(IsLightTheme), nameof(IsBlackTheme), nameof(ThemeColor),
            nameof(IsRedTheme), nameof(IsGreenTheme), nameof(IsBlueTheme), nameof(IsYellowTheme) })
            RaisePropertyChanged(name);
    }
    private void RefreshFromSettings()
    {
        _runtime.UiPreferences.ActivePriceGroupNamesByProfile.TryGetValue(
            _runtime.ActiveProfile.Id, out var activeProfileGroupNames);
        ActiveBrowser.RestorePriceGroupNames(activeProfileGroupNames);
        var currentResults = ActiveActions
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().LastResult, StringComparer.Ordinal);
        var currentDrafts = Presets
            .GroupBy(item => item.HandlerId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Model.Clone(), StringComparer.Ordinal);
        var currentUserResults = UserPresets
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().LastResult, StringComparer.Ordinal);
        ActiveActions.Clear();
        UserPresets.Clear();
        Presets.Clear();
        var displayOrdinal = 1;
        foreach (var action in _runtime.Settings.Actions)
        {
            var viewModel = new ActionViewModel(
                action,
                catalogState: _runtime.CatalogState,
                originLabel: _runtime.GetActionOriginLabel(action.HandlerId),
                manifestAvailable: _runtime.IsHandlerAvailable(action.HandlerId),
                displayOrdinal: displayOrdinal++,
                hideUnavailableCatalogSections: _runtime.Settings.HideUnavailableCatalogSections);
            if (currentResults.TryGetValue(action.Id, out var result)) viewModel.LastResult = result;
            ActiveActions.Add(viewModel);
        }
        var userPresetOrdinal = 1;
        foreach (var preset in _runtime.Settings.UserPresets)
        {
            var viewModel = new ActionViewModel(
                preset,
                isUserPreset: true,
                catalogState: _runtime.CatalogState,
                originLabel: _runtime.GetActionOriginLabel(preset.HandlerId),
                manifestAvailable: _runtime.IsHandlerAvailable(preset.HandlerId),
                displayOrdinal: userPresetOrdinal++,
                hideUnavailableCatalogSections: _runtime.Settings.HideUnavailableCatalogSections);
            if (currentUserResults.TryGetValue(preset.Id, out var result)) viewModel.LastResult = result;
            UserPresets.Add(viewModel);
        }
        foreach (var preset in _runtime.Presets)
        {
            currentDrafts.TryGetValue(preset.HandlerId, out var existingDraft);
            Presets.Add(new ActionViewModel(
                CreatePresetDraft(preset, existingDraft),
                isPreset: true,
                catalogState: _runtime.CatalogState,
                originLabel: _runtime.GetActionOriginLabel(preset.HandlerId),
                manifestAvailable: _runtime.IsHandlerAvailable(preset.HandlerId),
                hideUnavailableCatalogSections: _runtime.Settings.HideUnavailableCatalogSections));
        }
        foreach (var action in ActiveActions.Concat(UserPresets).Concat(Presets))
        {
            action.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(ActionViewModel.CanTest) &&
                    TestCommand is AsyncRelayCommand command)
                    command.RaiseCanExecuteChanged();
                if (change.PropertyName == nameof(ActionViewModel.CanTest) &&
                    RunAsDonorCommand is AsyncRelayCommand donorCommand)
                    donorCommand.RaiseCanExecuteChanged();
            };
        }
        Currencies.Clear();
        foreach (var currency in _runtime.Settings.ObservedCurrencies) Currencies.Add(currency);
        RaisePropertyChanged(nameof(Armed));
        RaisePropertyChanged(nameof(ProcessingStatus));
        RaisePropertyChanged(nameof(CanArm));
        RaisePropertyChanged(nameof(ArmHint));
        RaisePropertyChanged(nameof(DataDirectory));
        RaiseThemeProperties();
        ExtensionStatus = _runtime.ExtensionStatus;
        if (SortActiveByPriceCommand is AsyncRelayCommand sortCommand) sortCommand.RaiseCanExecuteChanged();
        if (ExportActivePresetsCommand is AsyncRelayCommand exportCommand) exportCommand.RaiseCanExecuteChanged();
        if (TestCommand is AsyncRelayCommand testCommand) testCommand.RaiseCanExecuteChanged();
        if (RunAsDonorCommand is AsyncRelayCommand donorCommand) donorCommand.RaiseCanExecuteChanged();
        if (AddHistoryExclusionCommand is AsyncRelayCommand exclusionCommand)
            exclusionCommand.RaiseCanExecuteChanged();
    }

    private async Task AddFromPresetAsync(ActionViewModel preset)
    {
        if (!preset.IsUserPreset)
        {
            ShowError("В активные можно добавить только пользовательский шаблон. Сначала сохраните коробочный шаблон в пользовательские.");
            return;
        }
        if (!preset.TryCommitPendingEdits(out var parameterError))
        {
            ShowError(parameterError);
            return;
        }
        if (!preset.Model.Triggers.Any(trigger => trigger.Enabled))
        {
            ShowError("Добавьте хотя бы одну сумму перед добавлением шаблона в активные.");
            return;
        }
        try
        {
            await _runtime.AddActionInstanceAsync(preset.Model);
            Notice = $"«{preset.DisplayName}» добавлено в активные как новый независимый вариант.";
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async Task TestActionAsync(ActionViewModel action)
    {
        if (!action.TryCommitPendingEdits(out var error))
        {
            ShowError(error);
            return;
        }
        await RunAsync(() => _runtime.TestActionAsync(action.Model));
        await RefreshQueueStatusAsync();
    }

    private async Task RunAsDonorAsync(ActionViewModel action)
    {
        if (!action.TryCommitPendingEdits(out var error)) { ShowError(error); return; }
        var input = PromptForDonorLaunch?.Invoke(action);
        if (input is null) return;
        await RunAsync(() => _runtime.RunAsDonorAsync(action.Model, input.DonorName, input.Message));
        await RefreshQueueStatusAsync();
    }

    private async Task TestPriceGroupAsync(ActivePriceGroupViewModel group)
    {
        foreach (var action in group.Actions)
        {
            if (action.TryCommitPendingEdits(out var error)) continue;
            ShowError($"«{action.DisplayName}»: {error}");
            return;
        }
        await RunAsync(() => _runtime.TestPriceGroupAsync(
            group.Actions.Select(action => action.Model)));
        await RefreshQueueStatusAsync();
    }

    private void RenamePriceGroup(ActivePriceGroupViewModel group)
    {
        var name = PromptForPresetName?.Invoke("Переименовать группу шаблонов", group.DisplayName);
        if (name is null) return;
        try
        {
            ActiveBrowser.RenamePriceGroup(group, name);
            Notice = $"Группа переименована в «{group.DisplayName}».";
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async Task SaveAsUserPresetAsync(ActionViewModel source)
    {
        if (!source.TryCommitPendingEdits(out var error))
        {
            ShowError(error);
            return;
        }
        var suggested = source.IsUserPreset ? source.DisplayName + " — копия" : source.DisplayName;
        var name = PromptForPresetName?.Invoke("Сохранить пользовательский шаблон", suggested);
        if (name is null) return;
        try
        {
            await _runtime.SaveAsUserPresetAsync(source.Model, name);
            Notice = $"Пользовательский шаблон «{name.Trim()}» сохранён без цены.";
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task RenameUserPresetAsync(ActionViewModel preset)
    {
        if (!preset.IsUserPreset) return;
        var name = PromptForPresetName?.Invoke("Переименовать пользовательский шаблон", preset.DisplayName);
        if (name is null) return;
        try
        {
            await _runtime.RenameUserPresetAsync(preset.Id, name);
            Notice = $"Пользовательский шаблон переименован в «{name.Trim()}».";
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task DeleteUserPresetAsync(ActionViewModel preset)
    {
        if (!preset.IsUserPreset) return;
        if (!AppDialogs.Confirm(null, "Удалить пользовательский шаблон",
            $"Удалить пользовательский шаблон «{preset.DisplayName}»?",
            "Удалить")) return;
        try
        {
            await _runtime.DeleteUserPresetAsync(preset.Id);
            Notice = $"Пользовательский шаблон «{preset.DisplayName}» удалён.";
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task DeleteActiveAsync(ActionViewModel action)
    {
        if (action.IsPreset) return;
        if (!AppDialogs.Confirm(null, "Удалить из активных",
            $"Удалить «{action.DisplayName}» из активных?\n\nИсходный шаблон останется во вкладке «Шаблоны».",
            "Удалить")) return;
        try
        {
            await _runtime.DeleteActionInstanceAsync(action.Id);
            Notice = $"«{action.DisplayName}» удалено из активных. Шаблон остался доступен.";
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async Task EditTriggerAsync(ActionViewModel action, TriggerRuleViewModel? existing)
    {
        var rollback = action.IsBuiltInPreset ? null : CloneEditableCollection(action);
        var result = ShowTriggerEditor?.Invoke(action, existing);
        if (result is null) return;
        if (result.ReplaceAllPrices)
        {
            action.ReplaceTriggers(result.Rules);
        }
        else if (existing is null)
        {
            action.AddTrigger(result.SingleRule);
        }
        else
        {
            var index = action.Model.Triggers.IndexOf(existing.Model);
            if (index < 0)
            {
                if (rollback is not null) RestoreEditableCollection(action, rollback);
                ShowError("Ценовое условие было изменено в другом окне. Откройте его заново.");
                return;
            }
            var rule = result.SingleRule;
            existing.Model.Comparator = rule.Comparator;
            existing.Model.Amount = rule.Amount;
            existing.Model.Currency = rule.Currency;
            existing.Model.Enabled = rule.Enabled;
            existing.Refresh();
        }
        if (action.IsBuiltInPreset)
        {
            Notice = result.ReplaceAllPrices
                ? $"Цены черновика пересчитаны для {result.Rules.Count} валют."
                : "Сумма добавлена в черновик шаблона.";
            return;
        }
        try
        {
            await _runtime.SaveActionChangesAsync([action.Model]);
            Notice = result.ReplaceAllPrices
                ? $"Прежние цены заменены расчётом для {result.Rules.Count} валют."
                : "Ценовое условие сохранено.";
        }
        catch (Exception exception)
        {
            if (rollback is not null) RestoreEditableCollection(action, rollback);
            ShowError(exception.Message);
        }
    }

    private async Task EditPriceGroupTriggerAsync(ActivePriceGroupViewModel group)
    {
        if (group.Actions.Count < 2) return;
        var result = ShowTriggerEditor?.Invoke(group.Actions[0], null);
        if (result is null) return;
        var rollback = CloneActions();
        try
        {
            foreach (var action in group.Actions)
            {
                if (result.ReplaceAllPrices)
                    action.ReplaceTriggers(result.Rules);
                else
                    action.AddTrigger(result.SingleRule.Clone());
            }
            await _runtime.SaveActionChangesAsync(group.Actions.Select(action => action.Model));
            Notice = result.ReplaceAllPrices
                ? $"Цены одновременно пересчитаны для шаблонов: {group.Actions.Count}."
                : $"Сумма добавлена сразу для шаблонов: {group.Actions.Count}.";
        }
        catch (Exception exception)
        {
            RestoreActions(rollback);
            ShowError(exception.Message);
        }
    }

    private async Task DeleteTriggerAsync(TriggerRuleViewModel trigger)
    {
        var rollback = trigger.Owner.IsBuiltInPreset ? null : CloneEditableCollection(trigger.Owner);
        trigger.Owner.RemoveTrigger(trigger);
        if (trigger.Owner.IsBuiltInPreset)
        {
            Notice = "Сумма удалена из черновика шаблона.";
            return;
        }
        try { await _runtime.SaveActionChangesAsync([trigger.Owner.Model]); Notice = "Ценовое условие удалено."; }
        catch (Exception exception) { if (rollback is not null) RestoreEditableCollection(trigger.Owner, rollback); ShowError(exception.Message); }
    }

    private async Task SaveActionsAsync(ActionViewModel? requestedAction = null)
    {
        var editedActions = requestedAction is null ? ActiveActions.ToList() : [requestedAction];
        foreach (var action in editedActions)
        {
            if (!action.TryCommitPendingEdits(out var error))
            {
                ShowError($"«{action.DisplayName}»: {error}");
                return;
            }
        }
        var rollback = CloneActions();
        try
        {
            await _runtime.SaveActionChangesAsync(editedActions.Select(action => action.Model));
            Notice = "Параметры и паузы после срабатывания сохранены.";
        }
        catch (Exception exception)
        {
            // The draft remains visible when validation fails, but it cannot reach the pipe;
            // SaveSettings writes atomically and the dispatcher validates again before enqueue.
            RestoreActions(rollback);
            ShowError(exception.Message);
        }
    }

    private async Task SortActiveByPriceAsync(bool descending)
    {
        var previousOrder = _runtime.Settings.Actions;
        var sorted = ActionPriceOrdering.SortByMinimumAssignedAmount(previousOrder, descending);
        if (sorted.Select(action => action.Id).SequenceEqual(previousOrder.Select(action => action.Id), StringComparer.Ordinal))
        {
            Notice = descending
                ? "Активные действия уже отсортированы по убыванию цены."
                : "Активные действия уже отсортированы по возрастанию цены.";
            return;
        }

        _runtime.Settings.Actions = sorted;
        try
        {
            await _runtime.SaveSettingsAsync();
            RefreshFromSettings();
            Notice = descending
                ? "Активные действия отсортированы по убыванию минимальной цены."
                : "Активные действия отсортированы по возрастанию минимальной цены.";
        }
        catch (Exception exception)
        {
            _runtime.Settings.Actions = previousOrder;
            RefreshFromSettings();
            ShowError(exception.Message);
        }
    }

    public Task MoveSequenceStepAsync(string key, string source, string target, bool after) => RunAsync(async () =>
    {
        await _runtime.ReorderActiveGroupAsync(key, source, target, after);
        Notice = "Порядок шагов сохранён. Он применяется к новым донатам.";
    });

    public Task SetStrictSequenceAsync(string key, bool enabled) => RunAsync(async () =>
    {
        await _runtime.SetGroupStrictSequenceAsync(key, enabled);
        Notice = enabled ? "Строгий порядок включён для новых донатов." : "Строгий порядок отключён.";
    });

    public Task SetGroupDelayAsync(string key, int seconds) => RunAsync(async () =>
    {
        await _runtime.SetGroupDelayAsync(key, seconds);
        Notice = $"Задержка группы: {seconds} сек. Настройка сохранена для новых донатов.";
    });

    internal void ReportGroupDelayInputError() =>
        Notice = "Задержка группы должна быть целым числом от 0 до 86 400 секунд.";

    private async Task ExportActivePresetsAsync()
    {
        var path = SelectActiveExportPath?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var exportActions = ActiveBrowser.BuildExportActions();
            var text = ActivePresetTextExporter.Build(exportActions);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Notice = $"Список из {exportActions.Count} строк экспортирован для стрима.";
        }
        catch (Exception exception) { ShowError("Не удалось экспортировать список: " + exception.Message); }
    }

    private List<ActionDefinition> CloneActions() => _runtime.Settings.Actions.Select(item => item.Clone()).ToList();

    private List<ActionDefinition> CloneEditableCollection(ActionViewModel action) =>
        (action.IsUserPreset ? _runtime.Settings.UserPresets : _runtime.Settings.Actions)
        .Select(item => item.Clone()).ToList();

    private void RestoreEditableCollection(ActionViewModel action, List<ActionDefinition> items)
    {
        if (action.IsUserPreset) _runtime.Settings.UserPresets = items;
        else _runtime.Settings.Actions = items;
        RefreshFromSettings();
    }

    private static ActionDefinition CreatePresetDraft(ActionDefinition preset, ActionDefinition? existing)
    {
        var draft = preset.Clone();
        draft.IsActive = false;
        if (existing is null) return draft;

        draft.CooldownSeconds = existing.CooldownSeconds;
        draft.ItemSpawns = existing.ItemSpawns.Select(entry => entry.Clone()).ToList();
        draft.SpawnGroups = existing.SpawnGroups.Select(entry => entry.Clone()).ToList();
        foreach (var definition in draft.ParameterDefinitions)
        {
            if (existing.Parameters.TryGetValue(definition.Key, out var value) &&
                (definition.CatalogKind is not null || definition.Validate(value) is null))
            {
                draft.Parameters[definition.Key] = value;
            }
        }
        draft.Triggers = existing.Triggers.Select(trigger => trigger.Clone()).ToList();
        return draft;
    }

    private void RestoreActions(List<ActionDefinition> actions)
    {
        _runtime.Settings.Actions = actions;
        RefreshFromSettings();
    }

    private void ApplyGameSnapshot(GameConnectionSnapshot snapshot)
    {
        GameConnected = snapshot.Connected;
        GameReady = snapshot.Ready;
        GameStatus = GameStatusPresentation.Connection(snapshot);
        RaisePropertyChanged(nameof(CanRefreshCatalog));
        if (RefreshCatalogCommand is AsyncRelayCommand command) command.RaiseCanExecuteChanged();
        _ = RefreshQueueStatusAsync();
    }

    private void ApplyDonationState(DonationConnectionState state)
    {
        DonationConnected = state.Connected;
        DonationStatus = state.Message;
        RaisePropertyChanged(nameof(CanArm));
        RaisePropertyChanged(nameof(ArmHint));
    }

    private void ApplyCatalogState(CatalogRuntimeState state)
    {
        CatalogStatus = state.Status;
        RaisePropertyChanged(nameof(CanRefreshCatalog));
        if (RefreshCatalogCommand is AsyncRelayCommand command) command.RaiseCanExecuteChanged();
        RefreshFromSettings();
    }

    private void ApplyCatalogProgress(CatalogSyncProgress progress)
    {
        IsCatalogProgressIndeterminate = progress.IsSynchronizing && progress.TotalCount <= 0;
        CatalogProgressPercent = progress.TotalCount > 0
            ? Math.Clamp(progress.ReceivedCount * 100d / progress.TotalCount, 0d, 100d)
            : 0d;
        CatalogProgressText = !progress.IsSynchronizing
            ? string.Empty
            : progress.TotalCount > 0
                ? $"Получено {progress.ReceivedCount:N0} из {progress.TotalCount:N0} секций"
                : "Игра подготавливает каталог…";
        IsCatalogSynchronizing = progress.IsSynchronizing;
    }

    private void AddHistory(EventHistoryEntry entry)
    {
        var selected = SelectedHistoryEntries;
        IsUpdatingHistory = true;
        try
        {
            var existingIndex = -1;
            for (var index = 0; index < History.Count; index++)
            {
                if (!EventHistoryProjection.IsSameLogicalEvent(History[index], entry)) continue;
                existingIndex = index;
                break;
            }

            if (existingIndex >= 0) History[existingIndex] = entry;
            else
            {
                var insertionIndex = 0;
                var entryTime = EventHistoryProjection.SortTime(entry);
                while (insertionIndex < History.Count &&
                       EventHistoryProjection.SortTime(History[insertionIndex]) >= entryTime)
                    insertionIndex++;
                History.Insert(insertionIndex, entry);
            }
            while (History.Count > 500) History.RemoveAt(History.Count - 1);
        }
        finally { IsUpdatingHistory = false; RestoreHistorySelection(selected); }
        _ = RefreshQueueStatusAsync();
        var action = ActiveActions.FirstOrDefault(item => item.Id == entry.ActionId);
        if (action is not null) action.LastResult = $"{entry.State}: {entry.Detail}";
    }

    private async Task RemoveQueuedAsync(EventHistoryEntry entry)
    {
        await _runtime.RemoveFromQueueAsync(entry);
        Notice = $"«{entry.ActionName}» удалено из очереди.";
        await RefreshQueueStatusAsync();
    }

    private async Task ClearQueueAsync()
    {
        if (!AppDialogs.Confirm(null, "Очистить очередь",
            "Удалить все ещё не отправленные эффекты из очереди? Уже переданный игре эффект отменён не будет, но если игра его отложит, он не вернётся в очередь.",
            "Очистить")) return;
        try
        {
            var result = await _runtime.ClearQueueAsync();
            Notice = result switch
            {
                { AffectedCount: 0 } => "Очередь уже пуста.",
                { CancelledEntries.Count: 0, SuppressedInFlightRetries: > 0 } =>
                    "Очередь очищена. Текущий отложенный эффект не вернётся в неё после ответа игры.",
                { SuppressedInFlightRetries: > 0 } =>
                    $"Из очереди удалено эффектов: {result.CancelledEntries.Count}. Текущий отложенный эффект также не будет повторён.",
                _ => $"Из очереди удалено эффектов: {result.CancelledEntries.Count}."
            };
            await RefreshQueueStatusAsync();
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task RefreshQueueStatusAsync()
    {
        if (_isRefreshingQueueStatus) return;
        _isRefreshingQueueStatus = true;
        try
        {
            var state = await _runtime.GetQueueTimingAsync();
            QueueStatusText = state switch
            {
                { AwaitingResult: true, WaitingCount: > 0 } => $"Выполняется эффект · ещё в очереди: {state.WaitingCount}",
                { AwaitingResult: true } => "Выполняется эффект · ждём подтверждение игры",
                { WaitingForGame: true } => $"В очереди: {state.WaitingCount} · ждём готовность игры",
                { WaitingCount: <= 0 } => string.Empty,
                { SecondsUntilNext: > 0 } => $"Следующий эффект через {FormatCountdown(state.SecondsUntilNext)} · в очереди: {state.WaitingCount}",
                _ => $"Следующий эффект готов к запуску · в очереди: {state.WaitingCount}"
            };
        }
        catch (OperationCanceledException) { }
        catch { QueueStatusText = string.Empty; }
        finally { _isRefreshingQueueStatus = false; }
    }

    private static string FormatCountdown(int seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private async Task RefreshEventsAsync()
    {
        IsRefreshingEvents = true;
        try
        {
            var refreshed = await _runtime.RefreshEventsAsync();
            var selectedHistory = SelectedHistoryEntries;
            IsUpdatingHistory = true;
            try
            {
                History.Clear();
                foreach (var entry in EventHistoryProjection.LatestPerCommand(refreshed.History)) History.Add(entry);
            }
            finally { IsUpdatingHistory = false; RestoreHistorySelection(selectedHistory); }

            var recovery = refreshed.Recovery;
            Notice = recovery.RequestedCount == 0
                ? "История обновлена. Зависших ответов от игры нет."
                : recovery.GameProcessChanged
                    ? "История обновлена. Игра была перезапущена, поэтому неподтверждённый эффект безопасно не повторяется."
                    : recovery.RemainingCount == 0
                        ? $"История обновлена. Восстановлено ответов игры: {recovery.RecoveredCount}."
                        : !recovery.GameConnected
                            ? $"Игра не подключена. Ответов ожидается: {recovery.RemainingCount}; они будут запрошены после возврата этой же игровой сессии."
                            : recovery.ReplayRequested
                                ? $"Игра получила повторный запрос, но пока не вернула ответов: {recovery.RemainingCount}."
                                : $"Игра подключена, но сейчас не готова. Ответов ожидается: {recovery.RemainingCount}; запрос выполнится после загрузки.";
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            IsRefreshingEvents = false;
        }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private static bool CanRepeatHistory(EventHistoryEntry entry) =>
        entry.State is not (DispatchState.Queued or DispatchState.Sent) && BridgeRuntime.CanRetryHistoryEntry(entry);

    private bool CanRetrySelected() => SelectedHistoryEntries.Any(CanRepeatHistory);

    private async Task RetrySelectedHistoryAsync()
    {
        foreach (var entry in SelectedHistoryEntries.Where(CanRepeatHistory).ToArray())
            await _runtime.RetryManuallyAsync(entry);
    }

    private async Task ApplySelectedQueuedImmediatelyAsync()
    {
        var count = await _runtime.ApplyQueuedImmediatelyAsync(SelectedHistoryEntries.ToArray());
        Notice = count == 0 ? "Выбранные события уже вышли из очереди."
            : $"Без ожидания пауз запрошено событий: {count}. Выполнение — при готовности игры.";
        RefreshHistoryCommands();
        await RefreshQueueStatusAsync();
    }

    private async Task RemoveSelectedQueuedAsync()
    {
        foreach (var entry in SelectedHistoryEntries.Where(_runtime.CanRemoveFromQueue).ToArray())
            await RemoveQueuedAsync(entry);
    }

    private bool CanAddSelectedHistoryToExclusions() =>
        SelectedHistoryEntries.Any(entry => AvailableHistoryExclusionCandidates(entry).Count > 0);

    private IReadOnlyList<CatalogEntry> AvailableHistoryExclusionCandidates(EventHistoryEntry entry)
    {
        var excluded = _runtime.Settings.CatalogExclusions
            .Select(item => (item.Kind, item.SectionId))
            .ToHashSet();
        return _runtime.HistoryExclusionCandidates(entry)
            .Where(candidate => !excluded.Contains((candidate.Kind, candidate.SectionId)))
            .ToList();
    }

    private async Task AddSelectedHistoryToExclusionsAsync()
    {
        var candidates = SelectedHistoryEntries.SelectMany(AvailableHistoryExclusionCandidates)
            .DistinctBy(entry => (entry.Kind, entry.SectionId)).ToArray();
        if (candidates.Length == 0)
        {
            Notice = "В этой записи нет новых секций каталога для исключения.";
            return;
        }
        IReadOnlyList<CatalogEntry>? selected = candidates.Length == 1
            ? candidates
            : SelectCatalogExclusions?.Invoke(candidates);
        if (selected is null || selected.Count == 0) return;
        var added = await _runtime.AddCatalogExclusionsAsync(selected);
        Notice = added == 1
            ? $"Секция {selected[0].SectionId} добавлена в исключения."
            : $"В исключения добавлено секций: {added}.";
        if (AddHistoryExclusionCommand is AsyncRelayCommand command) command.RaiseCanExecuteChanged();
    }

    private bool CanRemoveQueued(object? parameter) =>
        parameter is EventHistoryEntry entry && _runtime.CanRemoveFromQueue(entry);

    private void Run(Action operation)
    {
        try { operation(); }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void ShowError(string message)
    {
        Notice = message;
        AppDialogs.Info(null, "Anomaly Donation Bridge", message);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
