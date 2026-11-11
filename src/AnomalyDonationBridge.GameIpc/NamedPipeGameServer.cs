using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.GameIpc;

public sealed record GameConnectionSnapshot(
    bool Connected,
    bool Ready,
    string Reason,
    string AddonVersion,
    int GameProcessId);

public sealed record CatalogSyncProgress(
    bool IsSynchronizing,
    int ReceivedCount,
    int TotalCount);

public sealed record ResultRecoveryReport(
    int RequestedCount,
    int RemainingCount,
    bool ReplayRequested,
    bool GameProcessChanged,
    bool GameConnected)
{
    public int RecoveredCount => Math.Max(0, RequestedCount - RemainingCount);
}

public sealed partial class NamedPipeGameServer : IGameCommandTransport, IGameCommandPreflight, IAsyncDisposable
{
    private const int PipeBufferBytes = PipeProtocol.MaximumLineBytes * 16;
    private const string CorrelatedPingCapability = "correlated_ping";
    private readonly string _applicationVersion;
    private readonly string _pipeName;
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _heartbeatSilenceTimeout;
    private readonly TimeSpan _commandResultTimeout;
    private readonly TimeSpan _readinessProbeTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _resultRecoveryGate = new(1, 1);
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private readonly ConcurrentDictionary<string, AwaitingGameCommand> _awaitingResults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _readinessProbes = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();
    private NamedPipeServerStream? _activePipe;
    private Task? _serverTask;
    private bool _gameReady;
    private string _reason = "Игра не подключена";
    private bool _lastReportedGameReady;
    private string _lastReportedReason = "Игра не подключена";
    private bool _heartbeatSuspended;
    private string _addonVersion = string.Empty;
    private int _gameProcessId;
    private CapabilitySet _capabilities = CapabilitySet.Empty;
    private CatalogSnapshot? _catalog;
    private CatalogInfoMessage? _catalogInfo;
    private bool _catalogSyncActive;

    public NamedPipeGameServer(string applicationVersion)
        : this(
            applicationVersion,
            PipeProtocol.PipeName,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(5))
    {
    }

    public NamedPipeGameServer(string applicationVersion, string pipeName)
        : this(
            applicationVersion,
            pipeName,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(5))
    {
    }

    public NamedPipeGameServer(
        string applicationVersion,
        string pipeName,
        TimeSpan pingInterval,
        TimeSpan heartbeatSilenceTimeout)
        : this(
            applicationVersion,
            pipeName,
            pingInterval,
            heartbeatSilenceTimeout,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(5))
    {
    }

