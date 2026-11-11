using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AnomalyDonationBridge.Core;

public enum VotingPhase { Draft, Collecting, Paused, Ready, Failed, Drawn }
public enum VotingCondition { All, Any }
public enum VotingSequenceOrder { InOrder, Random }
public enum VotingHudPosition { Left, Right, Top, Bottom }
public enum VotingHudFormat { Percent, Amount, Timer }

public sealed class VotingAutomation
{
    public bool Enabled { get; set; }
    public bool AutomaticDraw { get; set; } = true;
    public bool AutomaticWinnerEffect { get; set; } = true;
    public bool AutoNextCollection { get; set; } = true;
    public VotingSequenceOrder Order { get; set; }
    public bool ShowGameHud { get; set; } = true;
    public VotingHudPosition GameHudPosition { get; set; }
    // Normalised top-left coordinates survive resolution/aspect changes.
    public decimal GameHudX { get; set; } = .02m;
    public decimal GameHudY { get; set; } = .35m;
    public VotingHudFormat GameHudFormat { get; set; }
    public bool ShowGameHudCommands { get; set; } = true;
    public bool ShowGameHudTemplateNames { get; set; }
    public VotingAutomation Clone() => new() { Enabled = Enabled, AutomaticDraw = AutomaticDraw,
        AutoNextCollection = AutoNextCollection, Order = Order, AutomaticWinnerEffect = AutomaticWinnerEffect,
        ShowGameHud = ShowGameHud, GameHudPosition = GameHudPosition, GameHudX = GameHudX, GameHudY = GameHudY,
        GameHudFormat = GameHudFormat, ShowGameHudCommands = ShowGameHudCommands,
        ShowGameHudTemplateNames = ShowGameHudTemplateNames };
}

public sealed class VotingOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = string.Empty;
    public ActionDefinition? Preset { get; set; }
    public decimal Amount { get; set; }
    // Keep currencies separate when the collection currency is edited live.
    public Dictionary<string, decimal> AmountsByCurrency { get; set; } = new(StringComparer.Ordinal);
    public VotingOption Clone() => new() { Id = Id, Enabled = Enabled, Command = Command,
        Preset = VotingPresets.Snapshot(Preset), Amount = Amount, AmountsByCurrency = new(AmountsByCurrency, StringComparer.Ordinal) };
}

public sealed class VotingInterval
{
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public VotingInterval Clone() => new() { Start = Start, End = End };
}

