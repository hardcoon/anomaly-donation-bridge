namespace AnomalyDonationBridge.Core;

public sealed partial class VotingCoordinator
{
    private VotingRound? _chatOptionsRound;
    private VotingOption[] _chatOptions = [];

    private async Task<int> RecordChatBatchesAsync(string roundId, string profileId,
        IReadOnlyList<ChatVotingMessage> messages, CancellationToken token)
    {
        var counted = 0;
        // Bound each journal record and keep every chunk pinned to this round.
        for (var start = 0; start < messages.Count; start += 256)
            counted += await RecordChatChunkAsync(roundId, profileId, messages, start,
                Math.Min(messages.Count, start + 256), token).ConfigureAwait(false);
        return counted;
    }

    private async Task<int> RecordChatChunkAsync(string roundId, string profileId,
        IReadOnlyList<ChatVotingMessage> messages, int start, int end, CancellationToken token)
    {
        var visibleChange = false;
        var counted = 0;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                var original = _document;
                var before = AllCollections(original).FirstOrDefault(item => item.Id == roundId);
                if (before is null || !original.Automation.Enabled || !before.IsActive || !before.AcceptYouTubeVotes ||
                    before.ProfileId != profileId || before.Phase != VotingPhase.Collecting) return;
                var now = _now();
                var options = ChatOptions(before);
                if (!CompletionState(before, now).Complete)
                {
                    var useful = false;
                    for (var i = start; i < end && !useful; i++)
                        useful = TryChatCandidate(before, options, messages[i], now, out _);
                    // No copy, retained ID, disk write or UI event for plain chat.
                    if (!useful) return;
                }
                var round = before.CloneForChat();
                var users = new HashSet<string>(StringComparer.Ordinal);
                var balances = new HashSet<string>(StringComparer.Ordinal);
                var remembered = new List<string>();
                visibleChange = Evaluate(round, now);
                if (!visibleChange)
                {
                    for (var i = start; i < end; i++)
                    {
                        if (!TryChatCandidate(round, options, messages[i], now, out var vote)) continue;
                        var selected = round.Options.First(option => option.Id == vote.OptionId);
                        VotingChatReplay.Remember(round, vote.MessageId); remembered.Add(vote.MessageId);
                        users.Add(vote.UserId);
                        if (round.YouTubeParticipants.TryGetValue(vote.UserId, out var oldParticipant))
                        {
                            var participant = oldParticipant.Clone();
                            participant.LastMessageAt = vote.At; participant.LastMessageId = vote.MessageId;
                            round.YouTubeParticipants[vote.UserId] = participant;
                            // Persist the watermark so an older delayed change
                            // cannot override a more recent repeated choice.
                            if (participant.OptionId == selected.Id) continue;
                            var previous = round.Options.FirstOrDefault(option => option.Id == participant.OptionId);
                            if (previous is not null)
                            { AddYouTubeWeight(previous, participant.Currency, -participant.Weight, round.Currency); balances.Add(previous.Id); }
                            AddYouTubeWeight(selected, participant.Currency, participant.Weight, round.Currency);
                            participant.OptionId = selected.Id;
                        }
                        else
                        {
                            var weight = Money.FromMinor(Money.ToMinor(round.YouTubeVoteWeight, round.Currency), round.Currency);
                            AddYouTubeWeight(selected, round.Currency, weight, round.Currency);
                            round.YouTubeParticipants.Add(vote.UserId, new YouTubeVotingParticipant
                            { OptionId = selected.Id, Weight = weight, Currency = round.Currency,
                                LastMessageAt = vote.At, LastMessageId = vote.MessageId });
                        }
                        balances.Add(selected.Id); counted++; visibleChange = true;
                        if (Evaluate(round, now)) break;
                    }
                }
                round.ChatRevision = checked(before.ChatRevision + 1);
                var document = new VotingDocument
                {
                    SchemaVersion = original.SchemaVersion, ChatJournalId = original.ChatJournalId, ActiveProfileId = original.ActiveProfileId,
                    HasCollections = original.HasCollections, Current = ReferenceEquals(before, original.Current) ? round : original.Current,
                    OtherCollections = original.OtherCollections.Select(item => ReferenceEquals(item, before) ? round : item).ToList(),
                    CollectionOrder = original.CollectionOrder, Automation = original.Automation,
                    PreviousRounds = original.PreviousRounds, SeenDonationIds = original.SeenDonationIds
                };
                var commit = VotingChatCommit.Create(before, round, users, balances, remembered);
                if (store is IChatVotingStore journal) await journal.SaveChatAsync(document, commit, token).ConfigureAwait(false);
                else await store.SaveAsync(document, token).ConfigureAwait(false);
                Volatile.Write(ref _document, document);
                // Chat only changes counters: reuse the validated command set.
                _chatOptionsRound = round;
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (visibleChange) NotifyChanged();
        return counted;
    }

