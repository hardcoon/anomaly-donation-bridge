using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.DonationAlerts;
using AnomalyDonationBridge.DonateX;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonUiPreferencesStore _uiPreferencesStore;
    private readonly SemaphoreSlim _uiPreferencesSaveGate = new(1, 1);
    private readonly DpapiSecretStore _secretStore;
    private readonly JsonLineHistoryStore _historyStore;
    private readonly JsonLineProcessedDonationStore _processedStore;
    private readonly JsonPendingDispatchStore _pendingDispatchStore;
    private readonly JsonCatalogCacheStore _catalogCacheStore;
    private readonly SafeFileLogger _logger;
    private readonly NotifyingHistorySink _notifyingHistory;
    private readonly NamedPipeGameServer _gameServer;
    private readonly HttpClient _httpClient;
    private readonly DonationAlertsApiRateLimiter _apiRateLimiter;
    private readonly DonationAlertsRestClient _donationRest;
    private readonly DonateXApiRateLimiter _donateXRateLimiter;
    private readonly DonateXRestClient _donateXRest;
    private readonly LoopbackOAuthFlow _oauthFlow;
    private readonly SemaphoreSlim _donationGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly SemaphoreSlim _donateXRecoveryGate = new(1, 1);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly SemaphoreSlim _bridgeInstallGate = new(1, 1);
    private readonly SemaphoreSlim _resultHandlingGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ActionManifestLoader _manifestLoader = new();
    private readonly AddonPackageManager _addonPackageManager;
    private readonly FilePresetLoader _filePresetLoader;
    private readonly EffectScriptManager _effectScriptManager;
    private readonly ExtensionManifestLoader _extensionManifestLoader;
    private readonly ConcurrentDictionary<string, string> _persistedQueuedCommands = new(StringComparer.Ordinal);
    private List<ActionDefinition> _presets = [];
    private IReadOnlyDictionary<string, string> _filePresetSources =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private string _filePresetFingerprint = string.Empty;
    private DonationDispatcher _dispatcher;
    private DonationAlertsSession? _donationSession;
    private DonateXSession? _donateXSession;
    private DonationConnectionState _donationAlertsConnection = new(false, "DonationAlerts не подключён");
    private DonateXConnectionState _donateXConnection = new(false, "DonateX не подключён");
    private Task? _drainLoop;
    private CatalogRuntimeState _catalogState = CatalogRuntimeState.Empty;
    private CatalogSnapshot? _catalogSource;
    private IReadOnlyDictionary<string, ExtensionActionOrigin> _extensionOrigins =
        new Dictionary<string, ExtensionActionOrigin>(StringComparer.Ordinal);
    private string _extensionStatus = "Расширения не загружены";
    private readonly SemaphoreSlim _fortuneWheelGate = new(1, 1);
    private int _fortuneWheelConfiguredMinutes = -1;
    private DateTimeOffset _fortuneWheelDeadline = DateTimeOffset.MaxValue;
    private bool _settingsLoaded;
    private volatile bool _safeZoneConfigSynchronized;
    private bool _votingLoaded;
    private volatile string? _votingDrawingRoundId;
    private volatile string? _votingDrawingCollectionId;
    private readonly SemaphoreSlim _votingDrawGate = new(1, 1);

    public BridgeRuntime(AppPaths? paths = null)
    {
        _paths = paths ?? AppPaths.CreateDefault();
        _settingsStore = new JsonSettingsStore(_paths);
        Voting = new VotingCoordinator(new JsonVotingStore(_paths));
        _uiPreferencesStore = new JsonUiPreferencesStore(_paths);
        _secretStore = new DpapiSecretStore(_paths);
        _historyStore = new JsonLineHistoryStore(_paths);
        _processedStore = new JsonLineProcessedDonationStore(_paths);
        _pendingDispatchStore = new JsonPendingDispatchStore(_paths);
        _catalogCacheStore = new JsonCatalogCacheStore(_paths);
        _logger = new SafeFileLogger(_paths);
        _addonPackageManager = new AddonPackageManager(_paths);
        _filePresetLoader = new FilePresetLoader();
        _effectScriptManager = new EffectScriptManager(_paths, _filePresetLoader);
        _extensionManifestLoader = new ExtensionManifestLoader(_paths);
        _notifyingHistory = new NotifyingHistorySink(_historyStore, entry =>
        {
            UpdateCheckpointFromHistory(entry);
            UpdatePersistedQueueAvailability(entry);
            HistoryAdded?.Invoke(entry);
        });
        _dispatcher = new DonationDispatcher(_processedStore, _notifyingHistory,
            pendingStore: _pendingDispatchStore);
        _gameServer = new NamedPipeGameServer(typeof(BridgeRuntime).Assembly.GetName().Version?.ToString(3) ?? "1.0.53");
        _gameServer.AdditionalCommandPolicy = (command, capabilities) => SafeZoneConfig.Prepare(
            command, capabilities, Settings.SafeZone, _safeZoneConfigSynchronized);
        _httpClient = new HttpClient();
        _apiRateLimiter = new DonationAlertsApiRateLimiter(() => Settings.LimitDonationAlertsApiRequests);
        _donationRest = new DonationAlertsRestClient(_httpClient, _apiRateLimiter);
        _donateXRateLimiter = new DonateXApiRateLimiter(() => Settings.LimitDonateXApiRequests);
        _donateXRest = new DonateXRestClient(_httpClient, _donateXRateLimiter);
        _oauthFlow = new LoopbackOAuthFlow(_donationRest);
    }

    public AppSettings Settings { get; private set; } = new();
    public VotingCoordinator Voting { get; }
    public bool VotingIsSpinning => _votingDrawingRoundId is not null;
    public string? VotingDrawingCollectionId => _votingDrawingCollectionId;
    internal string? VotingDrawingRoundId => _votingDrawingRoundId;
    public event Action<VotingRound>? VotingDrawStarted;
    private string _votingError = string.Empty;
    public string VotingError { get => _votingError.Length > 0 ? _votingError : _youTubeVotingError; private set => _votingError = value; }
    public UiPreferences UiPreferences { get; private set; } = new();
    public Task SaveUiPreferencesAsync(UiPreferences preferences) =>
        UpdateUiPreferencesAsync(_ => preferences);

    public async Task UpdateUiPreferencesAsync(Func<UiPreferences, UiPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _uiPreferencesSaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var preferences = update(UiPreferences);
            await _uiPreferencesStore.SaveAsync(preferences).ConfigureAwait(false);
            UiPreferences = preferences;
        }
        finally { _uiPreferencesSaveGate.Release(); }
    }
    public SecretBundle Secrets { get; private set; } = new();
    public IReadOnlyList<ActionDefinition> Presets => _presets;
    public IReadOnlyList<DonationProfile> Profiles => Settings.Profiles;
    public DonationProfile ActiveProfile => Settings.Profiles.First(profile =>
        string.Equals(profile.Id, Settings.ActiveProfileId, StringComparison.Ordinal));
    public string DataDirectory => _paths.RootDirectory;
    public CatalogRuntimeState CatalogState => _catalogState;
    public string ExtensionsDirectory => _paths.ExtensionsDirectory;
    public string EffectsDirectory => _filePresetLoader.EffectsDirectory;
    public string ExtensionStatus => _extensionStatus;
    public bool CanRefreshCatalog => _gameServer.IsConnected &&
        _gameServer.Capabilities.SupportsCapability("dynamic_catalog");
    public bool AccountMatchesExpected => string.IsNullOrWhiteSpace(Settings.ExpectedAccountCode) ||
        string.IsNullOrWhiteSpace(Settings.DonationAlertsAccountCode) ||
        string.Equals(Settings.ExpectedAccountCode, Settings.DonationAlertsAccountCode, StringComparison.OrdinalIgnoreCase);
    public bool DonationAlertsAccountConfigured => Secrets.HasOAuthTokens &&
        Settings.DonationAlertsUserId is not null &&
        !string.IsNullOrWhiteSpace(Settings.DonationAlertsAccountCode);
    public bool DonateXAccountConfigured => Secrets.HasDonateXToken &&
        !string.IsNullOrWhiteSpace(Settings.DonateXUserId) &&
        !string.IsNullOrWhiteSpace(Settings.DonateXAccountName);
    public bool DonationAccountConfigured => DonationAlertsAccountConfigured || DonateXAccountConfigured;
    public bool YouTubeChatConfigured => Secrets.HasYouTubeApiKey && Settings.YouTubeChatEnabled &&
        !string.IsNullOrWhiteSpace(Settings.YouTubeChatVideoUrl);
    public bool ProcessingIntegrationConfigured => DonationAccountConfigured || YouTubeChatConfigured;
    public bool CanArmDonationProcessing => CanArmIntegrations(
        DonationAlertsAccountConfigured, AccountMatchesExpected, DonateXAccountConfigured, YouTubeChatConfigured);

    internal static bool CanArmIntegrations(
        bool donationAlertsConfigured,
        bool donationAlertsAccountMatches,
        bool donateXConfigured, bool youTubeConfigured = false) =>
        youTubeConfigured || donateXConfigured || (donationAlertsConfigured && donationAlertsAccountMatches);

    public event Action<GameConnectionSnapshot>? GameConnectionChanged;
    public event Action<DonationConnectionState>? DonationConnectionChanged;
    public event Action<EventHistoryEntry>? HistoryAdded;
    public event Action? SettingsChanged;
    public event Action<CatalogRuntimeState>? CatalogStateChanged;
    public event Action<CatalogSyncProgress>? CatalogSyncProgressChanged;

    public async Task<IReadOnlyList<EventHistoryEntry>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _settingsLoaded = true;
        await CatalogExclusionConfig.WriteAsync(
            Settings.GamePath, Settings.CatalogExclusions, cancellationToken).ConfigureAwait(false);
        await SynchronizeSafeZoneAsync(cancellationToken).ConfigureAwait(false);
        UpdateFortuneWheelConfiguration(DateTimeOffset.UtcNow);
        UiPreferences = await _uiPreferencesStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        Secrets = await _secretStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var extensionWarning in RefreshFilePresets())
        {
            await _logger.WriteAsync("WARN", extensionWarning, cancellationToken).ConfigureAwait(false);
        }
        if (Settings.Armed && !CanArmDonationProcessing)
        {
            Settings.Armed = false;
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(Settings.GamePath))
        {
            var manifestWarnings = _manifestLoader.MergeFromGame(Settings.GamePath, _presets).ToList();
            manifestWarnings.AddRange(_manifestLoader.ApplyPresetsToInstances(Settings.Actions, _presets));
            manifestWarnings.AddRange(_manifestLoader.ApplyPresetsToInstances(Settings.UserPresets, _presets));
            foreach (var warning in manifestWarnings)
            {
                await _logger.WriteAsync("WARN", warning, cancellationToken).ConfigureAwait(false);
            }

            var cachedCatalog = await _catalogCacheStore.LoadAsync(Settings.GamePath, cancellationToken).ConfigureAwait(false);
            if (cachedCatalog is not null)
            {
                var snapshot = cachedCatalog.ToSnapshot();
                _catalogSource = snapshot;
                var effectiveCatalog = ApplyCatalog(snapshot, isLive: false);
                _catalogState = new CatalogRuntimeState(
                    cachedCatalog.ToCapabilities(),
                    effectiveCatalog,
                    false,
                    $"Офлайн-кэш · {snapshot.Entries.Count} секций · {cachedCatalog.CachedAt:dd.MM.yyyy HH:mm}");
            }
        }

        _dispatcher = new DonationDispatcher(_processedStore, _notifyingHistory,
            pendingStore: _pendingDispatchStore)
        {
            GlobalCooldown = TimeSpan.FromSeconds(Settings.GlobalCooldownSeconds),
            AnonymousDonorName = Settings.AnonymousDonorName
        };
        await _dispatcher.RestoreAsync(cancellationToken).ConfigureAwait(false);
        var history = await _historyStore.LoadRecentAsync(500, cancellationToken).ConfigureAwait(false);
        await LoadPersistedQueueAvailabilityAsync(history, cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeVotingModuleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            VotingError = error.Message;
            await _logger.WriteAsync("WARN", "Voting disabled: " + error.Message, cancellationToken).ConfigureAwait(false);
        }

        _gameServer.ConnectionChanged += snapshot =>
        {
            var catalogFailure = GameStatusPresentation.CatalogFailure(snapshot.Reason);
            if (snapshot.Connected && !_catalogState.IsLive && catalogFailure is not null)
            {
                _catalogState = _catalogState with
                {
                    Status = $"{EngineProfileLabel(_catalogState.Capabilities.Profile)} · каталог недоступен: {catalogFailure}"
                };
                CatalogStateChanged?.Invoke(_catalogState);
            }
            GameConnectionChanged?.Invoke(snapshot);
            _ = DrainSafelyAsync();
        };
        _gameServer.ResultReceived += result => _ = CompleteResultSafelyAsync(result);
        _gameServer.CapabilitiesChanged += capabilities =>
        {
            InvalidateCatalogRevisions();
            var disconnected = ReferenceEquals(capabilities, CapabilitySet.Empty);
            _catalogState = _catalogState with
            {
                Capabilities = disconnected && _catalogState.Catalog is not null
                    ? _catalogState.Capabilities
                    : capabilities,
                IsLive = false,
                Status = disconnected
                    ? _catalogState.Catalog is null ? "Игра отключена; каталога нет" : "Игра отключена; показан офлайн-кэш"
                    : capabilities.SupportsCapability("catalog_identity")
                        ? $"{EngineProfileLabel(capabilities.Profile)} · проверка сохранённого каталога…"
                        : _catalogState.Catalog is null
                            ? $"{EngineProfileLabel(capabilities.Profile)} · нажмите ↻, чтобы загрузить каталог"
                            : $"{EngineProfileLabel(capabilities.Profile)} · показан кэш; нажмите ↻ для обновления"
            };
            CatalogStateChanged?.Invoke(_catalogState);
        };
        _gameServer.CatalogInfoChanged += catalogInfo => _ = ApplyCatalogInfoSafelyAsync(catalogInfo);
        _gameServer.CatalogChanged += snapshot => _ = ApplyLiveCatalogSafelyAsync(snapshot);
        _gameServer.CatalogSyncProgressChanged += progress =>
            CatalogSyncProgressChanged?.Invoke(progress);
        _gameServer.ProtocolError += exception => _ = _logger.WriteAsync("WARN", $"Game IPC: {exception.Message}");
        _gameServer.Start();
        _drainLoop = Task.Run(() => DrainLoopAsync(_lifetime.Token));

        if (Secrets.HasOAuthTokens && !string.IsNullOrWhiteSpace(Settings.DonationAlertsClientId) && !string.IsNullOrWhiteSpace(Secrets.ClientSecret))
        {
            _ = Task.Run(() => StartDonationSessionSafelyAsync(_lifetime.Token));
        }
        else
        {
            SetDonationAlertsConnection(new DonationConnectionState(false, "DonationAlerts не подключён"));
        }

        if (Secrets.HasDonateXToken)
        {
            _ = Task.Run(() => StartDonateXSessionSafelyAsync(_lifetime.Token));
        }
        else
        {
            SetDonateXConnection(new DonateXConnectionState(false, "DonateX не подключён"));
        }

        if (Settings.YouTubeChatEnabled) _youTubeStartup = ResumeYouTubeChatAsync();
        return history;
    }

    public async Task ConnectDonationAlertsAsync(
        string clientId,
        string? newClientSecret,
        Action<Uri> openBrowser,
        CancellationToken cancellationToken = default)
    {
        var clientSecret = string.IsNullOrWhiteSpace(newClientSecret) ? Secrets.ClientSecret : newClientSecret.Trim();
        var result = await _oauthFlow.AuthorizeAsync(clientId.Trim(), clientSecret, openBrowser, cancellationToken).ConfigureAwait(false);

        Settings.DonationAlertsClientId = clientId.Trim();
        Settings.DonationAlertsUserId = result.Profile.Id;
        Settings.DonationAlertsAccountCode = result.Profile.Code;
        Settings.DonationAlertsAccountName = result.Profile.Name;
        Settings.InitialDonationSnapshotCompleted = false;
        Settings.LastDonationId = string.Empty;
        Settings.LastDonationCreatedAt = null;
        Secrets.ClientSecret = clientSecret;
        ApplyTokens(result.Tokens);
        Secrets.SocketConnectionToken = result.Profile.SocketConnectionToken;
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        await StartDonationSessionAsync(result.Profile, cancellationToken).ConfigureAwait(false);
        if (!AccountMatchesExpected)
        {
            if (!CanArmDonationProcessing) Settings.Armed = false;
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
            SetDonationAlertsConnection(new DonationConnectionState(false,
                $"DonationAlerts: подключён {Settings.DonationAlertsAccountCode}, ожидался {Settings.ExpectedAccountCode}"));
        }
        SettingsChanged?.Invoke();
    }

    public async Task DisconnectDonationAlertsAsync(bool forgetApplicationSecret, CancellationToken cancellationToken = default)
    {
        if (_donationSession is not null)
        {
            await _donationSession.DisposeAsync().ConfigureAwait(false);
            _donationSession = null;
        }
        Secrets.ClearTokens();
        if (forgetApplicationSecret)
        {
            Secrets.ClientSecret = string.Empty;
            Settings.DonationAlertsClientId = string.Empty;
        }
        Settings.DonationAlertsUserId = null;
        Settings.DonationAlertsAccountCode = string.Empty;
        Settings.DonationAlertsAccountName = string.Empty;
        if (!CanArmDonationProcessing) Settings.Armed = false;
        Settings.InitialDonationSnapshotCompleted = false;
        Settings.LastDonationId = string.Empty;
        Settings.LastDonationCreatedAt = null;
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        SetDonationAlertsConnection(new DonationConnectionState(false, "DonationAlerts не подключён"));
        SettingsChanged?.Invoke();
    }

    public async Task ConnectDonateXAsync(
        string externalToken,
        CancellationToken cancellationToken = default)
    {
        externalToken = DonateXToken.Normalize(externalToken);
        if (externalToken.Length < 12)
            throw new InvalidOperationException("Вставьте внешний токен DonateX из кабинета: Настройки → API.");

        var profile = await _donateXRest.GetProfileAsync(externalToken, cancellationToken).ConfigureAwait(false);
        Secrets.DonateXExternalToken = externalToken;
        Settings.DonateXUserId = profile.Id;
        Settings.DonateXAccountName = profile.Username;
        Settings.DonateXInitialDonationSnapshotCompleted = false;
        Settings.LastDonateXDonationId = string.Empty;
        Settings.LastDonateXDonationCreatedAt = null;
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        await StartDonateXSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke();
    }

    public async Task DisconnectDonateXAsync(CancellationToken cancellationToken = default)
    {
        if (_donateXSession is not null)
        {
            await _donateXSession.DisposeAsync().ConfigureAwait(false);
            _donateXSession = null;
        }
        Secrets.DonateXExternalToken = string.Empty;
        Settings.DonateXUserId = string.Empty;
        Settings.DonateXAccountName = string.Empty;
        Settings.DonateXInitialDonationSnapshotCompleted = false;
        Settings.LastDonateXDonationId = string.Empty;
        Settings.LastDonateXDonationCreatedAt = null;
        if (!CanArmDonationProcessing) Settings.Armed = false;
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        SetDonateXConnection(new DonateXConnectionState(false, "DonateX не подключён"));
        SettingsChanged?.Invoke();
    }

    public async Task SetArmedAsync(bool armed, CancellationToken cancellationToken = default)
    {
        if (armed && !ProcessingIntegrationConfigured)
        {
            throw new InvalidOperationException("Сначала подключите DonationAlerts, DonateX или чат YouTube в настройках.");
        }
        if (armed && !CanArmDonationProcessing)
        {
            throw new InvalidOperationException($"Нельзя включить обработку: подключён аккаунт {Settings.DonationAlertsAccountCode}, ожидался {Settings.ExpectedAccountCode}.");
        }
        Settings.Armed = armed;
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        _dispatcher.GlobalCooldown = TimeSpan.FromSeconds(Math.Clamp(Settings.GlobalCooldownSeconds, 0, 300));
        Settings.AnonymousDonorName = SettingsNormalizer.NormalizeAnonymousDonorName(Settings.AnonymousDonorName);
        _dispatcher.AnonymousDonorName = Settings.AnonymousDonorName;
        Settings.CatalogExclusions = SettingsNormalizer.NormalizeCatalogExclusions(Settings.CatalogExclusions);
        Settings.SafeZone = SafeZoneSettings.Normalize(Settings.SafeZone);
        await SynchronizeSafeZoneAsync(cancellationToken).ConfigureAwait(false);
        await CatalogExclusionConfig.WriteAsync(
            Settings.GamePath, Settings.CatalogExclusions, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        if (_catalogSource is { } source)
        {
            var effective = ApplyCatalog(source, _catalogState.IsLive);
            _catalogState = _catalogState with { Catalog = effective };
            CatalogStateChanged?.Invoke(_catalogState);
        }
        SettingsChanged?.Invoke();
    }

    private async Task SynchronizeSafeZoneAsync(CancellationToken cancellationToken)
    {
        _safeZoneConfigSynchronized = false;
        await SafeZoneConfig.WriteAsync(Settings.GamePath, Settings.SafeZone, cancellationToken).ConfigureAwait(false);
        _safeZoneConfigSynchronized = !string.IsNullOrWhiteSpace(Settings.GamePath) && Directory.Exists(Settings.GamePath);
    }

    public IReadOnlyList<CatalogEntry> HistoryExclusionCandidates(EventHistoryEntry entry)
    {
        var catalog = _catalogSource ?? _catalogState.Catalog;
        return catalog is null ? [] : HistoryCatalogSelection.Resolve(entry, catalog);
    }

    public async Task<int> AddCatalogExclusionsAsync(
        IEnumerable<CatalogEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var existing = Settings.CatalogExclusions
            .Select(entry => (entry.Kind, entry.SectionId))
            .ToHashSet();
        var added = 0;
        foreach (var entry in entries)
        {
            if (!existing.Add((entry.Kind, entry.SectionId))) continue;
            Settings.CatalogExclusions.Add(new CatalogExclusion
            {
                Kind = entry.Kind,
                SectionId = entry.SectionId,
                DisplayName = entry.DisplayName,
                Category = entry.Category,
                AddedAt = DateTimeOffset.UtcNow
            });
            added++;
        }
        if (added > 0) await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    public async Task<DonationProfile> CreateProfileAsync(
        string name,
        bool copyUserPresets = false,
        CancellationToken cancellationToken = default,
        bool copyVotings = false)
    {
        if (!SettingsNormalizer.TryNormalizeProfileName(name, out var normalizedName))
            throw new InvalidOperationException($"Название профиля должно содержать от 1 до {SettingsNormalizer.ProfileNameMaxLength} символов.");
        if (Settings.Profiles.Any(profile =>
                string.Equals(profile.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Профиль с таким названием уже существует.");
        if (copyVotings && !_votingLoaded)
            throw new InvalidOperationException(VotingError.Length > 0 ? VotingError : "Голосования ещё не загружены.");

        SettingsNormalizer.SynchronizeActiveProfile(Settings);
        var previousProfileId = Settings.ActiveProfileId;
        var previousActions = Settings.Actions;
        var previousUserPresets = Settings.UserPresets;
        var previousHotkeys = Settings.ActivePresetHotkeys;
        var profile = new DonationProfile
        {
            Name = normalizedName,
            UserPresets = copyUserPresets
                ? previousUserPresets.Select(CloneUserPresetForProfile).ToList()
                : []
        };
        Settings.Profiles.Add(profile);
        Settings.ActiveProfileId = profile.Id;
        Settings.Actions = profile.Actions;
        Settings.UserPresets = profile.UserPresets;
        Settings.ActivePresetHotkeys = profile.ActivePresetHotkeys;
        try
        {
            if (_votingLoaded)
            {
                if (copyVotings) await Voting.CopyProfileAsync(previousProfileId, profile.Id, cancellationToken).ConfigureAwait(false);
                await Voting.EnterProfileAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            }
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
            return profile;
        }
        catch
        {
            Settings.Profiles.Remove(profile);
            Settings.ActiveProfileId = previousProfileId;
            Settings.Actions = previousActions;
            Settings.UserPresets = previousUserPresets;
            Settings.ActivePresetHotkeys = previousHotkeys;
            if (_votingLoaded)
            {
                await Voting.DeleteProfileAsync(profile.Id, CancellationToken.None).ConfigureAwait(false);
                await Voting.EnterProfileAsync(previousProfileId, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static ActionDefinition CloneUserPresetForProfile(ActionDefinition source)
    {
        var clone = source.Clone();
        clone.Id = ActionDefinition.NewInstanceId();
        clone.IsActive = false;
        clone.Triggers.Clear();
        return clone;
    }

    public async Task SwitchProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var target = Settings.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Выбранный профиль больше не существует.");
        if (string.Equals(Settings.ActiveProfileId, target.Id, StringComparison.Ordinal)) return;
        if (_votingLoaded) await Voting.EnterProfileAsync(target.Id, cancellationToken).ConfigureAwait(false);

        SettingsNormalizer.SynchronizeActiveProfile(Settings);
        var previousProfileId = Settings.ActiveProfileId;
        var previousActions = Settings.Actions;
        var previousUserPresets = Settings.UserPresets;
        var previousHotkeys = Settings.ActivePresetHotkeys;
        Settings.ActiveProfileId = target.Id;
        Settings.Actions = target.Actions;
        Settings.UserPresets = target.UserPresets;
        Settings.ActivePresetHotkeys = target.ActivePresetHotkeys;
        try
        {
            ApplyCurrentProfileCatalogBindings();
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.ActiveProfileId = previousProfileId;
            Settings.Actions = previousActions;
            Settings.UserPresets = previousUserPresets;
            Settings.ActivePresetHotkeys = previousHotkeys;
            ApplyCurrentProfileCatalogBindings();
            if (_votingLoaded) await Voting.EnterProfileAsync(previousProfileId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RenameProfileAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!SettingsNormalizer.TryNormalizeProfileName(name, out var normalizedName))
            throw new InvalidOperationException($"Название профиля должно содержать от 1 до {SettingsNormalizer.ProfileNameMaxLength} символов.");
        var profile = Settings.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Профиль уже удалён.");
        if (Settings.Profiles.Any(candidate =>
                !ReferenceEquals(candidate, profile) &&
                string.Equals(candidate.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Профиль с таким названием уже существует.");

        var previousName = profile.Name;
        profile.Name = normalizedName;
        try { await SaveSettingsAsync(cancellationToken).ConfigureAwait(false); }
        catch { profile.Name = previousName; throw; }
    }

    public async Task DeleteProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (Settings.Profiles.Count <= 1)
            throw new InvalidOperationException("Нельзя удалить единственный профиль.");
        var profile = Settings.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Профиль уже удалён.");

        SettingsNormalizer.SynchronizeActiveProfile(Settings);
        var index = Settings.Profiles.IndexOf(profile);
        var wasActive = string.Equals(profile.Id, Settings.ActiveProfileId, StringComparison.Ordinal);
        Settings.Profiles.RemoveAt(index);
        if (wasActive)
        {
            var replacement = Settings.Profiles[Math.Min(index, Settings.Profiles.Count - 1)];
            Settings.ActiveProfileId = replacement.Id;
            Settings.Actions = replacement.Actions;
            Settings.UserPresets = replacement.UserPresets;
            Settings.ActivePresetHotkeys = replacement.ActivePresetHotkeys;
            ApplyCurrentProfileCatalogBindings();
        }

        try
        {
            if (_votingLoaded) await Voting.EnterProfileAsync(Settings.ActiveProfileId, cancellationToken).ConfigureAwait(false);
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.Profiles.Insert(index, profile);
            if (wasActive)
            {
                Settings.ActiveProfileId = profile.Id;
                Settings.Actions = profile.Actions;
                Settings.UserPresets = profile.UserPresets;
                Settings.ActivePresetHotkeys = profile.ActivePresetHotkeys;
                ApplyCurrentProfileCatalogBindings();
            }
            if (_votingLoaded) await Voting.EnterProfileAsync(Settings.ActiveProfileId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        if (_votingLoaded) await Voting.DeleteProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveActionChangesAsync(
        IEnumerable<ActionDefinition> changedActions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedActions);
        var errors = ValidateActionsForPersistence(changedActions).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveActionParametersAsync(string instanceId, ActionDefinition draft)
    {
        var collection = Settings.Actions.Any(action => action.Id == instanceId)
            ? Settings.Actions
            : Settings.UserPresets;
        var index = collection.FindIndex(action => action.Id == instanceId);
        if (index < 0) throw new InvalidOperationException("Этот вариант уже удалён.");
        var previous = collection[index];
        var replacement = previous.Clone();
        replacement.Parameters = new Dictionary<string, string>(draft.Parameters, StringComparer.OrdinalIgnoreCase);
        replacement.ItemSpawns = draft.ItemSpawns.Select(entry => entry.Clone()).ToList();
        replacement.SpawnGroups = draft.SpawnGroups.Select(entry => entry.Clone()).ToList();
        replacement.CooldownSeconds = draft.CooldownSeconds;
        collection[index] = replacement;
        try { await SaveActionChangesAsync([replacement]); }
        catch { collection[index] = previous; throw; }
    }

    public async Task AddActionInstanceAsync(ActionDefinition draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var preset = _presets.FirstOrDefault(item =>
            string.Equals(item.HandlerId, draft.HandlerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Выбранный шаблон больше не существует. Обновите список шаблонов.");

        var instance = preset.Clone();
        instance.CooldownSeconds = draft.CooldownSeconds;
        instance.Parameters = new Dictionary<string, string>(draft.Parameters, StringComparer.OrdinalIgnoreCase);
        instance.ItemSpawns = draft.ItemSpawns.Select(entry => entry.Clone()).ToList();
        instance.SpawnGroups = draft.SpawnGroups.Select(entry => entry.Clone()).ToList();
        instance.Triggers = draft.Triggers.Select(trigger => trigger.Clone()).ToList();
        if (draft.HasCustomDisplayName &&
            Validation.TryNormalizeUserPresetName(draft.DisplayName, out var customName))
        {
            instance.DisplayName = customName;
            instance.HasCustomDisplayName = true;
        }
        instance = instance.CloneAsNewInstance();
        var errors = instance.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        Settings.Actions.Add(instance);
        try
        {
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.Actions.RemoveAll(item => string.Equals(item.Id, instance.Id, StringComparison.Ordinal));
            throw;
        }
    }

    public async Task SaveAsUserPresetAsync(
        ActionDefinition draft,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!Validation.TryNormalizeUserPresetName(displayName, out var normalizedName))
            throw new InvalidOperationException($"Название должно содержать от 1 до {Validation.UserPresetNameMaxLength} символов.");
        var template = _presets.FirstOrDefault(item =>
            string.Equals(item.HandlerId, draft.HandlerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Исходный встроенный шаблон больше не существует.");
        var userPreset = template.Clone();
        userPreset.Id = ActionDefinition.NewInstanceId();
        userPreset.IsActive = false;
        userPreset.DisplayName = normalizedName;
        userPreset.HasCustomDisplayName = true;
        userPreset.CooldownSeconds = draft.CooldownSeconds;
        userPreset.Parameters = new Dictionary<string, string>(draft.Parameters, StringComparer.OrdinalIgnoreCase);
        userPreset.ItemSpawns = draft.ItemSpawns.Select(entry => entry.Clone()).ToList();
        userPreset.SpawnGroups = draft.SpawnGroups.Select(entry => entry.Clone()).ToList();
        // A reusable preset deliberately starts without a donation price.
        userPreset.Triggers.Clear();
        var errors = ValidateActionsForPersistence([userPreset]);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        Settings.UserPresets.Add(userPreset);
        try { await SaveSettingsAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            Settings.UserPresets.Remove(userPreset);
            throw;
        }
    }

    public async Task RenameUserPresetAsync(
        string instanceId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (!Validation.TryNormalizeUserPresetName(displayName, out var normalizedName))
            throw new InvalidOperationException($"Название должно содержать от 1 до {Validation.UserPresetNameMaxLength} символов.");
        var preset = Settings.UserPresets.FirstOrDefault(item => item.Id == instanceId)
            ?? throw new InvalidOperationException("Пользовательский шаблон уже удалён.");
        var previousName = preset.DisplayName;
        preset.DisplayName = normalizedName;
        preset.HasCustomDisplayName = true;
        try { await SaveSettingsAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            preset.DisplayName = previousName;
            throw;
        }
    }

    public async Task DeleteUserPresetAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var preset = Settings.UserPresets.FirstOrDefault(item => item.Id == instanceId)
            ?? throw new InvalidOperationException("Пользовательский шаблон уже удалён.");
        var index = Settings.UserPresets.IndexOf(preset);
        Settings.UserPresets.RemoveAt(index);
        try { await SaveSettingsAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            Settings.UserPresets.Insert(index, preset);
            throw;
        }
    }

    public async Task DeleteActionInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var instance = Settings.Actions.FirstOrDefault(item =>
            string.Equals(item.Id, instanceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Активный вариант уже удалён.");
        Settings.Actions.Remove(instance);
        try
        {
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.Actions.Add(instance);
            throw;
        }
    }

    public string ValidateGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Выберите папку игры.";
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return exception.Message; }
        if (!File.Exists(Path.Combine(fullPath, "fsgame.ltx"))) return "Не найден fsgame.ltx.";
        if (!Directory.Exists(Path.Combine(fullPath, "bin"))) return "Не найдена папка bin.";
        var binPath = Path.Combine(fullPath, "bin");
        var hasEngine = File.Exists(Path.Combine(binPath, "xrEngine.exe")) ||
            Directory.EnumerateFiles(binPath, "AnomalyDX*.exe", SearchOption.TopDirectoryOnly).Any();
        if (!hasEngine) return "Не найден исполняемый файл Anomaly или xrEngine.exe.";
        return string.Empty;
    }

    public async Task SelectGamePathAsync(string path, CancellationToken cancellationToken = default)
    {
        var validation = ValidateGamePath(path);
        if (validation.Length > 0) throw new InvalidOperationException(validation);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(Settings.GamePath ?? string.Empty),
                normalized,
                StringComparison.OrdinalIgnoreCase))
        {
            Settings.GamePath = normalized;
            return;
        }

        Settings.GamePath = normalized;
        var gameConnected = _gameServer.IsConnected;
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearCatalogBindings();
            _catalogSource = null;
            var cached = await _catalogCacheStore.LoadAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (cached is null)
            {
                _catalogState = new CatalogRuntimeState(
                    gameConnected ? _gameServer.Capabilities : CapabilitySet.Empty,
                    null,
                    false,
                    gameConnected
                        ? $"{EngineProfileLabel(_gameServer.Capabilities.Profile)} · нажмите ↻, чтобы загрузить каталог"
                        : "Игра отключена; для выбранной установки каталога нет");
            }
            else
            {
                var snapshot = cached.ToSnapshot();
                _catalogSource = snapshot;
                var effectiveCatalog = ApplyCatalog(snapshot, isLive: false);
                _catalogState = new CatalogRuntimeState(
                    gameConnected ? _gameServer.Capabilities : cached.ToCapabilities(),
                    effectiveCatalog,
                    false,
                    gameConnected
                        ? $"{EngineProfileLabel(_gameServer.Capabilities.Profile)} · показан кэш; проверка ревизии…"
                        : $"Офлайн-кэш · {snapshot.Entries.Count} секций · {cached.CachedAt:dd.MM.yyyy HH:mm}");
            }
            CatalogStateChanged?.Invoke(_catalogState);
            SettingsChanged?.Invoke();
        }
        finally
        {
            _catalogGate.Release();
        }

        if (gameConnected && _gameServer.Capabilities.SupportsCapability("catalog_identity"))
        {
            try
            {
                await _gameServer.RequestCatalogInfoAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A disconnect raced the path change. The cached catalog remains
                // view-only and commands stay blocked until the next handshake.
            }
        }
    }

    public async Task RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefreshCatalog)
        {
            throw new InvalidOperationException(
                "Игра не подключена или установленный аддон не поддерживает динамический каталог.");
        }

        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InvalidateCatalogRevisions();
            _catalogState = _catalogState with
            {
                IsLive = false,
                Capabilities = _gameServer.Capabilities,
                Status = $"{EngineProfileLabel(_gameServer.Capabilities.Profile)} · загрузка каталога по запросу…"
            };
            CatalogStateChanged?.Invoke(_catalogState);
            SettingsChanged?.Invoke();
        }
        finally
        {
            _catalogGate.Release();
        }

        await _gameServer.RequestCatalogAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<AddonInstallationStatus> InspectAddonAsync(
        string gamePath,
        CancellationToken cancellationToken = default) =>
        _addonPackageManager.InspectAsync(gamePath, cancellationToken, VariantCatalog(gamePath));

    public async Task<BridgeInstallationStatus> InspectBridgeAsync(string gamePath, string mo2ModPath)
    {
        var gameError = ValidateGamePath(gamePath);
        if (gameError.Length > 0) throw new InvalidOperationException(gameError);
        var addon = InspectAddonAsync(gamePath);
        var effects = InspectEffectsAsync(gamePath, mo2ModPath);
        await Task.WhenAll(addon, effects);
        return new BridgeInstallationStatus(await addon, await effects);
    }

    // Shared by Settings and the header shortcut. Never saves an old settings
    // snapshot, arms donations, or sends effects to the running game.
    public async Task<BridgeInstallationStatus> InstallBridgeAsync(
        string gamePath, string mo2ModPath, CancellationToken cancellationToken = default)
    {
        await _bridgeInstallGate.WaitAsync(cancellationToken);
        try
        {
            var status = await InspectBridgeAsync(gamePath, mo2ModPath);
            if (status.Addon.State is not (AddonInstallState.Current or AddonInstallState.NotInstalled or AddonInstallState.UpdateAvailable))
                throw new InvalidOperationException(status.Addon.Message);
            await PreflightEffectsAsync(gamePath, mo2ModPath, cancellationToken);
            var addon = await InstallOrUpdateAddonAsync(gamePath, cancellationToken);
            var effects = await InstallEffectsAsync(gamePath, mo2ModPath, cancellationToken);
            return new BridgeInstallationStatus(addon, effects);
        }
        finally { _bridgeInstallGate.Release(); }
    }

    public Task<AddonInstallationStatus> InstallOrUpdateAddonAsync(
        string gamePath,
        CancellationToken cancellationToken = default) =>
        _addonPackageManager.InstallOrUpdateAsync(gamePath, cancellationToken, VariantCatalog(gamePath));

    private CatalogSnapshot? VariantCatalog(string gamePath) =>
        !string.IsNullOrWhiteSpace(gamePath) && !string.IsNullOrWhiteSpace(Settings.GamePath) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Settings.GamePath)), StringComparison.OrdinalIgnoreCase)
        && _catalogSource?.Entries.Any(e => e.Kind == CatalogEntryKind.Mutant && e.NativeClass.Length > 0) == true
            ? _catalogSource : null;

    public Task<EffectScriptInstallationStatus> InspectEffectsAsync(string gamePath, string mo2ModPath) =>
        _effectScriptManager.InspectAsync(string.IsNullOrWhiteSpace(mo2ModPath) ? gamePath : mo2ModPath);

    public Task PreflightEffectsAsync(
        string gamePath, string mo2ModPath, CancellationToken cancellationToken = default) =>
        _effectScriptManager.PreflightAsync(
            string.IsNullOrWhiteSpace(mo2ModPath) ? gamePath : mo2ModPath,
            cancellationToken);

    public async Task<EffectScriptInstallationStatus> InstallEffectsAsync(
        string gamePath, string mo2ModPath, CancellationToken cancellationToken = default)
    {
        var result = await _effectScriptManager.InstallAllAsync(
            string.IsNullOrWhiteSpace(mo2ModPath) ? gamePath : mo2ModPath,
            cancellationToken);
        RefreshActionManifest();
        return result;
    }

    public IReadOnlyList<string> RefreshActionManifest()
    {
        var warnings = RefreshFilePresets().ToList();
        warnings.AddRange(_manifestLoader.MergeFromGame(Settings.GamePath, _presets));
        warnings.AddRange(_manifestLoader.ApplyPresetsToInstances(Settings.Actions, _presets));
        warnings.AddRange(_manifestLoader.ApplyPresetsToInstances(Settings.UserPresets, _presets));
        if (_catalogSource is { } catalog)
        {
            var effective = ApplyCatalog(catalog, _catalogState.IsLive);
            _catalogState = _catalogState with { Catalog = effective };
        }
        SettingsChanged?.Invoke();
        return warnings;
    }

    public void RefreshActionManifestIfFilesChanged()
    {
        var current = _filePresetLoader.Fingerprint(Settings.GamePath, Settings.EffectMo2ModPath);
        if (!string.Equals(current, _filePresetFingerprint, StringComparison.Ordinal))
            RefreshActionManifest();
    }

    public string GetActionOriginLabel(string handlerId) =>
        IsHandlerAvailable(handlerId) ? string.Empty : $"Скрипт не установлен: {handlerId}";

    public bool IsHandlerAvailable(string handlerId) =>
        _presets.Any(preset => string.Equals(preset.HandlerId, handlerId, StringComparison.Ordinal));

    public void OpenActionCode(ActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_filePresetSources.TryGetValue(action.HandlerId, out var scriptPath) || !File.Exists(scriptPath))
            throw new FileNotFoundException("Исходный файл шаблона отсутствует в папке effects.", action.ScriptRelativePath);

        OpenCodeFile(scriptPath);
    }

    public void OpenTemplateSource(string handlerId, TemplateSourceLink link)
    {
        if (!TemplateSourceLinks.For(handlerId).Contains(link)) throw new InvalidOperationException("Этот файл не относится к шаблону.");
        OpenCodeFile(AddonPackageManager.ResolveTemplateSource(link.RelativePath));
    }

    private void OpenCodeFile(string scriptPath)
    {
        var editor = string.IsNullOrWhiteSpace(Settings.EditorPath) ? "notepad.exe" : Settings.EditorPath;
        var start = new ProcessStartInfo { FileName = editor, UseShellExecute = true };
        start.ArgumentList.Add(scriptPath);
        Process.Start(start);
    }

    public Task TestActionAsync(ActionDefinition action, CancellationToken cancellationToken = default) =>
        EnqueueManualActionAsync(action, "test:" + Guid.NewGuid().ToString("N"), "Локальный тест", string.Empty, cancellationToken);

    public Task RunAsDonorAsync(ActionDefinition action, string donorName, string? message, CancellationToken cancellationToken = default)
    {
        var input = DonorLaunchInput.Create(donorName, message);
        return EnqueueManualActionAsync(action, "manual:donor:" + Guid.NewGuid().ToString("N"), input.DonorName, input.Message, cancellationToken);
    }

    private async Task EnqueueManualActionAsync(ActionDefinition action, string donationId, string donorName, string message, CancellationToken cancellationToken)
    {
        EnsureActionCanExecute(action);
        var parameterErrors = action.Validate();
        if (parameterErrors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, parameterErrors));
        var trigger = action.Triggers.FirstOrDefault(rule => rule.Enabled);
        var donation = new DonationEvent
        {
            Id = donationId,
            Username = donorName,
            Message = message,
            Amount = trigger?.Amount ?? 0m,
            Currency = trigger?.Currency ?? "RUB",
            CreatedAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        await _dispatcher.EnqueueActionAsync(
            donation,
            action,
            Settings.QueuePolicy,
            cancellationToken,
            Settings.Actions,
            _presets,
            CurrentEffectExecutionOptions()).ConfigureAwait(false);
        await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, cancellationToken).ConfigureAwait(false);
    }

    internal async Task InitializeVotingModuleAsync(CancellationToken token = default)
    {
        if (Settings.ActiveProfileId.Length == 0) Settings = SettingsNormalizer.Normalize(Settings);
        await Voting.InitializeAsync(token).ConfigureAwait(false);
        await Voting.EnterProfileAsync(Settings.ActiveProfileId, token).ConfigureAwait(false);
        _votingLoaded = true;
    }

    public async Task<VotingRound> BeginVotingDrawAsync(CancellationToken token = default, string? collectionId = null)
    {
        if (!_votingLoaded) throw new InvalidOperationException(VotingError.Length > 0 ? VotingError : "Голосование ещё не загружено.");
        await _votingDrawGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_votingDrawingRoundId is not null) throw new InvalidOperationException("Рулетка уже вращается.");
            var selected = collectionId is null ? Voting.Snapshot : Voting.Collections.FirstOrDefault(round => round.CollectionId == collectionId)
                ?? throw new InvalidOperationException("Это голосование уже удалено.");
            _votingDrawingRoundId = selected.Id; _votingDrawingCollectionId = selected.CollectionId;
            VotingRound round;
            try { round = await Voting.ChooseWinnerAsync(token, selected.CollectionId, requireGamePresentation: true,
                automaticEffect: Voting.Automation.AutomaticWinnerEffect).ConfigureAwait(false); }
            catch { _votingDrawingRoundId = _votingDrawingCollectionId = null; throw; }
            return round;
        }
        finally { _votingDrawGate.Release(); }
    }

    public async Task FinishVotingDrawAsync(CancellationToken token = default)
    {
        // Completion is acknowledged by the game AFTER closing its modal,
        // never inferred from an application-side animation timer.
        await AdvanceVotingPresentationAsync(token).ConfigureAwait(false);
    }

    internal async Task AdvanceVotingAsync(CancellationToken token = default)
    {
        if (!_votingLoaded) return;
        await Voting.TickAsync(token).ConfigureAwait(false);
        await AdvanceVotingHudAsync(token).ConfigureAwait(false);
        var ready = Voting.Collections.FirstOrDefault(round => round.IsActive && round.Phase == VotingPhase.Ready && round.ProfileId == Settings.ActiveProfileId);
        if (Voting.Automation is { Enabled: true, AutomaticDraw: true } && ready is not null && !VotingIsSpinning)
        {
            try
            {
                await BeginVotingDrawAsync(token, ready.CollectionId).ConfigureAwait(false);
            }
            catch (InvalidOperationException error)
            {
                await Voting.SetNoticeAsync("Автозапуск ожидает: " + error.Message, token).ConfigureAwait(false);
            }
        }
        await AdvanceVotingPresentationAsync(token).ConfigureAwait(false);
        // Resume the same persisted outbox ID after a crash, never redraw.
        foreach (var pending in Voting.PendingWinners.Where(pending => pending.ProfileId == Settings.ActiveProfileId && (
            pending.GamePresentationRequired ? pending.GamePresentationCompleted :
            Voting.Automation is { Enabled: true, AutomaticWinnerEffect: true } &&
                Voting.Collections.Any(round => round.IsActive && round.CollectionId == pending.CollectionId))))
                await SubmitVotingWinnerAsync(token, pending.Id).ConfigureAwait(false);
        if (!VotingIsSpinning)
        {
            try { await Voting.StartNextCollectionAsync(Settings.ActiveProfileId, token).ConfigureAwait(false); }
            catch (InvalidOperationException error) { await Voting.SetNoticeAsync("Следующее голосование ожидает: " + error.Message, token).ConfigureAwait(false); }
        }
    }

    // Serialize destructive voting actions with winner enqueueing. A reset can
    // cancel an unsent winner, but cannot undo an effect already in the queue.
    public async Task ResetVotingAsync(string collectionId, CancellationToken token = default)
    {
        await _donationGate.WaitAsync(token).ConfigureAwait(false);
        try { await Voting.ResetAmountsAsync(collectionId, token).ConfigureAwait(false); }
        finally { _donationGate.Release(); }
    }

    public async Task DeleteVotingAsync(string collectionId, CancellationToken token = default)
    {
        await _donationGate.WaitAsync(token).ConfigureAwait(false);
        try { await Voting.DeleteCollectionAsync(collectionId, token).ConfigureAwait(false); }
        finally { _donationGate.Release(); }
    }

    public async Task SubmitVotingWinnerAsync(CancellationToken token = default, string? roundId = null)
    {
        if (!_votingLoaded) return;
        await _donationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var round = roundId is null ? Voting.PendingWinners.FirstOrDefault() : Voting.PendingWinners.FirstOrDefault(item => item.Id == roundId);
            if (round is null ||
                round.Id == _votingDrawingRoundId || round.Winner?.Preset is not { } preset) return;
            if (round.GamePresentationRequired && !round.GamePresentationCompleted)
            {
                await Voting.SetDeliveryNoteAsync("Победитель сохранён; ожидает завершения игровой рулетки.", token, round.Id).ConfigureAwait(false);
                return;
            }
            if (round.ProfileId != Settings.ActiveProfileId)
            {
                await Voting.SetDeliveryNoteAsync("Ожидает профиль, в котором было начато голосование.", token, round.Id).ConfigureAwait(false);
                return;
            }
            var action = preset.Clone();
            if (CatalogRevisionEnforcement.RequiresCatalog(action))
            {
                if (!_catalogState.IsLive || _catalogState.Catalog is not { } catalog)
                {
                    await Voting.SetDeliveryNoteAsync("Победитель сохранён; ожидает свежий каталог игры.", token, round.Id).ConfigureAwait(false);
                    return;
                }
                DynamicCatalogBinding.Apply(catalog, new[] { action });
            }
            if (!IsHandlerAvailable(action.HandlerId) || action.Validate().Count > 0)
            {
                await Voting.SetDeliveryNoteAsync("Победитель сохранён; его шаблон сейчас недоступен или не прошёл проверку.", token, round.Id).ConfigureAwait(false);
                return;
            }
            var now = DateTimeOffset.UtcNow;
            var resultSnapshot = round.ResultSnapshot();
            var donation = new DonationEvent { Id = round.DeliveryId, Username = resultSnapshot.Title,
                Message = action.HandlerId == DonationDispatcher.PdaMessageHandlerId
                    ? action.DisplayName : round.Winner.Command,
                Amount = resultSnapshot.Total, Currency = resultSnapshot.Currency,
                CreatedAt = round.DrawnAt ?? now, ReceivedAt = now };
            var result = await _dispatcher.EnqueueActionAsync(donation, action, Settings.QueuePolicy,
                token, Settings.Actions, _presets, CurrentEffectExecutionOptions()).ConfigureAwait(false);
            var note = result == DispatchState.Duplicate
                ? "Шаблон уже зарегистрирован в очереди/истории. Автоматического повтора не будет."
                : result == DispatchState.Rejected ? "Шаблон отклонён; подробности в истории программы."
                : "Победивший шаблон передан в общую очередь эффектов.";
            await Voting.MarkDeliveryAsync(round.Id, note, token).ConfigureAwait(false);
            await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, token).ConfigureAwait(false);
        }
        finally { _donationGate.Release(); }
    }

    public async Task TestPriceGroupAsync(
        IEnumerable<ActionDefinition> actions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var materialized = actions.ToList();
        if (materialized.Count < 2)
            throw new InvalidOperationException("Групповой тест требует минимум два активных шаблона.");
        foreach (var action in materialized)
        {
            EnsureActionCanExecute(action);
            var errors = action.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException($"«{action.DisplayName}»: " +
                    string.Join(Environment.NewLine, errors));
        }
        var trigger = materialized[0].Triggers.FirstOrDefault(rule => rule.Enabled)
            ?? throw new InvalidOperationException("У группы не назначена включённая сумма.");
        var donation = new DonationEvent
        {
            Id = "test-group:" + Guid.NewGuid().ToString("N"),
            Username = "Локальный тест",
            Amount = trigger.Amount,
            Currency = trigger.Currency,
            CreatedAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        await _dispatcher.EnqueueAsync(
            donation,
            materialized,
            Settings.QueuePolicy,
            cancellationToken,
            _presets,
            CurrentEffectExecutionOptions()).ConfigureAwait(false);
        await _dispatcher.DrainAsync(
            _gameServer,
            Settings.QueuePolicy,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ActivateActivePresetAsync(
        int presetNumber,
        CancellationToken cancellationToken = default)
    {
        if (presetNumber < 1 || presetNumber > Settings.Actions.Count)
            throw new InvalidOperationException($"Активный шаблон №{presetNumber} не существует.");
        return await ExecuteActivePresetAsync(
            Settings.Actions[presetNumber - 1].Clone(),
            "Горячая клавиша",
            "preset-hotkey",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> RunFortuneWheelIfDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        UpdateFortuneWheelConfiguration(now);
        if (_fortuneWheelConfiguredMinutes <= 0 || now < _fortuneWheelDeadline ||
            !_gameServer.IsConnected || !_gameServer.IsGameReady)
            return null;
        if (!await _fortuneWheelGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            now = DateTimeOffset.UtcNow;
            UpdateFortuneWheelConfiguration(now);
            if (_fortuneWheelConfiguredMinutes <= 0 || now < _fortuneWheelDeadline ||
                !_gameServer.IsConnected || !_gameServer.IsGameReady)
                return null;

            var candidates = Settings.Actions
                .Where(action => action.Triggers.Any(trigger => trigger.Enabled))
                .Where(action => action.Validate().Count == 0)
                .Where(IsActionReadyForDirectActivation)
                .ToList();
            if (candidates.Count == 0) return null;

            // Advance the deadline before dispatch so a rejected game command cannot
            // cause the same automatic event to be submitted once per UI timer tick.
            ResetFortuneWheelDeadline(now);
            var action = candidates[Random.Shared.Next(candidates.Count)].Clone();
            return await ExecuteActivePresetAsync(
                action,
                "Колесо фортуны",
                "fortune-wheel",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fortuneWheelGate.Release();
        }
    }

    private async Task<string> ExecuteActivePresetAsync(
        ActionDefinition action,
        string donor,
        string eventPrefix,
        CancellationToken cancellationToken)
    {
        EnsureActionCanExecute(action);
        var errors = action.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        var trigger = action.Triggers.FirstOrDefault(rule => rule.Enabled)
            ?? throw new InvalidOperationException("У активного шаблона не назначена включённая сумма.");
        var now = DateTimeOffset.UtcNow;
        var donation = new DonationEvent
        {
            Id = $"{eventPrefix}:{Guid.NewGuid():N}",
            Username = donor,
            Amount = trigger.Amount,
            Currency = trigger.Currency,
            CreatedAt = now,
            ReceivedAt = now
        };
        await _donationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _dispatcher.EnqueueActionAsync(
                donation,
                action,
                Settings.QueuePolicy,
                cancellationToken,
                Settings.Actions,
                _presets,
                CurrentEffectExecutionOptions()).ConfigureAwait(false);
            await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _donationGate.Release();
        }
        return action.DisplayName;
    }

    private bool IsActionReadyForDirectActivation(ActionDefinition action)
    {
        try
        {
            EnsureActionCanExecute(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void UpdateFortuneWheelConfiguration(DateTimeOffset now)
    {
        var configured = Math.Clamp(Settings.FortuneWheelIdleMinutes, 0, 10_080);
        if (configured == _fortuneWheelConfiguredMinutes) return;
        _fortuneWheelConfiguredMinutes = configured;
        ResetFortuneWheelDeadline(now);
    }

    private void ResetFortuneWheelDeadline(DateTimeOffset now)
    {
        _fortuneWheelDeadline = _fortuneWheelConfiguredMinutes <= 0
            ? DateTimeOffset.MaxValue
            : now.AddMinutes(_fortuneWheelConfiguredMinutes);
    }

    public async Task RetryManuallyAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!CanRetryHistoryEntry(entry))
            throw new InvalidOperationException("В записи нет действия или исходного доната для повтора.");
        await _donationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Reception-disabled records intentionally have no chosen action.
            // Replay the donation using ALL current matching rules, just like
            // the hotkey, without enabling automatic donation reception.
            if (entry.ActionSnapshot is null && string.IsNullOrWhiteSpace(entry.ActionId) &&
                string.IsNullOrWhiteSpace(entry.HandlerId))
            {
                await RepeatDonationUnderGateAsync(entry, "manual", cancellationToken).ConfigureAwait(false);
                return;
            }
            await RetryActionUnderGateAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally { _donationGate.Release(); }
    }

    internal static bool CanRetryHistoryEntry(EventHistoryEntry? entry) => entry is not null &&
        (entry.ActionSnapshot is not null || !string.IsNullOrWhiteSpace(entry.ActionId) ||
         !string.IsNullOrWhiteSpace(entry.HandlerId) ||
         !string.IsNullOrWhiteSpace(entry.DonationId) && entry.State == DispatchState.Skipped && entry.CanRetryManually);

    private async Task RetryActionUnderGateAsync(EventHistoryEntry entry, CancellationToken cancellationToken)
    {
        var action = entry.ActionSnapshot?.Clone()
            ?? Settings.Actions.FirstOrDefault(item => item.Id == entry.ActionId)?.Clone()
            ?? Settings.Actions.FirstOrDefault(item =>
                string.Equals(item.HandlerId, entry.HandlerId, StringComparison.Ordinal))?.Clone()
            ?? _presets.FirstOrDefault(item =>
                string.Equals(item.HandlerId, entry.HandlerId, StringComparison.Ordinal))?.Clone()
            ?? throw new InvalidOperationException("Скрипт этого действия больше не установлен.");
        if (_catalogState.Catalog is { } catalog && CatalogRevisionEnforcement.RequiresCatalog(action))
        {
            DynamicCatalogBinding.Apply(catalog, [action]);
        }
        EnsureActionCanExecute(action);
        var donation = new DonationEvent
        {
            Id = $"manual:{entry.DonationId}:{Guid.NewGuid():N}",
            Username = ManualRetryDonor(entry, action.HandlerId, Settings.AnonymousDonorName),
            Message = ManualRetryMessage(entry, action.HandlerId),
            Amount = entry.Amount,
            Currency = string.IsNullOrWhiteSpace(entry.Currency) ? "RUB" : entry.Currency,
            CreatedAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        await _dispatcher.EnqueueActionAsync(
            donation,
            action,
            Settings.QueuePolicy,
            cancellationToken,
            Settings.Actions,
            _presets,
            CurrentEffectExecutionOptions()).ConfigureAwait(false);
        await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
    }

    internal static string ManualRetryDonor(
        EventHistoryEntry entry,
        string handlerId,
        string anonymousDonorName = "Ручной повтор")
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!DonationDispatcher.UsesDonorName(handlerId))
            return "Ручной повтор";
        return string.IsNullOrWhiteSpace(entry.Donor)
            ? SettingsNormalizer.NormalizeAnonymousDonorName(anonymousDonorName)
            : entry.Donor.Trim();
    }

    internal static string ManualRetryMessage(EventHistoryEntry entry, string handlerId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DonationDispatcher.UsesDonationMessage(handlerId)
            ? entry.DonationMessage ?? string.Empty
            : string.Empty;
    }

    public async Task<DispatchState> RepeatLastDonationAsync(CancellationToken cancellationToken = default)
    {
        await _donationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var history = await _historyStore.LoadRecentAsync(500, cancellationToken).ConfigureAwait(false);
            var source = SelectLastDonationForRepeat(history)
                ?? throw new InvalidOperationException("В истории ещё нет доната, который можно повторить.");
            return await RepeatDonationUnderGateAsync(source, "hotkey", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _donationGate.Release();
        }
    }

    private async Task<DispatchState> RepeatDonationUnderGateAsync(
        EventHistoryEntry source, string prefix, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var donation = new DonationEvent
        {
            Id = $"{prefix}:{source.DonationId}:{Guid.NewGuid():N}",
            Username = source.Donor ?? string.Empty,
            Message = source.DonationMessage ?? string.Empty,
            Amount = source.Amount,
            Currency = string.IsNullOrWhiteSpace(source.Currency) ? "RUB" : source.Currency,
            CreatedAt = now,
            ReceivedAt = now
        };
        var result = await _dispatcher.EnqueueAsync(
            donation,
            Settings.Actions.Where(action => IsHandlerAvailable(action.HandlerId)).ToList(),
            Settings.QueuePolicy,
            cancellationToken,
            _presets,
            CurrentEffectExecutionOptions()).ConfigureAwait(false);
        await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static EventHistoryEntry? SelectLastDonationForRepeat(
        IEnumerable<EventHistoryEntry> history) => history
        .Where(entry => !string.IsNullOrWhiteSpace(entry.DonationId) &&
            !entry.DonationId.StartsWith("test:", StringComparison.OrdinalIgnoreCase) &&
            !entry.DonationId.StartsWith("voting:", StringComparison.OrdinalIgnoreCase) &&
            !entry.DonationId.StartsWith("manual:", StringComparison.OrdinalIgnoreCase) &&
            !entry.DonationId.StartsWith("hotkey:", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(EventHistoryProjection.SortTime)
        .ThenByDescending(entry => entry.Timestamp)
        .FirstOrDefault();

    public async Task<EventRefreshResult> RefreshEventsAsync(
        CancellationToken cancellationToken = default)
    {
        var recovery = await _gameServer
            .RecoverPendingResultsAsync(cancellationToken)
            .ConfigureAwait(false);

        // ResultReceived starts its handler synchronously. Once the server has
        // observed a RESULT, waiting for this gate guarantees that the matching
        // terminal history row has been persisted before the table is reloaded.
        await _resultHandlingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _resultHandlingGate.Release();

        var history = await _historyStore
            .LoadRecentAsync(500, cancellationToken)
            .ConfigureAwait(false);
        return new EventRefreshResult(history, recovery);
    }

    public bool CanRemoveFromQueue(EventHistoryEntry? entry)
    {
        if (entry is not { State: DispatchState.Queued, DonationId.Length: > 0 })
        {
            return false;
        }

        if (_dispatcher.IsQueued(entry.DonationId, entry.CommandId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(entry.CommandId) &&
            _persistedQueuedCommands.TryGetValue(entry.DonationId, out var commandId) &&
            string.Equals(commandId, entry.CommandId, StringComparison.Ordinal);
    }

    public async Task<QueueCancellationSummary> ClearQueueAsync(CancellationToken cancellationToken = default)
    {
        var history = await _historyStore.LoadRecentAsync(500, cancellationToken).ConfigureAwait(false);
        var cancelled = await _dispatcher.CancelAllQueuedAsync(history, cancellationToken).ConfigureAwait(false);
        _persistedQueuedCommands.Clear();
        if (cancelled.AffectedCount > 0) await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    public Task<QueueTimingSnapshot> GetQueueTimingAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.GetTimingAsync(_gameServer.IsConnected, _gameServer.IsGameReady, cancellationToken);

    private EffectExecutionOptions CurrentEffectExecutionOptions() => new()
    {
        SaveBeforeBatch = Settings.SaveBeforeDonationEffects,
        SaveAfterBatch = Settings.SaveAfterDonationEffects,
        EffectSaveRetentionCount = Settings.EffectSaveRetentionCount,
        ShowGameEffectLog = Settings.ShowGameEffectLog,
        ShowPdaEffectMessage = Settings.ShowPdaEffectMessage
    };

    public async Task RemoveFromQueueAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != DispatchState.Queued || string.IsNullOrWhiteSpace(entry.DonationId))
        {
            throw new InvalidOperationException("Удалить можно только событие, которое ещё находится в очереди.");
        }

        var cancelled = await _dispatcher.CancelQueuedAsync(entry, cancellationToken).ConfigureAwait(false);
        if (cancelled is null)
        {
            _persistedQueuedCommands.TryRemove(entry.DonationId, out _);
            throw new InvalidOperationException("Событие уже вышло из очереди и не может быть отменено.");
        }

        // A real donation becomes terminal here, so persist the checkpoint that
        // the notifying history sink advanced together with the cancellation.
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportRulesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var export = new RuleExport
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = SettingsNormalizer.CreatePortableActions(Settings.Actions),
            UserPresets = SettingsNormalizer.CreatePortableActions(Settings.UserPresets),
            QueuePolicy = Settings.QueuePolicy,
            GlobalCooldownSeconds = Settings.GlobalCooldownSeconds,
            SaveBeforeDonationEffects = Settings.SaveBeforeDonationEffects,
            SaveAfterDonationEffects = Settings.SaveAfterDonationEffects,
            EffectSaveRetentionCount = Settings.EffectSaveRetentionCount,
            ShowGameEffectLog = Settings.ShowGameEffectLog,
            ShowPdaEffectMessage = Settings.ShowPdaEffectMessage
        };
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, export, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportRulesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var import = await JsonSerializer.DeserializeAsync<RuleExport>(stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Пустой файл правил.");
        if (import.SchemaVersion is < 1 or > SettingsNormalizer.MaximumReadableSchemaVersion)
        {
            throw new InvalidDataException("Неподдерживаемая версия файла правил.");
        }
        var normalizedImport = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = import.SchemaVersion,
            Actions = import.Actions ?? [],
            UserPresets = import.UserPresets ?? [],
            QueuePolicy = import.QueuePolicy ?? new QueuePolicy(),
            GlobalCooldownSeconds = import.GlobalCooldownSeconds,
            SaveBeforeDonationEffects = import.SaveBeforeDonationEffects,
            SaveAfterDonationEffects = import.SaveAfterDonationEffects,
            EffectSaveRetentionCount = import.EffectSaveRetentionCount,
            ShowGameEffectLog = import.ShowGameEffectLog,
            ShowPdaEffectMessage = import.ShowPdaEffectMessage
        });
        var manifestWarnings = _manifestLoader.ApplyPresetsToInstances(normalizedImport.Actions, _presets).ToList();
        manifestWarnings.AddRange(_manifestLoader.ApplyPresetsToInstances(normalizedImport.UserPresets, _presets));
        if (manifestWarnings.Count > 0)
        {
            throw new InvalidDataException(
                "Файл правил содержит действия, которых нет в установленном аддоне:" +
                Environment.NewLine + string.Join(Environment.NewLine, manifestWarnings));
        }
        if (_catalogState.Catalog is { } importedCatalog)
        {
            DynamicCatalogBinding.Apply(importedCatalog, normalizedImport.Actions);
            DynamicCatalogBinding.Apply(importedCatalog, normalizedImport.UserPresets);
            if (!_catalogState.IsLive)
            {
                InvalidateCatalogRevisions(normalizedImport.Actions);
                InvalidateCatalogRevisions(normalizedImport.UserPresets);
            }
        }
        var errors = ValidateActionsForPersistence(normalizedImport.Actions)
            .Concat(ValidateActionsForPersistence(normalizedImport.UserPresets))
            .ToList();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var previousActions = Settings.Actions.Select(action => action.Clone()).ToList();
        var previousUserPresets = Settings.UserPresets.Select(action => action.Clone()).ToList();
        var previousQueuePolicy = Settings.QueuePolicy.Clone();
        var previousGlobalCooldown = Settings.GlobalCooldownSeconds;
        var previousSaveBefore = Settings.SaveBeforeDonationEffects;
        var previousSaveAfter = Settings.SaveAfterDonationEffects;
        var previousSaveRetention = Settings.EffectSaveRetentionCount;
        var previousShowGameEffectLog = Settings.ShowGameEffectLog;
        var previousShowPdaEffectMessage = Settings.ShowPdaEffectMessage;
        try
        {
            Settings.Actions = normalizedImport.Actions;
            Settings.UserPresets = normalizedImport.UserPresets;
            Settings.QueuePolicy = normalizedImport.QueuePolicy;
            Settings.GlobalCooldownSeconds = normalizedImport.GlobalCooldownSeconds;
            Settings.SaveBeforeDonationEffects = normalizedImport.SaveBeforeDonationEffects;
            Settings.SaveAfterDonationEffects = normalizedImport.SaveAfterDonationEffects;
            Settings.EffectSaveRetentionCount = normalizedImport.EffectSaveRetentionCount;
            Settings.ShowGameEffectLog = normalizedImport.ShowGameEffectLog;
            Settings.ShowPdaEffectMessage = normalizedImport.ShowPdaEffectMessage;
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.Actions = previousActions;
            Settings.UserPresets = previousUserPresets;
            Settings.QueuePolicy = previousQueuePolicy;
            Settings.GlobalCooldownSeconds = previousGlobalCooldown;
            Settings.SaveBeforeDonationEffects = previousSaveBefore;
            Settings.SaveAfterDonationEffects = previousSaveAfter;
            Settings.EffectSaveRetentionCount = previousSaveRetention;
            Settings.ShowGameEffectLog = previousShowGameEffectLog;
            Settings.ShowPdaEffectMessage = previousShowPdaEffectMessage;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await DisposeYouTubeChatAsync().ConfigureAwait(false);
        if (_settingsLoaded)
        {
            try { await SaveSettingsAsync().ConfigureAwait(false); }
            catch (Exception exception) { await _logger.WriteAsync("WARN", $"Save on exit: {exception.Message}").ConfigureAwait(false); }
        }
        if (_donationSession is not null) await _donationSession.DisposeAsync().ConfigureAwait(false);
        if (_donateXSession is not null) await _donateXSession.DisposeAsync().ConfigureAwait(false);
        await _gameServer.DisposeAsync().ConfigureAwait(false);
        if (_drainLoop is not null)
        {
            try { await _drainLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _httpClient.Dispose();
        _apiRateLimiter.Dispose();
        _donateXRateLimiter.Dispose();
        _donationGate.Dispose();
        _fortuneWheelGate.Dispose();
        _recoveryGate.Dispose();
        _donateXRecoveryGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ApplyLiveCatalogSafelyAsync(CatalogSnapshot? snapshot)
    {
        await _catalogGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (snapshot is null)
            {
                InvalidateCatalogRevisions();
                _catalogState = _catalogState with
                {
                    IsLive = false,
                    Status = _catalogState.Catalog is null
                        ? "Игра отключена; каталога нет"
                        : "Игра отключена; показан последний каталог только для просмотра"
                };
                CatalogStateChanged?.Invoke(_catalogState);
                SettingsChanged?.Invoke();
                return;
            }

            _catalogSource = snapshot;
            var effectiveCatalog = ApplyCatalog(snapshot, isLive: true);
            _catalogState = new CatalogRuntimeState(
                _gameServer.Capabilities,
                effectiveCatalog,
                true,
                $"{EngineProfileLabel(_gameServer.Capabilities.Profile)} · {snapshot.Entries.Count} секций · ревизия {snapshot.Revision}");
            if (!string.IsNullOrWhiteSpace(Settings.GamePath))
            {
                await _catalogCacheStore.SaveAsync(
                    Settings.GamePath,
                    _gameServer.Capabilities,
                    snapshot,
                    _lifetime.Token).ConfigureAwait(false);
            }
            CatalogStateChanged?.Invoke(_catalogState);
            SettingsChanged?.Invoke();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
        {
            if (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                await _logger.WriteAsync("WARN", $"Catalog: {exception.Message}").ConfigureAwait(false);
            }
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task ApplyCatalogInfoSafelyAsync(CatalogInfoMessage? catalogInfo)
    {
        if (catalogInfo is null) return;
        await _catalogGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_gameServer.IsCatalogSynchronizing) return;
            var cachedCatalog = _catalogSource;
            if (cachedCatalog is not null &&
                _gameServer.TryAdoptCachedCatalog(cachedCatalog, catalogInfo))
            {
                return;
            }

            InvalidateCatalogRevisions();
            _catalogState = _catalogState with
            {
                IsLive = false,
                Capabilities = _gameServer.Capabilities,
                Status = cachedCatalog is null
                    ? $"{EngineProfileLabel(_gameServer.Capabilities.Profile)} · каталог не загружен; нажмите ↻"
                    : $"{EngineProfileLabel(_gameServer.Capabilities.Profile)} · каталог игры изменился; нажмите ↻ для обновления"
            };
            CatalogStateChanged?.Invoke(_catalogState);
            SettingsChanged?.Invoke();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
        {
            if (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                await _logger.WriteAsync("WARN", $"Catalog identity: {exception.Message}").ConfigureAwait(false);
            }
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private CatalogSnapshot ApplyCatalog(CatalogSnapshot snapshot, bool isLive)
    {
        var effective = CatalogExclusionFilter.Apply(snapshot,
            Settings.CatalogExclusions.Select(entry => (entry.Kind, entry.SectionId)));
        DynamicCatalogBinding.Apply(effective, _presets);
        DynamicCatalogBinding.Apply(effective, Settings.Actions);
        DynamicCatalogBinding.Apply(effective, Settings.UserPresets);
        if (!isLive)
        {
            InvalidateCatalogRevisions();
        }
        return effective;
    }

    private void ApplyCurrentProfileCatalogBindings()
    {
        var warnings = _manifestLoader.ApplyPresetsToInstances(Settings.Actions, _presets).ToList();
        warnings.AddRange(_manifestLoader.ApplyPresetsToInstances(Settings.UserPresets, _presets));
        foreach (var warning in warnings)
        {
            _ = _logger.WriteAsync("WARN", warning);
        }
        if (_catalogState.Catalog is not { } catalog) return;
        DynamicCatalogBinding.Apply(catalog, Settings.Actions);
        DynamicCatalogBinding.Apply(catalog, Settings.UserPresets);
        if (!_catalogState.IsLive)
        {
            InvalidateCatalogRevisions(Settings.Actions);
            InvalidateCatalogRevisions(Settings.UserPresets);
        }
    }

    private void InvalidateCatalogRevisions()
    {
        InvalidateCatalogRevisions(_presets);
        InvalidateCatalogRevisions(Settings.Actions);
        InvalidateCatalogRevisions(Settings.UserPresets);
    }

    private static void InvalidateCatalogRevisions(IEnumerable<ActionDefinition> actions)
    {
        foreach (var action in actions.Where(CatalogRevisionEnforcement.RequiresCatalog))
        {
            action.CatalogRevision = null;
        }
    }

    private void ClearCatalogBindings()
    {
        ClearCatalogBindings(_presets);
        ClearCatalogBindings(Settings.Actions);
        ClearCatalogBindings(Settings.UserPresets);
    }

    private static void ClearCatalogBindings(IEnumerable<ActionDefinition> actions)
    {
        foreach (var action in actions)
        {
            var dynamicDefinitions = action.ParameterDefinitions
                .Where(definition => definition.CatalogKind is not null)
                .ToList();
            if (dynamicDefinitions.Count == 0) continue;
            action.CatalogRevision = null;
            foreach (var definition in dynamicDefinitions)
            {
                definition.Options.Clear();
                definition.OptionGroups.Clear();
                definition.OptionLabels.Clear();
                definition.DefaultValue = string.Empty;
            }
        }
    }

    private void EnsureActionCanExecute(ActionDefinition action)
    {
        if (!IsHandlerAvailable(action.HandlerId))
        {
            throw new InvalidOperationException(
                "Скрипт этого действия отсутствует или не установлен в игру.");
        }
        if (_catalogState.IsLive &&
            !ReferenceEquals(_catalogState.Capabilities, CapabilitySet.Empty) &&
            !_catalogState.Capabilities.SupportsHandler(action.HandlerId))
        {
            throw new InvalidOperationException("Установленная версия игрового аддона не объявила этот шаблон.");
        }
        if (CatalogRevisionEnforcement.RequiresCatalog(action) &&
            (!_catalogState.IsLive || _catalogState.Catalog is null || action.CatalogRevision != _catalogState.Catalog.Revision))
        {
            throw new InvalidOperationException(
                "Для этого шаблона нужен свежий каталог текущего сеанса игры. Офлайн-кэш доступен только для просмотра.");
        }
    }

    internal static IReadOnlyList<string> ValidateActionsForPersistence(IEnumerable<ActionDefinition> actions)
    {
        const string unresolvedCatalogSelection = "__catalog_unselected__";
        var materialized = actions.Select(action => action.Clone()).ToList();
        foreach (var action in materialized)
        {
            foreach (var definition in action.ParameterDefinitions.Where(item => item.CatalogKind is not null))
            {
                if (string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(action.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal))
                    {
                        foreach (var entry in action.ItemSpawns.Where(entry => string.IsNullOrWhiteSpace(entry.ItemId)))
                            entry.ItemId = unresolvedCatalogSelection;
                    }
                    else if (SpawnGroupBundleCodec.Supports(action.HandlerId))
                    {
                        foreach (var entry in action.SpawnGroups.Where(entry => string.IsNullOrWhiteSpace(entry.VariantId)))
                            entry.VariantId = unresolvedCatalogSelection;
                    }
                }
                action.Parameters.TryGetValue(definition.Key, out var rawValue);
                var bundledValues = string.Equals(action.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal) &&
                                     string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase)
                    ? action.ItemSpawns.Select(entry => entry.ItemId)
                    : SpawnGroupBundleCodec.Supports(action.HandlerId) &&
                      string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase)
                        ? action.SpawnGroups.Select(entry => entry.VariantId)
                        : [];
                var selectedValues = bundledValues
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var validationValue = selectedValues.FirstOrDefault() ??
                    (string.IsNullOrWhiteSpace(rawValue) ? unresolvedCatalogSelection : rawValue);
                action.Parameters[definition.Key] = validationValue;
                definition.Options = selectedValues.Count > 0
                    ? selectedValues
                    : definition.SelectedOptions(validationValue)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                if (definition.Kind != ParameterKind.MultiChoice && definition.Options.Count == 0)
                {
                    definition.Options.Add(validationValue);
                }
            }
        }
        return CatalogValidation.Validate(materialized);
    }

    private static string EngineProfileLabel(EngineProfile profile) => profile switch
    {
        EngineProfile.Anomaly153Vanilla => "Anomaly 1.5.3 vanilla",
        EngineProfile.XRayMonolithStandard => "xray-monolith standard",
        EngineProfile.XRayMonolithMtTest => "xray-monolith MT-TEST",
        _ => "Неизвестный совместимый движок"
    };

    private IReadOnlyList<string> LoadExtensions()
    {
        var builtInHandlers = _presets.Select(action => action.HandlerId).ToHashSet(StringComparer.Ordinal);
        var result = _extensionManifestLoader.Load(builtInHandlers);
        _presets.AddRange(result.Actions);
        _extensionOrigins = result.Origins;
        _extensionStatus = result.Errors.Count == 0
            ? result.Actions.Count == 0
                ? "Пользовательские расширения не установлены"
                : $"Загружено расширенных действий: {result.Actions.Count}"
            : $"Ошибок manifest: {result.Errors.Count}; корректных действий: {result.Actions.Count}. " +
              string.Join(" | ", result.Errors.Take(3).Select(error =>
                  $"{Path.GetFileName(error.ManifestPath)}: {error.Message}"));
        return result.Errors
            .Select(error => $"Extension manifest {error.ManifestPath}: {error.Message}")
            .ToList();
    }

    private IReadOnlyList<string> RefreshFilePresets()
    {
        var result = _filePresetLoader.Load(Settings.GamePath, Settings.EffectMo2ModPath);
        _presets = result.Presets.Select(preset => preset.Clone()).ToList();
        _filePresetSources = result.SourcePaths;
        _filePresetFingerprint = _filePresetLoader.Fingerprint(Settings.GamePath, Settings.EffectMo2ModPath);
        _extensionStatus = $"Файловых шаблонов установлено: {_presets.Count}; файлов в effects: {_filePresetSources.Count}";
        return result.Warnings;
    }

    internal async Task ProcessDonationAsync(DonationEvent donation)
    {
        UpdateFortuneWheelConfiguration(DateTimeOffset.UtcNow);
        ResetFortuneWheelDeadline(DateTimeOffset.UtcNow);
        await _donationGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var existingRecord = await _processedStore.FindAsync(donation.Id, _lifetime.Token).ConfigureAwait(false);
            if (existingRecord is { State: not DispatchState.Queued })
            {
                if (existingRecord.State == DispatchState.Sent && _gameServer.IsAwaitingResult(existingRecord.CommandId)) return;
                if (existingRecord.State == DispatchState.Sent)
                {
                    EventHistoryEntry? commandSnapshot = null;
                    try
                    {
                        commandSnapshot = await _historyStore.FindCommandSnapshotAsync(
                            donation.Id,
                            existingRecord.CommandId,
                            _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        await _logger.WriteAsync(
                            "WARN",
                            "Sent command history recovery: " + exception.Message,
                            _lifetime.Token).ConfigureAwait(false);
                    }
                    var sourceAction = commandSnapshot is null
                        ? null
                        : Settings.Actions.FirstOrDefault(action =>
                            string.Equals(action.Id, commandSnapshot.ActionId, StringComparison.Ordinal));
                    var uncertainHistory = HistoryRecovery.CreateInterruptedSendEntry(
                        donation,
                        existingRecord,
                        commandSnapshot,
                        sourceAction,
                        DateTimeOffset.UtcNow);
                    await _processedStore.RecordAsync(new ProcessedDonationRecord
                    {
                        DonationId = donation.Id,
                        CommandId = existingRecord.CommandId,
                        State = DispatchState.Uncertain,
                        RecordedAt = DateTimeOffset.UtcNow
                    }, _lifetime.Token).ConfigureAwait(false);
                    await _notifyingHistory.AppendAsync(uncertainHistory, _lifetime.Token).ConfigureAwait(false);
                }
                else
                {
                    UpdateCheckpoint(donation);
                }
                await _settingsStore.SaveAsync(Settings, _lifetime.Token).ConfigureAwait(false);
                return;
            }
            var currency = Money.NormalizeCurrency(donation.Currency);
            if (!Settings.ObservedCurrencies.Contains(currency, StringComparer.Ordinal))
            {
                Settings.ObservedCurrencies.Add(currency);
                Settings.ObservedCurrencies.Sort(StringComparer.Ordinal);
            }

            // Voting has its own explicit ON/OFF state and source selection.
            // The donation-processing switch controls only ordinary price effects.
            // Persist the vote first so provider recovery remains idempotent.
            if (_votingLoaded)
                await Voting.RecordAsync(donation, Settings.ActiveProfileId, _lifetime.Token).ConfigureAwait(false);

            if (!Settings.Armed)
            {
                await _processedStore.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = donation.Id,
                    State = DispatchState.Skipped,
                    RecordedAt = DateTimeOffset.UtcNow
                }, _lifetime.Token).ConfigureAwait(false);
                await _notifyingHistory.AppendAsync(new EventHistoryEntry
                {
                    DonationId = donation.Id,
                    DonationCreatedAt = donation.CreatedAt,
                    Donor = donation.Username,
                    DonationMessage = donation.Message,
                    Amount = donation.Amount,
                    Currency = currency,
                    State = DispatchState.Skipped,
                    Detail = "Обработка донатов выключена",
                    CanRetryManually = true
                }, _lifetime.Token).ConfigureAwait(false);
            }
            else
            {
                await _dispatcher.EnqueueAsync(
                    donation,
                    Settings.Actions.Where(action => IsHandlerAvailable(action.HandlerId)).ToList(),
                    Settings.QueuePolicy,
                    _lifetime.Token,
                    _presets,
                    CurrentEffectExecutionOptions()).ConfigureAwait(false);
                await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, _lifetime.Token).ConfigureAwait(false);
            }

            await _settingsStore.SaveAsync(Settings, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _donationGate.Release();
        }
    }

    private async Task StartDonationSessionSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartDonationSessionAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetDonationAlertsConnection(new DonationConnectionState(false, "DonationAlerts: " + exception.Message));
            await _logger.WriteAsync("ERROR", "DonationAlerts session: " + exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartDonationSessionAsync(DonationAlertsProfile? knownProfile, CancellationToken cancellationToken)
    {
        if (_donationSession is not null) await _donationSession.DisposeAsync().ConfigureAwait(false);
        _donationSession = new DonationAlertsSession(
            _donationRest,
            Settings.DonationAlertsClientId,
            Secrets.ClientSecret,
            ReadTokens,
            SaveTokensAsync);
        _donationSession.ConnectionChanged += state =>
        {
            if (state.Connected && !AccountMatchesExpected)
            {
                SetDonationAlertsConnection(new DonationConnectionState(false, $"DonationAlerts: подключён {Settings.DonationAlertsAccountCode}, ожидался {Settings.ExpectedAccountCode}"));
            }
            else
            {
                SetDonationAlertsConnection(state);
            }
            if (state.Connected && Settings.InitialDonationSnapshotCompleted)
            {
                _ = RecoverGapSafelyAsync();
            }
        };
        _donationSession.DonationReceived += donation => AccountMatchesExpected
            ? ProcessDonationAsync(donation)
            : Task.CompletedTask;
        var accessToken = await _donationSession.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var profile = knownProfile ?? await _donationRest.GetProfileAsync(accessToken, cancellationToken).ConfigureAwait(false);
        Settings.DonationAlertsUserId = profile.Id;
        Settings.DonationAlertsAccountCode = profile.Code;
        Settings.DonationAlertsAccountName = profile.Name;
        if (!CanArmDonationProcessing) Settings.Armed = false;
        Secrets.SocketConnectionToken = profile.SocketConnectionToken;
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);

        await _donationSession.StartAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!Settings.InitialDonationSnapshotCompleted)
        {
            var newestPage = await _donationRest.GetDonationsAsync(accessToken, 1, cancellationToken).ConfigureAwait(false);
            var newest = newestPage.Donations.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
            if (newest is not null) UpdateCheckpoint(newest);
            Settings.InitialDonationSnapshotCompleted = true;
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RecoverGapSafelyAsync().ConfigureAwait(false);
        }
        SettingsChanged?.Invoke();
    }

    private async Task StartDonateXSessionSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartDonateXSessionAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetDonateXConnection(new DonateXConnectionState(false,
                "DonateX: ошибка подключения: " + exception.Message));
            await _logger.WriteAsync("ERROR", "DonateX session: " + exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartDonateXSessionAsync(
        DonateXProfile? knownProfile,
        CancellationToken cancellationToken)
    {
        if (_donateXSession is not null) await _donateXSession.DisposeAsync().ConfigureAwait(false);
        var token = Secrets.DonateXExternalToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Внешний токен DonateX не сохранён.");

        var profile = knownProfile ?? await _donateXRest.GetProfileAsync(token, cancellationToken).ConfigureAwait(false);
        Settings.DonateXUserId = profile.Id;
        Settings.DonateXAccountName = profile.Username;
        _donateXSession = new DonateXSession(token);
        _donateXSession.ConnectionChanged += state =>
        {
            var message = state.Connected
                ? $"DonateX: {Settings.DonateXAccountName}"
                : state.Message;
            SetDonateXConnection(new DonateXConnectionState(state.Connected, message));
            if (state.Connected && Settings.DonateXInitialDonationSnapshotCompleted)
                _ = RecoverDonateXGapSafelyAsync();
        };
        _donateXSession.DonationReceived += ProcessDonationAsync;
        _donateXSession.Start();

        if (!Settings.DonateXInitialDonationSnapshotCompleted)
        {
            var newest = (await _donateXRest.GetDonationsAsync(token, 0, 1, cancellationToken)
                .ConfigureAwait(false)).FirstOrDefault();
            if (newest is not null) UpdateCheckpoint(newest);
            Settings.DonateXInitialDonationSnapshotCompleted = true;
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RecoverDonateXGapSafelyAsync().ConfigureAwait(false);
        }
        SettingsChanged?.Invoke();
    }

    private OAuthTokenSet ReadTokens()
    {
        var seconds = (int)Math.Max(0, (Secrets.AccessTokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        return new OAuthTokenSet
        {
            AccessToken = Secrets.AccessToken,
            RefreshToken = Secrets.RefreshToken,
            ExpiresInSeconds = seconds,
            ObtainedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task SaveTokensAsync(OAuthTokenSet tokens, CancellationToken cancellationToken)
    {
        ApplyTokens(tokens);
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyTokens(OAuthTokenSet tokens)
    {
        Secrets.AccessToken = tokens.AccessToken;
        Secrets.RefreshToken = tokens.RefreshToken;
        Secrets.AccessTokenExpiresAt = tokens.ExpiresAt;
    }

    private void UpdateCheckpoint(DonationEvent donation)
    {
        // Retain old history without advancing another provider's checkpoint.
        if (donation.Id.StartsWith("donatepay:", StringComparison.Ordinal)) return;
        if (donation.Id.StartsWith("donatex:", StringComparison.Ordinal))
        {
            if (Settings.LastDonateXDonationCreatedAt is null ||
                donation.CreatedAt >= Settings.LastDonateXDonationCreatedAt)
            {
                Settings.LastDonateXDonationCreatedAt = donation.CreatedAt;
                Settings.LastDonateXDonationId = donation.Id;
            }
            return;
        }
        if (Settings.LastDonationCreatedAt is null || donation.CreatedAt >= Settings.LastDonationCreatedAt)
        {
            Settings.LastDonationCreatedAt = donation.CreatedAt;
            Settings.LastDonationId = donation.Id;
        }
    }

    private async Task CompleteResultSafelyAsync(GameExecutionResult result)
    {
        if (TryCompleteActorBoostReset(result)) return;
        var entered = false;
        try
        {
            await _resultHandlingGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            entered = true;
            var completed = await _dispatcher.CompleteAsync(result, _lifetime.Token).ConfigureAwait(false);
            if (completed is not null) await _settingsStore.SaveAsync(Settings, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) { await _logger.WriteAsync("WARN", "Result handling: " + exception.Message).ConfigureAwait(false); }
        finally
        {
            if (entered) _resultHandlingGate.Release();
        }
    }

    private async Task RecoverGapSafelyAsync()
    {
        if (_donationSession is null || !_recoveryGate.Wait(0)) return;
        try
        {
            var checkpoint = new RecoveryCheckpoint
            {
                Initialized = Settings.InitialDonationSnapshotCompleted,
                LastDonationId = Settings.LastDonationId,
                LastCreatedAt = Settings.LastDonationCreatedAt
            };
            var recovered = await _donationSession.RecoverGapAsync(checkpoint, Settings.QueuePolicy, _lifetime.Token).ConfigureAwait(false);
            foreach (var donation in recovered) await ProcessDonationAsync(donation).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _logger.WriteAsync("WARN", "Donation recovery: " + exception.Message).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task RecoverDonateXGapSafelyAsync()
    {
        if (_donateXSession is null || !Settings.DonateXInitialDonationSnapshotCompleted ||
            !_donateXRecoveryGate.Wait(0)) return;
        try
        {
            var token = Secrets.DonateXExternalToken;
            if (string.IsNullOrWhiteSpace(token)) return;
            var lowerBound = Settings.LastDonateXDonationCreatedAt ?? DateTimeOffset.MinValue;
            var recovered = new List<DonationEvent>();
            for (var skip = 0; skip < 500; skip += 100)
            {
                var page = await _donateXRest.GetDonationsAsync(token, skip, 100, _lifetime.Token)
                    .ConfigureAwait(false);
                recovered.AddRange(page.Where(item => item.CreatedAt >= lowerBound));
                if (page.Count < 100 || page.Any(item => item.CreatedAt < lowerBound)) break;
            }
            foreach (var donation in recovered
                         .GroupBy(item => item.Id, StringComparer.Ordinal)
                         .Select(group => group.First())
                         .OrderBy(item => item.CreatedAt)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                await ProcessDonationAsync(donation).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _logger.WriteAsync("WARN", "DonateX recovery: " + exception.Message)
                .ConfigureAwait(false);
        }
        finally
        {
            _donateXRecoveryGate.Release();
        }
    }

    private void SetDonationAlertsConnection(DonationConnectionState state)
    {
        if (state.Connected)
            state = state with
            {
                Message = IntegrationAccountStatus(
                    "DonationAlerts", Settings.DonationAlertsAccountCode)
            };
        _donationAlertsConnection = state;
        PublishIntegrationConnection();
    }

    private void SetDonateXConnection(DonateXConnectionState state)
    {
        if (state.Connected)
            state = state with
            {
                Message = IntegrationAccountStatus("DonateX", Settings.DonateXAccountName)
            };
        _donateXConnection = state;
        PublishIntegrationConnection();
    }

    internal static string IntegrationAccountStatus(string integrationName, string? accountName)
    {
        var safeIntegration = string.IsNullOrWhiteSpace(integrationName)
            ? "Интеграция"
            : integrationName.Trim();
        var safeAccount = new string((accountName ?? string.Empty)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        safeAccount = string.Join(' ', safeAccount
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"{safeIntegration}: {(safeAccount.Length == 0 ? "аккаунт подключён" : safeAccount)}";
    }

    private void PublishIntegrationConnection()
    {
        var messages = new List<string>();
        if (DonationAlertsAccountConfigured ||
            !_donationAlertsConnection.Message.Contains("не подключён", StringComparison.OrdinalIgnoreCase))
            messages.Add(_donationAlertsConnection.Message);
        if (DonateXAccountConfigured ||
            !_donateXConnection.Message.Contains("не подключён", StringComparison.OrdinalIgnoreCase))
            messages.Add(_donateXConnection.Message);
        if (messages.Count == 0) messages.Add("Сервисы донатов не подключены");
        DonationConnectionChanged?.Invoke(new DonationConnectionState(
            _donationAlertsConnection.Connected || _donateXConnection.Connected,
            string.Join(" · ", messages)));
    }

    private async Task DrainSafelyAsync()
    {
        try { await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { await _logger.WriteAsync("WARN", "Queue drain: " + exception.Message).ConfigureAwait(false); }
    }

    private void UpdateCheckpointFromHistory(EventHistoryEntry entry)
    {
        if (entry.State is DispatchState.Queued or DispatchState.Deferred or DispatchState.Sent) return;
        if (entry.DonationCreatedAt == default || string.IsNullOrWhiteSpace(entry.DonationId) ||
            entry.DonationId.StartsWith("test:", StringComparison.Ordinal) ||
            entry.DonationId.StartsWith("manual:", StringComparison.Ordinal) ||
            entry.DonationId.StartsWith("hotkey:", StringComparison.Ordinal) ||
            entry.DonationId.StartsWith("preset-hotkey:", StringComparison.Ordinal) ||
            entry.DonationId.StartsWith("fortune-wheel:", StringComparison.Ordinal)) return;
        UpdateCheckpoint(new DonationEvent { Id = entry.DonationId, CreatedAt = entry.DonationCreatedAt });
    }

    private async Task LoadPersistedQueueAvailabilityAsync(
        IReadOnlyList<EventHistoryEntry> history,
        CancellationToken cancellationToken)
    {
        _persistedQueuedCommands.Clear();
        foreach (var group in history
                     .Where(entry => entry.State == DispatchState.Queued &&
                         !string.IsNullOrWhiteSpace(entry.DonationId) &&
                         !string.IsNullOrWhiteSpace(entry.CommandId))
                     .GroupBy(entry => entry.DonationId, StringComparer.Ordinal))
        {
            var current = await _processedStore.FindAsync(group.Key, cancellationToken).ConfigureAwait(false);
            if (current is not { State: DispatchState.Queued } ||
                string.IsNullOrWhiteSpace(current.CommandId) ||
                !group.Any(entry => string.Equals(entry.CommandId, current.CommandId, StringComparison.Ordinal)))
            {
                continue;
            }

            _persistedQueuedCommands[group.Key] = current.CommandId;
        }
    }

    private void UpdatePersistedQueueAvailability(EventHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DonationId))
        {
            return;
        }

        if (entry.State == DispatchState.Queued && !string.IsNullOrWhiteSpace(entry.CommandId))
        {
            _persistedQueuedCommands[entry.DonationId] = entry.CommandId;
            return;
        }

        _persistedQueuedCommands.TryRemove(entry.DonationId, out _);
    }

    private async Task DrainLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await DrainSafelyAsync().ConfigureAwait(false);
            if (!_votingLoaded) continue;
            try
            {
                await AdvanceVotingAsync(cancellationToken).ConfigureAwait(false);
                VotingError = string.Empty;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (VotingError != error.Message)
                    await _logger.WriteAsync("WARN", "Voting: " + error.Message, cancellationToken).ConfigureAwait(false);
                VotingError = error.Message;
            }
        }
    }

    private sealed class NotifyingHistorySink : IEventHistorySink
    {
        private readonly IEventHistorySink _inner;
        private readonly Action<EventHistoryEntry> _notify;
        public NotifyingHistorySink(IEventHistorySink inner, Action<EventHistoryEntry> notify) { _inner = inner; _notify = notify; }
        public async Task AppendAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            await _inner.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            _notify(entry);
        }
    }
}

public sealed record EventRefreshResult(
    IReadOnlyList<EventHistoryEntry> History,
    ResultRecoveryReport Recovery);

public sealed class RuleExport
{
    public int SchemaVersion { get; set; } = SettingsNormalizer.CurrentSchemaVersion;
    public List<ActionDefinition> Actions { get; set; } = [];
    public List<ActionDefinition> UserPresets { get; set; } = [];
    public QueuePolicy QueuePolicy { get; set; } = new();
    public int GlobalCooldownSeconds { get; set; } = 5;
    public bool SaveBeforeDonationEffects { get; set; }
    public bool SaveAfterDonationEffects { get; set; }
    public int EffectSaveRetentionCount { get; set; } = 50;
    public bool ShowGameEffectLog { get; set; } = true;
    public bool ShowPdaEffectMessage { get; set; }
}
