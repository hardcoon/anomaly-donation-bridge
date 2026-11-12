using System.Collections.Concurrent;
using System.Globalization;

namespace AnomalyDonationBridge.Core;

public interface IProcessedDonationStore
{
    Task<ProcessedDonationRecord?> FindAsync(string donationId, CancellationToken cancellationToken = default);
    Task<ProcessedDonationRecord?> FindCommandAsync(string donationId, string commandId, CancellationToken cancellationToken = default);
    Task RecordAsync(ProcessedDonationRecord record, CancellationToken cancellationToken = default);
}

public interface IPendingDispatchStore
{
    Task<IReadOnlyList<PendingDispatchSnapshot>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<PendingDispatchSnapshot> pending, CancellationToken cancellationToken = default);
}

public interface IEventHistorySink
{
    Task AppendAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default);
}

public interface IGameCommandTransport
{
    bool IsConnected { get; }
    bool IsGameReady { get; }
    bool CanAttempt(GameCommand command) => true;
    Task SendAsync(GameCommand command, CancellationToken cancellationToken = default);
}

public static class BtrPassengerCommands
{
    public const string Handler = "hardcoon.adb.btr_companion";
    public const string WaitingReason = "actor_in_btr";

    public static bool IsAllowed(GameCommand command) => command.ActionId switch
    {
        "spawn_npcs" or "spawn_random_npcs" or "spawn_mutants" or "spawn_random_mutants" or
            "spawn_donor_boss" or "hardcoon.adb.show_squads" => true,
        Handler => command.Parameters.GetValueOrDefault("extend_current") != "false",
        _ => false
    };
}

/// <summary>
/// The transport rechecked readiness immediately before writing and proved
/// that no command bytes were sent. The dispatcher may therefore safely keep
/// the same command in the persistent queue.
/// </summary>
public sealed class GameNotReadyBeforeSendException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Lets the official transport reject a stale or unsafe command before the
/// dispatcher persists SENT. Temporary handshake/catalog unavailability keeps
/// the command queued; invalid commands are rejected, never marked uncertain.
/// </summary>
public interface IGameCommandPreflight
{
    CatalogCommandValidationResult Prepare(GameCommand command);
}

public sealed class InMemoryProcessedDonationStore : IProcessedDonationStore
{
    private readonly Dictionary<string, ProcessedDonationRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessedDonationRecord> _commands = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProcessedDonationRecord?> FindAsync(string donationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _records.TryGetValue(donationId, out var record) ? record : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProcessedDonationRecord?> FindCommandAsync(
        string donationId,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _commands.TryGetValue(CommandKey(donationId, commandId), out var record) ? record : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(ProcessedDonationRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _records[record.DonationId] = record;
            if (!string.IsNullOrWhiteSpace(record.CommandId))
                _commands[CommandKey(record.DonationId, record.CommandId)] = record;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CommandKey(string donationId, string commandId) => donationId + "\0" + commandId;
}

public sealed class InMemoryHistorySink : IEventHistorySink
{
    private readonly List<EventHistoryEntry> _entries = [];
    public IReadOnlyList<EventHistoryEntry> Entries => _entries;

    public Task AppendAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Add(entry);
        return Task.CompletedTask;
    }
}

public sealed class PendingDispatch
{
    public required DonationEvent Donation { get; init; }
    public required RuleMatch Match { get; init; }
    public required GameCommand Command { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public QueueMode QueueMode { get; init; }
    public string SequenceId { get; init; } = string.Empty;
    public int SequenceIndex { get; init; }
    public string GroupBatchId { get; init; } = string.Empty;
    public int GroupDelaySeconds { get; init; }
    public DateTimeOffset? GroupLastSentAt { get; set; }
    public DateTimeOffset? SpawnPreparationRetryAt { get; init; }
    public DateTimeOffset? SpawnPreparationReservedAt { get; set; }
    public DateTimeOffset? NeedsRetryAt { get; init; }
    public DateTimeOffset? VehicleRetryAt { get; init; }
    internal DispatchClockSnapshot? PreviousClocks { get; set; }
    public bool ImmediateRequested { get; set; }
}

public sealed class PendingDispatchSnapshot
{
    public DonationEvent Donation { get; set; } = new();
    public ActionDefinition Action { get; set; } = new();
    public TriggerRule Trigger { get; set; } = new();
    public ActionDefinition? SourceAction { get; set; }
    public string? HistoryActionId { get; set; }
    public GameCommand Command { get; set; } = new();
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public QueueMode QueueMode { get; set; }
    public bool InFlight { get; set; }
    public string SequenceId { get; set; } = string.Empty;
    public int SequenceIndex { get; set; }
    public string GroupBatchId { get; set; } = string.Empty;
    public int GroupDelaySeconds { get; set; }
    public DateTimeOffset? GroupLastSentAt { get; set; }
    public DateTimeOffset? SpawnPreparationRetryAt { get; set; }
    public DateTimeOffset? SpawnPreparationReservedAt { get; set; }
    public DateTimeOffset? NeedsRetryAt { get; set; }
    public DateTimeOffset? VehicleRetryAt { get; set; }
    public bool ImmediateRequested { get; set; }
}

internal sealed record DispatchClockSnapshot(DateTimeOffset Global, DateTimeOffset? Group,
    DateTimeOffset? Action, DateTimeOffset? SourceAction);

public sealed record QueueTimingSnapshot(
    int WaitingCount,
    bool AwaitingResult,
    bool WaitingForGame,
    int SecondsUntilNext);

public sealed record QueueCancellationSummary(
    IReadOnlyList<EventHistoryEntry> CancelledEntries,
    int SuppressedInFlightRetries)
{
    public int AffectedCount => CancelledEntries.Count + SuppressedInFlightRetries;
}

public sealed class DonationDispatcher
{
    private const string DefaultAnonymousDonorName = "Анонимный донатчик";
    public const string RandomActiveHandlerId = "random_active";
    public const string RandomAllPresetsHandlerId = "random_all_presets";
    private const string NamedCompanionHandlerId = "spawn_named_companion";
    private const string MutantCompanionHandlerId = "spawn_mutant_companion";
    private const string ChangePlayerNameHandlerId = "change_player_name";
    private const string RenameNearestNpcHandlerId = "hardcoon.adb.rename_nearest_npc";
    private const string AnnoyingFanHandlerId = "hardcoon.adb.annoying_fan";
    public const string PdaMessageHandlerId = "hardcoon.adb.pda_message";
    private readonly IProcessedDonationStore _processed;
    private readonly IEventHistorySink _history;
    private readonly IPendingDispatchStore? _pendingStore;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<int, int> _randomIndex;
    private readonly Queue<PendingDispatch> _queue = [];
    private readonly ConcurrentDictionary<string, byte> _queuedDonationIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queuedCommandIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingDispatch> _sentCommands = new(StringComparer.Ordinal);
    private readonly HashSet<string> _suppressDeferredRetryCommandIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastActionExecution = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastGlobalExecution = DateTimeOffset.MinValue;
    private string _anonymousDonorName = DefaultAnonymousDonorName;

    public DonationDispatcher(
        IProcessedDonationStore processed,
        IEventHistorySink history,
        Func<DateTimeOffset>? utcNow = null,
        Func<int, int>? randomIndex = null,
        IPendingDispatchStore? pendingStore = null)
    {
        _processed = processed;
        _history = history;
        _pendingStore = pendingStore;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _randomIndex = randomIndex ?? Random.Shared.Next;
    }

    public int Count => _queue.Count;
    public TimeSpan GlobalCooldown { get; set; } = TimeSpan.FromSeconds(5);
    public string AnonymousDonorName
    {
        get => _anonymousDonorName;
        set => _anonymousDonorName = NormalizeDisplayText(value, DefaultAnonymousDonorName, 64);
    }
    public bool IsQueued(string donationId) =>
        !string.IsNullOrWhiteSpace(donationId) && _queuedDonationIds.ContainsKey(donationId);
    public bool IsQueued(string donationId, string commandId) =>
        !string.IsNullOrWhiteSpace(donationId) && !string.IsNullOrWhiteSpace(commandId) &&
        _queuedDonationIds.ContainsKey(donationId) && _queuedCommandIds.ContainsKey(commandId);

    public async Task<int> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingStore is null) return 0;
        var snapshots = await _pendingStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var restored = 0;
            foreach (var snapshot in snapshots)
            {
                if (string.IsNullOrWhiteSpace(snapshot.Donation.Id) ||
                    string.IsNullOrWhiteSpace(snapshot.Command.CommandId) ||
                    !string.Equals(snapshot.Donation.Id, snapshot.Command.DonationId, StringComparison.Ordinal))
                    continue;
                var ledger = await _processed.FindCommandAsync(
                    snapshot.Donation.Id, snapshot.Command.CommandId, cancellationToken).ConfigureAwait(false);
                if (snapshot.InFlight)
                {
                    if (ledger is { State: DispatchState.Sent })
                    {
                        await RecordTerminalAsync(snapshot.Donation, new RuleMatch
                        {
                            Action = snapshot.Action,
                            Trigger = snapshot.Trigger,
                            SourceAction = snapshot.SourceAction,
                            HistoryActionId = snapshot.HistoryActionId
                        }, DispatchState.Uncertain,
                            "Приложение было перезапущено после начала отправки; автоматический повтор запрещён",
                            cancellationToken, snapshot.Command.CommandId,
                            canRetryManually: snapshot.SourceAction?.HandlerId != RandomAllPresetsHandlerId)
                            .ConfigureAwait(false);
                        continue;
                    }
                    // A Deferred result is recorded as Queued before the
                    // snapshot is flipped from in-flight to waiting.  If the
                    // process stops between those durable writes, the queued
                    // ledger proves the game answered without executing it and
                    // the command is safe to restore.
                    if (ledger is not { State: DispatchState.Queued }) continue;
                }
                if (ledger is not { State: DispatchState.Queued }) continue;
                if (snapshot.Action.Validate().Count > 0) continue;
                snapshot.Command.SourceHandlerId = snapshot.SourceAction?.HandlerId ?? snapshot.Command.SourceHandlerId;
                var pending = new PendingDispatch
                {
                    Donation = snapshot.Donation,
                    Match = new RuleMatch
                    {
                        Action = snapshot.Action,
                        Trigger = snapshot.Trigger,
                        SourceAction = snapshot.SourceAction,
                        HistoryActionId = snapshot.HistoryActionId
                    },
                    Command = snapshot.Command,
                    SequenceId = snapshot.SequenceId ?? string.Empty,
                    SequenceIndex = snapshot.SequenceIndex,
                    GroupBatchId = snapshot.GroupBatchId ?? string.Empty,
                    GroupDelaySeconds = Math.Clamp(snapshot.GroupDelaySeconds, 0, 86_400),
                    GroupLastSentAt = snapshot.GroupLastSentAt,
                    SpawnPreparationRetryAt = IsDirectSpawnCommand(snapshot.Command) ? snapshot.SpawnPreparationRetryAt : null,
                    SpawnPreparationReservedAt = IsDirectSpawnCommand(snapshot.Command) ? snapshot.SpawnPreparationReservedAt : null,
                    NeedsRetryAt = snapshot.Command.ActionId == "change_needs" ? snapshot.NeedsRetryAt : null,
                    VehicleRetryAt = snapshot.VehicleRetryAt,
                    ImmediateRequested = snapshot.ImmediateRequested,
                    EnqueuedAt = snapshot.EnqueuedAt,
                    // Queue delivery is guaranteed in schema 5.  Normalize old
                    // Skip/TTL snapshots while restoring so an application
                    // restart cannot discard a donation that was already queued.
                    ExpiresAt = null,
                    QueueMode = QueueMode.WaitIndefinitely
                };
                _queue.Enqueue(pending);
                _queuedDonationIds.TryAdd(pending.Donation.Id, 0);
                _queuedCommandIds.TryAdd(pending.Command.CommandId, 0);
                restored++;
            }
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return restored;
        }
        finally { _gate.Release(); }
    }

