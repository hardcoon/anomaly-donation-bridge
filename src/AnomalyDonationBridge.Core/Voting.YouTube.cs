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

    public Task<int> RecordChatAsync(string roundId, string profileId,
        IReadOnlyList<ChatVotingMessage> messages, CancellationToken token = default) =>
        RecordChatBatchesAsync(roundId, profileId, messages, token);
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
        if (round.YouTubeParticipants is null || round.SeenYouTubeMessageIds is null || round.ChatRevision < 0 ||
            !round.AcceptDonationVotes && !round.AcceptYouTubeVotes || round.YouTubeVoteWeight is <= 0 or > 1_000_000_000m ||
            round.SeenYouTubeMessageIds.Any(string.IsNullOrWhiteSpace) ||
            round.YouTubeParticipants.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                string.IsNullOrWhiteSpace(pair.Value.OptionId) || string.IsNullOrWhiteSpace(pair.Value.LastMessageId) ||
                pair.Value.Weight is <= 0 or > 1_000_000_000m || pair.Value.LastMessageAt == default ||
                !Money.TryNormalizeCurrency(pair.Value.Currency, out var code) || code != pair.Value.Currency))
            throw new InvalidDataException("Повреждён учёт голосов чата в voting.json. Автоматический сброс запрещён.");
    }
}
