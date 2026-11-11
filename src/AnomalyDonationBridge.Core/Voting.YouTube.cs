namespace AnomalyDonationBridge.Core;

// Chat is a vote source, not a donation. No synthetic DonationEvent reaches the
// effect dispatcher, history or processed-donation journal.
public sealed record YouTubeVotingMessage(string Id, string AuthorChannelId, string Text,
    DateTimeOffset? PublishedAt, bool IsHistorical = false);

public sealed class YouTubeVotingParticipant
{
    public string OptionId { get; set; } = string.Empty;
    public decimal Weight { get; set; }
    public string Currency { get; set; } = "RUB";
    public string LastMessageId { get; set; } = string.Empty;
    public DateTimeOffset LastMessageAt { get; set; }
    public YouTubeVotingParticipant Clone() => (YouTubeVotingParticipant)MemberwiseClone();
}

public sealed partial class VotingCoordinator
{
    // Capture before awaiting disk/locks. A late batch must never be reassigned
    // to the next round, even if automatic drawing/reset runs in the meantime.
    public string? YouTubeRoundId(string profileId) => AllCollections(Volatile.Read(ref _document))
        .FirstOrDefault(round => round.IsActive && round.AcceptYouTubeVotes && round.ProfileId == profileId &&
            round.Phase == VotingPhase.Collecting)?.Id;

    public async Task<int> RecordYouTubeAsync(string roundId, string profileId,
        IReadOnlyList<YouTubeVotingMessage> messages, CancellationToken token = default)
    {
        var counted = 0;
        await MutateAsync(document =>
        {
            var round = AllCollections(document).FirstOrDefault(item => item.Id == roundId);
            if (round is null || !document.Automation.Enabled || !round.IsActive || !round.AcceptYouTubeVotes ||
                round.ProfileId != profileId || round.Phase != VotingPhase.Collecting) return false;
            var now = _now();
            if (Evaluate(round, now)) return true;
            var interval = round.Intervals.LastOrDefault(item => item.End is null);
            if (interval is null) return false;
            var options = round.Options.Where(option => option.Enabled && option.Preset is not null &&
                option.Preset.Validate().Count == 0).ToArray();
            var changed = false;
            foreach (var message in messages)
            {
                // Strict live boundary: donation recovery deliberately has
                // different rules. Old chat never fills a newly opened round.
                if (message.IsHistorical || message.PublishedAt is not { } at || at < interval.Start || at > now.AddSeconds(30) ||
                    string.IsNullOrWhiteSpace(message.Id) || string.IsNullOrWhiteSpace(message.AuthorChannelId) ||
                    round.SeenYouTubeMessageIds.Contains(message.Id)) continue;
                var matches = options.Where(option => VotingRules.ContainsCommand(message.Text, option.Command)).ToArray();
                // Remember ambiguous/no-command messages too: editing commands
                // later must not reinterpret a redelivered message as a new vote.
                round.SeenYouTubeMessageIds.Add(message.Id); changed = true;
                if (matches.Length != 1) continue;
                var selected = matches[0];
                if (round.YouTubeParticipants.TryGetValue(message.AuthorChannelId, out var participant))
                {
                    if (at <= participant.LastMessageAt) continue;
                    participant.LastMessageAt = at; participant.LastMessageId = message.Id;
                    if (!round.AllowYouTubeVoteChange || participant.OptionId == selected.Id) continue;
                    var previous = round.Options.FirstOrDefault(option => option.Id == participant.OptionId);
                    if (previous is not null) AddYouTubeWeight(previous, participant.Currency, -participant.Weight, round.Currency);
                    AddYouTubeWeight(selected, participant.Currency, participant.Weight, round.Currency);
                    participant.OptionId = selected.Id;
                }
                else
                {
                    var weight = Money.FromMinor(Money.ToMinor(round.YouTubeVoteWeight, round.Currency), round.Currency);
                    AddYouTubeWeight(selected, round.Currency, weight, round.Currency);
                    round.YouTubeParticipants.Add(message.AuthorChannelId, new YouTubeVotingParticipant
                    {
                        OptionId = selected.Id, Weight = weight, Currency = round.Currency,
                        LastMessageAt = at, LastMessageId = message.Id
                    });
                }
                counted++;
                // Reaching the goal seals this round inside this same transaction.
                // Remaining messages in the batch cannot spill into its successor.
                if (Evaluate(round, now)) break;
            }
            return changed;
        }, token).ConfigureAwait(false);
        return counted;
    }

    private static void AddYouTubeWeight(VotingOption option, string currency, decimal delta, string currentCurrency)
    {
        var old = currency == currentCurrency ? option.Amount : option.AmountsByCurrency.GetValueOrDefault(currency);
        var value = checked(old + delta);
        if (value < 0) throw new InvalidDataException("Повреждён учёт голосов YouTube. Сброс не выполнен.");
        option.AmountsByCurrency[currency] = value;
        if (currency == currentCurrency) option.Amount = value;
    }

    private static void ValidateYouTubeAccounting(VotingRound round)
    {
        if (round.YouTubeParticipants is null || round.SeenYouTubeMessageIds is null ||
            !round.AcceptDonationVotes && !round.AcceptYouTubeVotes || round.YouTubeVoteWeight is <= 0 or > 1_000_000_000m ||
            round.SeenYouTubeMessageIds.Any(string.IsNullOrWhiteSpace) ||
            round.YouTubeParticipants.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                string.IsNullOrWhiteSpace(pair.Value.OptionId) || string.IsNullOrWhiteSpace(pair.Value.LastMessageId) ||
                pair.Value.Weight is <= 0 or > 1_000_000_000m || pair.Value.LastMessageAt == default ||
                !Money.TryNormalizeCurrency(pair.Value.Currency, out var code) || code != pair.Value.Currency))
            throw new InvalidDataException("Повреждён учёт голосов YouTube в voting.json. Автоматический сброс запрещён.");
    }
}