    public async Task<QueueTimingSnapshot> GetTimingAsync(
        bool gameConnected,
        bool gameReady,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sentCommands.Count > 0)
                return new QueueTimingSnapshot(_queue.Count, true, false, 0);
            if (!_queue.TryPeek(out var pending))
                return new QueueTimingSnapshot(0, false, false, 0);
            if (!gameConnected || !gameReady)
                return new QueueTimingSnapshot(_queue.Count, false, true, 0);
            var now = _utcNow();
            // A cooling-down action no longer blocks unrelated actions behind
            // it.  Show the earliest runnable queue item in the shared timer.
            var next = DateTimeOffset.MaxValue;
            foreach (var candidate in _queue)
            {
                if (!candidate.ImmediateRequested && HasEarlierSequenceStep(candidate)) continue;
                var candidateNext = Max(now, DispatchCooldownEnd(candidate));
                if (!candidate.ImmediateRequested && _lastActionExecution.TryGetValue(candidate.Match.Action.Id, out var lastAction) &&
                    !IsOwnPreparationReservation(candidate, lastAction))
                    candidateNext = Max(candidateNext, lastAction + TimeSpan.FromSeconds(
                        Math.Clamp(candidate.Match.Action.CooldownSeconds, 0, 86_400)));
                if (!candidate.ImmediateRequested && candidate.Match.SourceAction is { } source &&
                    _lastActionExecution.TryGetValue(source.Id, out var lastSource) &&
                    !IsOwnPreparationReservation(candidate, lastSource))
                    candidateNext = Max(candidateNext, lastSource + TimeSpan.FromSeconds(
                        Math.Clamp(source.CooldownSeconds, 0, 86_400)));
                next = Min(next, candidateNext);
            }
            var seconds = next == DateTimeOffset.MaxValue
                ? 0
                : Math.Max(0, (int)Math.Ceiling((next - now).TotalSeconds));
            return new QueueTimingSnapshot(_queue.Count, false, false, seconds);
        }
        finally { _gate.Release(); }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    public async Task<DispatchState> EnqueueAsync(
        DonationEvent donation,
        IEnumerable<ActionDefinition> actions,
        QueuePolicy policy,
        CancellationToken cancellationToken = default,
        IEnumerable<ActionDefinition>? availablePresets = null,
        EffectExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(donation);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(policy);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _processed.FindAsync(donation.Id, cancellationToken).ConfigureAwait(false);
            if ((existing is not null && existing.State != DispatchState.Queued) || _queuedDonationIds.ContainsKey(donation.Id))
            {
                await AppendAsync(donation, null, DispatchState.Duplicate, "Событие уже обработано", cancellationToken).ConfigureAwait(false);
                return DispatchState.Duplicate;
            }

            var availableActions = actions.ToList();
            var matches = RuleMatcher.MatchAll(donation, availableActions);
            if (matches.Count == 0)
            {
                await RecordTerminalAsync(donation, null, DispatchState.NoRule, "Подходящее правило не найдено", cancellationToken).ConfigureAwait(false);
                return DispatchState.NoRule;
            }

            var resolvedMatches = new List<RuleMatch>();
            foreach (var match in matches)
            {
                var resolved = ResolveBridgeMetaAction(match, availableActions, availablePresets);
                if (resolved is null)
                {
                    await AppendAsync(donation, match, DispatchState.Rejected,
                        MetaActionUnavailableDetail(match.Action), cancellationToken,
                        canRetryManually: CanRetryUnavailableMetaActionManually(match.Action)).ConfigureAwait(false);
                    continue;
                }
                var errors = resolved.Action.Validate();
                if (errors.Count > 0)
                {
                    await AppendAsync(donation, resolved, DispatchState.Rejected,
                        "Настройки действия недопустимы: " + string.Join("; ", errors),
                        cancellationToken, canRetryManually: true).ConfigureAwait(false);
                    continue;
                }
                resolvedMatches.Add(resolved);
            }
            if (resolvedMatches.Count == 0)
            {
                await _processed.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = donation.Id,
                    State = DispatchState.Rejected,
                    RecordedAt = _utcNow()
                }, cancellationToken).ConfigureAwait(false);
                return DispatchState.Rejected;
            }
            executionOptions ??= new EffectExecutionOptions();
            // Each donation gets its own group clock, independently of strict ordering.
            var groups = resolvedMatches.GroupBy(match => ActionPriceOrdering.GroupKey(match.SourceAction ?? match.Action))
                .Where(group => group.Key is not null && group.Count() > 1)
                .ToDictionary(group => group.Key!, group => (
                    Id: Guid.NewGuid().ToString("N"),
                    Delay: ActionPriceOrdering.ResolveGroupDelay(group.Select(match => match.SourceAction ?? match.Action)),
                    Strict: group.Any(match => (match.SourceAction ?? match.Action).StrictSequence)), StringComparer.Ordinal);
            for (var index = 0; index < resolvedMatches.Count; index++)
            {
                var source = resolvedMatches[index].SourceAction ?? resolvedMatches[index].Action;
                var key = ActionPriceOrdering.GroupKey(source);
                var group = key is not null && groups.TryGetValue(key, out var found) ? found : default;
                await EnqueueMatchUnderLockAsync(
                    donation,
                    resolvedMatches[index],
                    policy,
                    cancellationToken,
                    executionOptions,
                    index + 1,
                    resolvedMatches.Count, group.Strict ? group.Id : string.Empty,
                    group.Id ?? string.Empty, group.Delay).ConfigureAwait(false);
            }
            return DispatchState.Queued;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DispatchState> EnqueueActionAsync(
        DonationEvent syntheticEvent,
        ActionDefinition action,
        QueuePolicy policy,
        CancellationToken cancellationToken = default,
        IEnumerable<ActionDefinition>? availableActions = null,
        IEnumerable<ActionDefinition>? availablePresets = null,
        EffectExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(syntheticEvent);
        ArgumentNullException.ThrowIfNull(action);
        var trigger = action.Triggers.FirstOrDefault(rule => rule.Enabled) ?? new TriggerRule
        {
            Comparator = TriggerComparator.Exact,
            Amount = syntheticEvent.Amount,
            Currency = syntheticEvent.Currency
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _processed.FindAsync(syntheticEvent.Id, cancellationToken).ConfigureAwait(false);
            if ((existing is not null && existing.State != DispatchState.Queued) || _queuedDonationIds.ContainsKey(syntheticEvent.Id))
            {
                return DispatchState.Duplicate;
            }
            var match = new RuleMatch { Action = action, Trigger = trigger };
            var resolvedMatch = ResolveBridgeMetaAction(
                match,
                availableActions ?? [action],
                availablePresets);
            if (resolvedMatch is null)
            {
                await RecordTerminalAsync(
                    syntheticEvent,
                    match,
                    DispatchState.Rejected,
                    MetaActionUnavailableDetail(match.Action),
                    cancellationToken,
                    canRetryManually: CanRetryUnavailableMetaActionManually(match.Action)).ConfigureAwait(false);
                return DispatchState.Rejected;
            }
            return await EnqueueMatchUnderLockAsync(
                syntheticEvent,
                resolvedMatch,
                policy,
                cancellationToken,
                executionOptions ?? new EffectExecutionOptions(),
                1,
                1).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RuleMatch? ResolveBridgeMetaAction(
        RuleMatch match,
        IEnumerable<ActionDefinition> availableActions,
        IEnumerable<ActionDefinition>? availablePresets)
    {
        if (IsBridgeMetaHandler(match.Action.HandlerId))
        {
            var selectedOnly = match.Action.HandlerId == ActiveTemplateSelection.HandlerId;
            if (selectedOnly && (!match.Action.ParameterDefinitions.Any(ActiveTemplateSelection.IsParameter) ||
                                 match.Action.Validate().Count != 0)) return null;
            var selected = selectedOnly ? ActiveTemplateSelection.SelectedIds(match.Action) : null;
            var now = _utcNow();
            var candidates = availableActions
                .Where(action => action.IsActive)
                .Where(action => !IsBridgeMetaHandler(action.HandlerId))
                .Where(action => action.Id != match.Action.Id && (selected is null || selected.Contains(action.Id)))
                .DistinctBy(action => action.Id, StringComparer.Ordinal)
                .Where(action => !CatalogRevisionEnforcement.RequiresCatalog(action) ||
                    action.CatalogRevision is not null)
                .Where(action => action.Validate().Count == 0)
                .Where(action => !_lastActionExecution.TryGetValue(action.Id, out var lastExecution) ||
                    now - lastExecution >= TimeSpan.FromSeconds(Math.Clamp(action.CooldownSeconds, 0, 86_400)))
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            return new RuleMatch
            {
                Action = candidates[NextIndex(candidates.Count)].Clone(),
                Trigger = match.Trigger,
                SourceAction = match.Action.Clone(),
                HistoryActionId = selectedOnly ? match.Action.Id : null
            };
        }

        return match;
    }

    private int NextIndex(int count)
    {
        if (count <= 1)
        {
            return 0;
        }
        return Math.Clamp(_randomIndex(count), 0, count - 1);
    }

    private static bool IsBridgeMetaHandler(string handlerId) =>
        string.Equals(handlerId, RandomActiveHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal);

    private static string MetaActionUnavailableDetail(ActionDefinition action) =>
        string.Equals(action.HandlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal)
            ? "Нет доступных выбранных активных шаблонов. Проверьте выбор, текущий профиль, настройки и паузы."
            : "Для шаблона «Рандомный по активным шаблонам» нет доступных активных действий";

    private static bool CanRetryUnavailableMetaActionManually(ActionDefinition action) =>
        !string.Equals(action.HandlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal);

    private static bool IsRandomAllResolution(RuleMatch match) =>
        string.Equals(match.SourceAction?.HandlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal);

    private async Task<DispatchState> EnqueueMatchUnderLockAsync(
        DonationEvent donation,
        RuleMatch match,
        QueuePolicy policy,
        CancellationToken cancellationToken,
        EffectExecutionOptions executionOptions,
        int batchIndex,
        int batchSize,
        string sequenceId = "",
        string groupBatchId = "",
        int groupDelaySeconds = 0)
    {
            var actionErrors = match.Action.Validate();
            if (actionErrors.Count > 0)
            {
                await RecordTerminalAsync(
                    donation,
                    match,
                    DispatchState.Rejected,
                    "Настройки действия недопустимы: " + string.Join("; ", actionErrors),
                    cancellationToken,
                    canRetryManually: true).ConfigureAwait(false);
                return DispatchState.Rejected;
            }

            var now = _utcNow();
            var parameters = new Dictionary<string, string>(
                match.Action.EffectiveParameters(), StringComparer.OrdinalIgnoreCase);
            if (UsesDonorName(match.Action.HandlerId))
                parameters["donor_name"] = NormalizeDonorName(donation.Username);
            if (UsesDonationMessage(match.Action.HandlerId))
                parameters["donation_message"] = NormalizeDonationMessage(donation.Message);
            var command = new GameCommand
            {
                DonationId = donation.Id,
                ActionId = match.Action.HandlerId,
                SourceHandlerId = match.SourceAction?.HandlerId ?? string.Empty,
                AmountMinor = donation.AmountMinor,
                Currency = Money.NormalizeCurrency(donation.Currency),
                Parameters = parameters,
                CatalogRevision = match.Action.CatalogRevision,
                DonorName = NormalizeDisplayText(donation.Username, _anonymousDonorName, 96),
                ActionName = NormalizeDisplayText(match.Action.DisplayName, match.Action.HandlerId, 160),
                BatchIndex = batchIndex,
                BatchSize = batchSize,
                SaveBeforeBatch = executionOptions.SaveBeforeBatch,
                SaveAfterBatch = executionOptions.SaveAfterBatch,
                EffectSaveRetentionCount = executionOptions.EffectSaveRetentionCount,
                ShowGameEffectLog = executionOptions.ShowGameEffectLog,
                // This template sends the actual message itself, even if generic
                // effect announcements are disabled. Do not announce it twice.
                ShowPdaEffectMessage = executionOptions.ShowPdaEffectMessage &&
                    match.Action.HandlerId != PdaMessageHandlerId,
                CreatedAt = now
            };

            _queue.Enqueue(new PendingDispatch
            {
                Donation = donation,
                Match = match,
                Command = command,
                SequenceId = sequenceId,
                SequenceIndex = batchIndex,
                GroupBatchId = groupBatchId,
                GroupDelaySeconds = groupDelaySeconds,
                EnqueuedAt = now,
                ExpiresAt = null,
                QueueMode = QueueMode.WaitIndefinitely
            });
            _queuedDonationIds.TryAdd(donation.Id, 0);
            _queuedCommandIds.TryAdd(command.CommandId, 0);
            await _processed.RecordAsync(new ProcessedDonationRecord
            {
                DonationId = donation.Id,
                CommandId = command.CommandId,
                State = DispatchState.Queued,
                RecordedAt = now
            }, cancellationToken).ConfigureAwait(false);
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            await AppendAsync(donation, match, DispatchState.Queued, "Добавлено в очередь", cancellationToken, command.CommandId).ConfigureAwait(false);
            return DispatchState.Queued;
    }

    private string NormalizeDonorName(string? value) =>
        NormalizeDisplayText(value, _anonymousDonorName, 64);

    public static bool UsesDonorName(string handlerId) =>
        string.Equals(handlerId, NamedCompanionHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, "hardcoon.adb.special_companion", StringComparison.Ordinal) ||
        string.Equals(handlerId, MutantCompanionHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, "hardcoon.adb.mutant_companion_v2", StringComparison.Ordinal) ||
        string.Equals(handlerId, "hardcoon.adb.helicopter_boss", StringComparison.Ordinal) ||
        string.Equals(handlerId, "spawn_donor_boss", StringComparison.Ordinal) ||
        string.Equals(handlerId, ChangePlayerNameHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, RenameNearestNpcHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, AnnoyingFanHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, PdaMessageHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, "hardcoon.adb.wandering_trader", StringComparison.Ordinal) ||
        string.Equals(handlerId, "hardcoon.adb.donor_thief", StringComparison.Ordinal) ||
        string.Equals(handlerId, "spawn_npcs", StringComparison.Ordinal) ||
        string.Equals(handlerId, "spawn_random_npcs", StringComparison.Ordinal);

    public static bool UsesDonationMessage(string handlerId) =>
        string.Equals(handlerId, AnnoyingFanHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, PdaMessageHandlerId, StringComparison.Ordinal);

    private static string NormalizeDonationMessage(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        normalized = string.Join(' ', normalized
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= 512) return normalized;
        var length = 512;
        if (char.IsHighSurrogate(normalized[length - 1])) length--;
        return normalized[..length].TrimEnd();
    }

    private static string NormalizeDisplayText(string? value, string fallback, int maximumLength)
    {
        var normalized = new string((value ?? string.Empty)
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        normalized = string.Join(' ', normalized
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) normalized = fallback;
        if (normalized.Length <= maximumLength) return normalized;
        var length = maximumLength;
        if (char.IsHighSurrogate(normalized[length - 1])) length--;
        return normalized[..length].TrimEnd();
    }

    // Mark the original waiting commands, never construct replays from history.
    // Queue order among selected commands is retained. This explicit manual
    // override may select a later scenario step without selecting its siblings.
    public async Task<int> RequestImmediateAsync(
        IEnumerable<string> commandIds, CancellationToken cancellationToken = default)
    {
        var ids = commandIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = _queue.ToArray();
            var count = 0;
            foreach (var entry in entries.Where(entry => ids.Contains(entry.Command.CommandId)))
            {
                entry.ImmediateRequested = true;
                count++;
            }
            if (count == 0) return 0;
            PrioritizeImmediateUnderLock();
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return count;
        }
        finally { _gate.Release(); }
    }

    private bool PrioritizeImmediateUnderLock()
    {
        if (_queue.Count < 2 || !_queue.Any(entry => entry.ImmediateRequested)) return false;
        var before = _queue.ToArray();
        var after = before.OrderByDescending(entry => entry.ImmediateRequested).ToArray();
        if (before.SequenceEqual(after)) return false;
        _queue.Clear();
        foreach (var entry in after) _queue.Enqueue(entry);
        return true;
    }

    public async Task<EventHistoryEntry?> CancelQueuedAsync(
        string donationId,
        CancellationToken cancellationToken = default)
        => await CancelQueuedUnderLockAsync(donationId, null, cancellationToken).ConfigureAwait(false);

    public async Task<EventHistoryEntry?> CancelQueuedAsync(
        EventHistoryEntry queuedEntry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queuedEntry);
        if (queuedEntry.State != DispatchState.Queued)
        {
            return null;
        }

        return await CancelQueuedUnderLockAsync(
            queuedEntry.DonationId,
            queuedEntry,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueueCancellationSummary> CancelAllQueuedAsync(
        IEnumerable<EventHistoryEntry>? persistedEntries = null,
        CancellationToken cancellationToken = default)
    {
        var persisted = (persistedEntries ?? [])
            .Where(entry => entry.State == DispatchState.Queued &&
                !string.IsNullOrWhiteSpace(entry.DonationId) &&
                !string.IsNullOrWhiteSpace(entry.CommandId))
            .GroupBy(entry => entry.CommandId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cancelledEntries = new List<EventHistoryEntry>();
            var cancelledIds = new HashSet<string>(StringComparer.Ordinal);
            var suppressedInFlightRetries = 0;
            foreach (var commandId in _sentCommands.Keys)
            {
                if (_suppressDeferredRetryCommandIds.Add(commandId))
                {
                    suppressedInFlightRetries++;
                }
            }
            while (_queue.TryDequeue(out var pending))
            {
                await _processed.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = pending.Donation.Id,
                    CommandId = pending.Command.CommandId,
                    State = DispatchState.Cancelled,
                    RecordedAt = _utcNow()
                }, cancellationToken).ConfigureAwait(false);
                _queuedCommandIds.TryRemove(pending.Command.CommandId, out _);
                ReleaseDonationIfFinished(pending.Donation.Id);
                cancelledIds.Add(pending.Command.CommandId);
                cancelledEntries.Add(await AppendAsync(pending.Donation, pending.Match,
                    DispatchState.Cancelled, "Очередь очищена пользователем", cancellationToken,
                    pending.Command.CommandId, canRetryManually: true).ConfigureAwait(false));
            }
            foreach (var entry in persisted.Where(entry => !cancelledIds.Contains(entry.CommandId)))
            {
                var cancelled = await CancelPersistedQueuedUnderLockAsync(entry, cancellationToken).ConfigureAwait(false);
                if (cancelled is not null) cancelledEntries.Add(cancelled);
            }
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return new QueueCancellationSummary(cancelledEntries, suppressedInFlightRetries);
        }
        finally { _gate.Release(); }
    }

    private async Task<EventHistoryEntry?> CancelQueuedUnderLockAsync(
        string donationId,
        EventHistoryEntry? persistedEntry,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(donationId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cancelled = _queue.FirstOrDefault(pending =>
                string.Equals(pending.Donation.Id, donationId, StringComparison.Ordinal) &&
                (persistedEntry is null || string.Equals(pending.Command.CommandId, persistedEntry.CommandId, StringComparison.Ordinal)));
            if (cancelled is null)
            {
                return persistedEntry is null
                    ? null
                    : await CancelPersistedQueuedUnderLockAsync(persistedEntry, cancellationToken).ConfigureAwait(false);
            }

            // Make the terminal ledger durable before mutating the in-memory
            // queue. A crash at any later point cannot resurrect the donation.
            await _processed.RecordAsync(new ProcessedDonationRecord
            {
                DonationId = cancelled.Donation.Id,
                CommandId = cancelled.Command.CommandId,
                State = DispatchState.Cancelled,
                RecordedAt = _utcNow()
            }, cancellationToken).ConfigureAwait(false);

            var queuedCount = _queue.Count;
            for (var index = 0; index < queuedCount; index++)
            {
                var pending = _queue.Dequeue();
                if (ReferenceEquals(pending, cancelled))
                {
                    continue;
                }

                _queue.Enqueue(pending);
            }

            _queuedCommandIds.TryRemove(cancelled.Command.CommandId, out _);
            ReleaseDonationIfFinished(cancelled.Donation.Id);
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return await AppendAsync(
                cancelled.Donation,
                cancelled.Match,
                DispatchState.Cancelled,
                "Удалено из очереди пользователем",
                cancellationToken,
                cancelled.Command.CommandId,
                canRetryManually: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EventHistoryEntry?> CancelPersistedQueuedUnderLockAsync(
        EventHistoryEntry queuedEntry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queuedEntry.DonationId) ||
            string.IsNullOrWhiteSpace(queuedEntry.CommandId))
        {
            return null;
        }

        var current = await _processed.FindCommandAsync(
            queuedEntry.DonationId, queuedEntry.CommandId, cancellationToken).ConfigureAwait(false);
        if (current is not { State: DispatchState.Queued } ||
            string.IsNullOrWhiteSpace(current.CommandId) ||
            !string.Equals(current.CommandId, queuedEntry.CommandId, StringComparison.Ordinal))
        {
            return null;
        }

        // A restored queue row is not reconstructed as an executable command:
        // history does not contain a trustworthy snapshot of all action settings.
        // Persisting the terminal state under the dispatcher gate also prevents a
        // concurrent DonationAlerts recovery from enqueueing the same event later.
        await _processed.RecordAsync(new ProcessedDonationRecord
        {
            DonationId = queuedEntry.DonationId,
            CommandId = current.CommandId,
            State = DispatchState.Cancelled,
            RecordedAt = _utcNow()
        }, cancellationToken).ConfigureAwait(false);

        var cancelled = new EventHistoryEntry
        {
            Timestamp = _utcNow(),
            DonationCreatedAt = queuedEntry.DonationCreatedAt,
            DonationId = queuedEntry.DonationId,
            CommandId = current.CommandId,
            Donor = queuedEntry.Donor,
            Amount = queuedEntry.Amount,
            Currency = queuedEntry.Currency,
            ActionId = queuedEntry.ActionId,
            HandlerId = queuedEntry.HandlerId,
            ActionName = queuedEntry.ActionName,
            ActionSnapshot = queuedEntry.ActionSnapshot?.Clone(),
            State = DispatchState.Cancelled,
            Detail = "Удалено из очереди пользователем",
            CanRetryManually = true
        };
        await _history.AppendAsync(cancelled, cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    private bool HasEarlierSequenceStep(PendingDispatch candidate) => candidate.SequenceId.Length > 0 &&
        _queue.Any(other => other.SequenceId == candidate.SequenceId && other.SequenceIndex < candidate.SequenceIndex);

    private static bool IsDirectSpawnCommand(GameCommand command) => command.ActionId is
        "spawn_mutants" or "spawn_npcs" or "spawn_random_mutants" or "spawn_random_npcs" or
        "hardcoon.adb.wandering_trader";

    private static bool IsSpawnPreparationResult(GameCommand command, string reason) =>
        IsDirectSpawnCommand(command) && reason is
            "spawn_point_search_running" or "spawn_point_search_busy" or "spawn_point_actor_moved" or
            "trader_preparing" or "trader_waiting_spawn_point" or "trader_catalog_preparing";

    private static bool IsSpawnPreparationRetry(PendingDispatch pending) =>
        IsDirectSpawnCommand(pending.Command) && pending.SpawnPreparationRetryAt is not null;

    private static bool IsNeedsWaitingResult(GameCommand command, string reason) =>
        command.ActionId == "change_needs" && reason is
            "needs_waiting_food" or "needs_waiting_water" or "needs_waiting_food_and_water";

    private static bool IsOwnPreparationReservation(PendingDispatch pending, DateTimeOffset last) =>
        IsSpawnPreparationRetry(pending) && pending.SpawnPreparationReservedAt == last;

    private DateTimeOffset DispatchCooldownEnd(PendingDispatch pending)
    {
        if (pending.ImmediateRequested)
            return pending.VehicleRetryAt ?? pending.NeedsRetryAt ?? pending.SpawnPreparationRetryAt ?? DateTimeOffset.MinValue;
        var grouped = pending.GroupBatchId.Length > 0 && pending.GroupLastSentAt is not null;
        var last = grouped ? pending.GroupLastSentAt!.Value : _lastGlobalExecution;
        var end = last + (grouped ? TimeSpan.FromSeconds(pending.GroupDelaySeconds) : GlobalCooldown);
        if (pending.VehicleRetryAt is { } vehicleRetry) return Max(vehicleRetry, end);
        if (pending.NeedsRetryAt is { } needsRetry) return Max(needsRetry, end);
        if (!IsSpawnPreparationRetry(pending)) return end;
        // Ignore only this command's own preparation reservation. An unrelated
        // effect that ran during a long search still imposes its normal pause.
        return IsOwnPreparationReservation(pending, last)
            ? pending.SpawnPreparationRetryAt!.Value
            : Max(pending.SpawnPreparationRetryAt!.Value, end);
    }

    private void UpdateDispatchClocks(PendingDispatch pending, DateTimeOffset now)
    {
        _lastGlobalExecution = now;
        _lastActionExecution[pending.Match.Action.Id] = now;
        if (pending.Match.SourceAction is { } source) _lastActionExecution[source.Id] = now;
        if (pending.GroupBatchId.Length == 0) return;
        pending.GroupLastSentAt = now;
        foreach (var sibling in _queue.Where(item => item.GroupBatchId == pending.GroupBatchId))
            sibling.GroupLastSentAt = now;
    }

    private void RestoreDispatchClocks(PendingDispatch pending)
    {
        if (pending.PreviousClocks is not { } previous) return;
        _lastGlobalExecution = previous.Global;
        RestoreActionClock(pending.Match.Action.Id, previous.Action);
        if (pending.Match.SourceAction is { } source)
            RestoreActionClock(source.Id, previous.SourceAction);
        pending.GroupLastSentAt = previous.Group;
        if (pending.GroupBatchId.Length > 0)
            foreach (var sibling in _queue.Where(item => item.GroupBatchId == pending.GroupBatchId))
                sibling.GroupLastSentAt = previous.Group;
    }

    private void RestoreActionClock(string id, DateTimeOffset? clock)
    {
        if (clock is { } value) _lastActionExecution[id] = value;
        else _lastActionExecution.Remove(id);
    }

    public async Task<IReadOnlyList<EventHistoryEntry>> DrainAsync(
        IGameCommandTransport transport,
        QueuePolicy policy,
        CancellationToken cancellationToken = default)
    {
        _ = policy; // Kept in the public API for settings-file compatibility.
        var emitted = new List<EventHistoryEntry>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sentCommands.Count > 0)
            {
                return emitted;
            }
            var candidatesRemaining = _queue.Count;
            _queue.TryPeek(out var originalHead);
            // A readiness race or read-only preparation may have requeued an
            // explicitly selected command at the tail. Restore its priority on
            // the next pass without letting it overlap an in-flight command.
            var queueOrderChanged = PrioritizeImmediateUnderLock();
            while (candidatesRemaining-- > 0 && _queue.TryPeek(out var pending))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _utcNow();
                if (!transport.IsConnected || !transport.IsGameReady)
                {
                    break;
                }

                if (!pending.ImmediateRequested && HasEarlierSequenceStep(pending))
                {
                    _queue.Enqueue(_queue.Dequeue());
                    queueOrderChanged = true;
                    continue;
                }

                if (!transport.CanAttempt(pending.Command) || now < DispatchCooldownEnd(pending))
                {
                    // A group continuation can bypass the global pause, including
                    // when another donation is ahead of it in the queue.
                    _queue.Enqueue(_queue.Dequeue());
                    queueOrderChanged = true;
                    continue;
                }

                var actionCooldown = TimeSpan.FromSeconds(Math.Clamp(pending.Match.Action.CooldownSeconds, 0, 86_400));
                var sourceAction = pending.Match.SourceAction;
                var actionCooldownActive =
                    _lastActionExecution.TryGetValue(pending.Match.Action.Id, out var lastAction) &&
                    !IsOwnPreparationReservation(pending, lastAction) &&
                    now - lastAction < actionCooldown;
                var sourceCooldownActive = sourceAction is not null &&
                    _lastActionExecution.TryGetValue(sourceAction.Id, out var lastSourceAction) &&
                    !IsOwnPreparationReservation(pending, lastSourceAction) &&
                    now - lastSourceAction < TimeSpan.FromSeconds(Math.Clamp(sourceAction.CooldownSeconds, 0, 86_400));
                if (!pending.ImmediateRequested && (actionCooldownActive || sourceCooldownActive))
                {
                    // A long action-specific pause must not freeze unrelated
                    // effects. Rotate this item once and inspect the next one.
                    _queue.Enqueue(_queue.Dequeue());
                    queueOrderChanged = true;
                    continue;
                }

                if (transport is IGameCommandPreflight preflight)
                {
                    var validation = preflight.Prepare(pending.Command);
                    if (!validation.IsValid)
                    {
                        // Actor readiness may arrive before the catalog after
                        // reconnect/load. Nothing was sent: keep the exact queue
                        // entry, ledger and order until the handshake completes.
                        if (validation.IsTransient) break;
                        _queue.Dequeue();
                        _queuedCommandIds.TryRemove(pending.Command.CommandId, out _);
                        ReleaseDonationIfFinished(pending.Donation.Id);
                        emitted.Add(await RecordTerminalAsync(
                            pending.Donation,
                            pending.Match,
                            DispatchState.Rejected,
                            validation.Reason,
                            cancellationToken,
                            pending.Command.CommandId,
                            canRetryManually: !IsRandomAllResolution(pending.Match)).ConfigureAwait(false));
                        await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                _queue.Dequeue();
                _queuedCommandIds.TryRemove(pending.Command.CommandId, out _);

                // Persist SENT before touching the pipe. A crash or broken ACK can never cause an automatic duplicate.
                await _processed.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = pending.Donation.Id,
                    CommandId = pending.Command.CommandId,
                    State = DispatchState.Sent,
                    RecordedAt = now
                }, cancellationToken).ConfigureAwait(false);

                try
                {
                    if (IsDirectSpawnCommand(pending.Command) && !IsSpawnPreparationRetry(pending))
                        pending.SpawnPreparationReservedAt = now;
                    _sentCommands[pending.Command.CommandId] = pending;
                    await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    await transport.SendAsync(pending.Command, cancellationToken).ConfigureAwait(false);
                    // Needs can decline in its read-only precondition. Start
                    // its gameplay pause only once the native result confirms
                    // execution (or an uncertain outcome), never on each poll.
                    if (!IsSpawnPreparationRetry(pending) && pending.Command.ActionId != "change_needs")
                    {
                        pending.PreviousClocks = new DispatchClockSnapshot(_lastGlobalExecution,
                            pending.GroupLastSentAt, _lastActionExecution.TryGetValue(pending.Match.Action.Id, out var priorAction)
                                ? priorAction : null,
                            pending.Match.SourceAction is { } source && _lastActionExecution.TryGetValue(source.Id, out var priorSource)
                                ? priorSource : null);
                        UpdateDispatchClocks(pending, now);
                        if (pending.GroupBatchId.Length > 0)
                            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    }
                    emitted.Add(await AppendAsync(pending.Donation, pending.Match, DispatchState.Sent, "Команда передана игре", cancellationToken, pending.Command.CommandId).ConfigureAwait(false));
                    break;
                }
                catch (GameNotReadyBeforeSendException exception)
                {
                    _sentCommands.Remove(pending.Command.CommandId);
                    // The readiness probe failed before a command was written,
                    // therefore it is safe to keep the same command at the tail.
                    _queue.Enqueue(new PendingDispatch
                    {
                        Donation = pending.Donation,
                        Match = pending.Match,
                        Command = pending.Command,
                        SequenceId = pending.SequenceId,
                        SequenceIndex = pending.SequenceIndex,
                        GroupBatchId = pending.GroupBatchId,
                        GroupDelaySeconds = pending.GroupDelaySeconds,
                        GroupLastSentAt = pending.GroupLastSentAt,
                        SpawnPreparationRetryAt = pending.SpawnPreparationRetryAt,
                        SpawnPreparationReservedAt = pending.SpawnPreparationReservedAt,
                        NeedsRetryAt = pending.NeedsRetryAt,
                        VehicleRetryAt = pending.VehicleRetryAt,
                        ImmediateRequested = pending.ImmediateRequested,
                        EnqueuedAt = pending.EnqueuedAt,
                        ExpiresAt = null,
                        QueueMode = QueueMode.WaitIndefinitely
                    });
                    _queuedDonationIds.TryAdd(pending.Donation.Id, 0);
                    _queuedCommandIds.TryAdd(pending.Command.CommandId, 0);
                    await _processed.RecordAsync(new ProcessedDonationRecord
                    {
                        DonationId = pending.Donation.Id,
                        CommandId = pending.Command.CommandId,
                        State = DispatchState.Queued,
                        RecordedAt = _utcNow()
                    }, cancellationToken).ConfigureAwait(false);
                    emitted.Add(await AppendAsync(
                        pending.Donation,
                        pending.Match,
                        DispatchState.Queued,
                        "Игра временно не готова; эффект сохранён в очереди: " + exception.Message,
                        cancellationToken,
                        pending.Command.CommandId).ConfigureAwait(false));
                    await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    if (pending.Command.ActionId == "change_needs") UpdateDispatchClocks(pending, _utcNow());
                    _sentCommands.Remove(pending.Command.CommandId);
                    ReleaseDonationIfFinished(pending.Donation.Id);
                    var detail = "Соединение потеряно после начала отправки; автоматический повтор запрещён";
                    await _processed.RecordAsync(new ProcessedDonationRecord
                    {
                        DonationId = pending.Donation.Id,
                        CommandId = pending.Command.CommandId,
                        State = DispatchState.Uncertain,
                        RecordedAt = _utcNow()
                    }, cancellationToken).ConfigureAwait(false);
                    await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    emitted.Add(await AppendAsync(
                        pending.Donation,
                        pending.Match,
                        DispatchState.Uncertain,
                        detail,
                        cancellationToken,
                        pending.Command.CommandId,
                        canRetryManually: !IsRandomAllResolution(pending.Match)).ConfigureAwait(false));
                }
            }
            if (queueOrderChanged && _queue.TryPeek(out var currentHead) && !ReferenceEquals(currentHead, originalHead))
            {
                await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        return emitted;
    }

    public async Task<EventHistoryEntry?> CompleteAsync(GameExecutionResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sentCommands.Remove(result.CommandId, out var pending))
            {
                return null;
            }

            var suppressDeferredRetry = _suppressDeferredRetryCommandIds.Remove(result.CommandId);
            var state = result.Status switch
            {
                GameResultStatus.Executed => DispatchState.Executed,
                GameResultStatus.Deferred when suppressDeferredRetry => DispatchState.Cancelled,
                GameResultStatus.Deferred => DispatchState.Queued,
                GameResultStatus.Rejected when result.Reason.StartsWith("safe_zone_blocked:", StringComparison.Ordinal) => DispatchState.Skipped,
                GameResultStatus.Rejected => DispatchState.Rejected,
                GameResultStatus.Uncertain => DispatchState.Uncertain,
                _ => DispatchState.Uncertain
            };
            var willRetry = result.Status == GameResultStatus.Deferred && !suppressDeferredRetry;
            // Navigation preparation has not spawned anything. Poll the same
            // command after a short bounded interval, without paying global,
            // template or group gameplay delays again for each search batch.
            var spawnPreparation = willRetry && IsSpawnPreparationResult(pending.Command, result.Reason);
            var needsWaiting = willRetry && IsNeedsWaitingResult(pending.Command, result.Reason);
            var vehicleWaiting = result.Status == GameResultStatus.Deferred && result.Reason is
                BtrPassengerCommands.WaitingReason or "btr_spawn_point_waiting" or "btr_spawn_anchor_waiting";
            if (vehicleWaiting) RestoreDispatchClocks(pending);
            if ((result.Status == GameResultStatus.Executed && IsSpawnPreparationRetry(pending)) ||
                (pending.Command.ActionId == "change_needs" && result.Status is GameResultStatus.Executed or GameResultStatus.Uncertain))
                UpdateDispatchClocks(pending, _utcNow());
            var randomAllResolution = IsRandomAllResolution(pending.Match);
            await _processed.RecordAsync(new ProcessedDonationRecord
            {
                DonationId = pending.Donation.Id,
                CommandId = result.CommandId,
                State = state,
                RecordedAt = _utcNow()
            }, cancellationToken).ConfigureAwait(false);
            var detail = suppressDeferredRetry && result.Status == GameResultStatus.Deferred
                ? "Очередь очищена пользователем; отложенный эффект не будет повторён" +
                    (string.IsNullOrWhiteSpace(result.Reason) ? string.Empty : $": {result.Reason}")
                : willRetry && result.Reason.StartsWith("safe_zone_waiting:", StringComparison.Ordinal)
                    ? "Безопасная зона: ожидание выхода с локации " + result.Reason["safe_zone_waiting:".Length..]
                : spawnPreparation
                    ? "Поиск места для спавна; повторная проверка без глобальной паузы: " + result.Reason
                : needsWaiting
                    ? "Ожидание достаточного запаса еды или воды; повторная проверка через 5 секунд."
                : willRetry && pending.Command.ActionId == "hardcoon.adb.donor_thief" && result.Reason == "donor_thief_waiting_ui"
                    ? "Данат-воришка ждёт закрытия игрового окна."
                    : willRetry && vehicleWaiting
                    ? result.Reason == BtrPassengerCommands.WaitingReason
                        ? "Эффект ждёт выхода из БТР."
                        : "Ожидание безопасной точки рядом с БТР."
                : willRetry
                    ? "Временно не выполнено; эффект перенесён в конец очереди" +
                        (string.IsNullOrWhiteSpace(result.Reason) ? string.Empty : $": {result.Reason}")
                    : state == DispatchState.Skipped && result.Reason.StartsWith("safe_zone_blocked:", StringComparison.Ordinal)
                        ? "Безопасная зона: эффект пропущен на локации " + result.Reason["safe_zone_blocked:".Length..]
                        : string.IsNullOrWhiteSpace(result.Reason) ? state.ToString() : result.Reason;
            var history = await AppendAsync(
                pending.Donation,
                pending.Match,
                state,
                detail,
                cancellationToken,
                result.CommandId,
                canRetryManually: !randomAllResolution && !willRetry &&
                    state is DispatchState.Deferred or DispatchState.Rejected or DispatchState.Uncertain or DispatchState.Skipped).ConfigureAwait(false);

            if (willRetry)
            {
                _queue.Enqueue(new PendingDispatch
                {
                    Donation = pending.Donation,
                    Match = pending.Match,
                    // The addon deliberately does not remember Deferred command
                    // ids, so reusing the id is safe and keeps one history row
                    // whose status changes in place.
                    Command = pending.Command,
                    SequenceId = pending.SequenceId,
                    SequenceIndex = pending.SequenceIndex,
                    GroupBatchId = pending.GroupBatchId,
                    GroupDelaySeconds = pending.GroupDelaySeconds,
                    GroupLastSentAt = pending.GroupLastSentAt,
                    SpawnPreparationRetryAt = spawnPreparation ? _utcNow() + TimeSpan.FromMilliseconds(250) : null,
                    SpawnPreparationReservedAt = spawnPreparation ? pending.SpawnPreparationReservedAt : null,
                    NeedsRetryAt = needsWaiting ? _utcNow() + TimeSpan.FromSeconds(5) : null,
                    VehicleRetryAt = vehicleWaiting ? _utcNow() + TimeSpan.FromSeconds(1) : null,
                    // Read-only navigation preparation belongs to the same
                    // attempt. Other native deferrals return to normal policy.
                    ImmediateRequested = (spawnPreparation || needsWaiting || vehicleWaiting) && pending.ImmediateRequested,
                    EnqueuedAt = pending.EnqueuedAt,
                    ExpiresAt = null,
                    QueueMode = QueueMode.WaitIndefinitely
                });
                _queuedDonationIds.TryAdd(pending.Donation.Id, 0);
                _queuedCommandIds.TryAdd(pending.Command.CommandId, 0);
                await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ReleaseDonationIfFinished(pending.Donation.Id);
                await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            }

            return history;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EventHistoryEntry> RecordTerminalAsync(
        DonationEvent donation,
        RuleMatch? match,
        DispatchState state,
        string detail,
        CancellationToken cancellationToken,
        string commandId = "",
        bool? canRetryManually = null)
    {
        await _processed.RecordAsync(new ProcessedDonationRecord
        {
            DonationId = donation.Id,
            CommandId = commandId,
            State = state,
            RecordedAt = _utcNow()
        }, cancellationToken).ConfigureAwait(false);
        return await AppendAsync(donation, match, state, detail, cancellationToken, commandId, canRetryManually).ConfigureAwait(false);
    }

    private void ReleaseDonationIfFinished(string donationId)
    {
        if (_queue.Any(pending => string.Equals(pending.Donation.Id, donationId, StringComparison.Ordinal)) ||
            _sentCommands.Values.Any(pending => string.Equals(pending.Donation.Id, donationId, StringComparison.Ordinal)))
            return;
        _queuedDonationIds.TryRemove(donationId, out _);
    }

    private Task PersistQueueUnderLockAsync(CancellationToken cancellationToken)
    {
        if (_pendingStore is null) return Task.CompletedTask;
        var snapshots = _sentCommands.Values.Select(pending => ToSnapshot(pending, true))
            .Concat(_queue.Select(pending => ToSnapshot(pending, false)))
            .ToList();
        return _pendingStore.SaveAsync(snapshots, cancellationToken);
    }

    private static PendingDispatchSnapshot ToSnapshot(PendingDispatch pending, bool inFlight) => new()
        {
            Donation = new DonationEvent
            {
                Id = pending.Donation.Id,
                Username = pending.Donation.Username,
                Message = pending.Donation.Message,
                Amount = pending.Donation.Amount,
                Currency = pending.Donation.Currency,
                CreatedAt = pending.Donation.CreatedAt,
                ReceivedAt = pending.Donation.ReceivedAt
            },
            Action = pending.Match.Action.Clone(),
            Trigger = pending.Match.Trigger.Clone(),
            SourceAction = pending.Match.SourceAction?.Clone(),
            HistoryActionId = pending.Match.HistoryActionId,
            SequenceId = pending.SequenceId,
            SequenceIndex = pending.SequenceIndex,
            GroupBatchId = pending.GroupBatchId,
            GroupDelaySeconds = pending.GroupDelaySeconds,
            GroupLastSentAt = pending.GroupLastSentAt,
            SpawnPreparationRetryAt = pending.SpawnPreparationRetryAt,
            SpawnPreparationReservedAt = pending.SpawnPreparationReservedAt,
            NeedsRetryAt = pending.NeedsRetryAt,
            VehicleRetryAt = pending.VehicleRetryAt,
            ImmediateRequested = pending.ImmediateRequested,
            Command = new GameCommand
            {
                CommandId = pending.Command.CommandId,
                DonationId = pending.Command.DonationId,
                ActionId = pending.Command.ActionId,
                SourceHandlerId = pending.Command.SourceHandlerId,
                RequireSafeZoneCheck = pending.Command.RequireSafeZoneCheck,
                SafeZoneRevision = pending.Command.SafeZoneRevision,
                AmountMinor = pending.Command.AmountMinor,
                Currency = pending.Command.Currency,
                Parameters = new Dictionary<string, string>(pending.Command.Parameters, StringComparer.OrdinalIgnoreCase),
                CatalogRevision = pending.Command.CatalogRevision,
                DonorName = pending.Command.DonorName,
                ActionName = pending.Command.ActionName,
                BatchIndex = pending.Command.BatchIndex,
                BatchSize = pending.Command.BatchSize,
                SaveBeforeBatch = pending.Command.SaveBeforeBatch,
                SaveAfterBatch = pending.Command.SaveAfterBatch,
                EffectSaveRetentionCount = pending.Command.EffectSaveRetentionCount,
                ShowGameEffectLog = pending.Command.ShowGameEffectLog,
                ShowPdaEffectMessage = pending.Command.ShowPdaEffectMessage,
                CreatedAt = pending.Command.CreatedAt
            },
            EnqueuedAt = pending.EnqueuedAt,
            ExpiresAt = pending.ExpiresAt,
            QueueMode = pending.QueueMode,
            InFlight = inFlight
        };

    private async Task<EventHistoryEntry> AppendAsync(
        DonationEvent donation,
        RuleMatch? match,
        DispatchState state,
        string detail,
        CancellationToken cancellationToken,
        string commandId = "",
        bool? canRetryManually = null)
    {
        var entry = new EventHistoryEntry
        {
            Timestamp = _utcNow(),
            DonationCreatedAt = donation.CreatedAt,
            DonationId = donation.Id,
            CommandId = commandId,
            Donor = NormalizeDonorName(donation.Username),
            DonationMessage = donation.Message,
            Amount = donation.Amount,
            Currency = donation.Currency,
            ActionId = match?.HistoryActionId ?? match?.Action.Id ?? string.Empty,
            HandlerId = match?.Action.HandlerId ?? string.Empty,
            ActionName = match?.Action.DisplayName ?? string.Empty,
            ActionSnapshot = match is null ? null : CreateHistorySnapshot(match.Action),
            State = state,
            Detail = detail,
            CanRetryManually = canRetryManually ?? state is DispatchState.Rejected or DispatchState.Uncertain or DispatchState.Skipped or DispatchState.Expired
        };
        await _history.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    private static ActionDefinition CreateHistorySnapshot(ActionDefinition action)
    {
        var snapshot = action.Clone();
        snapshot.IsActive = false;
        snapshot.Triggers.Clear();
        foreach (var definition in snapshot.ParameterDefinitions.Where(item => item.CatalogKind is not null))
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snapshot.Parameters.TryGetValue(definition.Key, out var value))
            {
                foreach (var option in definition.SelectedOptions(value)) selected.Add(option);
            }
            if (string.Equals(definition.Key, "section_id", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in snapshot.ItemSpawns) selected.Add(item.ItemId);
                foreach (var group in snapshot.SpawnGroups) selected.Add(group.VariantId);
            }
            definition.Options = selected.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            definition.OptionGroups.Clear();
            definition.OptionLabels = definition.OptionLabels
                .Where(pair => selected.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        return snapshot;
    }
}
