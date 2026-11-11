using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using System.IO;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private readonly SemaphoreSlim _votingPresentationGate = new(1, 1);
    private string? _votingPresentationKnownId;
    private string? _votingPresentationAnnouncedId;
    // Only isolated tests replace transport; production always uses the live pipe.
    internal Func<VotingGamePresentation?, string, bool, CancellationToken, Task<VotingGameReply>>? VotingPresentationTransportForTests { get; set; }

    private async Task<VotingGameReply> ExchangeVotingPresentationAsync(VotingGamePresentation? request, string id, bool cancel, CancellationToken token)
        => VotingPresentationTransportForTests is { } fake ? await fake(request, id, cancel, token).ConfigureAwait(false) :
            await _gameServer.ExchangeVotingAsync(request, id, cancel, token).ConfigureAwait(false);

    internal async Task AdvanceVotingPresentationAsync(CancellationToken token = default)
    {
        if (!await _votingPresentationGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            var available = VotingPresentationTransportForTests is not null || _gameServer.IsConnected;
            if (available)
                foreach (var cancelled in Voting.CancelledGamePresentations.Where(round => round.ProfileId == Settings.ActiveProfileId))
                {
                    try
                    {
                        var response = await ExchangeVotingPresentationAsync(null, cancelled.Id, true, token).ConfigureAwait(false);
                        if (response.State is "done" or "unknown") await Voting.CompleteGamePresentationAsync(cancelled.Id, token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException) { break; }
                }
            var round = Voting.PendingWinners.FirstOrDefault(item => item.GamePresentationRequired && !item.GamePresentationCompleted &&
                item.ProfileId == Settings.ActiveProfileId);
            if (round is null)
            { _votingDrawingRoundId = _votingDrawingCollectionId = null; _votingPresentationKnownId = null; return; }
            _votingDrawingRoundId = round.Id; _votingDrawingCollectionId = round.CollectionId;
            if (!available)
            {
                await Voting.SetDeliveryNoteAsync("Победитель сохранён; игровая рулетка ожидает подключения игры.", token, round.Id).ConfigureAwait(false);
                return;
            }
            try
            {
                var request = _votingPresentationKnownId == round.Id ? null : VotingGamePresentation.Create(round);
                var response = await ExchangeVotingPresentationAsync(request, round.Id, false, token).ConfigureAwait(false);
                if (response.State == "unknown") { _votingPresentationKnownId = null; return; }
                if (response.State == "rejected")
                {
                    _votingPresentationKnownId = null;
                    await Voting.SetDeliveryNoteAsync("Игровая рулетка ожидает исправления: " + response.Reason, token, round.Id).ConfigureAwait(false);
                    return;
                }
                _votingPresentationKnownId = round.Id;
                if (response.State == "busy")
                {
                    var note = response.Reason.StartsWith("ui_unavailable", StringComparison.Ordinal)
                        ? "Не удалось открыть игровую рулетку: " + response.Reason + ". Подробности в игровом логе."
                        : "Игровая рулетка ожидает свободного героя: " + response.Reason;
                    await Voting.SetDeliveryNoteAsync(note, token, round.Id).ConfigureAwait(false);
                    return;
                }
                if (_votingPresentationAnnouncedId != round.Id)
                {
                    _votingPresentationAnnouncedId = round.Id;
                    if (VotingDrawStarted is { } handlers)
                        foreach (Action<VotingRound> handler in handlers.GetInvocationList())
                            try { handler(round.Clone()); } catch { /* OBS cannot undo a persisted result. */ }
                }
                if (response.State == "active")
                {
                    await Voting.SetDeliveryNoteAsync("Рулетка в игре: дождитесь результата" +
                        (round.GamePresentationAutomaticEffect ? "." : " и нажмите «Применить»."), token, round.Id).ConfigureAwait(false);
                    return;
                }
                if (response.State == "done")
                {
                    await Voting.CompleteGamePresentationAsync(round.Id, token).ConfigureAwait(false);
                    _votingDrawingRoundId = _votingDrawingCollectionId = null; _votingPresentationKnownId = null;
                    // The manual game button is itself explicit authorisation.
                    await SubmitVotingWinnerAsync(token, round.Id).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
            {
                _votingPresentationKnownId = null;
                await Voting.SetDeliveryNoteAsync("Победитель сохранён; игровая рулетка ожидает: " + error.Message, token, round.Id).ConfigureAwait(false);
            }
        }
        finally { _votingPresentationGate.Release(); }
    }
}
