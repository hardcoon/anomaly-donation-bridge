namespace AnomalyDonationBridge.Core;

public interface IChatVotingStore : IVotingStore
{
    Task SaveChatAsync(VotingDocument document, VotingChatCommit commit, CancellationToken token = default);
}

public static class VotingChatReplay
{
    public const int Capacity = 2048;

    public static void Remember(VotingRound round, string id)
    {
        var order = round.ChatReplayOrder ??= new(round.SeenYouTubeMessageIds);
        if (!round.SeenYouTubeMessageIds.Add(id)) return;
        order.Enqueue(id);
        while (round.SeenYouTubeMessageIds.Count > Capacity)
            round.SeenYouTubeMessageIds.Remove(order.Dequeue());
    }

    public static void Trim(VotingRound round)
    {
        if (round.SeenYouTubeMessageIds.Count <= Capacity) return;
        // Participant identity/timestamp is the authoritative per-user ledger.
        // Keep its latest IDs ahead of disposable legacy no-command entries.
        round.SeenYouTubeMessageIds = round.YouTubeParticipants.Values.OrderByDescending(item => item.LastMessageAt).Select(item => item.LastMessageId)
            .Concat(round.SeenYouTubeMessageIds).Distinct(StringComparer.Ordinal).Take(Capacity)
            .ToHashSet(StringComparer.Ordinal);
        round.ChatReplayOrder = null;
    }
}

public sealed class VotingChatBalance
{
    public string OptionId { get; set; } = "";
    public decimal Amount { get; set; }
    public Dictionary<string, decimal> AmountsByCurrency { get; set; } = new(StringComparer.Ordinal);
}

// Small, versioned transaction: no templates, donation ID set or round archive.
public sealed class VotingChatCommit
{
    public string RoundId { get; set; } = "";
    public long PreviousRevision { get; set; }
    public long Revision { get; set; }
    public Dictionary<string, YouTubeVotingParticipant> Participants { get; set; } = new(StringComparer.Ordinal);
    public List<VotingChatBalance> Balances { get; set; } = [];
    public List<string> RememberedIds { get; set; } = [];
    public VotingPhase Phase { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public List<VotingInterval> Intervals { get; set; } = [];
    public string Notice { get; set; } = "";

    internal static VotingChatCommit Create(VotingRound before, VotingRound after,
        HashSet<string> users, HashSet<string> options, List<string> ids) => new()
    {
        RoundId = after.Id, PreviousRevision = before.ChatRevision, Revision = after.ChatRevision,
        Participants = users.ToDictionary(id => id, id => after.YouTubeParticipants[id].Clone(), StringComparer.Ordinal),
        Balances = after.Options.Where(option => options.Contains(option.Id)).Select(option => new VotingChatBalance
        { OptionId = option.Id, Amount = option.Amount, AmountsByCurrency = new(option.AmountsByCurrency, StringComparer.Ordinal) }).ToList(),
        RememberedIds = ids, Phase = after.Phase, ClosedAt = after.ClosedAt,
        Intervals = after.Intervals.Select(interval => interval.Clone()).ToList(), Notice = after.Notice
    };

    public void Apply(VotingDocument document)
    {
        if (string.IsNullOrWhiteSpace(RoundId) || PreviousRevision < 0 || PreviousRevision == long.MaxValue ||
            Revision != PreviousRevision + 1 || Participants is null || Balances is null || RememberedIds is null ||
            Intervals is null || Notice is null || !Enum.IsDefined(Phase) ||
            RememberedIds.Any(string.IsNullOrWhiteSpace) ||
            Participants.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null ||
                string.IsNullOrWhiteSpace(item.Value.OptionId) || string.IsNullOrWhiteSpace(item.Value.LastMessageId) ||
                item.Value.LastMessageAt == default || item.Value.Weight is <= 0 or > 1_000_000_000m ||
                !Money.TryNormalizeCurrency(item.Value.Currency, out var code) || code != item.Value.Currency) ||
            Balances.Any(item => item is null || item.Amount < 0 || item.AmountsByCurrency is null ||
                item.AmountsByCurrency.Any(amount => amount.Value < 0 ||
                    !Money.TryNormalizeCurrency(amount.Key, out var code) || code != amount.Key)) ||
            Balances.Select(item => item.OptionId).Distinct(StringComparer.Ordinal).Count() != Balances.Count ||
            Intervals.Any(item => item is null || item.End < item.Start))
            throw new InvalidDataException("Повреждена запись журнала голосов чата. Автоматический сброс запрещён.");
        var round = new[] { document.Current }.Concat(document.OtherCollections).Concat(document.PreviousRounds)
            .FirstOrDefault(item => item.Id == RoundId)
            ?? throw new InvalidDataException("Журнал голосов чата относится к другому состоянию голосований.");
        // A checkpoint can be committed before the old journal is removed.
        if (round.ChatRevision >= Revision) return;
        if (round.ChatRevision != PreviousRevision || round.Phase != VotingPhase.Collecting ||
            Balances.Any(item => round.Options.All(option => option.Id != item.OptionId)))
            throw new InvalidDataException("Нарушена последовательность журнала голосов чата.");
        foreach (var item in Participants) round.YouTubeParticipants[item.Key] = item.Value.Clone();
        foreach (var item in Balances)
        {
            var option = round.Options.Single(option => option.Id == item.OptionId);
            option.Amount = item.Amount; option.AmountsByCurrency = new(item.AmountsByCurrency, StringComparer.Ordinal);
        }
        foreach (var id in RememberedIds) VotingChatReplay.Remember(round, id);
        round.Phase = Phase; round.ClosedAt = ClosedAt; round.Notice = Notice;
        round.Intervals = Intervals.Select(interval => interval.Clone()).ToList(); round.ChatRevision = Revision;
    }
}
