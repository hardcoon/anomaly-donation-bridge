using System.Collections.Concurrent;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private readonly ConcurrentDictionary<VotingChatPlatform, string> _chatVotingErrors = new();
    private string ChatVotingError => _chatVotingErrors.Values.FirstOrDefault() ?? string.Empty;

    private async Task AcceptChatVotesAsync(IReadOnlyList<ChatVotingMessage> messages, CancellationToken token)
    {
        if (!_votingLoaded) return;
        var profileId = Settings.ActiveProfileId;
        // Pin the round before any disk/lock wait. Reconnects, profile switches
        // and automatic transitions cannot reassign a late batch to a new round.
        var roundId = Voting.ChatRoundId(profileId);
        if (roundId is null) return;
        var votes = messages.Where(message => !message.IsHistorical && !message.IsMirrored).ToArray();
        if (votes.Length == 0) return;
        var source = votes[0].Platform;
        try
        {
            while (Settings.ActiveProfileId == profileId)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // VotingCoordinator serializes all platforms in the same
                    // transaction path. Winners keep the existing game queue.
                    await Voting.RecordChatAsync(roundId, profileId, votes, token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    _chatVotingErrors[source] = "Не удалось сохранить голоса чата. Повтор через 5 с. Проверьте доступ к папке настроек.";
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            }
        }
        finally { _chatVotingErrors.TryRemove(source, out _); }
    }
}