    public NamedPipeGameServer(
        string applicationVersion,
        string pipeName,
        TimeSpan pingInterval,
        TimeSpan heartbeatSilenceTimeout,
        TimeSpan commandResultTimeout,
        TimeSpan readinessProbeTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pingInterval));
        }
        if (heartbeatSilenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatSilenceTimeout));
        }
        if (commandResultTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandResultTimeout));
        }
        if (readinessProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readinessProbeTimeout));
        }
        _applicationVersion = applicationVersion;
        _pipeName = pipeName;
        _pingInterval = pingInterval;
        _heartbeatSilenceTimeout = heartbeatSilenceTimeout;
        _commandResultTimeout = commandResultTimeout;
        _readinessProbeTimeout = readinessProbeTimeout;
    }

    public event Action<GameConnectionSnapshot>? ConnectionChanged;
    public event Action<GameExecutionResult>? ResultReceived;
    public event Action<Exception>? ProtocolError;
    public event Action<CapabilitySet>? CapabilitiesChanged;
    public event Action<CatalogSnapshot?>? CatalogChanged;
    public event Action<CatalogInfoMessage?>? CatalogInfoChanged;
    public event Action<CatalogSyncProgress>? CatalogSyncProgressChanged;

    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
            {
                return _activePipe?.IsConnected == true;
            }
        }
    }

    public bool IsGameReady
    {
        get
        {
            lock (_stateGate)
            {
                return _activePipe?.IsConnected == true &&
                    _gameReady;
            }
        }
    }

    public bool IsAwaitingResult(string commandId) => _awaitingResults.ContainsKey(commandId);

    public bool CanAttempt(GameCommand command)
    {
        lock (_stateGate)
            return _lastReportedReason != BtrPassengerCommands.WaitingReason || BtrPassengerCommands.IsAllowed(command);
    }

    public int AwaitingResultCount => _awaitingResults.Count;

    public bool IsCatalogSynchronizing
    {
        get
        {
            lock (_stateGate)
            {
                return _catalogSyncActive;
            }
        }
    }

    public CapabilitySet Capabilities
    {
        get
        {
            lock (_stateGate)
            {
                return _capabilities;
            }
        }
    }

    public CatalogSnapshot? Catalog
    {
        get
        {
            lock (_stateGate)
            {
                return _catalog;
            }
        }
    }

    public void Start()
    {
        if (_serverTask is not null)
        {
            return;
        }

        _serverTask = Task.Run(() => ServerLoopAsync(_lifetime.Token));
    }

    public async Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
    {
        var validation = Prepare(command);
        if (!validation.IsValid)
        {
            if (validation.IsTransient) throw new GameNotReadyBeforeSendException(validation.Reason);
            throw new InvalidOperationException(validation.Reason);
        }

        NamedPipeServerStream pipe;
        int gameProcessId;
        lock (_stateGate)
        {
            pipe = _activePipe is { IsConnected: true } connected
                ? connected
                : throw new GameNotReadyBeforeSendException("Игра не подключена.");
            if (!_gameReady)
            {
                throw new GameNotReadyBeforeSendException(
                    "Игра ещё не готова принимать эффекты.");
            }
            gameProcessId = _gameProcessId;
        }

        await ConfirmGameLoopAsync(pipe, gameProcessId, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            if (!ReferenceEquals(_activePipe, pipe) ||
                _activePipe?.IsConnected != true ||
                !_gameReady ||
                _gameProcessId != gameProcessId)
            {
                throw new GameNotReadyBeforeSendException(
                    "Игра остановила обновление до отправки эффекта.");
            }
        }

        // A refresh or load can invalidate the catalog while the correlated
        // probe is in flight. This is still before any COMMAND bytes: the
        // dispatcher can safely queue it again and revalidate on the next tick.
        validation = Prepare(command);
        if (!validation.IsValid) throw new GameNotReadyBeforeSendException(validation.Reason);

        var awaiting = new AwaitingGameCommand(
            command,
            gameProcessId,
            DateTimeOffset.UtcNow,
            NeedsReplay: false);
        if (!_awaitingResults.TryAdd(command.CommandId, awaiting))
        {
            throw new InvalidOperationException("Команда с таким ID уже ожидает подтверждения игры.");
        }
        try
        {
            await WriteLineAsync(pipe, PipeProtocol.Command(command), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            MarkForReplay(command.CommandId);
            try { pipe.Dispose(); }
            catch { }
        }
        catch
        {
            _awaitingResults.TryRemove(command.CommandId, out _);
            throw;
        }
    }

    public Func<GameCommand, CapabilitySet, CatalogCommandValidationResult>? AdditionalCommandPolicy { get; set; }

    public CatalogCommandValidationResult Prepare(GameCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        CapabilitySet capabilities;
        CatalogSnapshot? catalog;
        lock (_stateGate)
        {
            capabilities = _capabilities;
            catalog = _catalog;
        }

        var policyValidation = AdditionalCommandPolicy?.Invoke(command, capabilities);
        if (policyValidation is { IsValid: false }) return policyValidation;

        // CapabilitySet.Empty is the only "not reported yet" sentinel.  Never
        // send before the capability handshake.  Once reported, even an empty
        // handler list is authoritative for an unsupported engine profile.
        if (ReferenceEquals(capabilities, CapabilitySet.Empty))
        {
            return CatalogCommandValidationResult.Awaiting(
                "Игра ещё не сообщила поддерживаемые обработчики.");
        }

        if (!capabilities.SupportsHandler(command.ActionId))
        {
            return CatalogCommandValidationResult.Invalid(
                $"Установленный игровой аддон не объявил обработчик {command.ActionId}.");
        }

        var catalogValidation = CatalogRevisionEnforcement.Validate(command, catalog);
        if (!catalogValidation.IsValid)
        {
            return catalogValidation;
        }

        // Serialize during preflight so an oversized or otherwise malformed
        // command is rejected before the dispatcher durably records SENT.
        try
        {
            _ = PipeProtocol.Command(command);
        }
        catch (InvalidDataException exception)
        {
            return CatalogCommandValidationResult.Invalid(exception.Message);
        }

        return CatalogCommandValidationResult.Valid;
    }

    public async Task RequestCatalogAsync(CancellationToken cancellationToken = default)
    {
        NamedPipeServerStream pipe;
        lock (_stateGate)
        {
            pipe = _activePipe is { IsConnected: true } connected
                ? connected
                : throw new InvalidOperationException("Игра не подключена.");
            _catalog = null;
            _catalogSyncActive = true;
        }

        CatalogSyncProgressChanged?.Invoke(new CatalogSyncProgress(true, 0, 0));
        try
        {
            await WriteLineAsync(pipe, PipeProtocol.GetCatalog(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            SetCatalogProgress(false, 0, 0);
            throw;
        }
    }

    public async Task RequestCatalogInfoAsync(CancellationToken cancellationToken = default)
    {
        NamedPipeServerStream pipe;
        lock (_stateGate)
        {
            pipe = _activePipe is { IsConnected: true } connected
                ? connected
                : throw new InvalidOperationException("Игра не подключена.");
            if (!_capabilities.SupportsCapability("catalog_identity"))
            {
                throw new InvalidOperationException(
                    "Установленный аддон не поддерживает быструю проверку каталога.");
            }
        }

        await WriteLineAsync(pipe, PipeProtocol.GetCatalogInfo(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResultRecoveryReport> RecoverPendingResultsAsync(
        CancellationToken cancellationToken = default)
    {
        await _resultRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var commandIds = _awaitingResults.Keys.ToArray();
            if (commandIds.Length == 0)
            {
                return new ResultRecoveryReport(0, 0, false, false, IsConnected);
            }

            var expectedProcessIds = _awaitingResults
                .Where(pair => commandIds.Contains(pair.Key, StringComparer.Ordinal))
                .Select(pair => pair.Value.GameProcessId)
                .Where(processId => processId > 0)
                .Distinct()
                .ToArray();
            foreach (var commandId in commandIds)
            {
                MarkForReplay(commandId);
            }

            NamedPipeServerStream? pipe;
            int activeProcessId;
            bool gameReady;
            lock (_stateGate)
            {
                pipe = _activePipe is { IsConnected: true } connected ? connected : null;
                activeProcessId = _gameProcessId;
                gameReady = _gameReady;
            }

            var gameConnected = pipe is not null;
            var processChanged = gameConnected && activeProcessId > 0 &&
                expectedProcessIds.Length > 0 &&
                !expectedProcessIds.Contains(activeProcessId);
            var replayRequested = gameConnected && gameReady && !processChanged;
            if (replayRequested)
            {
                // Re-send the exact command IDs over the healthy session. The Lua
                // addon returns its remembered RESULT when execution already
                // finished, and executes only a command it never observed. Closing
                // the pipe here used to manufacture a visible disconnect and could
                // leave the sole server instance unavailable during reconnect.
                await ReplayAwaitingAsync(
                    pipe!, activeProcessId, cancellationToken).ConfigureAwait(false);
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_stateGate)
                {
                    processChanged = _activePipe?.IsConnected == true &&
                        _gameProcessId > 0 &&
                        expectedProcessIds.Length > 0 &&
                        !expectedProcessIds.Contains(_gameProcessId);
                }
                var remaining = commandIds.Count(_awaitingResults.ContainsKey);
                if (remaining == 0)
                {
                    return new ResultRecoveryReport(
                        commandIds.Length, 0, replayRequested, processChanged,
                        gameConnected);
                }
                if (processChanged)
                {
                    break;
                }

                bool stillConnected;
                bool stillReady;
                lock (_stateGate)
                {
                    stillConnected = _activePipe?.IsConnected == true;
                    stillReady = _gameReady;
                }
                // If the game is absent or loading, merely arm the exact-ID
                // replay and return immediately. STATUS ready on this same game
                // process will deliver it; the UI button must never look hung.
                if (!stillConnected || !stillReady)
                {
                    break;
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return new ResultRecoveryReport(
                commandIds.Length,
                commandIds.Count(_awaitingResults.ContainsKey),
                replayRequested,
                processChanged,
                gameConnected);
        }
        finally
        {
            _resultRecoveryGate.Release();
        }
    }

    public bool TryAdoptCachedCatalog(CatalogSnapshot cachedCatalog, CatalogInfoMessage catalogInfo)
    {
        ArgumentNullException.ThrowIfNull(cachedCatalog);
        ArgumentNullException.ThrowIfNull(catalogInfo);
        if (cachedCatalog.Entries.Count != catalogInfo.EntryCount ||
            !string.Equals(cachedCatalog.ContentHash, catalogInfo.ContentHash, StringComparison.Ordinal) ||
            !string.Equals(
                CatalogContentHash.Compute(cachedCatalog.Entries, cachedCatalog.MonsterCommunities),
                catalogInfo.ContentHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        var liveSnapshot = new CatalogSnapshot(
            catalogInfo.Revision,
            catalogInfo.ContentHash,
            cachedCatalog.Entries, monsterCommunities: cachedCatalog.MonsterCommunities);
        lock (_stateGate)
        {
            if (_activePipe?.IsConnected != true || _catalogSyncActive || _catalogInfo != catalogInfo)
            {
                return false;
            }
            _catalog = liveSnapshot;
        }

        CatalogChanged?.Invoke(liveSnapshot);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        NamedPipeServerStream? pipe;
        lock (_stateGate)
        {
            pipe = _activePipe;
            _activePipe = null;
        }

        pipe?.Dispose();
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _writeGate.Dispose();
        _resultRecoveryGate.Dispose();
        _replayGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    PipeBufferBytes,
                    PipeBufferBytes);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleSessionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Shutdown or an I/O failure disposed the active pipe. Manual
                // result refreshes replay over a healthy session and never come
                // through this path.
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                ProtocolError?.Invoke(exception);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DisconnectActiveSession("Соединение с игрой потеряно");
            }
        }
    }

    private async Task HandleSessionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var catalogAssembler = new CatalogSnapshotAssembler();
        var reader = new PipeLineReader(pipe);
        var firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (PipeProtocol.ParseInbound(firstLine) is not HelloMessage hello)
        {
            throw new InvalidDataException("Первым сообщением игры должен быть HELLO.");
        }

        lock (_stateGate)
        {
            _activePipe = pipe;
            _gameReady = false;
            _reason = "Аддон подключён, ожидается готовность игры";
            _lastReportedGameReady = false;
            _lastReportedReason = _reason;
            _heartbeatSuspended = false;
            _addonVersion = hello.AddonVersion;
            _gameProcessId = hello.GameProcessId;
            _capabilities = CapabilitySet.Empty;
            _catalog = null;
            _catalogInfo = null;
            _catalogSyncActive = false;
        }
        foreach (var pending in _awaitingResults
                     .Where(pair => pair.Value.GameProcessId != hello.GameProcessId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            EmitUncertain(
                pending,
                "Игра была перезапущена до подтверждения команды; автоматический повтор запрещён");
        }
        PublishSnapshot();
        await WriteLineAsync(pipe, PipeProtocol.Welcome(_applicationVersion), cancellationToken).ConfigureAwait(false);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = new SessionHeartbeat();
        var pingTask = PingLoopAsync(pipe, hello.GameProcessId, heartbeat, session.Token);
        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                // STATUS, catalog records and RESULT prove that the game loop is
                // alive just as reliably as PONG. This is especially important
                // immediately after a long level load when queued PING messages
                // may not be the first records drained by the Lua addon.
                heartbeat.MarkActivity();
                var message = PipeProtocol.ParseInbound(line);
                if (message is not StatusMessage && RestoreAfterHeartbeatSuspension(pipe))
                {
                    PublishSnapshot();
                }
                switch (message)
                {
                    case VotingGameReply voting:
                        if (_votingReplies.TryGetValue(voting.RoundId, out var reply)) reply.TrySetResult(voting);
                        break;
                    case StatusMessage status:
                    {
                        bool canReplay;
                        lock (_stateGate)
                        {
                            _lastReportedGameReady = status.Ready;
                            _lastReportedReason = status.Reason;
                            var addonCurrent = _capabilities.SupportsCapability(
                                CorrelatedPingCapability);
                            _gameReady = status.Ready && addonCurrent;
                            _reason = status.Ready && !addonCurrent
                                ? "addon_update_required"
                                : status.Reason;
                            _heartbeatSuspended = false;
                            canReplay = _gameReady;
                        }
                        PublishSnapshot();
                        if (canReplay)
                        {
                            await ReplayAwaitingAsync(
                                pipe,
                                hello.GameProcessId,
                                cancellationToken).ConfigureAwait(false);
                        }
                        if (status.Reason.StartsWith("catalog_", StringComparison.Ordinal))
                        {
                            SetCatalogProgress(false, 0, 0);
                        }
                        break;
                    }
                    case ResultMessage result:
                        if (_awaitingResults.TryRemove(result.Result.CommandId, out _))
                        {
                            ResultReceived?.Invoke(result.Result);
                        }
                        break;
                    case CapabilitiesMessage capabilities:
                    {
                        bool stateChanged;
                        lock (_stateGate)
                        {
                            _capabilities = capabilities.Capabilities;
                            var previousReady = _gameReady;
                            var previousReason = _reason;
                            if (!_heartbeatSuspended)
                            {
                                var addonCurrent = capabilities.Capabilities.SupportsCapability(
                                    CorrelatedPingCapability);
                                _gameReady = _lastReportedGameReady && addonCurrent;
                                _reason = _lastReportedGameReady && !addonCurrent
                                    ? "addon_update_required"
                                    : _lastReportedReason;
                            }
                            stateChanged = previousReady != _gameReady ||
                                !string.Equals(previousReason, _reason, StringComparison.Ordinal);
                        }
                        CapabilitiesChanged?.Invoke(capabilities.Capabilities);
                        if (stateChanged)
                        {
                            PublishSnapshot();
                        }
                        if (capabilities.Capabilities.SupportsCapability("catalog_identity"))
                        {
                            await WriteLineAsync(
                                pipe, PipeProtocol.GetCatalogInfo(), cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }
                    case CatalogInfoMessage catalogInfo:
                        lock (_stateGate)
                        {
                            _catalogInfo = catalogInfo;
                        }
                        CatalogInfoChanged?.Invoke(catalogInfo);
                        break;
                    case CatalogBeginMessage begin:
                        lock (_stateGate)
                        {
                            _catalog = null;
                            _catalogSyncActive = true;
                        }
                        catalogAssembler.Begin(begin);
                        CatalogSyncProgressChanged?.Invoke(
                            new CatalogSyncProgress(true, 0, begin.ExpectedCount));
                        break;
                    case CatalogEntryMessage entry:
                        catalogAssembler.Add(entry);
                        var progressStep = Math.Max(1, catalogAssembler.ExpectedCount / 100);
                        if (catalogAssembler.ReceivedCount == catalogAssembler.ExpectedCount ||
                            catalogAssembler.ReceivedCount % progressStep == 0)
                        {
                            CatalogSyncProgressChanged?.Invoke(new CatalogSyncProgress(
                                true,
                                catalogAssembler.ReceivedCount,
                                catalogAssembler.ExpectedCount));
                        }
                        break;
                    case CatalogEndMessage end:
                        var snapshot = catalogAssembler.End(end);
                        lock (_stateGate)
                        {
                            _catalog = snapshot;
                            _catalogInfo = new CatalogInfoMessage(
                                snapshot.Revision, snapshot.Entries.Count, snapshot.ContentHash);
                            _catalogSyncActive = false;
                        }
                        CatalogChanged?.Invoke(snapshot);
                        CatalogSyncProgressChanged?.Invoke(new CatalogSyncProgress(
                            false, snapshot.Entries.Count, snapshot.Entries.Count));
                        break;
                    case PongMessage pong:
                        if (!string.IsNullOrWhiteSpace(pong.ProbeId) &&
                            _readinessProbes.TryRemove(pong.ProbeId, out var probe))
                        {
                            probe.TrySetResult(true);
                        }
                        break;
                    case HelloMessage:
                        throw new InvalidDataException("Повторный HELLO в активной сессии запрещён.");
                }
            }
        }
        finally
        {
            session.Cancel();
            try { await pingTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task PingLoopAsync(
        NamedPipeServerStream pipe,
        int gameProcessId,
        SessionHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pingInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var suspendedNow = false;
            lock (_stateGate)
            {
                var sessionActive = ReferenceEquals(_activePipe, pipe) &&
                    _activePipe?.IsConnected == true;
                if (sessionActive &&
                    _gameReady &&
                    !_heartbeatSuspended &&
                    heartbeat.LastActivityAt <= DateTimeOffset.UtcNow - _heartbeatSilenceTimeout)
                {
                    // A silent addon loop means "temporarily not ready", not a
                    // broken pipe. Keep the actual PID session and queued work
                    // intact; the next inbound record restores reported state.
                    _heartbeatSuspended = true;
                    _gameReady = false;
                    _reason = "game_suspended";
                    suspendedNow = true;
                }
            }
            if (suspendedNow)
            {
                PublishSnapshot();
            }
            try
            {
                await WriteLineAsync(pipe, PipeProtocol.Ping(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                MarkProcessCommandsForReplay(gameProcessId);
                try { pipe.Dispose(); }
                catch { }
                return;
            }
            var now = DateTimeOffset.UtcNow;
            var recoveryDeadline = now - TimeSpan.FromSeconds(30);
            var terminalDeadline = now - _commandResultTimeout;
            int activeProcessId;
            bool mayExpire;
            lock (_stateGate)
            {
                activeProcessId = _gameProcessId;
                mayExpire = _activePipe?.IsConnected == true && _gameReady;
            }
            foreach (var pending in _awaitingResults.Where(pair =>
                         pair.Value.LastSentAt <= terminalDeadline).ToArray())
            {
                EmitUncertain(
                    pending.Key,
                    $"Игра не подтвердила выполнение за {(int)Math.Ceiling(_commandResultTimeout.TotalSeconds)} сек.; автоматический повтор запрещён");
            }
            if (!mayExpire)
            {
                continue;
            }
            foreach (var pending in _awaitingResults.Where(pair =>
                         pair.Value.GameProcessId == activeProcessId &&
                         !pair.Value.NeedsReplay &&
                         !pair.Value.RecoverySuggested &&
                         pair.Value.LastSentAt <= recoveryDeadline).ToArray())
            {
                if (MarkRecoverySuggested(pending.Key))
                {
                    lock (_stateGate)
                    {
                        if (_activePipe?.IsConnected == true && _gameProcessId == activeProcessId)
                        {
                            _reason = "Ответ игры задерживается — нажмите «Обновить события»";
                        }
                    }
                    PublishSnapshot();
                }
            }
        }
    }

    private void DisconnectActiveSession(string reason)
    {
        lock (_stateGate)
        {
            _activePipe = null;
            _gameReady = false;
            _reason = reason;
            _lastReportedGameReady = false;
            _lastReportedReason = reason;
            _heartbeatSuspended = false;
            _addonVersion = string.Empty;
            _gameProcessId = 0;
            _capabilities = CapabilitySet.Empty;
            _catalog = null;
            _catalogInfo = null;
            _catalogSyncActive = false;
        }

        foreach (var commandId in _awaitingResults.Keys) MarkForReplay(commandId);
        foreach (var probeId in _readinessProbes.Keys)
        {
            if (_readinessProbes.TryRemove(probeId, out var probe))
            {
                probe.TrySetResult(false);
            }
        }
        CapabilitiesChanged?.Invoke(CapabilitySet.Empty);
        CatalogInfoChanged?.Invoke(null);
        CatalogChanged?.Invoke(null);
        CatalogSyncProgressChanged?.Invoke(new CatalogSyncProgress(false, 0, 0));
        PublishSnapshot();
    }

    private void SetCatalogProgress(bool active, int receivedCount, int totalCount)
    {
        lock (_stateGate)
        {
            _catalogSyncActive = active;
        }
        CatalogSyncProgressChanged?.Invoke(
            new CatalogSyncProgress(active, receivedCount, totalCount));
    }

    private bool RestoreAfterHeartbeatSuspension(NamedPipeServerStream pipe)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_activePipe, pipe) ||
                _activePipe?.IsConnected != true ||
                !_heartbeatSuspended)
            {
                return false;
            }

            _heartbeatSuspended = false;
            var addonCurrent = _capabilities.SupportsCapability(CorrelatedPingCapability);
            _gameReady = _lastReportedGameReady && addonCurrent;
            _reason = _lastReportedGameReady && !addonCurrent
                ? "addon_update_required"
                : _lastReportedReason;
            return true;
        }
    }

    private void EmitUncertain(string commandId, string reason)
    {
        if (_awaitingResults.TryRemove(commandId, out _))
        {
            ResultReceived?.Invoke(new GameExecutionResult
            {
                CommandId = commandId,
                Status = GameResultStatus.Uncertain,
                Reason = reason,
                ReceivedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private void MarkForReplay(string commandId)
    {
        while (_awaitingResults.TryGetValue(commandId, out var pending))
        {
            var replay = pending with { NeedsReplay = true };
            if (_awaitingResults.TryUpdate(commandId, replay, pending)) return;
        }
    }

    private void MarkProcessCommandsForReplay(int gameProcessId)
    {
        foreach (var commandId in _awaitingResults
                     .Where(pair => pair.Value.GameProcessId == gameProcessId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            MarkForReplay(commandId);
        }
    }

    private bool MarkRecoverySuggested(string commandId)
    {
        while (_awaitingResults.TryGetValue(commandId, out var pending))
        {
            if (pending.RecoverySuggested) return false;
            var updated = pending with { RecoverySuggested = true };
            if (_awaitingResults.TryUpdate(commandId, updated, pending)) return true;
        }
        return false;
    }

    private async Task ReplayAwaitingAsync(
        NamedPipeServerStream pipe,
        int gameProcessId,
        CancellationToken cancellationToken)
    {
        await _replayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var pair in _awaitingResults
                         .Where(item => item.Value.GameProcessId == gameProcessId && item.Value.NeedsReplay)
                         .OrderBy(item => item.Value.LastSentAt)
                         .ToArray())
            {
                var pending = pair.Value;
                try
                {
                    await WriteLineAsync(
                        pipe,
                        PipeProtocol.Command(pending.Command),
                        cancellationToken).ConfigureAwait(false);
                    _awaitingResults.TryUpdate(
                        pair.Key,
                        pending with
                        {
                            LastSentAt = DateTimeOffset.UtcNow,
                            NeedsReplay = false,
                            RecoverySuggested = false
                        },
                        pending);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    MarkForReplay(pair.Key);
                    try { pipe.Dispose(); }
                    catch { }
                    return;
                }
            }
        }
        finally
        {
            _replayGate.Release();
        }
    }

    private void PublishSnapshot()
    {
        GameConnectionSnapshot snapshot;
        lock (_stateGate)
        {
            snapshot = new GameConnectionSnapshot(
                _activePipe?.IsConnected == true,
                _activePipe?.IsConnected == true && _gameReady,
                _reason,
                _addonVersion,
                _gameProcessId);
        }
        ConnectionChanged?.Invoke(snapshot);
    }

    private async Task ConfirmGameLoopAsync(
        NamedPipeServerStream pipe,
        int gameProcessId,
        CancellationToken cancellationToken)
    {
        var probeId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_readinessProbes.TryAdd(probeId, completion))
        {
            throw new InvalidOperationException("Не удалось создать проверку игрового цикла.");
        }

        try
        {
            await WriteLineAsync(
                pipe,
                PipeProtocol.Ping(probeId),
                cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(_readinessProbeTimeout);
            bool confirmed;
            try
            {
                confirmed = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                confirmed = false;
            }

            if (!confirmed)
            {
                SuspendUnresponsiveGame(pipe, gameProcessId);
                throw new GameNotReadyBeforeSendException(
                    "Игровой цикл не ответил на проверку готовности; эффект оставлен в очереди.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            throw new GameNotReadyBeforeSendException(
                "Связь с игровым циклом потеряна до отправки эффекта.");
        }
        finally
        {
            _readinessProbes.TryRemove(probeId, out _);
        }
    }

    private void SuspendUnresponsiveGame(NamedPipeServerStream pipe, int gameProcessId)
    {
        var changed = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(_activePipe, pipe) &&
                _activePipe?.IsConnected == true &&
                _gameProcessId == gameProcessId &&
                (!_heartbeatSuspended || _gameReady || _reason != "game_suspended"))
            {
                _heartbeatSuspended = true;
                _gameReady = false;
                _reason = "game_suspended";
                changed = true;
            }
        }
        if (changed)
        {
            PublishSnapshot();
        }
    }

    private async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        if (bytes.Length > PipeProtocol.MaximumLineBytes)
        {
            throw new InvalidDataException("Исходящая строка протокола превышает 16 КиБ.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed class PipeLineReader(Stream stream)
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly byte[] _readBuffer = new byte[PipeProtocol.MaximumLineBytes];
        private readonly byte[] _lineBuffer = new byte[PipeProtocol.MaximumLineBytes];
        private int _readOffset;
        private int _readCount;

        public async ValueTask<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var lineLength = 0;
            while (true)
            {
                if (_readOffset >= _readCount)
                {
                    _readCount = await stream.ReadAsync(
                        _readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _readOffset = 0;
                    if (_readCount == 0)
                    {
                        throw new EndOfStreamException("Канал игры закрыт.");
                    }
                }

                var newline = Array.IndexOf(
                    _readBuffer, (byte)'\n', _readOffset, _readCount - _readOffset);
                var segmentEnd = newline >= 0 ? newline : _readCount;
                var segmentLength = segmentEnd - _readOffset;
                if (lineLength + segmentLength > PipeProtocol.MaximumLineBytes - 1)
                {
                    throw new InvalidDataException(
                        "Входящая строка протокола превышает 16 КиБ.");
                }

                if (segmentLength > 0)
                {
                    Buffer.BlockCopy(
                        _readBuffer, _readOffset, _lineBuffer, lineLength, segmentLength);
                    lineLength += segmentLength;
                }

                _readOffset = newline >= 0 ? newline + 1 : _readCount;
                if (newline < 0)
                {
                    continue;
                }

                if (lineLength > 0 && _lineBuffer[lineLength - 1] == (byte)'\r')
                {
                    lineLength--;
                }

                try
                {
                    return StrictUtf8.GetString(_lineBuffer, 0, lineLength);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        "Входящая строка протокола содержит повреждённый UTF-8.", exception);
                }
            }
        }
    }

    private sealed record AwaitingGameCommand(
        GameCommand Command,
        int GameProcessId,
        DateTimeOffset LastSentAt,
        bool NeedsReplay,
        bool RecoverySuggested = false);

    private sealed class SessionHeartbeat
    {
        private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;

        public DateTimeOffset LastActivityAt =>
            new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

        public void MarkActivity() =>
            Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}
