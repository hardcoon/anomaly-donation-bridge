using System.IO;
using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private (string RoundId, long OpenedAt, List<RecruitmentDonation> Candidates)? _traitorHistory;
    private TraitorRecruitmentStatus? _syncedTraitor;
    private Task _traitorUpdate = Task.CompletedTask;

    private void ScheduleTraitorSync(CancellationToken token)
    {
        if (!_traitorUpdate.IsCompleted) return;
        var status = _gameServer.TraitorRecruitment;
        if (!_gameServer.Capabilities.SupportsCapability(TraitorPipe.Capability) || status is { Needed: 0 })
        {
            _syncedTraitor = null;
            _traitorHistory = null;
            return;
        }

        if (status is not null && ReferenceEquals(_syncedTraitor, status)) return;
        _traitorUpdate = Task.Run(() => SyncTraitorAsync(status, token), token);
    }

    private async Task SyncTraitorAsync(TraitorRecruitmentStatus? status, CancellationToken token)
    {
        try
        {
            if (status is null)
            {
                _syncedTraitor = null;
                await _gameServer.SendTraitorUpdateAsync(TraitorPipe.Query, token).ConfigureAwait(false);
                return;
            }

            if (_traitorHistory is not { } lookup || lookup.RoundId != status.RoundId ||
                lookup.OpenedAt != status.OpenedAtMilliseconds)
            {
                var candidates = await DonationHistory.TraitorCandidatesAsync(_historyStore, status, token).ConfigureAwait(false);
                lookup = (status.RoundId, status.OpenedAtMilliseconds, candidates);
                _traitorHistory = lookup;
            }
            if (!ReferenceEquals(status, _gameServer.TraitorRecruitment)) return;

            var start = status.Cursor.Length == 0 ? 0 :
                lookup.Candidates.FindIndex(candidate => candidate.DonationId == status.Cursor) + 1;
            if (status.Cursor.Length > 0 && start == 0) start = lookup.Candidates.Count;
            var count = Math.Min(Math.Min(status.Needed, TraitorPipe.ParticipantsPerMessage), lookup.Candidates.Count - start);
            var last = start + count == lookup.Candidates.Count;
            var batch = TraitorPipe.Roster(status, lookup.Candidates.GetRange(start, count), last);
            if (await _gameServer.SendTraitorUpdateAsync(batch, token).ConfigureAwait(false)) _syncedTraitor = status;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _logger.WriteAsync("WARN", "Предатель в отряде: " + error.Message, token).ConfigureAwait(false);
        }
    }
}
