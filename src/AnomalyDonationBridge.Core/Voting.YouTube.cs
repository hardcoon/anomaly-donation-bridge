namespace AnomalyDonationBridge.Core;

// Chat is a vote source, not a donation. No synthetic DonationEvent reaches the
// effect dispatcher, history or processed-donation journal.
public sealed record YouTubeVotingMessage(string Id, string AuthorChannelId, string Text,
    DateTimeOffset? PublishedAt, bool IsHistorical = false);

public enum VotingChatPlatform { YouTube, Twitch, VkVideoLive }
public sealed record ChatVotingMessage(VotingChatPlatform Platform, string ChannelId, string Id,
    string UserId, string Text, DateTimeOffset? PublishedAt, bool IsHistorical = false, bool IsMirrored = false);

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
    public string? YouTubeRoundId(string profileId) => ChatRoundId(profileId);
    public string? ChatRoundId(string profileId) => AllCollections(Volatile.Read(ref _document))
        .FirstOrDefault(round => round.IsActive && round.AcceptYouTubeVotes && round.ProfileId == profileId &&
            round.Phase == VotingPhase.Collecting)?.Id;

    public Task<int> RecordYouTubeAsync(string roundId, string profileId,
        IReadOnlyList<YouTubeVotingMessage> messages, CancellationToken token = default) =>
        RecordChatAsync(roundId, profileId, messages.Select(message => new ChatVotingMessage(
            VotingChatPlatform.YouTube, "", message.Id, message.AuthorChannelId, message.Text,
            message.PublishedAt, message.IsHistorical)).ToArray(), token);

    public async Task<int> RecordChatAsync(string roundId, string profileId,
        IReadOnlyList<ChatVotingMessage> messages, CancellationToken token = default)
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
                if (message.IsHistorical || message.IsMirrored || message.PublishedAt is not { } at ||
                    at < interval.Start || at > now.AddSeconds(30) || !Enum.IsDefined(message.Platform) ||
                    string.IsNullOrWhiteSpace(message.Id) || string.IsNullOrWhiteSpace(message.UserId) ||
                    message.Platform != VotingChatPlatform.YouTube && string.IsNullOrWhiteSpace(message.ChannelId)) continue;
                // Keep native YouTube keys so an existing saved round cannot grant
                // the same account another vote after upgrading to the shared chat.
                // Other sources have independent message and account namespaces.
                var source = message.Platform == VotingChatPlatform.Twitch ? "twitch" : "vk-video-live";
                var messageId = message.Platform == VotingChatPlatform.YouTube ? message.Id :
                    $"{source}:{message.ChannelId.Length}:{message.ChannelId}:{message.Id}";
                var userId = message.Platform == VotingChatPlatform.YouTube ? message.UserId : $"{source}:{message.UserId}";
                if (round.SeenYouTubeMessageIds.Contains(messageId)) continue;
                var matches = options.Where(option => VotingRules.ContainsCommand(message.Text, option.Command)).ToArray();
                // Remember ambiguous/no-command messages too: editing commands
                // later must not reinterpret a redelivered message as a new vote.
                round.SeenYouTubeMessageIds.Add(messageId); changed = true;
                if (matches.Length != 1) continue;
                var selected = matches[0];
                if (round.YouTubeParticipants.TryGetValue(userId, out var participant))
                {
                    if (at <= participant.LastMessageAt) continue;
                    participant.LastMessageAt = at; participant.LastMessageId = messageId;
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
                    round.YouTubeParticipants.Add(userId, new YouTubeVotingParticipant
                    {
                        OptionId = selected.Id, Weight = weight, Currency = round.Currency,
                        LastMessageAt = at, LastMessageId = messageId
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
        if (value < 0) throw new InvalidDataException("Повреждён учёт голосов чата. Сброс не выполнен.");
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
            throw new InvalidDataException("Повреждён учёт голосов чата в voting.json. Автоматический сброс запрещён.");
    }
}