public sealed class VotingRound
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CollectionId { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Голосование №1";
    public bool UseInSequence { get; set; } = true;
    public bool IsActive { get; set; }
    public string Currency { get; set; } = "RUB";
    public bool MoneyGoal { get; set; } = true;
    public bool TimeGoal { get; set; }
    public decimal Target { get; set; } = 1000m;
    public int DurationSeconds { get; set; } = 600;
    public VotingCondition Condition { get; set; } = VotingCondition.Any;
    public bool AcceptDonationVotes { get; set; } = true;
    public bool AcceptYouTubeVotes { get; set; }
    public decimal YouTubeVoteWeight { get; set; } = 1m;
    public bool AllowYouTubeVoteChange { get; set; }
    public Dictionary<string, YouTubeVotingParticipant> YouTubeParticipants { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> SeenYouTubeMessageIds { get; set; } = new(StringComparer.Ordinal);
    public VotingPhase Phase { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public DateTimeOffset? Deadline { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? DrawnAt { get; set; }
    public bool SuccessorStarted { get; set; }
    public List<VotingInterval> Intervals { get; set; } = [];
    public List<VotingOption> Options { get; set; } = [];
    public string WinnerOptionId { get; set; } = string.Empty;
    public decimal WinnerFraction { get; set; }
    public VotingOption? FrozenWinner { get; set; }
    public List<VotingOption> FrozenOptions { get; set; } = [];
    public decimal FrozenTotal { get; set; }
    public string FrozenCurrency { get; set; } = string.Empty;
    public string FrozenTitle { get; set; } = string.Empty;
    public bool DeliveryAccepted { get; set; }
    public bool DeliveryCancelled { get; set; }
    public bool GamePresentationRequired { get; set; }
    public bool GamePresentationCompleted { get; set; }
    public bool GamePresentationAutomaticEffect { get; set; }
    public string DeliveryNote { get; set; } = string.Empty;
    public string Notice { get; set; } = string.Empty;
    [JsonIgnore] public decimal Total => Options.Where(option => option.Enabled).Sum(option => option.Amount);
    [JsonIgnore] public bool CanEdit => true;
    [JsonIgnore] public VotingOption? Winner => FrozenWinner ?? Options.FirstOrDefault(option => option.Id == WinnerOptionId);
    [JsonIgnore] public string DeliveryId => "voting:" + Id;

    public VotingRound Clone() => new()
    {
        Id = Id, CollectionId = CollectionId, Title = Title, UseInSequence = UseInSequence, IsActive = IsActive,
        Currency = Currency, MoneyGoal = MoneyGoal, TimeGoal = TimeGoal,
        Target = Target, DurationSeconds = DurationSeconds, Condition = Condition, Phase = Phase,
        AcceptDonationVotes = AcceptDonationVotes, AcceptYouTubeVotes = AcceptYouTubeVotes,
        YouTubeVoteWeight = YouTubeVoteWeight, AllowYouTubeVoteChange = AllowYouTubeVoteChange,
        YouTubeParticipants = YouTubeParticipants.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
        SeenYouTubeMessageIds = new(SeenYouTubeMessageIds, StringComparer.Ordinal),
        DrawnAt = DrawnAt, SuccessorStarted = SuccessorStarted,
        ProfileId = ProfileId, Deadline = Deadline, PausedAt = PausedAt, ClosedAt = ClosedAt,
        Intervals = Intervals.Select(interval => interval.Clone()).ToList(),
        Options = Options.Select(option => option.Clone()).ToList(), WinnerOptionId = WinnerOptionId,
        WinnerFraction = WinnerFraction, DeliveryAccepted = DeliveryAccepted, DeliveryCancelled = DeliveryCancelled,
        GamePresentationRequired = GamePresentationRequired, GamePresentationCompleted = GamePresentationCompleted,
        GamePresentationAutomaticEffect = GamePresentationAutomaticEffect,
        FrozenWinner = FrozenWinner?.Clone(), FrozenOptions = FrozenOptions.Select(option => option.Clone()).ToList(),
        FrozenTotal = FrozenTotal, FrozenCurrency = FrozenCurrency, FrozenTitle = FrozenTitle,
        DeliveryNote = DeliveryNote, Notice = Notice
    };

    public VotingRound ResultSnapshot()
    {
        var result = Clone();
        if (Phase == VotingPhase.Drawn && FrozenWinner is not null)
        { result.Options = FrozenOptions.Select(option => option.Clone()).ToList(); result.Currency = FrozenCurrency; result.Title = FrozenTitle; }
        return result;
    }
}

public sealed class VotingDocument
{
    public int SchemaVersion { get; set; } = 7;
    public string ActiveProfileId { get; set; } = string.Empty;
    public bool HasCollections { get; set; } = true;
    public VotingRound Current { get; set; } = new();
    public List<VotingRound> OtherCollections { get; set; } = [];
    public List<string> CollectionOrder { get; set; } = [];
    public VotingAutomation Automation { get; set; } = new();
    public List<VotingRound> PreviousRounds { get; set; } = [];
    public HashSet<string> SeenDonationIds { get; set; } = new(StringComparer.Ordinal);
    public VotingDocument Clone() => new() { SchemaVersion = SchemaVersion, ActiveProfileId = ActiveProfileId, HasCollections = HasCollections, Current = Current.Clone(),
        OtherCollections = OtherCollections.Select(round => round.Clone()).ToList(), CollectionOrder = [.. CollectionOrder],
        Automation = Automation.Clone(),
        PreviousRounds = PreviousRounds.Select(round => round.Clone()).ToList(),
        SeenDonationIds = new HashSet<string>(SeenDonationIds, StringComparer.Ordinal) };
}

public interface IVotingStore
{
    Task<VotingDocument> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(VotingDocument document, CancellationToken cancellationToken = default);
}

public static class VotingRules
{
    public static string NormalizeCommand(string value) => value.Trim().Normalize(NormalizationForm.FormKC);

    // Literal, case-insensitive phrases, not regular expressions or executable input.
    // Word boundaries prevent !долг from matching !долгий. Repeating the same command
    // still counts once; messages containing different choices are ambiguous.
    public static bool ContainsCommand(string message, string command)
    {
        message = message.Normalize(NormalizationForm.FormKC);
        command = NormalizeCommand(command);
        if (command.Length == 0) return false;
        var offset = 0;
        while (offset <= message.Length - command.Length)
        {
            var index = message.IndexOf(command, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + command.Length;
            if ((index == 0 || !Word(message[index - 1])) && (end == message.Length || !Word(message[end])))
                return true;
            offset = index + 1;
        }
        return false;
    }

    private static bool Word(char value) => char.IsLetterOrDigit(value) || value == '_';

    public static void ValidateSettings(VotingRound round)
    {
        if (string.IsNullOrWhiteSpace(round.Title) || round.Title.Length > 120)
            throw new InvalidOperationException("Заголовок: от 1 до 120 символов.");
        if (!Money.TryNormalizeCurrency(round.Currency, out _))
            throw new InvalidOperationException("Укажите трёхбуквенный код валюты, например RUB.");
        if (!round.MoneyGoal && !round.TimeGoal)
            throw new InvalidOperationException("Включите сумму голосования и/или время.");
        if (round.MoneyGoal && round.Target is <= 0 or > 1_000_000_000m)
            throw new InvalidOperationException("Сумма голосования: больше нуля, не более 1 000 000 000.");
        if (round.TimeGoal && round.DurationSeconds is < 1 or > 31_536_000)
            throw new InvalidOperationException("Длительность: от 1 секунды до 365 дней.");
        if (!Enum.IsDefined(round.Condition)) throw new InvalidOperationException("Неизвестное условие голосования.");
        if (!round.AcceptDonationVotes && !round.AcceptYouTubeVotes)
            throw new InvalidOperationException("Включите хотя бы один источник голосов.");
        if (round.YouTubeVoteWeight is <= 0 or > 1_000_000_000m || Money.ToMinor(round.YouTubeVoteWeight, round.Currency.Trim().ToUpperInvariant()) <= 0)
            throw new InvalidOperationException("Вес голоса YouTube: положительная сумма в валюте голосования, не более 1 000 000 000.");
        if (round.Options.Count > 200) throw new InvalidOperationException("Допускается не более 200 команд.");
        if (round.Options.Any(option => string.IsNullOrWhiteSpace(option.Id) || option.Command is null || option.Command.Length > 80) ||
            round.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != round.Options.Count)
            throw new InvalidOperationException("Некорректная команда или повторяющийся идентификатор команды.");
    }

    public static void Validate(VotingRound round)
    {
        ValidateSettings(round);
        var options = round.Options.Where(option => option.Enabled).ToList();
        if (options.Count < 2) throw new InvalidOperationException("Нужны минимум две включённые команды.");
        if (options.Any(option => string.IsNullOrWhiteSpace(option.Command) || option.Command.Length > 80))
            throw new InvalidOperationException("Пропишите команды длиной от 1 до 80 символов.");
        if (options.Select(option => NormalizeCommand(option.Command)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
            throw new InvalidOperationException("Команды не должны повторяться (регистр не учитывается).");
        if (options.Any(option => option.Preset is null || option.Preset.Validate().Count > 0))
            throw new InvalidOperationException("Выберите корректный шаблон для каждой включённой команды.");
    }

    public static long RandomBelow(long maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        var bound = (ulong)maximum;
        var limit = ulong.MaxValue - ulong.MaxValue % bound;
        Span<byte> bytes = stackalloc byte[8];
        ulong value;
        do { RandomNumberGenerator.Fill(bytes); value = BitConverter.ToUInt64(bytes); } while (value >= limit);
        return (long)(value % bound);
    }
}

/// <summary>Each mutation is saved on an isolated copy before it becomes visible.
/// A failed disk write can neither count money nor publish an unpersisted winner.</summary>
public sealed partial class VotingCoordinator(IVotingStore store, Func<DateTimeOffset>? utcNow = null,
    Func<long, long>? randomBelow = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<long, long> _random = randomBelow ?? VotingRules.RandomBelow;
    private VotingDocument _document = new();
    public event Action? Changed;
    public VotingRound Snapshot => Volatile.Read(ref _document).Current.Clone();
    public VotingAutomation Automation => Volatile.Read(ref _document).Automation.Clone();
    public IReadOnlyList<VotingRound> PendingWinners
    {
        get
        {
            var document = Volatile.Read(ref _document);
            return AllCollections(document).Concat(document.PreviousRounds)
                .Where(round => round.Phase == VotingPhase.Drawn && !round.DeliveryAccepted && !round.DeliveryCancelled)
                .OrderBy(round => round.DrawnAt).Select(round => round.ResultSnapshot()).ToList();
        }
    }
    public IReadOnlyList<VotingRound> Collections
    {
        get
        {
            var document = Volatile.Read(ref _document);
            return AllCollections(document).OrderBy(round => document.CollectionOrder.IndexOf(round.CollectionId))
                .Select(round => round.Clone()).ToList();
        }
    }
    public IReadOnlyList<VotingRound> CancelledGamePresentations
    {
        get
        {
            var document = Volatile.Read(ref _document);
            return AllCollections(document).Concat(document.PreviousRounds).Where(round => round.GamePresentationRequired &&
                !round.GamePresentationCompleted && round.DeliveryCancelled).Select(round => round.ResultSnapshot()).ToList();
        }
    }
    public VotingRound SnapshotFor(string? collectionId)
    {
        var document = Volatile.Read(ref _document);
        return (AllCollections(document).FirstOrDefault(round => round.CollectionId == collectionId) ?? document.Current).Clone();
    }
    private static IEnumerable<VotingRound> AllCollections(VotingDocument document) => document.HasCollections
        ? new[] { document.Current }.Concat(document.OtherCollections) : [];
    private static VotingRound FindCollection(VotingDocument document, string? collectionId) =>
        collectionId is null && document.HasCollections ? document.Current : AllCollections(document).FirstOrDefault(round => round.CollectionId == collectionId)
            ?? throw new InvalidOperationException("Это голосование уже удалено.");

    public async Task InitializeAsync(CancellationToken token = default)
    {
        var document = await store.LoadAsync(token).ConfigureAwait(false);
        if (document.SchemaVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7) || document.Current is null || document.SeenDonationIds is null || document.ActiveProfileId is null ||
            document.OtherCollections is null || document.CollectionOrder is null || document.Automation is null ||
            !Enum.IsDefined(document.Automation.Order) || !Enum.IsDefined(document.Automation.GameHudPosition) ||
            !Enum.IsDefined(document.Automation.GameHudFormat) || document.Automation.GameHudX is < 0 or > 1 ||
            document.Automation.GameHudY is < 0 or > 1 || document.OtherCollections.Count > 9999)
            throw new InvalidDataException("Неизвестная версия или повреждённый файл voting.json.");
        var current = document.Current;
        if (document.PreviousRounds is null || current.Options is null || current.Intervals is null ||
            string.IsNullOrWhiteSpace(current.Id) || !Enum.IsDefined(current.Phase) || !Enum.IsDefined(current.Condition) ||
            current.Title is null || current.Currency is null || current.Notice is null || current.DeliveryNote is null ||
            current.ProfileId is null || current.WinnerOptionId is null || current.Options.Count > 200 ||
            document.PreviousRounds.Any(round => round is null || round.Options is null || round.Intervals is null) ||
            current.Options.Any(option => option is null || option.Command is null || option.Amount < 0 || string.IsNullOrWhiteSpace(option.Id) ||
                option.AmountsByCurrency is null || option.AmountsByCurrency.Any(pair => pair.Value < 0 || !Money.TryNormalizeCurrency(pair.Key, out var code) || code != pair.Key)) ||
            current.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != current.Options.Count ||
            current.Phase == VotingPhase.Drawn && (current.Winner is null || current.Winner.Amount <= 0 || current.WinnerFraction is < 0 or > 1))
            throw new InvalidDataException("Файл voting.json содержит некорректный учёт. Автоматический сброс запрещён.");
        foreach (var round in AllCollections(document))
        {
            if (round is null || round.Options is null || round.Intervals is null || round.FrozenOptions is null || round.Title is null || round.ProfileId is null ||
                round.Currency is null || round.DeliveryNote is null || round.Notice is null || round.Options.Count > 200 ||
                !Enum.IsDefined(round.Phase) || !Enum.IsDefined(round.Condition) || string.IsNullOrWhiteSpace(round.CollectionId) ||
                round.Options.Any(option => option is null || option.AmountsByCurrency is null || option.Amount < 0 ||
                    option.AmountsByCurrency.Any(pair => pair.Value < 0)))
                throw new InvalidDataException("Повреждён список голосований в voting.json.");
            if (document.SchemaVersion < 3 && round.Title.StartsWith("Сбор №", StringComparison.Ordinal) && int.TryParse(round.Title[6..], out _))
                round.Title = "Голосование №" + round.Title[6..];
            foreach (var option in round.Options) option.AmountsByCurrency[round.Currency] = option.Amount;
            if (!document.CollectionOrder.Contains(round.CollectionId)) document.CollectionOrder.Add(round.CollectionId);
        }
        if ((!document.HasCollections && document.OtherCollections.Count > 0) || AllCollections(document).Select(round => round.CollectionId).Distinct().Count() != AllCollections(document).Count())
            throw new InvalidDataException("Повторяющийся идентификатор голосования в voting.json.");
        // Earlier local previews stored no global switch. Preserve an active round on migration.
        if (document.SchemaVersion < 3 && current.Phase == VotingPhase.Collecting) document.Automation.Enabled = true;
        foreach (var round in AllCollections(document).Concat(document.PreviousRounds))
        {
            ValidateYouTubeAccounting(round);
            if (round.Phase == VotingPhase.Drawn && round.FrozenWinner is null) FreezeResult(round);
            if (round.Phase == VotingPhase.Drawn && (round.FrozenWinner?.Preset is null || round.FrozenOptions is null || round.FrozenTotal <= 0))
                throw new InvalidDataException("Повреждён сохранённый победитель голосования.");
        }
        if (document.SchemaVersion < 4)
        {
            // Older previews allowed parallel collection. Keep the current running
            // vote (or the first running one), pausing the rest without losing time.
            var chosen = document.Automation.Enabled ? AllCollections(document).FirstOrDefault(round => round.Phase == VotingPhase.Collecting)
                ?? AllCollections(document).FirstOrDefault(round => round.UseInSequence) : null;
            foreach (var round in AllCollections(document).Where(round => round.Total == 0 && round.Notice.StartsWith("Накопленные суммы сброшены.", StringComparison.Ordinal)))
                CancelPendingResults(document, round.CollectionId, "Отменено сбросом прогресса.");
            foreach (var round in AllCollections(document))
            { round.IsActive = round == chosen; if (!round.IsActive) Pause(round, _now()); }
            document.Automation.Enabled = chosen is not null;
            document.SchemaVersion = 4;
            await store.SaveAsync(document, token).ConfigureAwait(false);
        }
        if (AllCollections(document).Count(round => round.IsActive) > 1 ||
            AllCollections(document).Count(round => round.Phase == VotingPhase.Collecting) > 1 ||
            document.Automation.Enabled != AllCollections(document).Any(round => round.IsActive))
            throw new InvalidDataException("Повреждён статус активного голосования. Автоматический сброс запрещён.");
        if (document.SchemaVersion < 5)
        {
            // Existing drawn results retain their previous delivery semantics.
            document.SchemaVersion = 5;
            await store.SaveAsync(document, token).ConfigureAwait(false);
        }
        if (document.SchemaVersion < 6)
        {
            // Property defaults preserve donation-only behaviour in older files.
            document.SchemaVersion = 6;
            await store.SaveAsync(document, token).ConfigureAwait(false);
        }
        if (document.SchemaVersion < 7)
        {
            document = document.Clone(); // Compact historical catalog copies once.
            document.SchemaVersion = 7;
            await store.SaveAsync(document, token).ConfigureAwait(false);
        }
        Volatile.Write(ref _document, document);
        await TickAsync(token).ConfigureAwait(false);
    }

    public Task SaveSettingsAsync(VotingRound draft, CancellationToken token = default) => MutateAsync(document =>
    {
        var round = FindCollection(document, draft.CollectionId);
        if (draft.Id != round.Id) throw new InvalidOperationException("Прогресс голосования уже сброшен. Откройте его настройки заново.");
        VotingRules.ValidateSettings(draft);
        var now = _now();
        var currency = draft.Currency.Trim().ToUpperInvariant();
        var options = draft.Options.Select(option =>
        {
            var next = option.Clone();
            // Never trust stale UI amounts: donations may arrive during editing.
            var previous = round.Options.FirstOrDefault(saved => saved.Id == option.Id);
            next.AmountsByCurrency = previous is null ? new(StringComparer.Ordinal) : new(previous.AmountsByCurrency, StringComparer.Ordinal);
            if (previous is not null) next.AmountsByCurrency[round.Currency] = previous.Amount;
            next.Amount = next.AmountsByCurrency.GetValueOrDefault(currency);
            next.Command = VotingRules.NormalizeCommand(next.Command);
            return next;
        }).ToList();
        if (round.Phase != VotingPhase.Draft)
        {
            // Only an actual duration edit starts a fresh countdown. A paused
            // vote uses its pause anchor so resuming grants the full duration.
            var restartTimer = !round.TimeGoal || round.Deadline is null || draft.DurationSeconds != round.DurationSeconds;
            round.Deadline = !draft.TimeGoal ? null : restartTimer
                ? (round.PausedAt ?? now).AddSeconds(draft.DurationSeconds) : round.Deadline;
        }
        round.Title = draft.Title.Trim(); round.Currency = currency;
        round.Target = draft.Target; round.DurationSeconds = draft.DurationSeconds;
        round.MoneyGoal = draft.MoneyGoal; round.TimeGoal = draft.TimeGoal;
        round.Condition = draft.Condition; round.Options = options;
        round.AcceptDonationVotes = draft.AcceptDonationVotes; round.AcceptYouTubeVotes = draft.AcceptYouTubeVotes;
        round.YouTubeVoteWeight = Money.FromMinor(Money.ToMinor(draft.YouTubeVoteWeight, currency), currency);
        round.AllowYouTubeVoteChange = draft.AllowYouTubeVoteChange;
        // The participant registry belongs to the runtime, never to a UI draft.
        if (round.Phase is VotingPhase.Ready or VotingPhase.Failed)
        {
            var cutoff = round.ClosedAt;
            round.ClosedAt = null; round.Phase = VotingPhase.Collecting;
            if (Evaluate(round, now)) round.ClosedAt = cutoff ?? round.ClosedAt;
            else
            {
                round.Intervals.Add(new VotingInterval { Start = now });
                round.Notice = "Настройки обновлены. Голосование продолжается.";
            }
        }
        else if (round.Phase == VotingPhase.Collecting) Evaluate(round, now);
        if (round.Phase == VotingPhase.Collecting && (!round.IsActive || !document.Automation.Enabled)) Pause(round, now);
        if (round.IsActive && document.Automation.Enabled && (round.Phase is VotingPhase.Draft or VotingPhase.Paused) && CanStart(round))
            Resume(round, round.ProfileId, now);
        return true;
    }, token);

    public Task RenameAsync(string title, string? collectionId = null, CancellationToken token = default) => MutateAsync(document =>
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 120)
            throw new InvalidOperationException("Заголовок: от 1 до 120 символов.");
        FindCollection(document, collectionId).Title = title.Trim(); return true;
    }, token);

    public Task SaveAutomationAsync(VotingAutomation automation, CancellationToken token = default) => MutateAsync(document =>
    {
        if (!Enum.IsDefined(automation.Order)) throw new InvalidOperationException("Неизвестный порядок голосований.");
        if (!Enum.IsDefined(automation.GameHudPosition) || !Enum.IsDefined(automation.GameHudFormat))
            throw new InvalidOperationException("Неизвестный формат или положение игрового прогресса.");
        if (automation.GameHudX is < 0 or > 1 || automation.GameHudY is < 0 or > 1)
            throw new InvalidOperationException("Положение игрового прогресса находится за пределами экрана.");
        var enabled = document.Automation.Enabled;
        document.Automation = automation.Clone(); document.Automation.Enabled = enabled;
        return true;
    }, token);

    public Task SaveHudPositionAsync(decimal x, decimal y, CancellationToken token = default) => MutateAsync(document =>
    {
        if (x is < 0 or > 1 || y is < 0 or > 1) throw new InvalidOperationException("Некорректные координаты HUD.");
        // Desktop placement must not overwrite concurrent general-setting edits or funds.
        document.Automation.GameHudX = x; document.Automation.GameHudY = y; return true;
    }, token);

    public async Task<string> AddCollectionAsync(CancellationToken token = default, string? sourceCollectionId = null, string? profileId = null)
    {
        var collectionId = string.Empty;
        await MutateAsync(document =>
        {
            var owner = profileId ?? (document.ActiveProfileId.Length > 0 ? document.ActiveProfileId : document.Current.ProfileId);
            var existing = AllCollections(document).Where(round => round.ProfileId == owner).ToList();
            if (existing.Count >= 100) throw new InvalidOperationException("Допускается не более 100 голосований в профиле.");
            var titleNumber = Enumerable.Range(1, 101).First(number => existing.All(round => round.Title != "Голосование №" + number));
            var source = existing.FirstOrDefault(round => round.CollectionId == sourceCollectionId) ??
                existing.FirstOrDefault(round => round.Id == document.Current.Id) ?? existing.LastOrDefault();
            var round = new VotingRound { Title = "Голосование №" + titleNumber, ProfileId = owner,
                AcceptDonationVotes = source?.AcceptDonationVotes ?? true, AcceptYouTubeVotes = source?.AcceptYouTubeVotes ?? false,
                YouTubeVoteWeight = source?.YouTubeVoteWeight ?? 1m, AllowYouTubeVoteChange = source?.AllowYouTubeVoteChange ?? false,
                Currency = source?.Currency ?? "RUB", MoneyGoal = source?.MoneyGoal ?? true,
                TimeGoal = source?.TimeGoal ?? false, DurationSeconds = source?.DurationSeconds ?? 600,
                Condition = source?.Condition ?? VotingCondition.Any, Target = source?.Target ?? 1000m };
            collectionId = round.CollectionId;
            if (!document.HasCollections) { document.Current = round; document.HasCollections = true; }
            else document.OtherCollections.Add(round);
            document.CollectionOrder.Add(collectionId);
            return true;
        }, token).ConfigureAwait(false);
        return collectionId;
    }

    public Task SetSequenceAsync(IReadOnlyList<string> order, IReadOnlySet<string> included, CancellationToken token = default, string? profileId = null) => MutateAsync(document =>
    {
        var rounds = AllCollections(document).Where(round => profileId is null || round.ProfileId == profileId).ToList();
        if (order.Count != rounds.Count || order.Distinct().Count() != rounds.Count || rounds.Any(round => !order.Contains(round.CollectionId)))
            throw new InvalidOperationException("Список голосований изменился. Откройте общие настройки заново.");
        document.CollectionOrder = profileId is null ? order.ToList() : document.CollectionOrder.Where(id => !order.Contains(id)).Concat(order).ToList();
        foreach (var round in rounds) round.UseInSequence = included.Contains(round.CollectionId);
        return true;
    }, token);

    public Task SetCollectionActiveAsync(string collectionId, bool active, string profileId, CancellationToken token = default) => MutateAsync(document =>
    {
        var round = FindCollection(document, collectionId);
        if (active) ActivateOnly(document, round, profileId, _now());
        else if (round.IsActive)
        {
            round.IsActive = false; document.Automation.Enabled = false; Pause(round, _now());
        }
        return true;
    }, token);

    public Task ActivateCollectionAsync(string collectionId, string profileId, CancellationToken token = default) =>
        SelectCollectionAsync(collectionId, profileId, true, token);

    public Task SelectCollectionAsync(string collectionId, string profileId, bool enable = false, CancellationToken token = default) => MutateAsync(document =>
    {
        var target = FindCollection(document, collectionId); var now = _now();
        if (target.ProfileId.Length > 0 && target.ProfileId != profileId)
            throw new InvalidOperationException("Это голосование принадлежит другому профилю.");
        if (document.Current.Id != target.Id)
        {
            document.OtherCollections.Remove(target); document.OtherCollections.Add(document.Current); document.Current = target;
        }
        if (enable) ActivateOnly(document, target, profileId, now);
        return true;
    }, token);

    public Task SetEnabledAsync(bool enabled, string profileId, CancellationToken token = default) => MutateAsync(document =>
    {
        var now = _now();
        if (!enabled)
        {
            foreach (var round in AllCollections(document)) { round.IsActive = false; Pause(round, now); }
            document.Automation.Enabled = false;
        }
        else if ((AllCollections(document).FirstOrDefault(round => round.IsActive) ??
            AllCollections(document).FirstOrDefault(round => (round.ProfileId.Length == 0 || round.ProfileId == profileId) && CanStart(round))) is { } target)
            ActivateOnly(document, target, profileId, now);
        return true;
    }, token);

    private static void ActivateOnly(VotingDocument document, VotingRound target, string profileId, DateTimeOffset now)
    {
        if (target.ProfileId.Length > 0 && target.ProfileId != profileId)
            throw new InvalidOperationException("Продолжить голосование можно только в исходном профиле.");
        foreach (var round in AllCollections(document))
        { round.IsActive = round == target; if (!round.IsActive) Pause(round, now); }
        document.Automation.Enabled = true;
        if (target.ProfileId.Length == 0) target.ProfileId = profileId;
        if ((target.Phase is VotingPhase.Draft or VotingPhase.Paused) && CanStart(target)) Resume(target, profileId, now);
    }

    public async Task<string> RecordAsync(DonationEvent donation, string profileId, CancellationToken token = default)
    {
        var outcome = "not_collecting";
        await MutateAsync(document =>
        {
            var candidates = AllCollections(document).Where(round =>
                round.Phase is not (VotingPhase.Draft or VotingPhase.Drawn) && round.ProfileId == profileId).ToList();
            if (candidates.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(donation.Id) || document.SeenDonationIds.Contains(donation.Id))
            { outcome = "duplicate"; return false; }
            var now = _now(); foreach (var candidate in candidates) Evaluate(candidate, now);
            document.SeenDonationIds.Add(donation.Id);
            var at = donation.CreatedAt == default ? now : donation.CreatedAt;
            if (at > now.AddMinutes(5)) { outcome = "invalid_time"; return true; }
            // Recovery may refer to a different collection that was active when
            // the donation was created, not whichever page is selected now.
            var eligible = candidates.Where(candidate => candidate.AcceptDonationVotes && candidate.Intervals.Any(interval =>
                at >= interval.Start && at < (interval.End ?? candidate.Deadline ?? DateTimeOffset.MaxValue))).ToList();
            if (eligible.Count == 0)
            { outcome = "outside_collection"; return true; }
            if (!Money.TryNormalizeCurrency(donation.Currency, out var currency) || eligible.All(round => currency != round.Currency))
            { outcome = "currency_mismatch"; return true; }
            if (donation.Amount is <= 0 or > 1_000_000_000m) { outcome = "invalid_amount"; return true; }
            var amount = Money.FromMinor(Money.ToMinor(donation.Amount, currency), currency);
            if (amount <= 0) { outcome = "invalid_amount"; return true; }
            var counted = false; var ambiguous = false;
            foreach (var round in eligible.Where(round => round.Currency == currency))
            {
                var matches = round.Options.Where(option => option.Enabled && option.Preset is not null && option.Preset.Validate().Count == 0 &&
                    VotingRules.ContainsCommand(donation.Message ?? string.Empty, option.Command)).ToList();
                if (matches.Count != 1) { ambiguous |= matches.Count > 1; continue; }
                matches[0].Amount = checked(matches[0].Amount + amount); matches[0].AmountsByCurrency[currency] = matches[0].Amount;
                Evaluate(round, now); counted = true;
            }
            // Delayed provider recovery may deliver a donation created before the
            // cutoff after the timer expired. Accept it until the winner is frozen.
            outcome = counted ? "counted" : ambiguous ? "ambiguous_commands" : "no_command"; return true;
        }, token).ConfigureAwait(false);
        return outcome;
    }

    public Task TickAsync(CancellationToken token = default)
    {
        if (!AllCollections(Volatile.Read(ref _document)).Any(round => round.Phase == VotingPhase.Collecting && round.Deadline is { } deadline && _now() >= deadline))
            return Task.CompletedTask;
        return MutateAsync(document =>
        {
            var changed = false;
            foreach (var round in AllCollections(document)) changed |= Evaluate(round, _now());
            return changed;
        }, token);
    }

    public async Task<VotingRound> ChooseWinnerAsync(CancellationToken token = default, string? collectionId = null,
        bool requireGamePresentation = false, bool automaticEffect = true)
    {
        await MutateAsync(document =>
        {
            var round = FindCollection(document, collectionId); collectionId = round.CollectionId; Evaluate(round, _now());
            if (round.Phase == VotingPhase.Drawn) return false;
            if (round.Phase != VotingPhase.Ready) throw new InvalidOperationException("Условия голосования ещё не выполнены.");
            VotingRules.Validate(round);
            var options = round.Options.Where(option => option.Enabled && option.Amount > 0).ToList();
            var weights = options.Select(option => Money.ToMinor(option.Amount, round.Currency)).ToList();
            var total = weights.Aggregate(0L, (sum, weight) => checked(sum + weight));
            if (total <= 0) throw new InvalidOperationException("Нет оплаченных голосов.");
            var pick = _random(total);
            if (pick < 0 || pick >= total) throw new InvalidOperationException("Некорректный результат генератора.");
            long before = 0;
            for (var index = 0; index < options.Count; index++)
            {
                if (pick < before + weights[index])
                {
                    round.WinnerOptionId = options[index].Id;
                    round.WinnerFraction = (before + weights[index] / 2m) / total;
                    break;
                }
                before += weights[index];
            }
            round.Phase = VotingPhase.Drawn; round.DrawnAt = _now();
            round.GamePresentationRequired = requireGamePresentation;
            round.GamePresentationCompleted = false;
            round.GamePresentationAutomaticEffect = automaticEffect;
            FreezeResult(round);
            round.Notice = "Победитель сохранён. Повторный розыгрыш запрещён.";
            return true;
        }, token).ConfigureAwait(false);
        return SnapshotFor(collectionId);
    }

    public Task MarkDeliveryAsync(string roundId, string note, CancellationToken token = default) => MutateAsync(document =>
    {
        var round = AllCollections(document).Concat(document.PreviousRounds).FirstOrDefault(round => round.Id == roundId);
        if (round is null || round.Phase != VotingPhase.Drawn || round.DeliveryCancelled)
            throw new InvalidOperationException("Победитель относится к другому голосованию.");
        if (round.GamePresentationRequired && !round.GamePresentationCompleted)
            throw new InvalidOperationException("Дождитесь закрытия игровой рулетки.");
        round.DeliveryAccepted = true; round.DeliveryNote = note; return true;
    }, token);

    public Task CompleteGamePresentationAsync(string roundId, CancellationToken token = default) => MutateAsync(document =>
    {
        var round = AllCollections(document).Concat(document.PreviousRounds).FirstOrDefault(item => item.Id == roundId);
        if (round is null || round.GamePresentationCompleted) return false;
        if (round.Phase != VotingPhase.Drawn || !round.GamePresentationRequired)
            throw new InvalidOperationException("Нет ожидающей игровой рулетки.");
        round.GamePresentationCompleted = true;
        if (!round.DeliveryCancelled) round.DeliveryNote = "Игровая рулетка закрыта; победитель ожидает отправки в очередь.";
        return true;
    }, token);

    public Task SetDeliveryNoteAsync(string note, CancellationToken token = default, string? roundId = null) => MutateAsync(document =>
    {
        var round = roundId is null ? document.Current : AllCollections(document).Concat(document.PreviousRounds).FirstOrDefault(round => round.Id == roundId)
            ?? throw new InvalidOperationException("Сохранённый результат не найден.");
        if (round.DeliveryCancelled || round.DeliveryNote == note) return false;
        round.DeliveryNote = note; return true;
    }, token);

    public Task ResetAmountsAsync(string? collectionId = null, CancellationToken token = default) => MutateAsync(document =>
    {
        var original = FindCollection(document, collectionId);
        CancelPendingResults(document, original.CollectionId, "Отменено сбросом прогресса.");
        var previous = original.Clone();
        var now = _now(); CloseInterval(previous, now); document.PreviousRounds.Add(previous);
        var next = previous.Clone(); next.Id = Guid.NewGuid().ToString("N");
        next.YouTubeParticipants.Clear(); next.SeenYouTubeMessageIds.Clear();
        foreach (var option in next.Options) { option.Amount = 0; option.AmountsByCurrency.Clear(); }
        next.WinnerOptionId = string.Empty; next.WinnerFraction = 0; next.DeliveryAccepted = next.DeliveryCancelled = false; next.DeliveryNote = string.Empty; next.DrawnAt = null;
        ClearFrozenResult(next);
        next.Intervals.Clear(); next.ClosedAt = null;
        if (next.Phase is not (VotingPhase.Draft or VotingPhase.Paused)) next.Phase = VotingPhase.Collecting;
        if (next.Phase == VotingPhase.Collecting)
        {
            // Old/recovered donations must not refill the zeroed collection.
            next.Intervals.Add(new VotingInterval { Start = now }); Evaluate(next, now);
            if (!next.IsActive || !document.Automation.Enabled) Pause(next, now);
        }
        next.Notice = "Накопленные суммы сброшены. Настройки и таймер сохранены.";
        if (original == document.Current) document.Current = next;
        else document.OtherCollections[document.OtherCollections.IndexOf(original)] = next;
        return true;
    }, token);

    public Task DeleteCollectionAsync(string collectionId, CancellationToken token = default) => MutateAsync(document =>
    {
        var target = FindCollection(document, collectionId);
        CancelPendingResults(document, collectionId, "Отменено удалением голосования.");
        var archived = target.Clone(); CloseInterval(archived, _now()); archived.IsActive = false; document.PreviousRounds.Add(archived);
        document.CollectionOrder.Remove(collectionId);
        if (target.IsActive) document.Automation.Enabled = false;
        if (target == document.Current)
        {
            var replacement = document.OtherCollections.OrderBy(round => document.CollectionOrder.IndexOf(round.CollectionId)).FirstOrDefault();
            if (replacement is null) { document.HasCollections = false; document.Current = new VotingRound(); }
            else { document.OtherCollections.Remove(replacement); document.Current = replacement; }
        }
        else document.OtherCollections.Remove(target);
        return true;
    }, token);

    private static void CancelPendingResults(VotingDocument document, string collectionId, string note)
    {
        foreach (var round in AllCollections(document).Concat(document.PreviousRounds).Where(round => round.CollectionId == collectionId &&
            round.Phase == VotingPhase.Drawn && !round.DeliveryAccepted))
        { round.DeliveryCancelled = true; round.DeliveryNote = note; }
    }

    public Task StartNextCollectionAsync(string profileId, CancellationToken token = default)
    {
        var snapshot = Volatile.Read(ref _document);
        // Most timer ticks cannot advance anything. Do not clone the entire
        // saved history merely to discover that a live round is still running.
        if (!snapshot.Automation.Enabled || !snapshot.Automation.AutoNextCollection ||
            !AllCollections(snapshot).Any(round => round.IsActive && !round.SuccessorStarted && round.ProfileId == profileId &&
                (round.Phase == VotingPhase.Failed || round.Phase == VotingPhase.Drawn &&
                    (!round.GamePresentationRequired || round.GamePresentationCompleted) &&
                    (round.DrawnAt is null || _now() >= round.DrawnAt.Value.AddSeconds(7)))))
            return Task.CompletedTask;
        return MutateAsync(document =>
        {
        var now = _now();
        if (!document.Automation.Enabled || !document.Automation.AutoNextCollection) return false;
        var previous = AllCollections(document).FirstOrDefault(round => round.IsActive && !round.SuccessorStarted && round.ProfileId == profileId &&
            (round.Phase == VotingPhase.Failed || round.Phase == VotingPhase.Drawn &&
                (!round.GamePresentationRequired || round.GamePresentationCompleted) &&
                (round.DrawnAt is null || now >= round.DrawnAt.Value.AddSeconds(7))));
        if (previous is null) return false;
        // Leave the winner on screen briefly; the deadline survives a restart.
        if (previous.Phase == VotingPhase.Drawn && previous.DrawnAt is { } drawnAt && now < drawnAt.AddSeconds(7)) return false;
        var candidates = AllCollections(document).Where(round => round.UseInSequence && (round.ProfileId.Length == 0 || round.ProfileId == profileId) && CanStart(round))
            .OrderBy(round => document.CollectionOrder.IndexOf(round.CollectionId)).ToList();
        if (candidates.Count == 0) return false;
        var currentIndex = document.CollectionOrder.IndexOf(previous.CollectionId);
        var available = candidates.Where(round => document.CollectionOrder.IndexOf(round.CollectionId) > currentIndex)
            .Concat(candidates.Where(round => document.CollectionOrder.IndexOf(round.CollectionId) <= currentIndex)).ToList();
        if (available.Count == 0) return false;
        var target = document.Automation.Order == VotingSequenceOrder.Random ? available[(int)_random(available.Count)] : available[0];
        VotingRules.Validate(target);
        previous.SuccessorStarted = true;
        if (target.Phase is VotingPhase.Draft or VotingPhase.Paused)
        { ActivateOnly(document, target, profileId, now); target.Notice = "Следующее голосование начато автоматически."; return true; }
        if (target.Phase == VotingPhase.Ready) { ActivateOnly(document, target, profileId, now); return true; }
        document.PreviousRounds.Add(target.Clone());
        var next = target.Clone(); next.Id = Guid.NewGuid().ToString("N");
        next.YouTubeParticipants.Clear(); next.SeenYouTubeMessageIds.Clear();
        foreach (var option in next.Options) { option.Amount = 0; option.AmountsByCurrency.Clear(); }
        next.WinnerOptionId = string.Empty; next.WinnerFraction = 0; next.DeliveryAccepted = next.DeliveryCancelled = false; next.DeliveryNote = string.Empty;
        ClearFrozenResult(next);
        next.DrawnAt = next.ClosedAt = next.PausedAt = null;
        next.Deadline = next.TimeGoal ? now.AddSeconds(next.DurationSeconds) : null;
        next.Intervals = [new VotingInterval { Start = now }]; next.Phase = VotingPhase.Collecting;
        next.Notice = "Следующее голосование начато автоматически.";
        if (target == document.Current) document.Current = next;
        else document.OtherCollections[document.OtherCollections.IndexOf(target)] = next;
        ActivateOnly(document, next, profileId, now);
        return true;
        }, token);
    }

    private static void Resume(VotingRound round, string profileId, DateTimeOffset now)
    {
        VotingRules.Validate(round);
        if (round.Phase == VotingPhase.Paused && round.ProfileId != profileId)
            throw new InvalidOperationException("Продолжить голосование можно только в исходном профиле.");
        if (round.Phase == VotingPhase.Draft)
        { round.ProfileId = profileId; round.Deadline = round.TimeGoal ? now.AddSeconds(round.DurationSeconds) : null; }
        else if (round.Deadline is { } deadline && round.PausedAt is { } paused) round.Deadline = deadline + (now - paused);
        round.PausedAt = null; round.Phase = VotingPhase.Collecting; round.ClosedAt = null;
        round.Intervals.Add(new VotingInterval { Start = now });
        round.Notice = "Голосование идёт.";
    }

    private static bool CanStart(VotingRound round)
    { try { VotingRules.Validate(round); return true; } catch (InvalidOperationException) { return false; } }

    private static void FreezeResult(VotingRound round)
    {
        round.FrozenWinner = round.Options.FirstOrDefault(option => option.Id == round.WinnerOptionId)?.Clone();
        round.FrozenOptions = round.Options.Select(option => option.Clone()).ToList();
        round.FrozenTotal = round.Total; round.FrozenCurrency = round.Currency; round.FrozenTitle = round.Title;
    }

    private static void ClearFrozenResult(VotingRound round)
    { round.FrozenWinner = null; round.FrozenOptions.Clear(); round.FrozenTotal = 0; round.FrozenCurrency = round.FrozenTitle = string.Empty; round.SuccessorStarted = false;
      round.GamePresentationRequired = round.GamePresentationCompleted = round.GamePresentationAutomaticEffect = false; }

    private static void Pause(VotingRound round, DateTimeOffset now)
    {
        if (round.Phase != VotingPhase.Collecting) return;
        CloseInterval(round, now); round.PausedAt = now; round.Phase = VotingPhase.Paused;
        round.Notice = "Голосование на паузе; сумма и оставшееся время сохранены.";
    }

    public Task SetNoticeAsync(string notice, CancellationToken token = default) => MutateAsync(document =>
    {
        if (document.Current.Notice == notice) return false;
        document.Current.Notice = notice; return true;
    }, token);

    private static void CloseInterval(VotingRound round, DateTimeOffset at)
    {
        if (round.Intervals.LastOrDefault() is { End: null } interval) interval.End = at;
    }

    private static bool Evaluate(VotingRound round, DateTimeOffset now)
    {
        if (round.Phase is VotingPhase.Draft or VotingPhase.Drawn) return false;
        var previous = round.Phase;
        var timeOver = round.TimeGoal && round.Deadline is { } deadline && now >= deadline;
        if (round.Phase == VotingPhase.Paused) timeOver = false;
        var moneyMet = round.MoneyGoal && round.Total >= round.Target;
        var complete = round.MoneyGoal && round.TimeGoal
            ? round.Condition == VotingCondition.All ? timeOver : moneyMet || timeOver
            : round.MoneyGoal ? moneyMet : timeOver;
        // Completed rounds remain closed while delayed in-window donations can
        // turn an unsuccessful ALL round into READY before manual drawing.
        if (round.ClosedAt is not null) complete = true;
        if (!complete) return false;
        var success = round.Total > 0 && (!(round.MoneyGoal && round.TimeGoal && round.Condition == VotingCondition.All) || moneyMet);
        round.Phase = success ? VotingPhase.Ready : VotingPhase.Failed;
        round.ClosedAt ??= timeOver ? round.Deadline : now;
        CloseInterval(round, round.ClosedAt!.Value);
        round.Notice = success ? "Голосование завершено. Нажмите «Крутить»." : round.Total == 0
            ? "Голосование завершено без взносов; рулетка недоступна." : "За отведённое время сумма не собрана; рулетка недоступна.";
        return previous != round.Phase;
    }

    private async Task MutateAsync(Func<VotingDocument, bool> change, CancellationToken token)
    {
        var changed = false;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A completed semaphore await does not move work off the WPF
            // dispatcher. Keep copying, serialization and the durable flush
            // in the background, still under the same ordered transaction gate.
            changed = await Task.Run(async () =>
            {
                var draft = _document.Clone();
                if (!change(draft)) return false;
                if (AllCollections(draft).Count(round => round.IsActive) > 1 || AllCollections(draft).Count(round => round.Phase == VotingPhase.Collecting) > 1 ||
                    draft.Automation.Enabled != AllCollections(draft).Any(round => round.IsActive))
                    throw new InvalidOperationException("Одновременно может работать только одно голосование.");
                await store.SaveAsync(draft, token).ConfigureAwait(false);
                Volatile.Write(ref _document, draft);
                return true;
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (changed && Changed is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch { /* Presentation must not roll back durable accounting. */ }
    }
}