    private VotingOption[] ChatOptions(VotingRound round)
    {
        if (ReferenceEquals(_chatOptionsRound, round)) return _chatOptions;
        _chatOptionsRound = round;
        return _chatOptions = round.Options.Where(option => option.Enabled && option.Preset is not null &&
            option.Preset.Validate().Count == 0).ToArray();
    }

    private readonly record struct ChatCandidate(string UserId, string MessageId, string OptionId, DateTimeOffset At);

    private static bool TryChatCandidate(VotingRound round, VotingOption[] options, ChatVotingMessage message,
        DateTimeOffset now, out ChatCandidate candidate)
    {
        candidate = default;
        var interval = round.Intervals.LastOrDefault(item => item.End is null);
        if (interval is null || message is null || message.IsHistorical || message.IsMirrored || message.PublishedAt is not { } at ||
            at < interval.Start || round.ChatRulesChangedAt is { } boundary && at < boundary ||
            at > now.AddSeconds(30) || !Enum.IsDefined(message.Platform) ||
            string.IsNullOrWhiteSpace(message.Id) || message.Id.Length > 2048 ||
            string.IsNullOrWhiteSpace(message.UserId) || message.UserId.Length > 512 || string.IsNullOrEmpty(message.Text) ||
            message.ChannelId is null || message.ChannelId.Length > 1024 || message.Platform != VotingChatPlatform.YouTube && string.IsNullOrWhiteSpace(message.ChannelId)) return false;
        VotingOption? selected = null;
        try
        {
            foreach (var option in options)
            {
                if (!VotingRules.ContainsCommand(message.Text, option.Command)) continue;
                if (selected is not null) return false;
                selected = option;
            }
        }
        catch (ArgumentException) { return false; }
        if (selected is null) return false;
        var source = message.Platform == VotingChatPlatform.Twitch ? "twitch" : "vk-video-live";
        var messageId = message.Platform == VotingChatPlatform.YouTube ? message.Id : $"{source}:{message.ChannelId.Length}:{message.ChannelId}:{message.Id}";
        var userId = message.Platform == VotingChatPlatform.YouTube ? message.UserId : $"{source}:{message.UserId}";
        if (round.SeenYouTubeMessageIds.Contains(messageId) ||
            round.YouTubeParticipants.TryGetValue(userId, out var participant) &&
            (!round.AllowYouTubeVoteChange || at <= participant.LastMessageAt || participant.LastMessageId == messageId)) return false;
        candidate = new(userId, messageId, selected.Id, at); return true;
    }

    private static bool ChatRulesDiffer(VotingRound before, VotingRound settings, List<VotingOption> options)
    {
        if (before.AcceptYouTubeVotes != settings.AcceptYouTubeVotes || before.AllowYouTubeVoteChange != settings.AllowYouTubeVoteChange) return true;
        var old = before.Options.Where(item => item.Enabled && item.Preset is not null && item.Preset.Validate().Count == 0).ToArray();
        var next = options.Where(item => item.Enabled && item.Preset is not null && item.Preset.Validate().Count == 0).ToArray();
        return old.Length != next.Length || old.Any(item => !next.Any(other => other.Id == item.Id &&
            string.Equals(VotingRules.NormalizeCommand(item.Command), VotingRules.NormalizeCommand(other.Command), StringComparison.OrdinalIgnoreCase)));
    }

    private static void ResetChatLedger(VotingRound round)
    {
        round.YouTubeParticipants.Clear(); round.SeenYouTubeMessageIds.Clear();
        round.ChatReplayOrder = null;
        round.ChatRevision = 0; round.ChatRulesChangedAt = null;
    }

    private static void CompactArchivedChatReplay(VotingDocument document)
    {
        foreach (var round in document.PreviousRounds)
        { round.SeenYouTubeMessageIds.Clear(); round.ChatReplayOrder = null; }
    }
}
